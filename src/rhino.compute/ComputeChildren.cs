using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Serilog;

namespace rhino.compute
{
    static class ComputeChildren
    {
        public static int SpawnCount { get; set; } = 1;

        static DateTime _lastCall = DateTime.MinValue;
        public static void UpdateLastCall()
        {
            _lastCall = DateTime.Now;
        }

        public static TimeSpan ChildIdleSpan { get; set; } = TimeSpan.Zero;
        public static bool SpawnOnStartup { get; set; } = false;
        public static int ParentPort { get; set; } = 5000;
        public static string RhinoSysDir { get; set; }

        public static int IdleSpan()
        {
            if (_lastCall == DateTime.MinValue)
                return -1;
            var span = DateTime.Now - _lastCall;
            return (int)span.TotalSeconds;
        }

        public static int ActiveComputeCount
        {
            get
            {
                lock (_lockObject)
                {
                    return _computeProcesses.Count + _startingProcesses.Count;
                }
            }
        }

        static object _lockObject = new object();
        static List<ComputeProcessInfo> _computeProcesses = new List<ComputeProcessInfo>();
        static List<ComputeProcessInfo> _startingProcesses = new List<ComputeProcessInfo>();

        public static (string, int) GetComputeServerBaseUrl()
        {
            UpdateLastCall();
            Stopwatch sw = Stopwatch.StartNew();

            while (true)
            {
                ComputeProcessInfo availableProcess = null;
                bool canSpawnNewProcess = false;

                lock (_lockObject)
                {
                    CleanupExitedProcesses();

                    // Move ready starting processes to available
                    MoveReadyProcessesToAvailable();

                    // Try to find a free compute.geometry process
                    foreach (var procInfo in _computeProcesses)
                    {
                        if (!IsComputeGeometryBusy(procInfo.Port))
                        {
                            // Found a free process
                            availableProcess = procInfo;
                            break;
                        }
                    }

                    if (availableProcess != null)
                    {
                        // We have a free process
                        return ($"http://localhost:{availableProcess.Port}", availableProcess.Port);
                    }

                    // Determine if we can spawn a new process
                    int totalProcesses = _computeProcesses.Count + _startingProcesses.Count;
                    canSpawnNewProcess = totalProcesses < SpawnCount && _startingProcesses.Count == 0;

                    if (canSpawnNewProcess)
                    {
                        // Spawn a new process without waiting
                        LaunchCompute(waitUntilServing: false);
                        // New process is added to _startingProcesses within LaunchCompute
                    }
                }

                if (!canSpawnNewProcess)
                {
                    // Wait for starting processes to become ready or existing processes to become free
                    bool anyProcessBecameAvailable = WaitForProcessAvailability();

                    if (anyProcessBecameAvailable)
                    {
                        // Loop will continue and re-check for available processes
                        continue;
                    }
                }

                // No available process, wait a bit
                if (sw.Elapsed.TotalSeconds > 60)
                {
                    throw new Exception("No compute server available after waiting 60 seconds");
                }

                Thread.Sleep(500); // Wait for 0.5 second before trying again
            }
        }

        private static void MoveReadyProcessesToAvailable()
        {
            var readyProcesses = new List<ComputeProcessInfo>();

            foreach (var procInfo in _startingProcesses)
            {
                if (IsComputeGeometryReady(procInfo.Port))
                {
                    readyProcesses.Add(procInfo);
                }
            }

            // Move ready processes to _computeProcesses
            foreach (var procInfo in readyProcesses)
            {
                _startingProcesses.Remove(procInfo);
                _computeProcesses.Add(procInfo);
            }
        }

        private static bool WaitForProcessAvailability()
        {
            // Wait for a short period
            Thread.Sleep(500);

            lock (_lockObject)
            {
                // Move any ready starting processes to available
                MoveReadyProcessesToAvailable();

                // Check if any compute processes are now free
                foreach (var procInfo in _computeProcesses)
                {
                    if (!IsComputeGeometryBusy(procInfo.Port))
                    {
                        return true; // A process became available
                    }
                }

                // No process became available yet
                return false;
            }
        }

        static void CleanupExitedProcesses()
        {
            _computeProcesses.RemoveAll(p => p.Process.HasExited);
            _startingProcesses.RemoveAll(p => p.Process.HasExited);
        }

        public static ComputeProcessInfo LaunchCompute(bool waitUntilServing)
        {
            var pathToThisAssembly = new System.IO.FileInfo(typeof(ComputeChildren).Assembly.Location);
            var parentDirectory = pathToThisAssembly.Directory.Parent;
            string pathToCompute = System.IO.Path.Combine(parentDirectory.FullName, "compute.geometry", "compute.geometry.exe");

            if (!System.IO.File.Exists(pathToCompute))
            {
                pathToCompute = System.IO.Path.Combine(pathToThisAssembly.Directory.FullName, "compute.geometry", "compute.geometry.exe");
                if (!System.IO.File.Exists(pathToCompute))
                    return null;
            }

            var existingPorts = new HashSet<int>(_computeProcesses.Select(p => p.Port).Concat(_startingProcesses.Select(p => p.Port)));

            int port = 0;
            for (int i = 0; i < 256; i++)
            {
                port = 6001 + i;
                if (i == 255)
                    return null;

                if (existingPorts.Contains(port))
                    continue;

                if (IsPortOpen("localhost", port, new TimeSpan(0, 0, 0, 0, 100)))
                    continue;

                break;
            }

            var startInfo = new ProcessStartInfo(pathToCompute);
            var rhinoProcess = Process.GetCurrentProcess();
            string commandLineArgs = $"-port:{port} -childof:{rhinoProcess.Id}";
            if (!string.IsNullOrEmpty(RhinoSysDir))
            {
                commandLineArgs += $" -rhinosysdir \"{RhinoSysDir}\"";
            }
            if (ParentPort > 0 && ChildIdleSpan.TotalSeconds > 1.0)
            {
                int seconds = (int)ChildIdleSpan.TotalSeconds;
                commandLineArgs += $" -parentport:{ParentPort} -idlespan:{seconds}";
            }
            startInfo.Arguments = commandLineArgs;

            var process = Process.Start(startInfo);
            var processInfo = new ComputeProcessInfo
            {
                Process = process,
                Port = port
            };

            if (process != null)
            {
                if (waitUntilServing)
                {
                    // Wait until the process is ready
                    WaitForProcessToBeReady(processInfo);
                    lock (_lockObject)
                    {
                        _computeProcesses.Add(processInfo);
                    }
                }
                else
                {
                    lock (_lockObject)
                    {
                        _startingProcesses.Add(processInfo);
                    }
                }

                return processInfo;
            }

            return null;
        }

        private static void WaitForProcessToBeReady(ComputeProcessInfo processInfo)
        {
            var start = DateTime.Now;
            while (true)
            {
                if (IsComputeGeometryReady(processInfo.Port))
                {
                    break;
                }

                var span = DateTime.Now - start;
                if (span.TotalSeconds > 180)
                {
                    try
                    {
                        processInfo.Process.Kill();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to kill compute.geometry process");
                    }
                    string msg = "Unable to start a local compute server within 180 seconds";
                    Log.Information(msg);
                    throw new Exception(msg);
                }

                Thread.Sleep(500);
            }
        }

        private static bool IsComputeGeometryReady(int port)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(1);
                    var response = client.GetAsync($"http://localhost:{port}/healthcheck").Result;
                    return response.IsSuccessStatusCode;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsComputeGeometryBusy(int port)
        {
            if (!IsComputeGeometryReady(port))
            {
                return true; // Process not ready yet
            }

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(1);
                    var response = client.GetAsync($"http://localhost:{port}/isbusy").Result;
                    if (response.IsSuccessStatusCode)
                    {
                        var content = response.Content.ReadAsStringAsync().Result;
                        if (int.TryParse(content, out int activeRequests))
                        {
                            return activeRequests > 0;
                        }
                    }
                }
            }
            catch
            {
                return true; // If we can't connect, consider it busy
            }
            return true;
        }

        static bool IsPortOpen(string host, int port, TimeSpan timeout)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var result = client.BeginConnect(host, port, null, null);
                    var success = result.AsyncWaitHandle.WaitOne(timeout);
                    if (!success)
                    {
                        return false;
                    }
                    client.EndConnect(result);
                    client.Close();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public class ComputeProcessInfo
        {
            public Process Process { get; set; }
            public int Port { get; set; }
        }
    }
}
