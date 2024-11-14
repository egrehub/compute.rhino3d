using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// This value determines whether a child process should be started
        /// when rhino.compute is first launched. If running in a production
        /// environment, this value should be set to false.
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
                var processes = Process.GetProcessesByName("compute.geometry");
                return processes.Length;
            }
        }

        /// <summary>
        /// Get base url for a compute server. This function may return a
        /// different string each time it is called as it attempts to provide
        /// basic round robin scheduling when multiple compute servers are
        /// found to be available.
        /// </summary>
        /// <returns></returns>
        public static (string, int) GetComputeServerBaseUrl()
        {
            // Simple round robin scheduler using a queue of compute.geometry processes
            int activePort = 0;

            lock (_lockObject)
            {
                // Clean up exited or unresponsive processes
                CleanUpProcesses(); // Added to ensure only responsive processes are kept

                if (_computeProcesses.Count > 0)
                {
                    Tuple<Process, int> current = _computeProcesses.Dequeue();
                    if (!current.Item1.HasExited)
                    {
                        _computeProcesses.Enqueue(current);
                        activePort = current.Item2;
                    }
                }

                if (activePort == 0 && _computeProcesses.Count < SpawnCount)
                {
                    LaunchCompute(true);
                    if (_computeProcesses.Count > 0)
                    {
                        Tuple<Process, int> current = _computeProcesses.Dequeue();
                        _computeProcesses.Enqueue(current);
                        activePort = current.Item2;
                    }
                }
            }

            if (0 == activePort)
                throw new Exception("No compute server found");

            // Ensure that the number of compute.geometry does not exceed SpawnCount
            MaintainSpawnCount(); // Added to enforce maximum number of child processes

            //Log.Information($"Started child process at http://localhost:{activePort} at {DateTime.Now.ToLocalTime()}");
            return ($"http://localhost:{activePort}", activePort);
        }

        public static void MoveToFrontOfQueue(int port)
        {
            lock (_lockObject)
            {
                // TODO: We really should be using a simple list with an index
                // pointing at the next item to use
                if (_computeProcesses.Count > 1)
                {
                    for( int i=0; i<_computeProcesses.Count; i++)
                    {
                        if (_computeProcesses.Peek().Item2 == port)
                            break;
                        var item = _computeProcesses.Dequeue();
                        _computeProcesses.Enqueue(item);
                    }
                }
            }
        }

        public static void LaunchCompute(bool waitUntilServing)
        {
            lock (_lockObject)
            {
                LaunchCompute(_computeProcesses, waitUntilServing);
            }
        }

        static void LaunchCompute(Queue<Tuple<Process, int>> processQueue, bool waitUntilServing)
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
                    return;
            }

            var existingProcesses = processQueue.ToArray();
            var existingPorts = new HashSet<int>();
            foreach (var proc in existingProcesses)
                existingPorts.Add(proc.Item2);

            int port = 0;
            for (int i = 0; i < 256; i++)
            {
                // start at port 6001. Feel free to change this if there is a reason
                // to use a different port
                port = 6001 + i;
                if (i == 255)
                    return;

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
                        process.Kill();
                        string msg = "Unable to start a local compute server within 180 seconds";
                        Log.Information(msg);
                        throw new Exception(msg);
                    }
                }
            }
            else
            {
                // no matter what, give compute a little time to start
                System.Threading.Thread.Sleep(100);
            }

            if (process != null)
            {
                processQueue.Enqueue(Tuple.Create(process, port));
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
                    client.EndConnect(result);
                    return success;
                }
            }
            catch(Exception)
            {
                return false;
            }
        }
        static object _lockObject = new object();
        static Queue<Tuple<Process, int>> _computeProcesses = new Queue<Tuple<Process, int>>();

        // Added monitoring timer to regularly check and maintain compute.geometry processes
        static System.Timers.Timer _monitoringTimer;

        /// <summary>
        /// Initializes the monitoring timer to periodically check compute.geometry processes
        /// </summary>
        public static void InitializeMonitoring()
        {
            _monitoringTimer = new System.Timers.Timer(30000); // Check every 30 seconds
            _monitoringTimer.Elapsed += (sender, e) => MonitorProcesses();
            _monitoringTimer.AutoReset = true;
            _monitoringTimer.Enabled = true;
        }

        /// <summary>
        /// Monitors the compute.geometry processes and enforces SpawnCount
        /// </summary>
        private static void MonitorProcesses()
        {
            lock (_lockObject)
            {
                CleanUpProcesses();
                MaintainSpawnCount();
            }
        }

        /// <summary>
        /// Cleans up exited or unresponsive compute.geometry processes
        /// </summary>
        private static void CleanUpProcesses()
        {
            var processesToKeep = new List<Tuple<Process, int>>();
            foreach (var proc in _computeProcesses)
            {
                if (!proc.Item1.HasExited && IsProcessResponsive(proc.Item1))
                {
                    processesToKeep.Add(proc);
                }
                else
                {
                    try
                    {
                        proc.Item1.Kill();
                        Log.Information($"Killed unresponsive compute.geometry process on port {proc.Item2}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Error killing process on port {proc.Item2}: {ex.Message}");
                    }
                }
            }
            _computeProcesses = new Queue<Tuple<Process, int>>(processesToKeep);
        }

        /// <summary>
        /// Checks if a compute.geometry process is responsive
        /// </summary>
        /// <param name="process">Process to check</param>
        /// <returns>True if responsive, otherwise false</returns>
        private static bool IsProcessResponsive(Process process)
        {
            // Implement a simple check, such as verifying if the port is open
            // Assuming each compute.geometry process has a unique port
            foreach (var tuple in _computeProcesses)
            {
                if (tuple.Item1.Id == process.Id)
                {
                    return IsPortOpen("localhost", tuple.Item2, TimeSpan.FromSeconds(1));
                }
            }
            return false;
        }

        /// <summary>
        /// Maintains the spawn count by launching or killing processes as needed
        /// </summary>
        private static void MaintainSpawnCount()
        {
            while (_computeProcesses.Count > SpawnCount)
            {
                var proc = _computeProcesses.Dequeue();
                try
                {
                    proc.Item1.Kill();
                    Log.Information($"Killed excess compute.geometry process on port {proc.Item2}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Error killing process on port {proc.Item2}: {ex.Message}");
                }
            }

            while (_computeProcesses.Count < SpawnCount)
            {
                LaunchCompute(false);
            }
        }
    }
}

