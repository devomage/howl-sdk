using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    /// WebSocket client to the overlay app's server. Connects with exponential backoff (capped at 5 s). At most one
    /// send is in flight; if the previous one hasn't completed, the new batch is dropped and counted, and the next
    /// batch carries a full info snapshot. The worker never waits on the app.
    /// </summary>
    sealed class HowlWebSocketSink : IHowlSink
    {
        enum Phase { Waiting, Connecting, Open }

        const double MinBackoffMs = 250;
        const double MaxBackoffMs = 5000;

        readonly Uri m_Uri;
        ClientWebSocket m_Socket;
        Task m_Connect;
        Task m_Send;
        Phase m_Phase;
        long m_RetryAt;
        double m_BackoffMs = MinBackoffMs;
        byte[] m_SendBuffer = new byte[64 * 1024];

        public long DroppedBatches;
        /// <summary>Set on connect and after a dropped batch; the worker then resends all info.</summary>
        public volatile bool SnapshotRequested;
        public volatile HowlSubscription Subscription;

        public bool IsListening => m_Phase == Phase.Open;
        public string State => m_Phase == Phase.Open ? "open" : m_Phase == Phase.Connecting ? "connecting" : "waiting";

        public HowlWebSocketSink(string url) { m_Uri = new Uri(url); }

        public void Tick(long nowTicks)
        {
            switch (m_Phase)
            {
                case Phase.Waiting:
                    if (nowTicks >= m_RetryAt) Connect(nowTicks);
                    break;
                case Phase.Connecting:
                    if (!m_Connect.IsCompleted) break;
                    if (m_Connect.Status == TaskStatus.RanToCompletion && m_Socket.State == WebSocketState.Open)
                    {
                        m_Phase = Phase.Open;
                        m_BackoffMs = MinBackoffMs;
                        Subscription = null;
                        SnapshotRequested = true;
                        _ = ReceiveLoop(m_Socket);
                    }
                    else
                    {
                        Fail(nowTicks);
                    }
                    break;
                case Phase.Open:
                    if (m_Socket.State != WebSocketState.Open || (m_Send != null && m_Send.IsFaulted)) Fail(nowTicks);
                    break;
            }
        }

        void Connect(long nowTicks)
        {
            try
            {
                m_Socket?.Dispose();
                m_Socket = new ClientWebSocket();
                m_Send = null;
                m_Connect = m_Socket.ConnectAsync(m_Uri, CancellationToken.None);
                m_Phase = Phase.Connecting;
            }
            catch (Exception)
            {
                Fail(nowTicks);
            }
        }

        void Fail(long nowTicks)
        {
            m_Phase = Phase.Waiting;
            m_RetryAt = nowTicks + (long)(m_BackoffMs * Stopwatch.Frequency / 1000);
            m_BackoffMs = Math.Min(MaxBackoffMs, m_BackoffMs * 2);
            try { m_Socket?.Abort(); } catch (Exception) { }
            m_Send = null;
        }

        public void Send(byte[] data, int length)
        {
            if (m_Phase != Phase.Open) return;
            if (m_Send != null && !m_Send.IsCompleted)
            {
                DroppedBatches++;
                SnapshotRequested = true;
                return;
            }
            if (length > m_SendBuffer.Length) m_SendBuffer = new byte[Math.Max(length, m_SendBuffer.Length * 2)];
            System.Buffer.BlockCopy(data, 0, m_SendBuffer, 0, length);
            try
            {
                m_Send = m_Socket.SendAsync(new ArraySegment<byte>(m_SendBuffer, 0, length), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception)
            {
                Fail(Stopwatch.GetTimestamp());
            }
        }

        async Task ReceiveLoop(ClientWebSocket socket)
        {
            var buffer = new byte[4096];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage)
                        HandleMessage(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            }
            catch (Exception)
            {
                // Socket closed or aborted; Tick notices the state and reconnects.
            }
        }

        // Minimal parse of {"subscribe":["me","match_info","events"]}; anything else is ignored.
        void HandleMessage(string message)
        {
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
            var socket = m_Socket;
            m_Socket = null;
            if (socket == null) return;
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    if (m_Send != null && !m_Send.IsCompleted) m_Send.Wait(200);
                    socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "shutdown", CancellationToken.None).Wait(200);
                }
            }
            catch (Exception) { }
            try { socket.Dispose(); } catch (Exception) { }
        }
    }
}
