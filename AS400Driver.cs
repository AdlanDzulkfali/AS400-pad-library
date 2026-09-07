using System;
using System.Collections.Concurrent;
using AS400Automation.Protocol;

namespace AS400Automation
{
    /// <summary>
    /// Static entry-point driver for AS400 / IBM i 5250 terminal automation.
    /// Designed for zero-configuration interop inside Power Automate Desktop (Free/Standard tier) and PowerShell.
    /// Thread-safe and manages multiple concurrent sessions identified by unique session IDs.
    /// </summary>
    public static class AS400Driver
    {
        private static readonly ConcurrentDictionary<string, SessionInstance> _sessions =
            new ConcurrentDictionary<string, SessionInstance>(StringComparer.OrdinalIgnoreCase);

        private static string _globalLastError = string.Empty;
        private static readonly object _errorLock = new object();

        /// <summary>
        /// Global switch controlling whether methods throw exceptions on failure or record errors in GetLastError.
        /// Default is true so Power Automate Desktop try/catch blocks receive descriptive error messages.
        /// </summary>
        public static bool ThrowOnError { get; set; } = true;

        static AS400Driver()
        {
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (s, e) => DisconnectAll();
                AppDomain.CurrentDomain.DomainUnload += (s, e) => DisconnectAll();
            }
            catch { }
        }

        #region Session Management

        /// <summary>
        /// Establishes a raw TN5250 connection to the AS400 host and negotiates terminal type.
        /// Returns a unique sessionId handle.
        /// </summary>
        /// <param name="host">AS400 IP address or hostname.</param>
        /// <param name="port">Telnet port (default: 23, or 992 for SSL).</param>
        /// <param name="useSsl">True to enable TLS/SSL encryption.</param>
        /// <param name="timeoutSeconds">Connection timeout in seconds (default: 30).</param>
        /// <returns>Unique sessionId string.</returns>
        public static string Connect(string host, int port = 23, bool useSsl = false, int timeoutSeconds = 30)
        {
            return ConnectWithTerminalType(host, port, useSsl, Tn5250Constants.TERMINAL_MODEL_IBM3179, timeoutSeconds);
        }

        /// <summary>
        /// Establishes a raw TN5250 connection with an explicit terminal model (e.g. IBM-5555-C01 or IBM-3179-2).
        /// </summary>
        public static string ConnectWithTerminalType(string host, int port = 23, bool useSsl = false, string terminalType = Tn5250Constants.TERMINAL_MODEL_IBM3179, int timeoutSeconds = 30)
        {
            string sessionId = "AS400_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var session = new SessionInstance(sessionId);

            try
            {
                ClearError(sessionId);
                session.Connect(host, port, useSsl, terminalType, timeoutSeconds);
                _sessions[sessionId] = session;
                return sessionId;
            }
            catch (Exception ex)
            {
                session.Disconnect();
                session.Dispose();
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return string.Empty;
            }
        }

        /// <summary>
        /// Gracefully disconnects the session, closes network sockets, and frees memory.
        /// </summary>
        /// <param name="sessionId">Session ID handle returned by Connect.</param>
        /// <returns>True if successfully disconnected.</returns>
        public static bool Disconnect(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;

            if (_sessions.TryRemove(sessionId, out SessionInstance session))
            {
                try
                {
                    session.Disconnect();
                    session.Dispose();
                    return true;
                }
                catch (Exception ex)
                {
                    SetError(sessionId, ex.Message);
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Disconnects all active AS400 sessions. Useful for cleanup on flow exit.
        /// </summary>
        public static void DisconnectAll()
        {
            foreach (var kvp in _sessions)
            {
                try
                {
                    kvp.Value.Disconnect();
                    kvp.Value.Dispose();
                }
                catch { }
            }
            _sessions.Clear();
        }

        /// <summary>
        /// Checks socket health and session readiness.
        /// </summary>
        public static bool IsConnected(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            if (_sessions.TryGetValue(sessionId, out SessionInstance session))
            {
                return session.IsConnected;
            }
            return false;
        }

        #endregion

        #region Keyboard & Input Emulation

        /// <summary>
        /// Sends characters and attention identification (AID) control keys to the terminal screen.
        /// If text is a control key name (e.g. "F3", "PageDown"), it is automatically recognized and sent as the AID key.
        /// </summary>
        /// <param name="sessionId">Active session ID handle.</param>
        /// <param name="text">Text to type (or key name, or empty string to only press the key).</param>
        /// <param name="controlKey">Trailing control key (default: "Enter").</param>
        /// <returns>True on success.</returns>
        public static bool SendKeys(string sessionId, string text = "", string controlKey = "Enter")
        {
            try
            {
                ClearError(sessionId);
                var session = GetSession(sessionId);
                return session.SendKeys(text, controlKey);
            }
            catch (Exception ex)
            {
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return false;
            }
        }

        /// <summary>
        /// Transmits a standalone attention identification (AID) control key without typing text (e.g. "Enter", "F3", "F12", "PageDown", "Clear", "Help").
        /// </summary>
        /// <param name="sessionId">Active session ID handle.</param>
        /// <param name="controlKey">AID key name (default: "Enter").</param>
        /// <returns>True on success.</returns>
        public static bool SendKey(string sessionId, string controlKey = "Enter")
        {
            return SendKeys(sessionId, string.Empty, controlKey);
        }

        /// <summary>
        /// Moves the terminal cursor to specific coordinates (1-based row 1-24, col 1-80).
        /// </summary>
        public static bool SetCursor(string sessionId, int row, int col)
        {
            try
            {
                ClearError(sessionId);
                var session = GetSession(sessionId);
                return session.SetCursor(row, col);
            }
            catch (Exception ex)
            {
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return false;
            }
        }

        /// <summary>
        /// Moves the cursor to the target coordinate and enters text into the screen buffer.
        /// </summary>
        public static bool WriteAt(string sessionId, int row, int col, string text)
        {
            try
            {
                ClearError(sessionId);
                var session = GetSession(sessionId);
                return session.WriteAt(row, col, text);
            }
            catch (Exception ex)
            {
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return false;
            }
        }

        #endregion

        #region Screen Reading & Inspection

        /// <summary>
        /// Dumps the full 24x80 presentation space as plain text preserving line breaks.
        /// </summary>
        public static string GetScreen(string sessionId)
        {
            try
            {
                ClearError(sessionId);
                var session = GetSession(sessionId);
                return session.GetScreen();
            }
            catch (Exception ex)
            {
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return string.Empty;
            }
        }

        /// <summary>
        /// Extracts a substring starting from specific (row, col) coordinates.
        /// </summary>
        public static string ReadText(string sessionId, int row, int col, int length)
        {
            try
            {
                ClearError(sessionId);
                var session = GetSession(sessionId);
                return session.ReadText(row, col, length);
            }
            catch (Exception ex)
            {
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return string.Empty;
            }
        }

        /// <summary>
        /// Reads until the end of the field or the next screen attribute byte starting from (row, col).
        /// </summary>
        public static string ReadField(string sessionId, int row, int col)
        {
            try
            {
                ClearError(sessionId);
                var session = GetSession(sessionId);
                return session.ReadField(row, col);
            }
            catch (Exception ex)
            {
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return string.Empty;
            }
        }

        /// <summary>
        /// Retrieves the current cursor coordinate tuple (Row, Col).
        /// </summary>
        public static (int Row, int Col) GetCursorPosition(string sessionId)
        {
            try
            {
                ClearError(sessionId);
                var session = GetSession(sessionId);
                return session.GetCursorPosition();
            }
            catch (Exception ex)
            {
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return (1, 1);
            }
        }

        /// <summary>
        /// Helper for PowerShell returning the current cursor row (1-based).
        /// </summary>
        public static int GetCursorRow(string sessionId)
        {
            return GetCursorPosition(sessionId).Row;
        }

        /// <summary>
        /// Helper for PowerShell returning the current cursor column (1-based).
        /// </summary>
        public static int GetCursorCol(string sessionId)
        {
            return GetCursorPosition(sessionId).Col;
        }

        #endregion

        #region Synchronization & Flow Control

        /// <summary>
        /// Polls the presentation space buffer until the specified text appears on the screen.
        /// </summary>
        /// <param name="sessionId">Active session ID handle.</param>
        /// <param name="targetText">Target text to search for (case-insensitive).</param>
        /// <param name="timeoutSeconds">Max duration to wait in seconds.</param>
        /// <returns>True if found before timeout; otherwise false.</returns>
        public static bool WaitForText(string sessionId, string targetText, int timeoutSeconds = 10)
        {
            try
            {
                ClearError(sessionId);
                var session = GetSession(sessionId);
                bool found = session.WaitForText(targetText, timeoutSeconds);
                if (!found)
                {
                    SetError(sessionId, $"Timeout after {timeoutSeconds}s waiting for text '{targetText}' on AS400 screen.");
                }
                return found;
            }
            catch (Exception ex)
            {
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return false;
            }
        }

        /// <summary>
        /// Waits until the terminal unlock/ready state is received from the AS400 host.
        /// </summary>
        public static bool WaitForScreenUpdate(string sessionId, int timeoutSeconds = 10)
        {
            try
            {
                ClearError(sessionId);
                var session = GetSession(sessionId);
                bool updated = session.WaitForScreenUpdate(timeoutSeconds);
                if (!updated)
                {
                    SetError(sessionId, $"Timeout after {timeoutSeconds}s waiting for AS400 screen update.");
                }
                return updated;
            }
            catch (Exception ex)
            {
                SetError(sessionId, ex.Message);
                if (ThrowOnError) throw;
                return false;
            }
        }

        #endregion

        #region Error Handling

        /// <summary>
        /// Returns the most recent error message for the specified session, or global error if sessionId is null/empty.
        /// </summary>
        public static string GetLastError(string sessionId = null)
        {
            if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out SessionInstance session))
            {
                if (!string.IsNullOrEmpty(session.LastError))
                {
                    return session.LastError;
                }
            }

            lock (_errorLock)
            {
                return _globalLastError;
            }
        }

        private static void SetError(string sessionId, string error)
        {
            lock (_errorLock)
            {
                _globalLastError = error ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out SessionInstance session))
            {
                session.LastError = error ?? string.Empty;
            }
        }

        private static void ClearError(string sessionId)
        {
            lock (_errorLock)
            {
                _globalLastError = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out SessionInstance session))
            {
                session.LastError = string.Empty;
            }
        }

        private static SessionInstance GetSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                throw new AS400Exception(AS400ErrorCode.SessionNotFound, "SessionId cannot be null or empty.");
            }

            if (!_sessions.TryGetValue(sessionId, out SessionInstance session) || session == null)
            {
                throw new AS400Exception(AS400ErrorCode.SessionNotFound, $"AS400 session '{sessionId}' was not found.");
            }

            return session;
        }

        #endregion
    }
}
