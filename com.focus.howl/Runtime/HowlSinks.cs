using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Focus.Howl
{
    /// <summary>A destination for serialized batches. Driven only by the worker thread; must never block it.</summary>
    interface IHowlSink : IDisposable
    {
        bool IsListening { get; }
        string State { get; }
        void Send(byte[] data, int length);
        void Tick(long nowTicks);
    }

    /// <summary>Buffered JSONL log: 64 KB buffer, flushed every 250 ms, rolled at 50 MB. Replay, debugging, forensics.</summary>
    sealed class HowlFileSink : IHowlSink
    {
        const long RollBytes = 50L * 1024 * 1024;
        static readonly long FlushTicks = Stopwatch.Frequency / 4;

        readonly string m_Directory;
        readonly string m_BaseName;
        FileStream m_Stream;
        long m_Written;
        long m_NextFlush;
        int m_Part;

        public string Path { get; private set; }
        public bool IsListening => m_Stream != null;
        public string State => m_Stream != null ? "on" : "error";

        public HowlFileSink(string directory)
        {
            m_Directory = directory;
            m_BaseName = "howl_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            Directory.CreateDirectory(directory);
            Open();
        }

        void Open()
        {
            Path = System.IO.Path.Combine(m_Directory, m_BaseName + (m_Part == 0 ? "" : "_" + m_Part) + ".jsonl");
            m_Stream = new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024);
            m_Written = 0;
        }

        public void Send(byte[] data, int length)
        {
            if (m_Stream == null) return;
            m_Stream.Write(data, 0, length);
            m_Written += length;
            if (m_Written < RollBytes) return;
            m_Stream.Dispose();
            m_Part++;
            Open();
        }

        public void Tick(long nowTicks)
        {
            if (m_Stream == null || nowTicks < m_NextFlush) return;
            m_NextFlush = nowTicks + FlushTicks;
            m_Stream.Flush();
        }

        public void Dispose()
        {
            m_Stream?.Flush();
            m_Stream?.Dispose();
            m_Stream = null;
        }
    }

    /// <summary>Which categories the connected app asked for ({"subscribe":[...]}); null means everything.</summary>
    sealed class HowlSubscription
    {
        public readonly HashSet<string> Categories;
        public readonly bool Events;

        public HowlSubscription(HashSet<string> categories)
        {
            Categories = categories;
            Events = categories.Contains("events");
        }
    }

    /// <summary>
    /// Minimal RFC 6455 client on a raw non-blocking socket, so steady-state sending allocates nothing.
    /// The handshake allocates once per connection. Each batch is framed and masked into a reusable outgoing
    /// buffer and written with a non-blocking send; a partial write stays queued for the next tick. If the app
    /// falls more than <see cref="MaxPendingBytes"/> behind, batches are dropped and counted, and the next one
    /// carries a full info snapshot. Reconnects with exponential backoff (250 ms to 5 s). Never waits on the app.
    /// </summary>
    sealed class HowlWebSocketSink : IHowlSink
    {
        enum Phase { Waiting, Connecting, Handshake, Open }

        const int MaxPendingBytes = 4 * 1024 * 1024;
        const double MinBackoffMs = 250;
        const double MaxBackoffMs = 5000;
        static readonly long HandshakeTimeoutTicks = Stopwatch.Frequency * 2;

        readonly string m_Host;
        readonly int m_Port;
        readonly string m_Path;
        readonly Random m_Random = new Random();
        readonly byte[] m_Mask = new byte[4];
        readonly byte[] m_In = new byte[64 * 1024];
        byte[] m_Out = new byte[256 * 1024];
        int m_OutStart, m_OutEnd, m_InLength;

        Socket m_Socket;
        SocketAsyncEventArgs m_ConnectArgs;
        volatile bool m_ConnectDone;
        Phase m_Phase;
        long m_RetryAt, m_HandshakeDeadline;
        double m_BackoffMs = MinBackoffMs;

        public long DroppedBatches;
        /// <summary>Set on connect, on a new subscription and after a dropped batch; the worker then resends all info.</summary>
        public bool SnapshotRequested;
        public HowlSubscription Subscription;

        public bool IsListening => m_Phase == Phase.Open;
        public string State => m_Phase == Phase.Open ? "open" : m_Phase == Phase.Waiting ? "waiting" : "connecting";
        public int PendingBytes => m_OutEnd - m_OutStart;

        public HowlWebSocketSink(string url)
        {
            var uri = new Uri(url);
            m_Host = uri.Host;
            m_Port = uri.Port > 0 ? uri.Port : 80;
            m_Path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        }

        public void Tick(long nowTicks)
        {
            switch (m_Phase)
            {
                case Phase.Waiting:
                    if (nowTicks >= m_RetryAt) Connect(nowTicks);
                    break;
                case Phase.Connecting:
                    if (!m_ConnectDone) break;
                    if (m_ConnectArgs.SocketError != SocketError.Success)
                    {
                        Fail(nowTicks);
                        break;
                    }
                    SendHandshake();
                    m_Phase = Phase.Handshake;
                    m_HandshakeDeadline = nowTicks + HandshakeTimeoutTicks;
                    break;
                case Phase.Handshake:
                    if (!Flush(nowTicks)) break;
                    ReadHandshake(nowTicks);
                    break;
                case Phase.Open:
                    if (!Flush(nowTicks)) break;
                    Receive(nowTicks);
                    break;
            }
        }

        public void Send(byte[] data, int length)
        {
            if (m_Phase != Phase.Open) return;
            if (PendingBytes + length + 14 > MaxPendingBytes)
            {
                DroppedBatches++;
                SnapshotRequested = true;
                return;
            }
            WriteFrame(0x1, data, 0, length);
            Flush(Stopwatch.GetTimestamp());
        }

        // ---- Connection ----

        void Connect(long nowTicks)
        {
            try
            {
                Close();
                var address = ResolveIPv4(m_Host);
                m_Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, Blocking = false };
                m_ConnectArgs = new SocketAsyncEventArgs { RemoteEndPoint = new IPEndPoint(address, m_Port) };
                m_ConnectArgs.Completed += (_, __) => m_ConnectDone = true;
                m_ConnectDone = false;
                m_Phase = Phase.Connecting;
                if (!m_Socket.ConnectAsync(m_ConnectArgs)) m_ConnectDone = true; // completed synchronously
            }
            catch (Exception)
            {
                Fail(nowTicks);
            }
        }

        static IPAddress ResolveIPv4(string host)
        {
            if (host == "localhost") return IPAddress.Loopback;
            if (IPAddress.TryParse(host, out var ip)) return ip;
            foreach (var candidate in Dns.GetHostAddresses(host))
                if (candidate.AddressFamily == AddressFamily.InterNetwork) return candidate;
            throw new SocketException((int)SocketError.HostNotFound);
        }

        void SendHandshake()
        {
            var key = new byte[16];
            m_Random.NextBytes(key);
            string request =
                "GET " + m_Path + " HTTP/1.1\r\n" +
                "Host: " + m_Host + ":" + m_Port + "\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: " + Convert.ToBase64String(key) + "\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(request);
            EnsureOut(bytes.Length);
            Buffer.BlockCopy(bytes, 0, m_Out, m_OutEnd, bytes.Length);
            m_OutEnd += bytes.Length;
        }

        void ReadHandshake(long nowTicks)
        {
            if (!Read(nowTicks)) return;
            int end = IndexOfHeaderEnd();
            if (end < 0)
            {
                if (nowTicks > m_HandshakeDeadline || m_InLength == m_In.Length) Fail(nowTicks);
                return;
            }
            // "HTTP/1.1 101 Switching Protocols"
            bool upgraded = m_InLength >= 12 && m_In[9] == (byte)'1' && m_In[10] == (byte)'0' && m_In[11] == (byte)'1';
            if (!upgraded)
            {
                Fail(nowTicks);
                return;
            }
            Consume(end);
            m_Phase = Phase.Open;
            m_BackoffMs = MinBackoffMs;
            Subscription = null;
            SnapshotRequested = true;
            ParseFrames(nowTicks); // the server may have sent a frame right behind the handshake
        }

        int IndexOfHeaderEnd()
        {
            for (int i = 3; i < m_InLength; i++)
                if (m_In[i] == '\n' && m_In[i - 1] == '\r' && m_In[i - 2] == '\n' && m_In[i - 3] == '\r') return i + 1;
            return -1;
        }

        void Fail(long nowTicks)
        {
            Close();
            m_Phase = Phase.Waiting;
            m_RetryAt = nowTicks + (long)(m_BackoffMs * Stopwatch.Frequency / 1000);
            m_BackoffMs = Math.Min(MaxBackoffMs, m_BackoffMs * 2);
        }

        void Close()
        {
            try { m_Socket?.Close(); } catch (Exception) { }
            try { m_ConnectArgs?.Dispose(); } catch (Exception) { }
            m_Socket = null;
            m_ConnectArgs = null;
            m_OutStart = m_OutEnd = 0;
            m_InLength = 0;
        }

        // ---- I/O (non-blocking; SocketError out-params, so no exceptions or allocations on the hot path) ----

        bool Flush(long nowTicks)
        {
            while (m_OutStart < m_OutEnd)
            {
                int sent = m_Socket.Send(m_Out, m_OutStart, m_OutEnd - m_OutStart, SocketFlags.None, out SocketError error);
                if (error == SocketError.WouldBlock) break;
                if (error != SocketError.Success)
                {
                    Fail(nowTicks);
                    return false;
                }
                m_OutStart += sent;
            }
            if (m_OutStart == m_OutEnd) m_OutStart = m_OutEnd = 0;
            return true;
        }

        /// <summary>Reads whatever is available. False if the connection closed or failed.</summary>
        bool Read(long nowTicks)
        {
            while (m_InLength < m_In.Length && m_Socket.Poll(0, SelectMode.SelectRead))
            {
                int n = m_Socket.Receive(m_In, m_InLength, m_In.Length - m_InLength, SocketFlags.None, out SocketError error);
                if (error == SocketError.WouldBlock) break;
                if (error != SocketError.Success || n == 0)
                {
                    Fail(nowTicks);
                    return false;
                }
                m_InLength += n;
            }
            return true;
        }

        void Receive(long nowTicks)
        {
            if (!Read(nowTicks)) return;
            ParseFrames(nowTicks);
            if (m_Phase == Phase.Open && m_InLength == m_In.Length) Fail(nowTicks); // a frame larger than the buffer
        }

        void ParseFrames(long nowTicks)
        {
            int pos = 0;
            while (m_Phase == Phase.Open && m_InLength - pos >= 2)
            {
                int opcode = m_In[pos] & 0x0F;
                bool masked = (m_In[pos + 1] & 0x80) != 0;
                long length = m_In[pos + 1] & 0x7F;
                int header = 2;
                if (length == 126)
                {
                    if (m_InLength - pos < 4) break;
                    length = (m_In[pos + 2] << 8) | m_In[pos + 3];
                    header = 4;
                }
                else if (length == 127)
                {
                    if (m_InLength - pos < 10) break;
                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) | m_In[pos + 2 + i];
                    header = 10;
                }
                int maskAt = pos + header;
                if (masked) header += 4;
                if (m_InLength - pos < header + length) break;

                int payload = pos + header;
                if (masked)
                    for (int i = 0; i < length; i++) m_In[payload + i] ^= m_In[maskAt + (i & 3)];

                switch (opcode)
                {
                    case 0x1: HandleText(payload, (int)length); break;
                    case 0x8: Fail(nowTicks); return;                       // close
                    case 0x9: WriteFrame(0xA, m_In, payload, (int)length); break; // ping -> pong
                }
                pos += header + (int)length;
            }
            if (m_Phase == Phase.Open) Consume(pos);
        }

        void Consume(int count)
        {
            if (count <= 0) return;
            Buffer.BlockCopy(m_In, count, m_In, 0, m_InLength - count);
            m_InLength -= count;
        }

        void WriteFrame(int opcode, byte[] data, int offset, int length)
        {
            EnsureOut(length + 14);
            var o = m_Out;
            int e = m_OutEnd;
            o[e++] = (byte)(0x80 | opcode);
            if (length < 126)
            {
                o[e++] = (byte)(0x80 | length);
            }
            else if (length <= 0xFFFF)
            {
                o[e++] = 0x80 | 126;
                o[e++] = (byte)(length >> 8);
                o[e++] = (byte)length;
            }
            else
            {
                o[e++] = 0x80 | 127;
                for (int shift = 56; shift >= 0; shift -= 8) o[e++] = (byte)((long)length >> shift);
            }
            m_Random.NextBytes(m_Mask);
            o[e++] = m_Mask[0];
            o[e++] = m_Mask[1];
            o[e++] = m_Mask[2];
            o[e++] = m_Mask[3];
            for (int i = 0; i < length; i++) o[e + i] = (byte)(data[offset + i] ^ m_Mask[i & 3]);
            m_OutEnd = e + length;
        }

        void EnsureOut(int extra)
        {
            if (m_OutEnd + extra <= m_Out.Length) return;
            int pending = m_OutEnd - m_OutStart;
            if (m_OutStart > 0)
            {
                Buffer.BlockCopy(m_Out, m_OutStart, m_Out, 0, pending);
                m_OutStart = 0;
                m_OutEnd = pending;
            }
            if (m_OutEnd + extra > m_Out.Length)
            {
                int size = m_Out.Length;
                while (size < m_OutEnd + extra) size *= 2;
                Array.Resize(ref m_Out, size);
            }
        }

        // Minimal parse of {"subscribe":["me","match_info","events"]}; anything else is ignored. Rare, so it may allocate.
        void HandleText(int offset, int length)
        {
            string message = Encoding.UTF8.GetString(m_In, offset, length);
            int key = message.IndexOf("\"subscribe\"", StringComparison.Ordinal);
            if (key < 0) return;
            int open = message.IndexOf('[', key);
            int close = open < 0 ? -1 : message.IndexOf(']', open);
            if (close < 0) return;
            var categories = new HashSet<string>();
            int i = open;
            while (true)
            {
                int start = message.IndexOf('"', i + 1);
                if (start < 0 || start > close) break;
                int end = message.IndexOf('"', start + 1);
                if (end < 0 || end > close) break;
                categories.Add(message.Substring(start + 1, end - start - 1));
                i = end;
            }
            Subscription = new HowlSubscription(categories);
            SnapshotRequested = true;
        }

        public void Dispose()
        {
            if (m_Phase == Phase.Open && m_Socket != null)
            {
                try
                {
                    // Close frame (status 1000), then a short best-effort flush.
                    var status = new byte[] { 0x03, 0xE8 };
                    WriteFrame(0x8, status, 0, 2);
                    var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
                    while (PendingBytes > 0 && Stopwatch.GetTimestamp() < deadline && Flush(deadline))
                        System.Threading.Thread.Sleep(1);
                    m_Socket?.Shutdown(SocketShutdown.Both);
                }
                catch (Exception) { }
            }
            Close();
            m_Phase = Phase.Waiting;
        }
    }
}
