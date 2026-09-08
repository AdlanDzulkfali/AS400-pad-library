using System;
using System.Diagnostics;
using System.Threading;
using AS400Automation.Protocol;

namespace AS400Automation
{
    /// <summary>
    /// Represents an individual active AS400 TN5250 terminal session instance.
    /// Thread-safe and encapsulates socket, stream, screen presentation space, and synchronization.
    /// </summary>
    public class SessionInstance : IDisposable
    {
        public string SessionId { get; }
        public string Host { get; private set; }
        public int Port { get; private set; }
        public bool UseSsl { get; private set; }
        public string TerminalType { get; private set; }
        public string LastError { get; set; }

        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime LastAccessedAt { get; private set; } = DateTime.UtcNow;

        private Tn5250Stream _protocolStream;
        private readonly object _sessionLock = new object();
        private bool _disposed;

        public bool IsConnected
        {
            get
            {
                lock (_sessionLock)
                {
                    return _protocolStream != null && _protocolStream.IsConnected;
                }
            }
        }

        public Tn5250ScreenBuffer ScreenBuffer => _protocolStream?.ScreenBuffer;

        public SessionInstance(string sessionId)
        {
            SessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : Guid.NewGuid().ToString("N");
        }

        public void Connect(string host, int port = 23, bool useSsl = false, string terminalType = Tn5250Constants.TERMINAL_MODEL_IBM3179, int timeoutSeconds = 30)
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                Host = host;
                Port = port;
                UseSsl = useSsl;
                TerminalType = terminalType;

                if (_protocolStream != null)
                {
                    _protocolStream.Disconnect();
                    _protocolStream.Dispose();
                }

                _protocolStream = new Tn5250Stream(SessionId);
                _protocolStream.Connect(host, port, useSsl, terminalType, timeoutSeconds);
            }
        }

        public void Disconnect()
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                try
                {
                    _protocolStream?.Disconnect();
                    _protocolStream?.Dispose();
                }
                catch { }
                _protocolStream = null;
            }
        }

        public bool SendKeys(string text = "", string controlKey = "Enter")
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                EnsureConnected();
                _protocolStream.SendKeystrokes(text, controlKey);
                return true;
            }
        }

        public bool SendKey(string controlKey = "Enter")
        {
            return SendKeys(string.Empty, controlKey);
        }

        public bool SetCursor(int row, int col)
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                EnsureConnected();
                ScreenBuffer.SetCursor(row, col);
                return true;
            }
        }

        public bool WriteAt(int row, int col, string text)
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                EnsureConnected();
                ScreenBuffer.WriteAt(row, col, text);
                return true;
            }
        }

        public string GetScreen()
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                EnsureConnected();
                return ScreenBuffer.GetFullPresentationSpace();
            }
        }

        public string ReadText(int row, int col, int length)
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                EnsureConnected();
                return ScreenBuffer.ReadSlice(row, col, length);
            }
        }

        public string ReadField(int row, int col)
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                EnsureConnected();
                return ScreenBuffer.ReadField(row, col);
            }
        }

        public string GetSystemMessage()
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                EnsureConnected();
                return ScreenBuffer.GetSystemMessage();
            }
        }

        public (int Row, int Col) GetCursorPosition()
        {
            lock (_sessionLock)
            {
                LastAccessedAt = DateTime.UtcNow;
                EnsureConnected();
                return ScreenBuffer.GetCursorPosition();
            }
        }

        public bool WaitForText(string targetText, int timeoutSeconds = 10)
        {
            if (string.IsNullOrEmpty(targetText)) return true;

            Stopwatch sw = Stopwatch.StartNew();
            int timeoutMs = Math.Max(500, timeoutSeconds * 1000);

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (_sessionLock)
                {
                    EnsureConnected();
                    if (ScreenBuffer.ContainsText(targetText, ignoreCase: true))
                    {
                        return true;
                    }
                }

                // Sleep briefly between checks to avoid CPU spinning
                Thread.Sleep(100);
            }

            // Final check on timeout
            lock (_sessionLock)
            {
                EnsureConnected();
                return ScreenBuffer.ContainsText(targetText, ignoreCase: true);
            }
        }

        public bool WaitForScreenUpdate(int timeoutSeconds = 10)
        {
            lock (_sessionLock)
            {
                EnsureConnected();
                _protocolStream.ScreenUpdatedEvent.Reset();
            }

            int timeoutMs = Math.Max(500, timeoutSeconds * 1000);
            bool updated = _protocolStream.ScreenUpdatedEvent.Wait(timeoutMs);
            return updated;
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                throw new AS400Exception(AS400ErrorCode.SessionFaulted,
                    $"AS400 session '{SessionId}' is not connected or socket was closed.", SessionId);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            Disconnect();
        }

        ~SessionInstance()
        {
            Dispose(false);
        }
    }
}
