using System;
using System.Collections.Generic;
using System.Text;

namespace AS400Automation.Ipc
{
    /// <summary>
    /// Lightweight request contract for Named Pipe IPC communication.
    /// Uses tab-delimited framing with Base64 payload encoding for zero-dependency reliability.
    /// </summary>
    public class IpcRequest
    {
        public string Action { get; set; }
        public string SessionId { get; set; }
        public List<string> Arguments { get; } = new List<string>();

        public IpcRequest() { }

        public IpcRequest(string action, string sessionId = null, params string[] args)
        {
            Action = action;
            SessionId = sessionId ?? string.Empty;
            if (args != null)
            {
                Arguments.AddRange(args);
            }
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append(Action).Append('\t').Append(SessionId);
            foreach (var arg in Arguments)
            {
                sb.Append('\t');
                if (arg == null)
                {
                    sb.Append("<null>");
                }
                else
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(arg);
                    sb.Append(Convert.ToBase64String(bytes));
                }
            }
            return sb.ToString();
        }

        public static IpcRequest Deserialize(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            string[] parts = line.Split('\t');
            if (parts.Length < 2) return null;

            var req = new IpcRequest
            {
                Action = parts[0],
                SessionId = parts[1]
            };

            for (int i = 2; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part == "<null>")
                {
                    req.Arguments.Add(null);
                }
                else
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(part);
                        req.Arguments.Add(Encoding.UTF8.GetString(bytes));
                    }
                    catch
                    {
                        req.Arguments.Add(part);
                    }
                }
            }

            return req;
        }
    }

    /// <summary>
    /// Lightweight response contract for Named Pipe IPC communication.
    /// </summary>
    public class IpcResponse
    {
        public bool Success { get; set; }
        public string Data { get; set; }
        public int ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public string Serialize()
        {
            if (Success)
            {
                string encoded = Data != null ? Convert.ToBase64String(Encoding.UTF8.GetBytes(Data)) : string.Empty;
                return $"OK\t{encoded}";
            }
            else
            {
                string encodedErr = ErrorMessage != null ? Convert.ToBase64String(Encoding.UTF8.GetBytes(ErrorMessage)) : string.Empty;
                return $"ERR\t{ErrorCode}\t{encodedErr}";
            }
        }

        public static IpcResponse Deserialize(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return new IpcResponse { Success = false, ErrorMessage = "Empty IPC response from daemon." };
            }

            string[] parts = line.Split('\t');
            if (parts[0] == "OK")
            {
                string data = string.Empty;
                if (parts.Length > 1 && !string.IsNullOrEmpty(parts[1]))
                {
                    try
                    {
                        data = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
                    }
                    catch { data = parts[1]; }
                }
                return new IpcResponse { Success = true, Data = data };
            }
            else
            {
                int code = parts.Length > 1 && int.TryParse(parts[1], out int c) ? c : 0;
                string err = "Unknown daemon error.";
                if (parts.Length > 2 && !string.IsNullOrEmpty(parts[2]))
                {
                    try
                    {
                        err = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
                    }
                    catch { err = parts[2]; }
                }
                return new IpcResponse { Success = false, ErrorCode = code, ErrorMessage = err };
            }
        }
    }
}
