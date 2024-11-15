using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
                return _computeProcesses.Count + _startingProcesses.Count;
            }
        }

        static ConcurrentDictionary<int, ComputeProcessInfo> _computeProcesses = new ConcurrentDictionary<int, ComputeProcessInfo>();
        static ConcurrentDictionary<int, ComputeProcessInfo> _startingProcesses = new ConcurrentDictionary<int, ComputeProcessInfo>();

        public static async Task<(string, int, ComputeProcessInfo)> GetComputeServerInfoAsync()
        {
            UpdateLastCall();

            Stopwatch sw = Stopwatch.StartNew();

            while (true)
            {
                ComputeProcessInfo availableProcess = null;
                bool canSpawnNewProcess = false;

                CleanupExitedProcesses();

                // Move ready starting processes to available
                MoveReadyProcessesToAvailable();

                // Try to find a free compute.geometry process
                availableProcess = _computeProcesses.Values.FirstOrDefault(p => !p.IsBusy);

                if (availableProcess != null)
                {
                    availableProcess.IsBusy = true; // Mark as busy
                    return ($"http://localhost:{availableProcess.Port}", availableProcess.Port, availableProcess);
                }

                // Determine if we can spawn a new process
                int totalProcesses = _computeProcesses.Count + _startingProcesses.Count;
                canSpawnNewProcess = totalProcesses < SpawnCount && _startingProcesses.IsEmpty;

                if (canSpawnNewProcess)
                {
                    // Spawn a new process without waiting
                    LaunchCompute(waitUntilServing: false);
                    // New process is added to _startingProcesses within LaunchCompute
                }

                if (!canSpawnNewProcess)
                {
                    // Wait for starting processes to become ready or existing processes to become free
                    bool anyProcessBecameAvailable = await WaitForProcessAvailabilityAsync();

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

                await Task.Delay(50); // Wait for 50ms before trying again
            }
        }

        private static void MoveReadyProcessesToAvailable()
        {
            foreach (var kvp in _startingProcesses.ToList())
            {
                var procInfo = kvp.Value;
                if (IsComputeGeometryReady(procInfo.Port))
                {
                    if (_startingProcesses.TryRemove(procInfo.Port, out _))
                    {
                        _computeProcesses.TryAdd(procInfo.Port, procInfo);
                    }
                }
            }
        }

        private static async Task<bool> WaitForProcessAvailabilityAsync()
        {
            // Wait for a short period asynchronously
            await Task.Delay(50);

            MoveReadyProcessesToAvailable();

            // Check if any compute processes are now free
            var availableProcess = _computeProcesses.Values.FirstOrDefault(p => !p.IsBusy);

            return availableProcess != null;
        }

        static void CleanupExitedProcesses()
        {
            foreach (var kvp in _computeProcesses.ToList())
            {
                if (kvp.Value.Process.HasExited)
                {
                    _computeProcesses.TryRemove(kvp.Key, out _);
                }
            }

            foreach (var kvp in _startingProcesses.ToList())
            {
                if (kvp.Value.Process.HasExited)
                {
                    _startingProcesses.TryRemove(kvp.Key, out _);
                }
            }
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

            var existingPorts = new HashSet<int>(_computeProcesses.Keys.Concat(_startingProcesses.Keys));

            int port = 0;
            for (int i = 0; i < 256; i++)
            {
                port = 6001 + i;
                if (i == 255)
                    return null;

                if (existingPorts.Contains(port))
                    continue;

                if (IsPortOpen("localhost", port, TimeSpan.FromMilliseconds(100)))
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
                    _computeProcesses.TryAdd(processInfo.Port, processInfo);
                }
                else
                {
                    _startingProcesses.TryAdd(processInfo.Port, processInfo);
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
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var result = client.BeginConnect("localhost", port, null, null);
                    var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
                    if (!success)
                        return false;
                    client.EndConnect(result);
                    return true;
                }
            }
            catch
            {
                return false;
            }
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
            public bool IsBusy { get; set; } = false;
        }

        public static void ReleaseProcess(int port)
        {
            if (_computeProcesses.TryGetValue(port, out var processInfo))
            {
                processInfo.IsBusy = false;
            }
        }
    }
}
