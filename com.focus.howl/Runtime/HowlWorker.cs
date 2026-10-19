using System;
using System.Diagnostics;
using System.Threading;

namespace Focus.Howl
{
    /// <summary>
    /// The Howl worker: one dedicated background thread (below-normal priority). Each tick (default 8 ms, or sooner on
    /// Flush) it drains the ring, diffs info slots, serializes one GEP-shaped NDJSON batch and hands it to the sinks.
    /// Every failure stays here: it is counted, logged once, and never reaches gameplay.
    /// </summary>
    sealed class HowlWorker
    {
        const int MaxBatch = 4096;
        static readonly long Frequency = Stopwatch.Frequency;

        static readonly byte[] KOpen = HowlJsonWriter.Ascii("{\"v\":1,\"seq\":");
        static readonly byte[] KT = HowlJsonWriter.Ascii(",\"t\":");
        static readonly byte[] KT0 = HowlJsonWriter.Ascii(",\"t0\":");
        static readonly byte[] KWall = HowlJsonWriter.Ascii(",\"wall\":");
        static readonly byte[] KInfo = HowlJsonWriter.Ascii(",\"info\":{");
        static readonly byte[] KEvents = HowlJsonWriter.Ascii(",\"events\":[");
        static readonly byte[] KEvName = HowlJsonWriter.Ascii("{\"name\":\"");
        static readonly byte[] KEvT = HowlJsonWriter.Ascii("\",\"t\":");
        static readonly byte[] KEvData = HowlJsonWriter.Ascii(",\"data\":\"{");
        static readonly byte[] KEvDataEnd = HowlJsonWriter.Ascii("}\"");
        static readonly byte[] KCatOpen = HowlJsonWriter.Ascii("\":{");
        static readonly byte[] KKeyColon = HowlJsonWriter.Ascii("\":");
        static readonly byte[] KNull = HowlJsonWriter.Ascii("null");
        static readonly byte[] KTrue = HowlJsonWriter.Ascii("\"true\"");
        static readonly byte[] KFalse = HowlJsonWriter.Ascii("\"false\"");
        static readonly byte[] KQ2 = HowlJsonWriter.Ascii("\\\"");          // \"  (a quote inside the data string)
        static readonly byte[] KQ2Sep = HowlJsonWriter.Ascii("\\\":\\\"");  // \":\"
        static readonly byte[] KBridge = HowlJsonWriter.Ascii("\"bridge\":{");

        sealed class EventBytes
        {
            public byte[] Name, A, B, X;
        }

        readonly HowlSettings m_Settings;
        readonly HowlRing m_Ring;
        readonly AutoResetEvent m_Wake = new AutoResetEvent(false);
        readonly HowlEvent[] m_Batch = new HowlEvent[MaxBatch];
        readonly HowlJsonWriter m_Json = new HowlJsonWriter(64 * 1024);
        readonly int[] m_LastSentSeq = new int[Howl.MaxKeys];
        readonly int[] m_Changed = new int[Howl.MaxKeys];
        readonly HowlValue[] m_ChangedValues = new HowlValue[Howl.MaxKeys];

        Thread m_Thread;
        volatile bool m_Stop;
        HowlWebSocketSink m_Ws;
        HowlFileSink m_File;
        int m_FileRequest = -1;
        volatile bool m_ClearRequested;
        bool m_SnapshotPending;
        bool m_LoggedError;

        // Worker-only caches of escaped UTF-8 bytes.
        byte[][] m_Str1 = new byte[256][];
        byte[][] m_Str2 = new byte[256][];
        readonly EventBytes[] m_EventBytes = new EventBytes[Howl.MaxEvents];
        readonly byte[][] m_KeyNames = new byte[Howl.MaxKeys][];
        byte[][] m_CategoryNames = new byte[32][];
        bool[] m_CategoryAllowed = new bool[32];
        int m_CategoryAllowedCount = -1;
        HowlSubscription m_Subscription;
        bool m_EventsAllowed = true;
        bool m_BridgeAllowed = true;

        // Debug history for in-process views (read without locks; a torn read only affects a debug display).
        readonly long[] m_EventCounts = new long[Howl.MaxEvents];
        readonly HowlEvent[] m_Recent = new HowlEvent[Howl.RecentCapacity];
        int m_RecentNext;
        long m_HistoryTotal;

        // Stats window.
        long m_Seq;
        long m_StatsStart, m_NextStats;
        long m_WinEvents, m_WinBytes, m_WinBatches, m_WinSerializeTicks, m_WinSendTicks;
        int m_HighWater;
        volatile HowlStats m_Stats = new HowlStats();

        public HowlStats Stats => m_Stats;
        public long HistoryTotal => Volatile.Read(ref m_HistoryTotal);

        public HowlWorker(HowlSettings settings, HowlRing ring)
        {
            m_Settings = settings;
            m_Ring = ring;
        }

        public void Start()
        {
            m_Thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Howl Worker" };
            m_Thread.Start();
        }

        public void Stop()
        {
            m_Stop = true;
            m_Wake.Set();
            m_Thread?.Join(2000);
        }

        public void Wake() => m_Wake.Set();
        public void RequestFile(bool enabled) => Volatile.Write(ref m_FileRequest, enabled ? 1 : 0);
        public void RequestClearHistory() => m_ClearRequested = true;
        public long GetEventCount(ushort id) => id < m_EventCounts.Length ? Volatile.Read(ref m_EventCounts[id]) : 0;

        public int CopyRecent(HowlEvent[] buffer)
        {
            int available = (int)Math.Min(HistoryTotal, Howl.RecentCapacity);
            int count = Math.Min(available, buffer.Length);
            int next = Volatile.Read(ref m_RecentNext);
            for (int i = 0; i < count; i++) buffer[i] = m_Recent[(next - 1 - i) & (Howl.RecentCapacity - 1)];
            return count;
        }

        void Run()
        {
            try
            {
                if (m_Settings.WebSocket) m_Ws = new HowlWebSocketSink(m_Settings.WebSocketUrl);
                if (m_Settings.FileLog) OpenFile();
                m_StatsStart = Stopwatch.GetTimestamp();
                m_NextStats = m_StatsStart + Frequency;
                while (!m_Stop)
                {
                    m_Wake.WaitOne(m_Settings.TickMs);
                    SafeTick();
                }
                SafeTick(); // final drain
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
            finally
            {
                Howl.SinkListening = false;
                try { m_File?.Dispose(); } catch (Exception) { }
                try { m_Ws?.Dispose(); } catch (Exception) { }
            }
        }

        void SafeTick()
        {
            try
            {
                Tick(Stopwatch.GetTimestamp());
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        void LogOnce(Exception e)
        {
            if (m_LoggedError) return;
            m_LoggedError = true;
            UnityEngine.Debug.LogWarning("[Howl] Worker error (bridge keeps running, further errors are silent): " + e);
        }

        void OpenFile()
        {
            try
            {
                m_File = new HowlFileSink(m_Settings.ResolvedFileDirectory);
                m_SnapshotPending = true;
            }
            catch (Exception e)
            {
                m_File = null;
                LogOnce(e);
            }
        }

        void Tick(long now)
        {
            int fileRequest = Interlocked.Exchange(ref m_FileRequest, -1);
            if (fileRequest == 1 && m_File == null) OpenFile();
            else if (fileRequest == 0 && m_File != null)
            {
                m_File.Dispose();
                m_File = null;
            }
            if (m_ClearRequested)
            {
                m_ClearRequested = false;
                Array.Clear(m_EventCounts, 0, m_EventCounts.Length);
                Volatile.Write(ref m_HistoryTotal, 0);
            }

            m_Ws?.Tick(now);
            m_File?.Tick(now);
            bool listening = (m_Ws != null && m_Ws.IsListening) || (m_File != null && m_File.IsListening);
            Howl.SinkListening = listening;

            int pending = m_Ring.Count;
            if (pending > m_HighWater) m_HighWater = pending;

            while (true)
            {
                int n = m_Ring.Read(m_Batch, MaxBatch);
                RecordHistory(n);
                m_WinEvents += n;

                bool stats = now >= m_NextStats;
                if (stats) PublishStats(now);

                if (listening)
                {
                    if (m_Ws != null && m_Ws.SnapshotRequested)
                    {
                        m_Ws.SnapshotRequested = false;
                        m_SnapshotPending = true;
                    }
                    UpdateSubscription();
                    int changed = CollectInfo(m_SnapshotPending);
                    m_SnapshotPending = false;

                    int events = m_EventsAllowed ? n : 0;
                    if (events > 0 || changed > 0 || (stats && m_BridgeAllowed))
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        Serialize(events, changed, stats && m_BridgeAllowed, now);
                        long t1 = Stopwatch.GetTimestamp();
                        m_Ws?.Send(m_Json.Buffer, m_Json.Length);
                        m_File?.Send(m_Json.Buffer, m_Json.Length);
                        long t2 = Stopwatch.GetTimestamp();
                        m_WinSerializeTicks += t1 - t0;
                        m_WinSendTicks += t2 - t1;
                        m_WinBytes += m_Json.Length;
                        m_WinBatches++;
                    }
                }

                if (n < MaxBatch) break;
                now = Stopwatch.GetTimestamp();
            }
        }

        void RecordHistory(int n)
        {
            for (int i = 0; i < n; i++)
            {
                ref var e = ref m_Batch[i];
                if (e.Id < m_EventCounts.Length) m_EventCounts[e.Id]++;
                m_Recent[m_RecentNext] = e;
                m_RecentNext = (m_RecentNext + 1) & (Howl.RecentCapacity - 1);
            }
            if (n > 0) Volatile.Write(ref m_HistoryTotal, m_HistoryTotal + n);
        }

        int CollectInfo(bool snapshot)
        {
            int n = 0;
            int count = Howl.Keys.Count;
            for (int k = 1; k < count; k++)
            {
                if (!Howl.TryReadSlot(k, out var value, out int seq)) continue;
                if (seq == 0) continue;                                   // never set
                if (!snapshot && seq == m_LastSentSeq[k]) continue;       // unchanged since last send
                m_LastSentSeq[k] = seq;
                m_ChangedValues[k] = value;
                m_Changed[n++] = k;
            }
            return n;
        }

        void UpdateSubscription()
        {
            var subscription = m_Ws?.Subscription;
            int categories = Howl.Categories.Count;
            if (subscription == m_Subscription && categories == m_CategoryAllowedCount) return;
            m_Subscription = subscription;
            m_CategoryAllowedCount = categories;
            if (m_CategoryAllowed.Length < categories) m_CategoryAllowed = new bool[categories * 2];
            for (int c = 1; c < categories; c++)
                m_CategoryAllowed[c] = subscription == null || subscription.Categories.Contains(Howl.Categories[c]);
            m_EventsAllowed = subscription == null || subscription.Events;
            m_BridgeAllowed = subscription == null || subscription.Categories.Contains("bridge");
        }

        long UnixMs(long ticks) => Howl.BaseUnixMs + (ticks - Howl.BaseTicks) * 1000 / Frequency;

        // ---- Serialization: {"v":1,"seq":n,"t":s,"t0":ms,"wall":ms,"info":{...},"events":[...]}\n ----

        void Serialize(int events, int changed, bool stats, long now)
        {
            var w = m_Json;
            w.Clear();
            w.Raw(KOpen);
            w.Long(++m_Seq);
            w.Raw(KT);
            w.Number(Howl.TicksToSeconds(now), 3);
            w.Raw(KT0);
            w.Long(Howl.BaseUnixMs);
            w.Raw(KWall);
            w.Long(UnixMs(now));

            if (changed > 0 || stats)
            {
                w.Raw(KInfo);
                bool firstCategory = true;
                var keys = Howl.Keys;
                int categories = m_CategoryAllowedCount;
                for (int c = 1; c < categories; c++)
                {
                    if (!m_CategoryAllowed[c]) continue;
                    bool open = false;
                    for (int j = 0; j < changed; j++)
                    {
                        int k = m_Changed[j];
                        var def = keys[k];
                        if (def.CategoryIndex != c) continue;
                        if (!open)
                        {
                            if (!firstCategory) w.Byte((byte)',');
                            firstCategory = false;
                            w.Byte((byte)'"');
                            w.Raw(CategoryName(c));
                            w.Raw(KCatOpen);
                            open = true;
                        }
                        else
                        {
                            w.Byte((byte)',');
                        }
                        w.Byte((byte)'"');
                        w.Raw(KeyName(k, def));
                        w.Raw(KKeyColon);
                        WriteValue(m_ChangedValues[k]);
                    }
                    if (open) w.Byte((byte)'}');
                }
                if (stats)
                {
                    if (!firstCategory) w.Byte((byte)',');
                    WriteBridgeStats();
                }
                w.Byte((byte)'}');
            }

            if (events > 0)
            {
                w.Raw(KEvents);
                for (int i = 0; i < events; i++)
                {
                    if (i > 0) w.Byte((byte)',');
                    WriteEvent(m_Batch[i]);
                }
                w.Byte((byte)']');
            }

            w.Byte((byte)'}');
            w.Byte((byte)'\n');
        }

        void WriteValue(in HowlValue v)
        {
            var w = m_Json;
            switch (v.Kind)
            {
                case HowlValueKind.None:
                    w.Raw(KNull);
                    return;
                case HowlValueKind.Bool:
                    w.Raw(v.I != 0 ? KTrue : KFalse);
                    return;
            }
            w.Byte((byte)'"');
            switch (v.Kind)
            {
                case HowlValueKind.Float: w.Number(v.X, 3); break;
                case HowlValueKind.Int: w.Long(v.I); break;
                case HowlValueKind.String: w.Raw(Str(v.I, 1)); break;
                case HowlValueKind.Vector3: WriteVector(v.X, v.Y, v.Z); break;
            }
            w.Byte((byte)'"');
        }

        void WriteVector(float x, float y, float z)
        {
            var w = m_Json;
            w.Number(x, 2);
            w.Byte((byte)',');
            w.Number(y, 2);
            w.Byte((byte)',');
            w.Number(z, 2);
        }

        void WriteEvent(in HowlEvent e)
        {
            var w = m_Json;
            var def = Howl.Events[e.Id];
            var bytes = EventNames(e.Id, def);
            w.Raw(KEvName);
            w.Raw(bytes.Name);
            w.Raw(KEvT);
            w.Number(Howl.TicksToSeconds(e.Ticks), 4);
            if (def.AKind != HowlField.None || def.BKind != HowlField.None || def.XKind != HowlVectorField.None)
            {
                w.Raw(KEvData);
                bool first = true;
                if (def.AKind != HowlField.None) WriteDataField(ref first, bytes.A, def.AKind, e.A);
                if (def.BKind != HowlField.None) WriteDataField(ref first, bytes.B, def.BKind, e.B);
                if (def.XKind != HowlVectorField.None)
                {
                    BeginDataField(ref first, bytes.X);
                    if (def.XKind == HowlVectorField.Scalar) w.Number(e.X, 3);
                    else WriteVector(e.X, e.Y, e.Z);
                    w.Raw(KQ2);
                }
                w.Raw(KEvDataEnd);
            }
            w.Byte((byte)'}');
        }

        void BeginDataField(ref bool first, byte[] name)
        {
            var w = m_Json;
            if (!first) w.Byte((byte)',');
            first = false;
            w.Raw(KQ2);
            w.Raw(name);
            w.Raw(KQ2Sep);
        }

        void WriteDataField(ref bool first, byte[] name, HowlField kind, int value)
        {
            BeginDataField(ref first, name);
            if (kind == HowlField.String) m_Json.Raw(Str(value, 2));
            else m_Json.Long(value);
            m_Json.Raw(KQ2);
        }

        void WriteBridgeStats()
        {
            var s = m_Stats;
            var w = m_Json;
            w.Raw(KBridge);
            StatNumber("events_per_s", s.EventsPerSecond, true);
            StatNumber("bytes_per_s", s.BytesPerSecond, false);
            StatNumber("batches_per_s", s.BatchesPerSecond, false);
            StatNumber("emitted", s.Emitted, false);
            StatNumber("dropped", s.DroppedEvents, false);
            StatNumber("dropped_batches", s.DroppedBatches, false);
            StatNumber("ring_high_water", s.RingHighWater, false);
            StatNumber("serialize_us", s.SerializeUsAvg, false);
            StatNumber("send_us", s.SendUsAvg, false);
            StatString("ws", s.WebSocket);
            StatString("file", s.File);
            w.Byte((byte)'}');
        }

        static readonly System.Collections.Generic.Dictionary<string, byte[]> s_StatKeys = new System.Collections.Generic.Dictionary<string, byte[]>();

        static byte[] StatKey(string name)
        {
            if (!s_StatKeys.TryGetValue(name, out var bytes)) s_StatKeys[name] = bytes = HowlJsonWriter.Ascii("\"" + name + "\":\"");
            return bytes;
        }

        void StatNumber(string name, double value, bool first)
        {
            if (!first) m_Json.Byte((byte)',');
            m_Json.Raw(StatKey(name));
            m_Json.Number(value, 1);
            m_Json.Byte((byte)'"');
        }

        static readonly System.Collections.Generic.Dictionary<string, byte[]> s_StatValues = new System.Collections.Generic.Dictionary<string, byte[]>();

        void StatString(string name, string value)
        {
            if (!s_StatValues.TryGetValue(value, out var bytes)) s_StatValues[value] = bytes = HowlJsonWriter.Escaped(value, 1);
            m_Json.Byte((byte)',');
            m_Json.Raw(StatKey(name));
            m_Json.Raw(bytes);
            m_Json.Byte((byte)'"');
        }

        void PublishStats(long now)
        {
            double seconds = Math.Max(1e-3, (now - m_StatsStart) / (double)Frequency);
            double tickToUs = 1e6 / Frequency;
            m_Stats = new HowlStats
            {
                EventsPerSecond = (float)(m_WinEvents / seconds),
                BytesPerSecond = (float)(m_WinBytes / seconds),
                BatchesPerSecond = (float)(m_WinBatches / seconds),
                Emitted = Howl.TotalEmitted,
                DroppedEvents = Howl.DroppedEvents,
                DroppedBatches = m_Ws?.DroppedBatches ?? 0,
                RingHighWater = m_HighWater,
                RingCapacity = m_Ring.Capacity,
                SerializeUsAvg = m_WinBatches > 0 ? (float)(m_WinSerializeTicks * tickToUs / m_WinBatches) : 0f,
                SendUsAvg = m_WinBatches > 0 ? (float)(m_WinSendTicks * tickToUs / m_WinBatches) : 0f,
                WebSocket = m_Ws?.State ?? "off",
                File = m_File != null ? "on" : "off",
            };
            m_WinEvents = m_WinBytes = m_WinBatches = m_WinSerializeTicks = m_WinSendTicks = 0;
            m_HighWater = 0;
            m_StatsStart = now;
            m_NextStats = now + Frequency;
        }

        // ---- Cached escaped bytes ----

        byte[] Str(int index, int level)
        {
            ref var cache = ref (level == 1 ? ref m_Str1 : ref m_Str2);
            if (index >= cache.Length) Array.Resize(ref cache, Math.Max(cache.Length * 2, index + 1));
            return cache[index] ?? (cache[index] = HowlJsonWriter.Escaped(Howl.GetString(index), level));
        }

        EventBytes EventNames(int id, HowlEventDef def)
        {
            var bytes = m_EventBytes[id];
            if (bytes != null) return bytes;
            bytes = new EventBytes
            {
                Name = HowlJsonWriter.Escaped(def.Name, 1),
                A = HowlJsonWriter.Escaped(def.AName, 2),
                B = HowlJsonWriter.Escaped(def.BName, 2),
                X = HowlJsonWriter.Escaped(def.XName, 2),
            };
            m_EventBytes[id] = bytes;
            return bytes;
        }

        byte[] KeyName(int k, HowlKeyDef def) => m_KeyNames[k] ?? (m_KeyNames[k] = HowlJsonWriter.Escaped(def.Key, 1));

        byte[] CategoryName(int c)
        {
            if (c >= m_CategoryNames.Length) Array.Resize(ref m_CategoryNames, Math.Max(m_CategoryNames.Length * 2, c + 1));
            return m_CategoryNames[c] ?? (m_CategoryNames[c] = HowlJsonWriter.Escaped(Howl.Categories[c], 1));
        }
    }
}
