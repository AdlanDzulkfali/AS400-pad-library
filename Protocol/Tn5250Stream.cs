using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AS400Automation.Protocol
{
    /// <summary>
    /// Native C# TN5250 Telnet protocol client and data stream parser (RFC 854, RFC 1205, RFC 2877).
    /// Communicates directly with IBM i / AS400 over standard TCP or SSL/TLS streams.
    /// </summary>
    public class Tn5250Stream : IDisposable
    {
        public string SessionId { get; }
        public string Host { get; private set; }
        public int Port { get; private set; }
        public bool UseSsl { get; private set; }
        public string TerminalType { get; private set; }

        public Tn5250ScreenBuffer ScreenBuffer { get; }
        public ManualResetEventSlim ScreenUpdatedEvent { get; } = new ManualResetEventSlim(false);

        private TcpClient _tcpClient;
        private Stream _stream;
        private Thread _receiveThread;
        private CancellationTokenSource _cts;

        private readonly object _syncLock = new object();
        private volatile bool _isConnected;
        private volatile bool _keyboardLocked;
        private bool _disposed;

        public bool IsConnected
        {
            get
            {
                lock (_syncLock)
                {
                    return _isConnected && _tcpClient != null && _tcpClient.Connected;
                }
            }
        }

        public bool IsKeyboardLocked => _keyboardLocked;

        public Tn5250Stream(string sessionId, Tn5250ScreenBuffer screenBuffer = null)
        {
            SessionId = !string.IsNullOrWhiteSpace(sessionId) ? sessionId : Guid.NewGuid().ToString("N");
            ScreenBuffer = screenBuffer ?? new Tn5250ScreenBuffer();
        }

        /// <summary>
        /// Connects to the AS400 host and initiates Telnet negotiation.
        /// </summary>
        public void Connect(string host, int port = 23, bool useSsl = false, string terminalType = Tn5250Constants.TERMINAL_MODEL_IBM3179, int timeoutSeconds = 30)
        {
            if (string.IsNullOrWhiteSpace(host))
                throw new AS400Exception(AS400ErrorCode.ConnectionFailed, "AS400 Host cannot be null or empty.", SessionId);
            if (port <= 0 || port > 65535)
                throw new AS400Exception(AS400ErrorCode.ConnectionFailed, $"Invalid port '{port}'. Must be between 1 and 65535.", SessionId);

            Host = host.Trim();
            Port = port;
            UseSsl = useSsl;
            TerminalType = !string.IsNullOrWhiteSpace(terminalType) ? terminalType.Trim() : Tn5250Constants.TERMINAL_MODEL_IBM3179;

            int timeoutMs = Math.Max(1000, timeoutSeconds * 1000);

            lock (_syncLock)
            {
                if (IsConnected)
                {
                    Disconnect();
                }

                _cts = new CancellationTokenSource();
                ScreenUpdatedEvent.Reset();
                ScreenBuffer.Clear();

                try
                {
                    _tcpClient = new TcpClient
                    {
                        ReceiveTimeout = timeoutMs,
                        SendTimeout = timeoutMs,
                        NoDelay = true
                    };

                    // Asynchronous connection with strict timeout
                    IAsyncResult ar = _tcpClient.BeginConnect(Host, Port, null, null);
                    using (WaitHandle wh = ar.AsyncWaitHandle)
                    {
                        if (!wh.WaitOne(timeoutMs, false))
                        {
                            SafeTeardown();
                            throw new AS400Exception(AS400ErrorCode.ConnectionTimeout,
                                $"Connection to AS400 host '{Host}:{Port}' timed out after {timeoutSeconds} seconds.", SessionId);
                        }
                        _tcpClient.EndConnect(ar);
                    }

                    Stream netStream = _tcpClient.GetStream();

                    if (UseSsl)
                    {
                        var sslStream = new SslStream(
                            netStream,
                            false,
                            ValidateServerCertificate,
                            null
                        );

                        sslStream.AuthenticateAsClient(Host);
                        _stream = sslStream;
                    }
                    else
                    {
                        _stream = netStream;
                    }

                    _isConnected = true;
                    _keyboardLocked = true;

                    // Start background receive thread
                    _receiveThread = new Thread(ReceiveWorker)
                    {
                        IsBackground = true,
                        Name = $"TN5250_Worker_{SessionId}"
                    };
                    _receiveThread.Start();
                }
                catch (Exception ex)
                {
                    SafeTeardown();
                    if (ex is AS400Exception) throw;
                    throw new AS400Exception(AS400ErrorCode.ConnectionFailed,
                        $"Failed to connect to AS400 host '{Host}:{Port}': {ex.Message}", SessionId, ex);
                }
            }

            // Wait for initial screen data from host
            bool initialScreenReceived = ScreenUpdatedEvent.Wait(TimeSpan.FromSeconds(timeoutSeconds));
            if (!initialScreenReceived)
            {
                SafeTeardown();
                throw new AS400Exception(AS400ErrorCode.ConnectionTimeout,
                    $"AS400 host '{Host}:{Port}' connection timed out waiting for initial presentation space data ({timeoutSeconds} seconds).", SessionId);
            }
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // Accept certificates (including self-signed frequently found on AS400 systems)
            return true;
        }

        /// <summary>
        /// Safely tears down the TCP socket and streams.
        /// </summary>
        public void Disconnect()
        {
            SafeTeardown();
        }

        private void SafeTeardown()
        {
            lock (_syncLock)
            {
                _isConnected = false;

                try { _cts?.Cancel(); } catch { }
                try { _stream?.Close(); } catch { }
                try { _tcpClient?.Close(); } catch { }

                _stream = null;
                _tcpClient = null;
                try { ScreenUpdatedEvent?.Reset(); } catch { }
            }
        }

        /// <summary>
        /// Background thread reading incoming Telnet bytes and processing 5250 records.
        /// </summary>
        private void ReceiveWorker()
        {
            byte[] rawBuffer = new byte[8192];
            List<byte> recordBuffer = new List<byte>(4096);

            try
            {
                while (_cts != null && !_cts.IsCancellationRequested && IsConnected)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = _stream.Read(rawBuffer, 0, rawBuffer.Length);
                    }
                    catch (IOException) { break; }
                    catch (ObjectDisposedException) { break; }

                    if (bytesRead <= 0)
                    {
                        break; // Socket closed by remote host
                    }

                    int i = 0;
                    while (i < bytesRead)
                    {
                        byte b = rawBuffer[i++];

                        if (b == Tn5250Constants.IAC)
                        {
                            if (i >= bytesRead) break;
                            byte cmd = rawBuffer[i++];

                            switch (cmd)
                            {
                                case Tn5250Constants.IAC:
                                    // Escaped 0xFF byte literal
                                    recordBuffer.Add(Tn5250Constants.IAC);
                                    break;

                                case Tn5250Constants.DO:
                                    if (i < bytesRead)
                                    {
                                        byte opt = rawBuffer[i++];
                                        HandleTelnetDo(opt);
                                    }
                                    break;

                                case Tn5250Constants.DONT:
                                    if (i < bytesRead) i++; // Ignore
                                    break;

                                case Tn5250Constants.WILL:
                                    if (i < bytesRead)
                                    {
                                        byte opt = rawBuffer[i++];
                                        HandleTelnetWill(opt);
                                    }
                                    break;

                                case Tn5250Constants.WONT:
                                    if (i < bytesRead) i++; // Ignore
                                    break;

                                case Tn5250Constants.SB:
                                    // Subnegotiation until IAC SE
                                    List<byte> sbData = new List<byte>();
                                    while (i < bytesRead)
                                    {
                                        byte sbByte = rawBuffer[i++];
                                        if (sbByte == Tn5250Constants.IAC && i < bytesRead && rawBuffer[i] == Tn5250Constants.SE)
                                        {
                                            i++; // Consume SE
                                            break;
                                        }
                                        sbData.Add(sbByte);
                                    }
                                    HandleTelnetSubnegotiation(sbData.ToArray());
                                    break;

                                case Tn5250Constants.EOR:
                                    // Telnet End of Record: complete 5250 Workstation packet
                                    if (recordBuffer.Count > 0)
                                    {
                                        Process5250Record(recordBuffer.ToArray());
                                        recordBuffer.Clear();
                                    }
                                    _keyboardLocked = false;
                                    ScreenUpdatedEvent.Set();
                                    break;
                            }
                        }
                        else
                        {
                            recordBuffer.Add(b);
                        }
                    }
                }
            }
            catch
            {
                // Network error or aborted thread
            }
            finally
            {
                try { SafeTeardown(); } catch { }
            }
        }

        private void SendRaw(byte[] bytes)
        {
            lock (_syncLock)
            {
                if (!IsConnected || _stream == null) return;
                try
                {
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.Flush();
                }
                catch (Exception)
                {
                    SafeTeardown();
                    throw;
                }
            }
        }

        private void HandleTelnetDo(byte option)
        {
            switch (option)
            {
                case Tn5250Constants.OPT_TERMINAL_TYPE:
                case Tn5250Constants.OPT_END_OF_RECORD:
                case Tn5250Constants.OPT_TRANSMIT_BINARY:
                    SendRaw(new byte[] { Tn5250Constants.IAC, Tn5250Constants.WILL, option });
                    break;
                default:
                    SendRaw(new byte[] { Tn5250Constants.IAC, Tn5250Constants.WONT, option });
                    break;
            }
        }

        private void HandleTelnetWill(byte option)
        {
            switch (option)
            {
                case Tn5250Constants.OPT_END_OF_RECORD:
                case Tn5250Constants.OPT_TRANSMIT_BINARY:
                    SendRaw(new byte[] { Tn5250Constants.IAC, Tn5250Constants.DO, option });
                    break;
                default:
                    SendRaw(new byte[] { Tn5250Constants.IAC, Tn5250Constants.DONT, option });
                    break;
            }
        }

        private void HandleTelnetSubnegotiation(byte[] sbData)
        {
            if (sbData == null || sbData.Length < 2) return;

            byte option = sbData[0];
            byte qualifier = sbData[1];

            if (option == Tn5250Constants.OPT_TERMINAL_TYPE && qualifier == Tn5250Constants.QUAL_SEND)
            {
                // Send: IAC SB TERMINAL-TYPE IS <TerminalType> IAC SE
                byte[] modelBytes = Encoding.ASCII.GetBytes(TerminalType ?? Tn5250Constants.TERMINAL_MODEL_IBM3179);
                List<byte> response = new List<byte>
                {
                    Tn5250Constants.IAC,
                    Tn5250Constants.SB,
                    Tn5250Constants.OPT_TERMINAL_TYPE,
                    Tn5250Constants.QUAL_IS
                };
                response.AddRange(modelBytes);
                response.Add(Tn5250Constants.IAC);
                response.Add(Tn5250Constants.SE);

                SendRaw(response.ToArray());
            }
        }

        /// <summary>
        /// Parses an inbound 5250 record from the host and updates the presentation space.
        /// </summary>
        public void Process5250Record(byte[] record)
        {
            if (record == null || record.Length == 0) return;

            int idx = 0;

            // Optional 5250 GDS Workstation Header (Length + 0x12A0 ...)
            if (record.Length >= 6 && record[2] == 0x12 && record[3] == 0xA0)
            {
                idx = 6;
                if (record.Length > idx + 3 && record[idx] == 0x04 && record[idx + 1] == 0x00)
                {
                    idx += 3;
                }
            }

            while (idx < record.Length)
            {
                byte b = record[idx++];

                if (b == Tn5250Constants.ESC)
                {
                    if (idx >= record.Length) break;
                    byte cmd = record[idx++];

                    switch (cmd)
                    {
                        case Tn5250Constants.CMD_CLEAR_UNIT:
                        case Tn5250Constants.CMD_CLEAR_UNIT_ALTERNATE:
                            ScreenBuffer.Clear();
                            break;

                        case Tn5250Constants.CMD_WRITE_TO_DISPLAY:
                        case Tn5250Constants.CMD_RESTORE_SCREEN:
                            // Consume CC1 and CC2 control bytes
                            if (idx < record.Length)
                            {
                                byte cc1 = record[idx++];
                                // CC1 bit 4 (0x10): reset keyboard lock
                                if ((cc1 & 0x10) != 0)
                                {
                                    _keyboardLocked = false;
                                }
                            }
                            if (idx < record.Length) idx++; // CC2
                            break;

                        case Tn5250Constants.CMD_CLEAR_FORMAT_TABLE:
                            // Clear format table / fields
                            break;
                    }
                }
                else if (b == Tn5250Constants.ORDER_RA)
                {
                    // Repeat to Address: row, col, character
                    if (idx + 2 < record.Length)
                    {
                        int targetRow = record[idx++];
                        int targetCol = record[idx++];
                        byte fillByte = record[idx++];
                        char fillChar = EbcdicCodec.ToChar(fillByte);
                        ScreenBuffer.RepeatToAddress(targetRow, targetCol, fillChar);
                    }
                }
                else if (b == Tn5250Constants.ORDER_SBA)
                {
                    // Set Buffer Address: row, col
                    if (idx + 1 < record.Length)
                    {
                        int row = record[idx++];
                        int col = record[idx++];
                        ScreenBuffer.SetCursor(row, col);
                    }
                }
                else if (b == Tn5250Constants.ORDER_IC)
                {
                    // Insert Cursor: row, col
                    if (idx + 1 < record.Length)
                    {
                        int row = record[idx++];
                        int col = record[idx++];
                        ScreenBuffer.SetCursor(row, col);
                    }
                }
                else if (b == Tn5250Constants.ORDER_SF)
                {
                    // Start Field: FFW1, FFW2, Attribute
                    byte ffw1 = 0, ffw2 = 0, attr = 0x20;
                    if (idx < record.Length) ffw1 = record[idx++];
                    if (idx < record.Length) ffw2 = record[idx++];
                    if (idx < record.Length) attr = record[idx++];

                    ScreenBuffer.AddField(ScreenBuffer.CursorRow, ScreenBuffer.CursorCol, ffw1, ffw2, attr);
                    ScreenBuffer.WriteCharAndAdvance(' ', attr, isAttribute: true);
                }
                else if (b == Tn5250Constants.ORDER_SOH)
                {
                    // Start of Header
                    if (idx < record.Length) idx++; // skip header length
                }
                else if (b == Tn5250Constants.ORDER_EA)
                {
                    // Erase to Address
                    if (idx + 1 < record.Length)
                    {
                        int targetRow = record[idx++];
                        int targetCol = record[idx++];
                        ScreenBuffer.RepeatToAddress(targetRow, targetCol, ' ');
                    }
                }
                else
                {
                    // Standard EBCDIC screen character
                    char c = EbcdicCodec.ToChar(b);
                    ScreenBuffer.WriteCharAndAdvance(c, attribute: b);
                }
            }
        }

        /// <summary>
        /// Sends characters and control keys to the AS400 screen.
        /// </summary>
        public void SendKeystrokes(string text, string controlKey = "Enter")
        {
            if (!IsConnected)
            {
                SafeTeardown();
                throw new AS400Exception(AS400ErrorCode.SessionFaulted,
                    $"Session '{SessionId}' is not connected. Keystrokes cannot be sent.", SessionId);
            }

            try
            {
                ScreenUpdatedEvent.Reset();
                _keyboardLocked = true;

                byte aidCode = ResolveAidCode(controlKey);
                string processedText = text ?? string.Empty;

                // Also parse inline mnemonics: [ENTER], [TAB], [F1]-[F24], @E, @1-@24, [PAGEUP], [PAGEDOWN], [CLEAR], [HELP]
                Match m = Regex.Match(processedText, @"(@[A-Z0-9]{1,2}|\[[A-Z0-9]+\])", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string tag = m.Value.ToUpperInvariant();
                    byte parsedAid = ResolveAidCode(tag);
                    if (parsedAid != Tn5250Constants.AID_NO_AID)
                    {
                        aidCode = parsedAid;
                    }
                    processedText = processedText.Replace(m.Value, string.Empty);
                }

                if (aidCode == Tn5250Constants.AID_NO_AID)
                {
                    aidCode = Tn5250Constants.AID_ENTER;
                }

                // Write text to local buffer at current cursor location as well
                if (!string.IsNullOrEmpty(processedText))
                {
                    ScreenBuffer.WriteString(processedText);
                }

                byte[] textBytes = EbcdicCodec.ToEbcdicBytes(processedText);

                // Construct 5250 Inbound Workstation Data Record
                // Header (10 bytes): Length (2), 0x12A0 (2), Flags 0x0000 (2), Opcode 0x0400 (2), CursorRow (1), CursorCol (1)
                // Followed by: AID (1 byte), Field Data (N bytes), Telnet IAC EOR (2 bytes)
                int payloadLength = 11 + textBytes.Length; // 10 header bytes + 1 AID byte + text
                List<byte> packet = new List<byte>(payloadLength + 8)
                {
                    // GDS Header
                    (byte)((payloadLength >> 8) & 0xFF),
                    (byte)(payloadLength & 0xFF),
                    0x12,
                    0xA0,
                    0x00,
                    0x00,
                    // Opcode / Workstation Header
                    0x04,
                    0x00,
                    // Cursor Coordinates (1-based)
                    (byte)ScreenBuffer.CursorRow,
                    (byte)ScreenBuffer.CursorCol,
                    // AID Code
                    aidCode
                };

                // Add text bytes (if any)
                if (textBytes.Length > 0)
                {
                    packet.AddRange(textBytes);
                }

                // Escape any 0xFF in the payload for Telnet transmission
                List<byte> escapedPacket = new List<byte>(packet.Count + 4);
                foreach (byte b in packet)
                {
                    escapedPacket.Add(b);
                    if (b == Tn5250Constants.IAC)
                    {
                        escapedPacket.Add(Tn5250Constants.IAC); // Escaped 0xFF
                    }
                }

                // Telnet End-Of-Record
                escapedPacket.Add(Tn5250Constants.IAC);
                escapedPacket.Add(Tn5250Constants.EOR);

                SendRaw(escapedPacket.ToArray());
            }
            catch (Exception ex)
            {
                SafeTeardown();
                throw new AS400Exception(AS400ErrorCode.SendKeysFailed,
                    $"Failed to transmit keystrokes on session '{SessionId}': {ex.Message}", SessionId, ex);
            }
        }

        private static byte ResolveAidCode(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return Tn5250Constants.AID_ENTER;
            }

            string clean = key.Trim().ToUpperInvariant().Replace("[", "").Replace("]", "");

            switch (clean)
            {
                case "ENTER":
                case "@E":
                    return Tn5250Constants.AID_ENTER;

                case "CLEAR":
                case "@C":
                    return Tn5250Constants.AID_CLEAR;

                case "HELP":
                case "@H":
                    return Tn5250Constants.AID_HELP;

                case "PAGEUP":
                case "ROLLDOWN":
                case "@U":
                    return Tn5250Constants.AID_PAGE_UP;

                case "PAGEDOWN":
                case "ROLLUP":
                case "@D":
                    return Tn5250Constants.AID_PAGE_DOWN;

                case "PRINT":
                    return Tn5250Constants.AID_PRINT;

                default:
                    // Match F1..F24 or PF1..PF24
                    Match m = Regex.Match(clean, @"^(?:PF|F|@)(\d{1,2})$");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int fNum))
                    {
                        if (fNum >= 1 && fNum <= 12)
                        {
                            return (byte)(Tn5250Constants.AID_PF1 + (fNum - 1));
                        }
                        if (fNum >= 13 && fNum <= 24)
                        {
                            return (byte)(Tn5250Constants.AID_PF13 + (fNum - 13));
                        }
                    }
                    return Tn5250Constants.AID_ENTER;
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

            SafeTeardown();

            if (_receiveThread != null && _receiveThread.IsAlive && Thread.CurrentThread != _receiveThread)
            {
                try { _receiveThread.Join(500); } catch { }
            }

            if (disposing)
            {
                try { ScreenUpdatedEvent?.Dispose(); } catch { }
                try { _cts?.Dispose(); } catch { }
            }
        }

        ~Tn5250Stream()
        {
            Dispose(false);
        }
    }
}
