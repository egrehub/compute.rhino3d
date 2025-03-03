﻿using System;
using System.IO;
using System.Net;
using System.Collections.Generic;

using Rhino.Geometry;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;

using Resthopper.IO;
using Newtonsoft.Json;
using System.Linq;
using Serilog;
using System.Reflection;

namespace compute.geometry
{
    class GrasshopperDefinition
    {
        static Dictionary<string, FileSystemWatcher> _filewatchers;
        static HashSet<string> _watchedFiles = new HashSet<string>();
        static uint _watchedFileRuntimeSerialNumber = 1;
        public static uint WatchedFileRuntimeSerialNumber
        {
            get { return _watchedFileRuntimeSerialNumber; }
        }
        static void RegisterFileWatcher(string path)
        {
            if (_filewatchers == null)
            {
                _filewatchers = new Dictionary<string, FileSystemWatcher>();
            }
            if (!File.Exists(path))
                return;

            path = Path.GetFullPath(path);
            if (_watchedFiles.Contains(path.ToLowerInvariant()))
                return;

            _watchedFiles.Add(path.ToLowerInvariant());
            string directory = Path.GetDirectoryName(path);
            if (_filewatchers.ContainsKey(directory) || !Directory.Exists(directory))
                return;

            var fsw = new FileSystemWatcher(directory);
            fsw.NotifyFilter = NotifyFilters.Attributes |
                NotifyFilters.CreationTime |
                NotifyFilters.FileName |
                NotifyFilters.LastAccess |
                NotifyFilters.LastWrite |
                NotifyFilters.Size |
                NotifyFilters.Security;
            fsw.Changed += Fsw_Changed;
            fsw.EnableRaisingEvents = true;
            _filewatchers[directory] = fsw;
        }

        private static void Fsw_Changed(object sender, FileSystemEventArgs e)
        {
            string path = e.FullPath.ToLowerInvariant();
            if (_watchedFiles.Contains(path))
                _watchedFileRuntimeSerialNumber++;
        }

        public static void LogDebug(string message) { Log.Debug(message); }
        public static void LogError(string message) { Log.Error(message); }

        public static GrasshopperDefinition FromUrl(string url, bool cache)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;
            GrasshopperDefinition rc = DataCache.GetCachedDefinition(url);

            if (rc != null)
            {
                LogDebug("Using cached definition");
                return rc;
            }

            if (Guid.TryParse(url, out Guid componentId))
            {
                rc = Construct(componentId);
            }
            else
            {
                var archive = ArchiveFromUrl(url);
                if (archive == null)
                    return null;

                rc = Construct(archive);
                rc.CacheKey = url;
                rc.IsLocalFileDefinition = !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && File.Exists(url);
            }
            if (cache)
            {
                DataCache.SetCachedDefinition(url, rc, null);
                rc.InDataCache = true;
            }
            return rc;
        }

        public static GrasshopperDefinition FromBase64String(string data, bool cache)
        {
            var archive = ArchiveFromBase64String(data);
            if (archive == null)
                return null;

            var rc = Construct(archive);
            if (rc!=null)
            {
                rc.CacheKey = DataCache.CreateCacheKey(data);
                if (cache)
                {
                    DataCache.SetCachedDefinition(rc.CacheKey, rc, data);
                    rc.InDataCache = true;
                }
            }
            return rc;
        }

        private static GrasshopperDefinition Construct(Guid componentId)
        {
            var component = Grasshopper.Instances.ComponentServer.EmitObject(componentId) as GH_Component;
            if (component==null)
                return null;

            var definition = new GH_Document();
            definition.AddObject(component, false);

            try
            {
                // raise DocumentServer.DocumentAdded event (used by some plug-ins)
                Grasshopper.Instances.DocumentServer.AddDocument(definition);
            }
            catch (Exception e)
            {
                Log.Error(e, "Exception in DocumentAdded event handler");
            }

            GrasshopperDefinition rc = new GrasshopperDefinition(definition, null);
            rc._singularComponent = component;
            foreach(var input in component.Params.Input)
            {
                rc._input[input.NickName] = new InputGroup(input);
            }
            foreach(var output in component.Params.Output)
            {
                rc._output[output.NickName] = output;
            }
            return rc;
        }
        private static void AddInput(IGH_Param param, string name, ref GrasshopperDefinition rc)
        {
            if (rc._input.ContainsKey(name))
            {
                string msg = "Multiple input parameters with the same name were detected. Parameter names must be unique.";
                rc.HasErrors = true;
                rc.ErrorMessages.Add(msg);
                LogError(msg);
            }   
            else
                rc._input[name] = new InputGroup(param);
        }
        private static void AddOutput(IGH_Param param, string name, ref GrasshopperDefinition rc)
        {
            if (rc._output.ContainsKey(name))
            {
                string msg = "Multiple output parameters with the same name were detected. Parameter names must be unique.";
                rc.HasErrors = true;
                rc.ErrorMessages.Add(msg);
                LogError(msg);
            }  
            else
                rc._output[name] = param;
        }

        private static GrasshopperDefinition Construct(GH_Archive archive)
        {
            string icon = null;
            var chunk = archive.GetRootNode.FindChunk("Definition");
            if (chunk!=null)
            {
                chunk = chunk.FindChunk("DefinitionProperties");
                if (chunk != null)
                {
                    string s = String.Empty;
                    if (chunk.TryGetString("IconImageData", ref s))
                    {
                        icon = s;
                    }
                }
            }

            var definition = new GH_Document();
            if (!archive.ExtractObject(definition, "Definition"))
                throw new Exception("Unable to extract definition from archive");

            try
            {
                // raise DocumentServer.DocumentAdded event (used by some plug-ins)
                Grasshopper.Instances.DocumentServer.AddDocument(definition);
            }
            catch (Exception e)
            {
                Log.Error(e, "Exception in DocumentAdded event handler");
            }

            GrasshopperDefinition rc = new GrasshopperDefinition(definition, icon);
            foreach( var obj in definition.Objects)
            {
                IGH_ContextualParameter contextualParam = obj as IGH_ContextualParameter;
                if (contextualParam != null)
                {
                    IGH_Param param = obj as IGH_Param;
                    if (param != null)
                    {
                        AddInput(param, param.NickName, ref rc);
                    }
                    continue;
                }


                Type objectClass = obj.GetType();
                var className = objectClass.Name;
                if (className == "ContextBakeComponent")
                {
                    var contextBaker = obj as GH_Component;
                    IGH_Param param = contextBaker.Params.Input[0];
                    AddOutput(param, param.NickName, ref rc);
                }

                if (className == "ContextPrintComponent")
                {
                    var contextPrinter = obj as GH_Component;
                    IGH_Param param = contextPrinter.Params.Input[0];
                    AddOutput(param, param.NickName, ref rc);
                }

                var group = obj as GH_Group;
                if (group == null)
                    continue;

                string nickname = group.NickName;
                var groupObjects = group.Objects();
                if ( nickname.Contains("RH_IN") && groupObjects.Count>0)
                {
                    var param = groupObjects[0] as IGH_Param;
                    if (param != null)
                    {
                        AddInput(param, nickname, ref rc);
                    }
                }

                if (nickname.Contains("RH_OUT") && groupObjects.Count > 0)
                {
                    if (groupObjects[0] is IGH_Param param)
                    {
                        AddOutput(param, nickname, ref rc);
                    }
                    else if(groupObjects[0] is GH_Component component)
                    {
                        int outputCount = component.Params.Output.Count;
                        for(int i=0; i<outputCount; i++)
                        {
                            if(1==outputCount)
                            {
                                AddOutput(component.Params.Output[i], nickname, ref rc);
                            }
                            else
                            {
                                string itemName = $"{nickname} ({component.Params.Output[i].NickName})";
                                AddOutput(component.Params.Output[i], itemName, ref rc);
                            }
                        }
                    }
                }
            }
            return rc;
        }

        private GrasshopperDefinition(GH_Document definition, string icon)
        {
            Definition = definition;
            _iconString = icon;
            FileRuntimeCacheSerialNumber = _watchedFileRuntimeSerialNumber;
        }

        public GH_Document Definition { get; }
        public bool InDataCache { get; set; }
        public bool HasErrors { get; private set; } // default: false
        public bool IsLocalFileDefinition { get; set; } // default: false
        public uint FileRuntimeCacheSerialNumber { get; private set; }
        public string CacheKey { get; set; }
        string _iconString;
        GH_Component _singularComponent;
        Dictionary<string, InputGroup> _input = new Dictionary<string, InputGroup>();
        Dictionary<string, IGH_Param> _output = new Dictionary<string, IGH_Param>();
        public List<string> ErrorMessages = new List<string>();

        public GH_Path GetPath(string p)
        {
            string tempPath = p.Trim('{', '}');
            int[] pathIndices = tempPath.Split(';').Select(Int32.Parse).ToArray();
            return new GH_Path(pathIndices);
        }

        public void SetInputs(List<Resthopper.IO.DataTree<ResthopperObject>> values)
        {
            foreach (var tree in values)
            {
                if( !_input.TryGetValue(tree.ParamName, out var inputGroup))
                {
                    continue;
                }

                if (inputGroup.AlreadySet(tree))
                {
                    LogDebug("Skipping input tree... same input");
                    continue;
                }

                inputGroup.CacheTree(tree);

                IGH_ContextualParameter contextualParameter = inputGroup.Param as IGH_ContextualParameter;
                if(contextualParameter != null)
                {
                    var treeAccess = Convert.ToBoolean(contextualParameter.GetType().GetProperty("TreeAccess")?.GetValue(contextualParameter, null));
                    if (contextualParameter != null)
                    {
                        if (contextualParameter.AtLeast == 0)
                            (contextualParameter as IGH_Param).Optional = true;
                        switch (ParamTypeName(inputGroup.Param))
                        {
                            case "Boolean":
                                {
                                    Grasshopper.DataTree<GH_Boolean> inputTree = new Grasshopper.DataTree<GH_Boolean>();
                                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                                    {
                                        GH_Path path = GetPath(entree.Key);
                                        for (int i = 0; i < entree.Value.Count; i++)
                                        {
                                            ResthopperObject restobj = entree.Value[i];
                                            var b = new GH_Boolean(JsonConvert.DeserializeObject<bool>(restobj.Data));
                                            inputTree.Add(b, path);
                                        }
                                    }
                                    contextualParameter.GetType()
                                        .GetMethod("AssignContextualDataTree")?
                                        .Invoke(contextualParameter, new object[] { inputTree });
                                }
                                break;
                            case "Number":
                                {
                                    Grasshopper.DataTree<GH_Number> inputTree = new Grasshopper.DataTree<GH_Number>();
                                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                                    {
                                        GH_Path path = GetPath(entree.Key);
                                        for (int i = 0; i < entree.Value.Count; i++)
                                        {
                                            ResthopperObject restobj = entree.Value[i];
                                            var d = new GH_Number(JsonConvert.DeserializeObject<double>(restobj.Data));
                                            inputTree.Add(d, path);
                                        }
                                    }
                                    contextualParameter.GetType()
                                        .GetMethod("AssignContextualDataTree")?
                                        .Invoke(contextualParameter, new object[] { inputTree });
                                }
                                break;
                            case "Integer":
                                {
                                    Grasshopper.DataTree<GH_Integer> inputTree = new Grasshopper.DataTree<GH_Integer>();
                                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                                    {
                                        GH_Path path = GetPath(entree.Key);
                                        for (int i = 0; i < entree.Value.Count; i++)
                                        {
                                            ResthopperObject restobj = entree.Value[i];
                                            var integer = new GH_Integer(JsonConvert.DeserializeObject<int>(restobj.Data));
                                            inputTree.Add(integer, path);
                                        }
                                    }
                                    contextualParameter.GetType()
                                        .GetMethod("AssignContextualDataTree")?
                                        .Invoke(contextualParameter, new object[] { inputTree });
                                }
                                break;
                            case "Point":
                                {
                                    Grasshopper.DataTree<GH_Point> inputTree = new Grasshopper.DataTree<GH_Point>();
                                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                                    {
                                        GH_Path path = GetPath(entree.Key);
                                        for (int i = 0; i < entree.Value.Count; i++)
                                        {
                                            ResthopperObject restobj = entree.Value[i];
                                            var p = new GH_Point(JsonConvert.DeserializeObject<Rhino.Geometry.Point3d>(restobj.Data));
                                            inputTree.Add(p, path);
                                        }
                                    }
                                    contextualParameter.GetType()
                                        .GetMethod("AssignContextualDataTree")?
                                        .Invoke(contextualParameter, new object[] { inputTree });
                                }
                                break;
                            case "Plane":
                                {
                                    Grasshopper.DataTree<GH_Plane> inputTree = new Grasshopper.DataTree<GH_Plane>();
                                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                                    {
                                        GH_Path path = GetPath(entree.Key);
                                        for (int i = 0; i < entree.Value.Count; i++)
                                        {
                                            ResthopperObject restobj = entree.Value[i];
                                            var p = new GH_Plane(JsonConvert.DeserializeObject<Rhino.Geometry.Plane>(restobj.Data));
                                            inputTree.Add(p, path);
                                        }
                                    }
                                    contextualParameter.GetType()
                                        .GetMethod("AssignContextualDataTree")?
                                        .Invoke(contextualParameter, new object[] { inputTree });
                                }
                                break;
                            case "Line":
                                {
                                    Grasshopper.DataTree<GH_Line> inputTree = new Grasshopper.DataTree<GH_Line>();
                                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                                    {
                                        GH_Path path = GetPath(entree.Key);
                                        for (int i = 0; i < entree.Value.Count; i++)
                                        {
                                            ResthopperObject restobj = entree.Value[i];
                                            var l = new GH_Line(JsonConvert.DeserializeObject<Rhino.Geometry.Line>(restobj.Data));
                                            inputTree.Add(l, path);
                                        }
                                    }
                                    contextualParameter.GetType()
                                        .GetMethod("AssignContextualDataTree")?
                                        .Invoke(contextualParameter, new object[] { inputTree });
                                }
                                break;
                            case "Text":
                                {
                                    Grasshopper.DataTree<GH_String> inputTree = new Grasshopper.DataTree<GH_String>();
                                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                                    {
                                        GH_Path path = GetPath(entree.Key);
                                        for (int i = 0; i < entree.Value.Count; i++)
                                        {
                                            GH_String s;
                                            ResthopperObject restobj = entree.Value[i];
                                            try
                                            {
                                                // Use JsonConvert to properly unescape the string
                                                s = new GH_String(JsonConvert.DeserializeObject<string>(restobj.Data));
                                                inputTree.Add(s, path);
                                            }
                                            catch (Exception)
                                            {
                                                s = new GH_String(System.Text.RegularExpressions.Regex.Unescape(restobj.Data));
                                                inputTree.Add(s, path);
                                            }
                                        }
                                    }
                                    contextualParameter.GetType()
                                        .GetMethod("AssignContextualDataTree")?
                                        .Invoke(contextualParameter, new object[] { inputTree });
                                }
                                break;
                            case "Geometry":
                                {
                                    Grasshopper.DataTree<IGH_GeometricGoo> inputTree = new Grasshopper.DataTree<IGH_GeometricGoo>();
                                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                                    {
                                        GH_Path path = GetPath(entree.Key);
                                        for (int i = 0; i < entree.Value.Count; i++)
                                        {
                                            ResthopperObject restobj = entree.Value[i];
                                            var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(restobj.Data);
                                            var gb = Rhino.Runtime.CommonObject.FromJSON(dict) as GeometryBase;
                                            var goo = GH_Convert.ToGeometricGoo(gb);
                                            inputTree.Add(goo, path);
                                        }
                                    }
                                    contextualParameter.GetType()
                                        .GetMethod("AssignContextualDataTree")?
                                        .Invoke(contextualParameter, new object[] { inputTree });
                                }
                                break;
                        }
                        continue;
                    }
                }
                
                inputGroup.Param.VolatileData.Clear();
                inputGroup.Param.ExpireSolution(false); // mark param as expired but don't recompute just yet!

                if (inputGroup.Param is Param_Point)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Point3d rPt = JsonConvert.DeserializeObject<Rhino.Geometry.Point3d>(restobj.Data);
                            GH_Point data = new GH_Point(rPt);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Vector)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Vector3d rhVector = JsonConvert.DeserializeObject<Rhino.Geometry.Vector3d>(restobj.Data);
                            GH_Vector data = new GH_Vector(rhVector);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Integer)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            int rhinoInt = JsonConvert.DeserializeObject<int>(restobj.Data);
                            GH_Integer data = new GH_Integer(rhinoInt);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Number)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            double rhNumber = JsonConvert.DeserializeObject<double>(restobj.Data);
                            GH_Number data = new GH_Number(rhNumber);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_String)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            string rhString = restobj.Data;
                            GH_String data = new GH_String(rhString);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Line)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Line rhLine = JsonConvert.DeserializeObject<Rhino.Geometry.Line>(restobj.Data);
                            GH_Line data = new GH_Line(rhLine);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Curve)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            GH_Curve ghCurve;
                            try
                            {
                                Rhino.Geometry.Polyline data = JsonConvert.DeserializeObject<Rhino.Geometry.Polyline>(restobj.Data);
                                Rhino.Geometry.Curve c = new Rhino.Geometry.PolylineCurve(data);
                                ghCurve = new GH_Curve(c);
                            }
                            catch
                            {
                                var dict = JsonConvert.DeserializeObject<Dictionary<string, string>>(restobj.Data);
                                var c = (Rhino.Geometry.Curve)Rhino.Runtime.CommonObject.FromJSON(dict);
                                ghCurve = new GH_Curve(c);
                            }
                            inputGroup.Param.AddVolatileData(path, i, ghCurve);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Circle)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Circle rhCircle = JsonConvert.DeserializeObject<Rhino.Geometry.Circle>(restobj.Data);
                            GH_Circle data = new GH_Circle(rhCircle);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Plane)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Plane rhPlane = JsonConvert.DeserializeObject<Rhino.Geometry.Plane>(restobj.Data);
                            GH_Plane data = new GH_Plane(rhPlane);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Rectangle)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Rectangle3d rhRectangle = JsonConvert.DeserializeObject<Rhino.Geometry.Rectangle3d>(restobj.Data);
                            GH_Rectangle data = new GH_Rectangle(rhRectangle);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Box)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Box rhBox = JsonConvert.DeserializeObject<Rhino.Geometry.Box>(restobj.Data);
                            GH_Box data = new GH_Box(rhBox);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Surface)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Surface rhSurface = JsonConvert.DeserializeObject<Rhino.Geometry.Surface>(restobj.Data);
                            GH_Surface data = new GH_Surface(rhSurface);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Brep)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Brep rhBrep = JsonConvert.DeserializeObject<Rhino.Geometry.Brep>(restobj.Data);
                            GH_Brep data = new GH_Brep(rhBrep);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Mesh)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            Rhino.Geometry.Mesh rhMesh = JsonConvert.DeserializeObject<Rhino.Geometry.Mesh>(restobj.Data);
                            GH_Mesh data = new GH_Mesh(rhMesh);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is GH_NumberSlider)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            double rhNumber = JsonConvert.DeserializeObject<double>(restobj.Data);
                            GH_Number data = new GH_Number(rhNumber);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is Param_Boolean || inputGroup.Param is GH_BooleanToggle)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            bool boolean = JsonConvert.DeserializeObject<bool>(restobj.Data);
                            GH_Boolean data = new GH_Boolean(boolean);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }

                if (inputGroup.Param is GH_Panel)
                {
                    foreach (KeyValuePair<string, List<ResthopperObject>> entree in tree)
                    {
                        GH_Path path = new GH_Path(GhPath.FromString(entree.Key));
                        for (int i = 0; i < entree.Value.Count; i++)
                        {
                            ResthopperObject restobj = entree.Value[i];
                            string rhString = JsonConvert.DeserializeObject<string>(restobj.Data);
                            GH_String data = new GH_String(rhString);
                            inputGroup.Param.AddVolatileData(path, i, data);
                        }
                    }
                    continue;
                }
            }

        }

        public Schema Solve(int rhinoVersion)
        {
            HasErrors = false;
            Schema outputSchema = new Schema();
            outputSchema.Algo = "";

            // solve definition
            Definition.Enabled = true;
            Definition.NewSolution(false, GH_SolutionMode.CommandLine);

            foreach(string msg in ErrorMessages)
            {
                outputSchema.Errors.Add(msg);
            }

            LogRuntimeMessages(Definition.ActiveObjects(), outputSchema);

            foreach (var kvp in _output)
            {
                var param = kvp.Value;
                if (param == null)
                    continue;

                Resthopper.IO.DataTree<ResthopperObject> outputTree = SerializeDataTree(param.VolatileData, kvp.Key, rhinoVersion) as Resthopper.IO.DataTree<ResthopperObject>;
                outputSchema.Values.Add(outputTree);
            }

            if (outputSchema.Values.Count < 1)
                throw new System.Exceptions.PayAttentionException("Looks like you've missed something..."); // TODO

            // Setting warnings and errors to null ever so slightly shrinks down the json sent back to the client
            if (outputSchema.Warnings.Count < 1)
                outputSchema.Warnings = null;
            if (outputSchema.Errors.Count < 1)
                outputSchema.Errors = null;

            return outputSchema;
        }

        private static object SerializeDataTree(IGH_Structure data, string name, int rhinoVersion = 7)
        {
            // Get data
            var outputTree = new Resthopper.IO.DataTree<ResthopperObject>();
            outputTree.ParamName = name;

            foreach (var path in data.Paths)
            {
                var resthopperObjectList = new List<ResthopperObject>();
                foreach (var goo in data.get_Branch(path))
                {
                    if (goo == null)
                        continue;

                    switch (goo)
                    {
                        case GH_Boolean ghValue:
                            {
                                bool rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<bool>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Point ghValue:
                            {
                                Point3d rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Point3d>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Vector ghValue:
                            {
                                Vector3d rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Vector3d>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Integer ghValue:
                            {
                                int rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<int>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Number ghValue:
                            {
                                double rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<double>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_String ghValue:
                            {
                                string rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<string>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_SubD ghValue:
                            {
                                SubD rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<SubD>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Line ghValue:
                            {
                                Line rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Line>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Curve ghValue:
                            {
                                Curve rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Curve>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Circle ghValue:
                            {
                                Circle rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Circle>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Arc ghValue:
                            {
                                Arc rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Arc>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Plane ghValue:
                            {
                                Plane rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Plane>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Rectangle ghValue:
                            {
                                Rectangle3d rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Rectangle3d>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Box ghValue:
                            {
                                Box rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Box>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Surface ghValue:
                            {
                                Brep rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Brep>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Brep ghValue:
                            {
                                Brep rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Brep>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Mesh ghValue:
                            {
                                Mesh rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Mesh>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Extrusion ghValue:
                            {
                                Extrusion rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Extrusion>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_PointCloud ghValue:
                            {
                                PointCloud rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<PointCloud>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_InstanceReference ghValue:
                            {
                                InstanceReferenceGeometry rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<InstanceReferenceGeometry>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Hatch ghValue:
                            {
                                Hatch rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Hatch>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_LinearDimension ghValue:
                            {
                                LinearDimension rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<LinearDimension>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_RadialDimension ghValue:
                            {
                                RadialDimension rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<RadialDimension>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_AngularDimension ghValue:
                            {
                                AngularDimension rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<AngularDimension>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_OrdinateDimension ghValue:
                            {
                                OrdinateDimension rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<OrdinateDimension>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Leader ghValue:
                            {
                                Leader rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Leader>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_TextEntity ghValue:
                            {
                                TextEntity rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<TextEntity>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_TextDot ghValue:
                            {
                                TextDot rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<TextDot>(rhValue, rhinoVersion));
                            }
                            break;
                        case GH_Centermark ghValue:
                            {
                                Centermark rhValue = ghValue.Value;
                                resthopperObjectList.Add(GetResthopperObject<Centermark>(rhValue, rhinoVersion));
                            }
                            break;
                    }
                }
                // preserve paths when returning data
                outputTree.Add(path.ToString(), resthopperObjectList);
            }
            return outputTree;
        }

        private void LogRuntimeMessages(IEnumerable<IGH_ActiveObject> objects, Schema schema)
        {
            foreach (var obj in objects)
            {
                foreach (var msg in obj.RuntimeMessages(GH_RuntimeMessageLevel.Error))
                {
                    string errorMsg = $"{msg}: component \"{obj.Name}\" ({obj.InstanceGuid})";
                    LogError(errorMsg);
                    schema.Errors.Add(errorMsg);
                    HasErrors = true;
                }
                if (Config.Debug)
                {
                    foreach (var msg in obj.RuntimeMessages(GH_RuntimeMessageLevel.Warning))
                    {
                        string warningMsg = $"{msg}: component \"{obj.Name}\" ({obj.InstanceGuid})";
                        LogDebug(warningMsg);
                        schema.Warnings.Add(warningMsg);
                    }
                    foreach (var msg in obj.RuntimeMessages(GH_RuntimeMessageLevel.Remark))
                    {
                        LogDebug($"Remark in grasshopper component: \"{obj.Name}\" ({obj.InstanceGuid}): {msg}");
                    }
                }
            }
        }

        static string ParamTypeName(IGH_Param param)
        {
            Type t = param.GetType();
            // TODO: Figure out why the GetGeometryParameter throws exceptions when calling TypeName
            if (t.Name.Equals("GetGeometryParameter"))
            {
                return "Geometry";
            }
            return param.TypeName;
        }

        public string GetIconAsString()
        {
            if (!string.IsNullOrWhiteSpace(_iconString))
                return _iconString;

            System.Drawing.Bitmap bmp = null;
            if (_singularComponent!=null)
            {
                bmp = _singularComponent.Icon_24x24;
            }

            if (bmp!=null)
            {
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] bytes = ms.ToArray();
                    string rc = Convert.ToBase64String(bytes);
                    _iconString = rc;
                    return rc;
                }
            }
            return null;
        }

        public IoResponseSchema GetInputsAndOutputs()
        {
            // Parse input and output names
            List<string> inputNames = new List<string>();
            List<string> outputNames = new List<string>();
            var inputs = new List<InputParamSchema>();
            var outputs = new List<IoParamSchema>();

            var sortedInputs = from x in _input orderby x.Value.Param.Attributes.Pivot.Y select x;
            var sortedOutputs = from x in _output orderby x.Value.Attributes.Pivot.Y select x;

            foreach (var i in sortedInputs)
            {
                inputNames.Add(i.Key);
                var inputSchema = new InputParamSchema
                {
                    Name = i.Key,
                    ParamType = ParamTypeName(i.Value.Param),
                    Description = i.Value.GetDescription(),
                    AtLeast = i.Value.GetAtLeast(),
                    AtMost = i.Value.GetAtMost(),
                    TreeAccess = i.Value.GetTreeAccess(),
                    Default = i.Value.GetDefault(),
                    Minimum = i.Value.GetMinimum(),
                    Maximum = i.Value.GetMaximum(),
                };
                if (_singularComponent != null)
                {
                    inputSchema.Description = i.Value.Param.Description;
                    if (i.Value.Param.Access == GH_ParamAccess.item)
                    {
                        inputSchema.AtMost = inputSchema.AtLeast;
                    }
                }
                inputs.Add(inputSchema);
            }

            foreach (var o in sortedOutputs)
            {
                outputNames.Add(o.Key);
                outputs.Add(new IoParamSchema
                {
                    Name = o.Key,
                    ParamType = o.Value.TypeName
                });
            }

            string description = _singularComponent == null ?
                Definition.Properties.Description :
                _singularComponent.Description;

            return new IoResponseSchema
            {
                Description = description,
                InputNames = inputNames,
                OutputNames = outputNames,
                Inputs = inputs,
                Outputs = outputs
            };
        }

        public static GH_Archive ArchiveFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            if (File.Exists(url))
            {
                // local file
                var archive = new GH_Archive();
                if (archive.ReadFromFile(url))
                {
                    RegisterFileWatcher(url);
                    return archive;
                }
                return null;
            }

            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                byte[] byteArray = null;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.AutomaticDecompression = DecompressionMethods.GZip;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var memStream = new MemoryStream())
                {
                    stream.CopyTo(memStream);
                    byteArray = memStream.ToArray();
                }

                try
                {
                    var byteArchive = new GH_Archive();
                    if (byteArchive.Deserialize_Binary(byteArray))
                        return byteArchive;
                }
                catch (Exception) { }

                var grasshopperXml = StripBom(System.Text.Encoding.UTF8.GetString(byteArray));
                var xmlArchive = new GH_Archive();
                if (xmlArchive.Deserialize_Xml(grasshopperXml))
                    return xmlArchive;
            }
            return null;
        }

        public static GH_Archive ArchiveFromBase64String(string blob)
        {
            if (string.IsNullOrWhiteSpace(blob))
                return null;

            byte[] byteArray = Convert.FromBase64String(blob);
            try
            {
                var byteArchive = new GH_Archive();
                if (byteArchive.Deserialize_Binary(byteArray))
                    return byteArchive;
            }
            catch (Exception) { }

            var grasshopperXml = StripBom(System.Text.Encoding.UTF8.GetString(byteArray));
            var xmlArchive = new GH_Archive();
            if (xmlArchive.Deserialize_Xml(grasshopperXml))
                return xmlArchive;

            return null;
        }

        // strip bom from string -- [239, 187, 191] in byte array == (char)65279
        // https://stackoverflow.com/a/54894929/1902446
        static string StripBom(string str)
        {
            if (!string.IsNullOrEmpty(str) && str[0] == (char)65279)
                str = str.Substring(1);
            return str;
        }

        static ResthopperObject GetResthopperObject<T>(object goo, int rhinoVerion)
        {
            var v = (T)goo;
            ResthopperObject rhObj = new ResthopperObject();
            rhObj.Type = goo.GetType().FullName;

            if (v is GeometryBase geometry)
                rhObj.Data = geometry.ToJSON(new Rhino.FileIO.SerializationOptions() { RhinoVersion = rhinoVerion });
            else
                rhObj.Data = JsonConvert.SerializeObject(v, GeometryResolver.Settings(rhinoVerion));

            return rhObj;
        }

        class InputGroup
        {
            object _default = null;
            public InputGroup(IGH_Param param)
            {
                Param = param;

                param.ClearData();
                param.CollectData();
                _default = SerializeDataTree(param.VolatileData, param.Name);
            }

            public IGH_Param Param { get; }

            public string GetDescription()
            {
                IGH_ContextualParameter contextualParameter = Param as IGH_ContextualParameter;
                if (contextualParameter != null)
                {
                    return contextualParameter.Prompt;
                }
                return null;
            }

            public int GetAtLeast()
            {
                IGH_ContextualParameter contextualParameter = Param as IGH_ContextualParameter;
                if(contextualParameter!=null)
                {
                    return contextualParameter.AtLeast;
                }
                return 1;
            }

            public int GetAtMost()
            {
                IGH_ContextualParameter contextualParameter = Param as IGH_ContextualParameter;
                if (contextualParameter != null)
                {
                    return contextualParameter.AtMost;
                }
                if (Param is GH_NumberSlider)
                    return 1;
                return int.MaxValue;
            }

            public bool GetTreeAccess()
            {
                IGH_ContextualParameter contextualParameter = Param as IGH_ContextualParameter;
                if (contextualParameter != null)
                {
                    var result = contextualParameter.GetType().GetProperty("TreeAccess")?.GetValue(contextualParameter, null);
                    if(result != null)
                        return (bool)result;
                }
                return false;
            }

            public object GetDefault()
            {
                return _default;
            }

            public double? GetMinimum()
            {
                var p = Param;
                if (p is IGH_ContextualParameter)
                {
                    var par = p as IGH_ContextualParameter;
                    var pTypeName = ParamTypeName(p);
                    var pType = par.GetType();
                    var props = pType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance);
                    var info = props.FirstOrDefault(x => x.Name == "Minimum");
                    if(info != null)
                    {
                        var val = info.GetValue(par, null);
                        if (val != null)
                        {
                            var min = Convert.ToDouble(val);
                            if (pTypeName == "Integer")
                            {
                                if (min > int.MinValue + Rhino.RhinoMath.Epsilon)
                                    return min;
                            }
                            else if (pTypeName == "Number")
                            {
                                if (min > double.MinValue + Rhino.RhinoMath.Epsilon)
                                    return min;
                            }
                        }
                    }

                    if (p.Sources.Count == 1)
                        p = p.Sources[0];
                }
                
                if (p is GH_NumberSlider paramSlider)
                    return (double)paramSlider.Slider.Minimum;
                return null;
            }

            public double? GetMaximum()
            {
                var p = Param;
                if (p is IGH_ContextualParameter)
                {
                    var par = p as IGH_ContextualParameter;
                    var pType = par.GetType();
                    var pTypeName = ParamTypeName(p);
                    var props = pType.GetProperties(BindingFlags.NonPublic | BindingFlags.Instance);
                    var info = props.FirstOrDefault(x => x.Name == "Maximum");
                    if(info != null)
                    {
                        var val = info.GetValue(par, null);
                        if (val != null)
                        {
                            var max = Convert.ToDouble(val);
                            if (pTypeName == "Integer")
                            {
                                if (max < int.MinValue - Rhino.RhinoMath.Epsilon)
                                    return max;
                            }
                            else if (pTypeName == "Number")
                            {
                                if (max < double.MinValue - Rhino.RhinoMath.Epsilon)
                                    return max;
                            }
                        }
                    }

                    if (p.Sources.Count == 1)
                        p = p.Sources[0];
                }

                if (p is GH_NumberSlider paramSlider)
                    return (double)paramSlider.Slider.Maximum;

                return null;
            }

            public bool AlreadySet(Resthopper.IO.DataTree<ResthopperObject> tree)
            {
                if (_tree == null)
                    return false;

                var oldDictionary = _tree.InnerTree;
                var newDictionary = tree.InnerTree;

                if (!oldDictionary.Keys.SequenceEqual(newDictionary.Keys))
                {
                    return false;
                }

                foreach (var kvp in oldDictionary)
                {
                    var oldValue = kvp.Value;
                    if (!newDictionary.TryGetValue(kvp.Key, out List<ResthopperObject> newValue))
                        return false;

                    if (!newValue.SequenceEqual(oldValue))
                    {
                        return false;
                    }
                }

                return true;
            }

            public void CacheTree(Resthopper.IO.DataTree<ResthopperObject> tree)
            {
                _tree = tree;
            }

            Resthopper.IO.DataTree<ResthopperObject> _tree;
        }
    }
}
