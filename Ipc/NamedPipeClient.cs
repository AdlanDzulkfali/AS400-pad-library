using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AS400Automation.Ipc
{
    /// <summary>
    /// Named Pipe IPC client used by AS400Driver when running in multi-process environments
    /// (e.g. Power Automate Desktop where each script action runs in a new powershell.exe).
    /// </summary>
    public static class NamedPipeClient
    {
        public static string PipeName { get; set; } = NamedPipeDaemon.DefaultPipeName;
        public static int ConnectTimeoutMs { get; set; } = 5000;
        public static string LastError { get; set; } = string.Empty;

        private static readonly object _clientLock = new object();

        /// <summary>
        /// Sends an IPC request to the background daemon, auto-starting the daemon if not currently running.
        /// </summary>
        public static IpcResponse SendRequest(IpcRequest request, int timeoutMs = -1)
        {
            if (request == null)
            {
                return new IpcResponse { Success = false, ErrorMessage = "Request is null." };
            }

            int timeout = timeoutMs > 0 ? timeoutMs : ConnectTimeoutMs;

            lock (_clientLock)
            {
                // Attempt 1: Connect to existing daemon
                var response = TrySend(request, Math.Min(500, timeout), false);
                if (response != null)
                {
                    return response;
                }

                // If not running and auto-start is enabled, launch daemon
                if (AS400Driver.AutoStartDaemon)
                {
                    bool started = StartDaemonProcess();
                    if (started)
                    {
                        // Attempt 2: Connect after daemon start (allow up to remaining timeout)
                        response = TrySend(request, timeout, true);
                        if (response != null)
                        {
                            return response;
                        }
                    }
                }

                string err = $"Failed to communicate with AS400 daemon via Named Pipe '{PipeName}'. Daemon may not be running.";
                LastError = err;
                return new IpcResponse { Success = false, ErrorMessage = err };
            }
        }

        private static IpcResponse TrySend(IpcRequest request, int timeoutMs, bool retryOnConnect)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow <= deadline)
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None))
                    {
                        int waitMs = Math.Min(1000, Math.Max(100, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                        pipe.Connect(waitMs);

                        using (var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, true) { AutoFlush = true })
                        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                        {
                            writer.WriteLine(request.Serialize());
                            string line = reader.ReadLine();
                            if (line != null)
                            {
                                var resp = IpcResponse.Deserialize(line);
                                if (!resp.Success)
                                {
                                    LastError = resp.ErrorMessage;
                                }
                                return resp;
                            }
                        }
                    }
                }
                catch (TimeoutException)
                {
                    if (!retryOnConnect) return null;
                    Thread.Sleep(100);
                }
                catch (FileNotFoundException)
                {
                    // Pipe does not exist yet
                    if (!retryOnConnect) return null;
                    Thread.Sleep(100);
                }
                catch (IOException)
                {
                    // Pipe might be busy or starting up
                    if (!retryOnConnect) return null;
                    Thread.Sleep(100);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    if (!retryOnConnect) return null;
                    Thread.Sleep(100);
                }
            }

            return null;
        }

        /// <summary>
        /// Attempts to locate and start AS400Daemon.exe as a background/hidden process.
        /// </summary>
        public static bool StartDaemonProcess()
        {
            string exePath = FindDaemonExecutable();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                LastError = "AS400Daemon.exe not found. Searched application directories.";
                return false;
            }

            try
            {
                int parentPid = AS400Driver.DaemonParentPid;
                if (parentPid <= 0)
                {
                    parentPid = DetectHostProcessId();
                }

                string args = $"--pipe \"{PipeName}\" --idle-timeout {AS400Driver.DaemonIdleTimeoutMinutes}";
                if (parentPid > 0)
                {
                    args += $" --parent-pid {parentPid}";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var proc = Process.Start(psi);
                if (proc != null)
                {
                    // Give daemon a brief moment to initialize the named pipe
                    Thread.Sleep(150);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                LastError = "Failed to launch AS400Daemon.exe: " + ex.Message;
                return false;
            }
        }

        public static string FindDaemonExecutable()
        {
            if (!string.IsNullOrEmpty(AS400Driver.DaemonExecutablePath) && File.Exists(AS400Driver.DaemonExecutablePath))
            {
                return AS400Driver.DaemonExecutablePath;
            }

            string envPath = Environment.GetEnvironmentVariable("AS400_DAEMON_EXE");
            if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            {
                return envPath;
            }

            // Check assembly directory
            try
            {
                string asmDir = Path.GetDirectoryName(typeof(NamedPipeClient).Assembly.Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    string candidate = Path.Combine(asmDir, "AS400Daemon.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }

            // Check BaseDirectory
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (!string.IsNullOrEmpty(baseDir))
                {
                    string candidate = Path.Combine(baseDir, "AS400Daemon.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }

            // Check CurrentDirectory
            try
            {
                string curDir = Directory.GetCurrentDirectory();
                string candidate = Path.Combine(curDir, "AS400Daemon.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Detects the host process (e.g. Power Automate Desktop host or test runner)
        /// by finding the parent process of the current execution environment.
        /// </summary>
        public static int DetectHostProcessId()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return 0;
            }

            try
            {
                int currentPid = Process.GetCurrentProcess().Id;
                int parentPid = GetParentProcessId(currentPid);

                // If current process is powershell/cmd, inspect its parent (which is PAD)
                string curName = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
                if (curName.Contains("powershell") || curName.Contains("pwsh") || curName.Contains("cmd"))
                {
                    if (parentPid > 0)
                    {
                        try
                        {
                            var parent = Process.GetProcessById(parentPid);
                            if (!parent.HasExited)
                            {
                                return parentPid;
                            }
                        }
                        catch { }
                    }
                }

                return currentPid;
            }
            catch
            {
                return 0;
            }
        }

        #region Win32 Toolhelp Parent PID Lookup

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        public static int GetParentProcessId(int pid)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                return 0;
            }

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            {
                return 0;
            }

            try
            {
                var entry = new PROCESSENTRY32();
                entry.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        if (entry.th32ProcessID == pid)
                        {
                            return (int)entry.th32ParentProcessID;
                        }
                    }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return 0;
        }

        #endregion
    }
}
