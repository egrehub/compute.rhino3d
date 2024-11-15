using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using Serilog;

namespace rhino.compute
{
    static class ComputeChildren
    {
        /// <summary>
        /// Number of child compute.geometry processes to launch
        /// </summary>
        public static int SpawnCount { get; set; } = 1;

        static DateTime _lastCall = DateTime.MinValue;
        public static void UpdateLastCall()
        {
            _lastCall = DateTime.Now;
        }

        /// <summary>
        /// Idle time child processes live. If rhino.compute is not called
        /// for this period of time to proxy requests, the child processes will
        /// shut down. The processes will be restarted on a later request
        /// </summary>
        public static TimeSpan ChildIdleSpan { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// Determines whether a child process should be started
        /// when rhino.compute is first launched.
        /// </summary>
        public static bool SpawnOnStartup { get; set; } = false;

        /// <summary>Port that rhino.compute is running on</summary>
        public static int ParentPort { get; set; } = 5000;

        /// <summary>
        /// The system directory for the Rhino executable
        /// </summary>
        public static string RhinoSysDir { get; set; }

        /// <summary>
        /// Length of time (in seconds) since rhino.compute last made a call
        /// to a child process. The child processes use this information to
        /// figure out if they should exit.
        /// </summary>
        /// <returns>
        /// -1 if a child process has never been called; otherwise
        /// span in seconds since the last call to a child process
        /// </returns>
        public static int IdleSpan()
        {
            if (_lastCall == DateTime.MinValue)
                return -1;
            var span = DateTime.Now - _lastCall;
            return (int)span.TotalSeconds;
        }

        /// <summary>
        /// Total number of compute.geometry processes being run
        /// </summary>
        public static int ActiveComputeCount
        {
            get
            {
                lock (_lockObject)
                {
                    return _computeProcesses.Count;
                }
            }
        }

        static object _lockObject = new object();
        static List<ComputeProcessInfo> _computeProcesses = new List<ComputeProcessInfo>();

        /// <summary>
        /// Get base URL for a compute server. This function will wait until a
        /// compute.geometry process is available, either because one is free or because
        /// a new one has been spawned.
        /// </summary>
        /// <returns></returns>
        public static (string, int) GetComputeServerBaseUrl()
        {
            UpdateLastCall();
            Stopwatch sw = Stopwatch.StartNew();

            while (true)
            {
                ComputeProcessInfo availableProcess = null;

                lock (_lockObject)
                {
                    CleanupExitedProcesses();

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

                    // All processes are busy
                    // If we can spawn more processes, do so
                    if (_computeProcesses.Count < SpawnCount)
                    {
                        var newProcessInfo = LaunchCompute(waitUntilServing: true);
                        if (newProcessInfo != null)
                        {
                            _computeProcesses.Add(newProcessInfo);
                            availableProcess = newProcessInfo;
                            return ($"http://localhost:{availableProcess.Port}", availableProcess.Port);
                        }
                    }
                }

                // No available process, wait for one to become free
                if (sw.Elapsed.TotalSeconds > 60)
                {
                    throw new Exception("No compute server available after waiting 60 seconds");
                }

                Thread.Sleep(1000); // Wait for 1 second before trying again
            }
        }

        /// <summary>
        /// Cleans up any compute.geometry processes that have exited.
        /// </summary>
        static void CleanupExitedProcesses()
        {
            _computeProcesses.RemoveAll(p => p.Process.HasExited);
        }

        /// <summary>
        /// Launches a new compute.geometry process.
        /// </summary>
        /// <param name="waitUntilServing">Whether to wait until the process is ready.</param>
        /// <returns>The process information of the launched compute.geometry process.</returns>
        public static ComputeProcessInfo LaunchCompute(bool waitUntilServing)
        {
            var pathToThisAssembly = new System.IO.FileInfo(typeof(ComputeChildren).Assembly.Location);
            // compute.geometry is allowed to be either in:
            // - a sibling directory named compute.geometry
            // - a child directory named compute.geometry
            var parentDirectory = pathToThisAssembly.Directory.Parent;
            string pathToCompute = System.IO.Path.Combine(parentDirectory.FullName, "compute.geometry", "compute.geometry.exe");

            if (!System.IO.File.Exists(pathToCompute))
            {
                pathToCompute = System.IO.Path.Combine(pathToThisAssembly.Directory.FullName, "compute.geometry", "compute.geometry.exe");
                if (!System.IO.File.Exists(pathToCompute))
                    return null;
            }

            var existingPorts = new HashSet<int>(_computeProcesses.Select(p => p.Port));

            int port = 0;
            for (int i = 0; i < 256; i++)
            {
                // start at port 6001
                port = 6001 + i;
                if (i == 255)
                    return null;

                if (existingPorts.Contains(port))
                    continue;

                bool isOpen = IsPortOpen("localhost", port, new TimeSpan(0, 0, 0, 0, 100));
                if (isOpen)
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
            var start = DateTime.Now;

            if (waitUntilServing)
            {
                while (true)
                {
                    bool isOpen = IsPortOpen("localhost", port, new TimeSpan(0, 0, 1));

                    if (isOpen)
                    {
                        break;
                    }

                    var span = DateTime.Now - start;
                    if (span.TotalSeconds > 180) // Increased timeout from 60 to 180 seconds
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Failed to kill compute.geometry process");
                        }
                        string msg = "Unable to start a local compute server within 180 seconds";
                        Log.Information(msg);
                        throw new Exception(msg);
                    }
                }
            }
            else
            {
                // No matter what, give compute a little time to start
                System.Threading.Thread.Sleep(100);
            }

            if (process != null)
            {
                var processInfo = new ComputeProcessInfo
                {
                    Process = process,
                    Port = port
                };
                return processInfo;
            }
            return null;
        }

        private static bool IsComputeGeometryBusy(int port)
        {
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
                // If we can't connect, assume the process is busy or unavailable
                return true;
            }
            return true;
        }


        /// <summary>
        /// Checks if a TCP port is open.
        /// </summary>
        /// <param name="host">The hostname to check.</param>
        /// <param name="port">The port number to check.</param>
        /// <param name="timeout">The timeout for the check.</param>
        /// <returns>True if the port is open; otherwise, false.</returns>
        static bool IsPortOpen(string host, int port, TimeSpan timeout)
        {
            try
            {
                using (var client = new TcpClient())
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
            catch (Exception)
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
