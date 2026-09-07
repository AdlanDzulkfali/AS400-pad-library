using System;

namespace AS400Automation
{
    /// <summary>
    /// Categorized error codes for AS400 / TN5250 communication and presentation space operations.
    /// </summary>
    public enum AS400ErrorCode
    {
        Unknown = 0,
        ConnectionFailed = 1001,
        ConnectionTimeout = 1002,
        NegotiationFailed = 1003,
        SessionNotFound = 1004,
        SessionFaulted = 1005,
        InvalidCoordinate = 1006,
        TimeoutWaitingForText = 1007,
        TimeoutWaitingForScreen = 1008,
        SendKeysFailed = 1009,
        ScreenReadFailed = 1010
    }

    /// <summary>
    /// Exception thrown when an AS400 terminal session encounters a network, protocol, or coordinate error.
    /// </summary>
    [Serializable]
    public class AS400Exception : Exception
    {
        public AS400ErrorCode ErrorCode { get; }
        public string SessionId { get; }

        public AS400Exception(AS400ErrorCode errorCode, string message, string sessionId = null, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            SessionId = sessionId;
        }

        public override string ToString()
        {
            string sessionInfo = !string.IsNullOrEmpty(SessionId) ? $" [Session: {SessionId}]" : string.Empty;
            return $"AS400Exception (ErrorCode: {ErrorCode}){sessionInfo}: {Message}";
        }
    }
}
