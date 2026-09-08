using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AS400Automation.Ipc
{
    /// <summary>
    /// Background daemon hosting the Named Pipe IPC server.
    /// Preserves live AS400 TCP sockets and presentation space across separate PowerShell executions.
    /// Implements 4-layer watchdog to guarantee process termination when PAD stops.
    /// </summary>
    public class NamedPipeDaemon : IDisposable
    {
        public const string DefaultPipeName = "AS400Automation_IPC";

        public string PipeName { get; }
        public int IdleTimeoutMinutes { get; set; } = 5;
        public int ZeroSessionTimeoutSeconds { get; set; } = 60;
        public int ParentProcessId { get; private set; }

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ManualResetEventSlim _shutdownEvent = new ManualResetEventSlim(false);
        private DateTime _lastActivity = DateTime.UtcNow;
        private bool _disposed;
        private Process _parentProcess;

        public bool IsRunning => !_cts.IsCancellationRequested && !_shutdownEvent.IsSet;

        public NamedPipeDaemon(string pipeName = DefaultPipeName, int idleTimeoutMinutes = 5)
        {
            PipeName = !string.IsNullOrWhiteSpace(pipeName) ? pipeName : DefaultPipeName;
            IdleTimeoutMinutes = idleTimeoutMinutes > 0 ? idleTimeoutMinutes : 5;
        }

        /// <summary>
        /// Registers a parent process (e.g. Power Automate Desktop) to monitor.
        /// If the parent process terminates, the daemon shuts down automatically.
        /// </summary>
        public void SetParentProcess(int parentPid)
        {
            if (parentPid <= 0) return;
            ParentProcessId = parentPid;

            try
            {
                _parentProcess = Process.GetProcessById(parentPid);
                _parentProcess.EnableRaisingEvents = true;
                _parentProcess.Exited += (s, e) =>
                {
                    RequestShutdown($"Parent process (PID {parentPid}) terminated.");
                };

                if (_parentProcess.HasExited)
                {
                    RequestShutdown($"Parent process (PID {parentPid}) has already exited.");
                }
            }
            catch (Exception ex)
            {
                // Process may have already exited
                RequestShutdown($"Failed to monitor parent PID {parentPid}: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts the Named Pipe server loop on a background thread.
        /// </summary>
        public void Start()
        {
            _lastActivity = DateTime.UtcNow;
            Task.Run(ServerLoop);
            Task.Run(WatchdogLoop);
        }

        /// <summary>
        /// Blocks until shutdown is requested or idle timeout fires.
        /// </summary>
        public void WaitForShutdown()
        {
            _shutdownEvent.Wait();
        }

        private async Task ServerLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var pipeServer = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipeServer.WaitForConnectionAsync(_cts.Token);
                    _lastActivity = DateTime.UtcNow;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (pipeServer)
                            using (var reader = new StreamReader(pipeServer, Encoding.UTF8, false, 4096, true))
                            using (var writer = new StreamWriter(pipeServer, Encoding.UTF8, 4096, true) { AutoFlush = true })
                            {
                                string line = await reader.ReadLineAsync();
                                if (!string.IsNullOrEmpty(line))
                                {
                                    var request = IpcRequest.Deserialize(line);
                                    var response = ExecuteRequest(request);
                                    await writer.WriteLineAsync(response.Serialize());

                                    if (request != null && request.Action == "STOP_DAEMON")
                                    {
                                        RequestShutdown("Explicit STOP_DAEMON command received.");
                                    }
                                }
                            }
                        }
                        catch { }
                    });
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    // Sleep briefly on pipe error before re-listening
                    if (!_cts.IsCancellationRequested)
                    {
                        Thread.Sleep(50);
                    }
                }
            }
        }

        private IpcResponse ExecuteRequest(IpcRequest request)
        {
            AS400Driver.DisableIpcForCurrentThread = true;
            if (request == null)
            {
                return new IpcResponse { Success = false, ErrorMessage = "Invalid request payload." };
            }

            _lastActivity = DateTime.UtcNow;

            try
            {
                switch (request.Action?.ToUpperInvariant())
                {
                    case "CONNECT":
                        string host = request.Arguments.Count > 0 ? request.Arguments[0] : "127.0.0.1";
                        int port = request.Arguments.Count > 1 && int.TryParse(request.Arguments[1], out int p) ? p : 23;
                        bool ssl = request.Arguments.Count > 2 && bool.TryParse(request.Arguments[2], out bool s) && s;
                        string model = request.Arguments.Count > 3 && !string.IsNullOrEmpty(request.Arguments[3]) ? request.Arguments[3] : "IBM-3179-2";
                        int timeout = request.Arguments.Count > 4 && int.TryParse(request.Arguments[4], out int to) ? to : 30;

                        string sid = AS400Driver.ConnectWithTerminalType(host, port, ssl, model, timeout);
                        return new IpcResponse { Success = !string.IsNullOrEmpty(sid), Data = sid, ErrorMessage = AS400Driver.GetLastError() };

                    case "DISCONNECT":
                        bool disc = AS400Driver.Disconnect(request.SessionId);
                        return new IpcResponse { Success = disc, Data = disc.ToString() };

                    case "DISCONNECTALL":
                        AS400Driver.DisconnectAll();
                        return new IpcResponse { Success = true, Data = "True" };

                    case "ISCONNECTED":
                        bool conn = AS400Driver.IsConnected(request.SessionId);
                        return new IpcResponse { Success = true, Data = conn.ToString() };

                    case "SENDKEYS":
                        string text = request.Arguments.Count > 0 ? request.Arguments[0] : string.Empty;
                        string key = request.Arguments.Count > 1 ? request.Arguments[1] : "Enter";
                        bool sk = AS400Driver.SendKeys(request.SessionId, text, key);
                        return new IpcResponse { Success = sk, Data = sk.ToString(), ErrorMessage = AS400Driver.GetLastError(request.SessionId) };

                    case "SENDKEY":
                        string cKey = request.Arguments.Count > 0 ? request.Arguments[0] : "Enter";
                        bool kRes = AS400Driver.SendKey(request.SessionId, cKey);
                        return new IpcResponse { Success = kRes, Data = kRes.ToString(), ErrorMessage = AS400Driver.GetLastError(request.SessionId) };

                    case "SETCURSOR":
                        int row = int.Parse(request.Arguments[0]);
                        int col = int.Parse(request.Arguments[1]);
                        bool sc = AS400Driver.SetCursor(request.SessionId, row, col);
                        return new IpcResponse { Success = sc, Data = sc.ToString(), ErrorMessage = AS400Driver.GetLastError(request.SessionId) };

                    case "WRITEAT":
                        int wRow = int.Parse(request.Arguments[0]);
                        int wCol = int.Parse(request.Arguments[1]);
                        string wText = request.Arguments.Count > 2 ? request.Arguments[2] : string.Empty;
                        bool wa = AS400Driver.WriteAt(request.SessionId, wRow, wCol, wText);
                        return new IpcResponse { Success = wa, Data = wa.ToString(), ErrorMessage = AS400Driver.GetLastError(request.SessionId) };

                    case "GETSCREEN":
                        string screen = AS400Driver.GetScreen(request.SessionId);
                        return new IpcResponse { Success = true, Data = screen };

                    case "READTEXT":
                        int rRow = int.Parse(request.Arguments[0]);
                        int rCol = int.Parse(request.Arguments[1]);
                        int rLen = int.Parse(request.Arguments[2]);
                        string txt = AS400Driver.ReadText(request.SessionId, rRow, rCol, rLen);
                        return new IpcResponse { Success = true, Data = txt };

                    case "READFIELD":
                        int fRow = int.Parse(request.Arguments[0]);
                        int fCol = int.Parse(request.Arguments[1]);
                        string fVal = AS400Driver.ReadField(request.SessionId, fRow, fCol);
                        return new IpcResponse { Success = true, Data = fVal };

                    case "GETCURSORPOSITION":
                        var (curRow, curCol) = AS400Driver.GetCursorPosition(request.SessionId);
                        return new IpcResponse { Success = true, Data = $"{curRow},{curCol}" };

                    case "GETSYSTEMMESSAGE":
                        string msg = AS400Driver.GetSystemMessage(request.SessionId);
                        return new IpcResponse { Success = true, Data = msg };

                    case "WAITFORTEXT":
                        string target = request.Arguments.Count > 0 ? request.Arguments[0] : string.Empty;
                        int wtTimeout = request.Arguments.Count > 1 && int.TryParse(request.Arguments[1], out int wto) ? wto : 10;
                        bool wFound = AS400Driver.WaitForText(request.SessionId, target, wtTimeout);
                        return new IpcResponse { Success = wFound, Data = wFound.ToString(), ErrorMessage = AS400Driver.GetLastError(request.SessionId) };

                    case "WAITFORSCREENUPDATE":
                        int sTimeout = request.Arguments.Count > 0 && int.TryParse(request.Arguments[0], out int sto) ? sto : 10;
                        bool sUpd = AS400Driver.WaitForScreenUpdate(request.SessionId, sTimeout);
                        return new IpcResponse { Success = sUpd, Data = sUpd.ToString(), ErrorMessage = AS400Driver.GetLastError(request.SessionId) };

                    case "GETLASTERROR":
                        string err = AS400Driver.GetLastError(request.SessionId);
                        return new IpcResponse { Success = true, Data = err };

                    case "PING":
                        return new IpcResponse { Success = true, Data = "PONG" };

                    case "STOP_DAEMON":
                        return new IpcResponse { Success = true, Data = "Daemon stopping" };

                    default:
                        return new IpcResponse { Success = false, ErrorMessage = $"Unknown action '{request.Action}'." };
                }
            }
            catch (Exception ex)
            {
                return new IpcResponse
                {
                    Success = false,
                    ErrorCode = (ex is AS400Exception aex) ? (int)aex.ErrorCode : 1,
                    ErrorMessage = ex.Message
                };
            }
        }

        private async Task WatchdogLoop()
        {
            DateTime? zeroSessionStartTime = null;

            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(2000, _cts.Token).ContinueWith(_ => { });
                if (_cts.IsCancellationRequested) break;

                // Watchdog Check 1: Parent Process alive
                if (_parentProcess != null)
                {
                    try
                    {
                        if (_parentProcess.HasExited)
                        {
                            RequestShutdown($"Parent process (PID {ParentProcessId}) exited.");
                            break;
                        }
                    }
                    catch
                    {
                        RequestShutdown($"Cannot access parent process (PID {ParentProcessId}).");
                        break;
                    }
                }

                // Watchdog Check 2: Idle Inactivity Timeout
                TimeSpan idleSpan = DateTime.UtcNow - _lastActivity;
                if (idleSpan.TotalMinutes >= IdleTimeoutMinutes)
                {
                    RequestShutdown($"Idle timeout ({IdleTimeoutMinutes} minutes) reached with no incoming commands.");
                    break;
                }

                // Watchdog Check 3: Zero-Session Shutdown
                if (AS400Driver.ActiveSessionCount == 0)
                {
                    if (!zeroSessionStartTime.HasValue)
                    {
                        zeroSessionStartTime = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - zeroSessionStartTime.Value).TotalSeconds >= ZeroSessionTimeoutSeconds)
                    {
                        RequestShutdown($"Zero active sessions for {ZeroSessionTimeoutSeconds} seconds.");
                        break;
                    }
                }
                else
                {
                    zeroSessionStartTime = null;
                }
            }
        }

        public void RequestShutdown(string reason = null)
        {
            if (_shutdownEvent.IsSet) return;

            try
            {
                AS400Driver.DisconnectAll();
            }
            catch { }

            _cts.Cancel();
            _shutdownEvent.Set();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            RequestShutdown();
            _cts.Dispose();
            _shutdownEvent.Dispose();
            _parentProcess?.Dispose();
        }
    }
}
