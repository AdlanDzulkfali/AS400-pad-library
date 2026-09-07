using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AS400Automation.Protocol;

namespace AS400Automation.Testing
{
    /// <summary>
    /// Embedded mock TN5250 Telnet server for offline testing and RPA flow development.
    /// Simulates RFC 854/1205 negotiations, initial sign-on screen, and menu transitions.
    /// </summary>
    public class MockTn5250Server : IDisposable
    {
        public int Port { get; private set; }
        public string NegotiatedTerminalModel { get; private set; }
        public List<byte[]> ReceivedPackets { get; } = new List<byte[]>();

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private TcpClient _connectedClient;
        private NetworkStream _clientStream;
        private readonly object _lock = new object();

        public MockTn5250Server(int port = 0)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(ListenLoop);
        }

        private async Task ListenLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    lock (_lock)
                    {
                        _connectedClient = client;
                        _clientStream = client.GetStream();
                    }

                    _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
                }
            }
            catch { }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[4096];

            try
            {
                // Step 1: Send Telnet options
                byte[] handshake = new byte[]
                {
                    Tn5250Constants.IAC, Tn5250Constants.DO, Tn5250Constants.OPT_TERMINAL_TYPE,
                    Tn5250Constants.IAC, Tn5250Constants.DO, Tn5250Constants.OPT_TRANSMIT_BINARY,
                    Tn5250Constants.IAC, Tn5250Constants.DO, Tn5250Constants.OPT_END_OF_RECORD
                };
                await stream.WriteAsync(handshake, 0, handshake.Length, ct);

                bool requestedModel = false;
                bool sentInitialScreen = false;

                while (!ct.IsCancellationRequested && client.Connected)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                    if (read <= 0) break;

                    byte[] packet = new byte[read];
                    Array.Copy(buffer, 0, packet, 0, read);
                    lock (ReceivedPackets)
                    {
                        ReceivedPackets.Add(packet);
                    }

                    // Check for WILL TERMINAL-TYPE
                    for (int i = 0; i < read - 2; i++)
                    {
                        if (buffer[i] == Tn5250Constants.IAC && buffer[i + 1] == Tn5250Constants.WILL && buffer[i + 2] == Tn5250Constants.OPT_TERMINAL_TYPE)
                        {
                            if (!requestedModel)
                            {
                                requestedModel = true;
                                byte[] reqModel = new byte[]
                                {
                                    Tn5250Constants.IAC, Tn5250Constants.SB, Tn5250Constants.OPT_TERMINAL_TYPE,
                                    Tn5250Constants.QUAL_SEND,
                                    Tn5250Constants.IAC, Tn5250Constants.SE
                                };
                                await stream.WriteAsync(reqModel, 0, reqModel.Length, ct);
                            }
                        }
                    }

                    // Check for Terminal Model subnegotiation
                    for (int i = 0; i < read - 4; i++)
                    {
                        if (buffer[i] == Tn5250Constants.IAC && buffer[i + 1] == Tn5250Constants.SB &&
                            buffer[i + 2] == Tn5250Constants.OPT_TERMINAL_TYPE && buffer[i + 3] == Tn5250Constants.QUAL_IS)
                        {
                            int start = i + 4;
                            int end = start;
                            while (end < read && buffer[end] != Tn5250Constants.IAC) end++;
                            if (end > start)
                            {
                                NegotiatedTerminalModel = Encoding.ASCII.GetString(buffer, start, end - start);
                            }

                            if (!sentInitialScreen)
                            {
                                sentInitialScreen = true;
                                SendSignOnScreen(stream);
                            }
                            break;
                        }
                    }

                    // Check for Inbound keystroke data (GDS header 0x12A0)
                    for (int i = 0; i < read - 3; i++)
                    {
                        if (buffer[i] == 0x12 && buffer[i + 1] == 0xA0)
                        {
                            SendMainMenuScreen(stream);
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        public void SendSignOnScreen(Stream stream = null)
        {
            var s = stream ?? _clientStream;
            if (s == null) return;

            var packet = new List<byte>();

            // GDS Header
            packet.AddRange(new byte[] { 0x00, 0x80, 0x12, 0xA0, 0x00, 0x00 });

            // Escape + Clear Unit
            packet.AddRange(new byte[] { Tn5250Constants.ESC, Tn5250Constants.CMD_CLEAR_UNIT });

            // Escape + Write to Display with CC1 bit 4 unlock keyboard
            packet.AddRange(new byte[] { Tn5250Constants.ESC, Tn5250Constants.CMD_WRITE_TO_DISPLAY, 0x10, 0x00 });

            // SBA (Row 1, Col 25)
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 1, 25 });
            foreach (char c in "IBM i Sign On Screen") packet.Add(EbcdicCodec.ToEbcdic(c));

            // SBA (Row 6, Col 10)
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 6, 10 });
            foreach (char c in "User . . . . . . . . . . . . :") packet.Add(EbcdicCodec.ToEbcdic(c));

            // Start Field (Row 6, Col 41): Normal Green Input Field
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 6, 41 });
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SF, 0x00, 0x00, 0x20 });
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_RA, 6, 52, 0x40 });

            // SBA (Row 7, Col 10)
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 7, 10 });
            foreach (char c in "Password . . . . . . . . . . :") packet.Add(EbcdicCodec.ToEbcdic(c));

            // Start Field (Row 7, Col 41): Hidden Input Field
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 7, 41 });
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SF, 0x00, 0x00, 0x27 });
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_RA, 7, 52, 0x40 });

            // Insert Cursor at (6, 42)
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_IC, 6, 42 });

            // Telnet IAC EOR
            packet.Add(Tn5250Constants.IAC);
            packet.Add(Tn5250Constants.EOR);

            byte[] data = packet.ToArray();
            s.Write(data, 0, data.Length);
            s.Flush();
        }

        public void SendMainMenuScreen(Stream stream = null)
        {
            var s = stream ?? _clientStream;
            if (s == null) return;

            var packet = new List<byte>();

            // GDS Header
            packet.AddRange(new byte[] { 0x00, 0x80, 0x12, 0xA0, 0x00, 0x00 });

            // Clear Unit + Write to Display
            packet.AddRange(new byte[] { Tn5250Constants.ESC, Tn5250Constants.CMD_CLEAR_UNIT });
            packet.AddRange(new byte[] { Tn5250Constants.ESC, Tn5250Constants.CMD_WRITE_TO_DISPLAY, 0x10, 0x00 });

            // SBA (Row 1, Col 28)
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 1, 28 });
            foreach (char c in "MAIN MENU - IBM i") packet.Add(EbcdicCodec.ToEbcdic(c));

            // SBA (Row 5, Col 10)
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 5, 10 });
            foreach (char c in "1. User tasks") packet.Add(EbcdicCodec.ToEbcdic(c));

            // SBA (Row 6, Col 10)
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 6, 10 });
            foreach (char c in "2. Office tasks") packet.Add(EbcdicCodec.ToEbcdic(c));

            // SBA (Row 7, Col 10)
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 7, 10 });
            foreach (char c in "3. General system tasks") packet.Add(EbcdicCodec.ToEbcdic(c));

            // SBA (Row 20, Col 10)
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 20, 10 });
            foreach (char c in "Selection or command ===>") packet.Add(EbcdicCodec.ToEbcdic(c));

            // Start Field for selection: Row 20, Col 36
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SBA, 20, 35 });
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_SF, 0x00, 0x00, 0x20 });
            packet.AddRange(new byte[] { Tn5250Constants.ORDER_RA, 20, 50, 0x40 });

            // Telnet IAC EOR
            packet.Add(Tn5250Constants.IAC);
            packet.Add(Tn5250Constants.EOR);

            byte[] data = packet.ToArray();
            s.Write(data, 0, data.Length);
            s.Flush();
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            try { _clientStream?.Close(); } catch { }
            try { _connectedClient?.Close(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
