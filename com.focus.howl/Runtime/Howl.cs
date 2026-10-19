using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Focus.Howl
{
    /// <summary>
    /// The game-facing API. Emit and Set are the only calls the game pays for: a struct write into a preallocated
    /// ring, or a seqlock-guarded write into an info slot. Diffing, JSON and all I/O happen on the Howl worker thread.
    ///
    /// Threading: Register*, Emit and Set must be called from the main thread (single producer).
    /// No profiler markers per call: wrap a frame's batch of calls in a marker instead (see Howl.Calls in Tools).
    /// </summary>
    public static partial class Howl
    {
        public const int MaxKeys = 2048;
        public const int MaxEvents = 1024;
        public const int RecentCapacity = 256;

        // Registration tables: main thread writes, worker reads.
        static readonly HowlAppendList<HowlEventDef> s_Events = new HowlAppendList<HowlEventDef>();
        static readonly Dictionary<string, ushort> s_EventsByName = new Dictionary<string, ushort>();
        static readonly HowlAppendList<HowlKeyDef> s_Keys = new HowlAppendList<HowlKeyDef>();
        static readonly Dictionary<string, ushort> s_KeysByPath = new Dictionary<string, ushort>();
        static readonly HowlAppendList<string> s_Categories = new HowlAppendList<string>();
        static readonly Dictionary<string, int> s_CategoryIndex = new Dictionary<string, int>();
        static readonly HowlAppendList<string> s_Strings = new HowlAppendList<string>();
        static readonly Dictionary<string, int> s_StringIndex = new Dictionary<string, int>();

        // Info slots. Seq is even when stable and odd while being written (seqlock); version = seq / 2.
        static readonly HowlValue[] s_Values = new HowlValue[MaxKeys];
        static readonly int[] s_Seq = new int[MaxKeys];

        static HowlRing s_Ring;
        static HowlWorker s_Worker;

        static bool s_Active = true;
        // Active and someone is listening. Emit checks this one field; when false (dormant) it returns immediately.
        static volatile bool s_Live;
        static int s_Observers;
        static volatile bool s_SinkListening;
        static int s_Session;

        // Main-thread counters; the worker reads them for stats.
        static long s_Emitted;
        static long s_Dropped;

        static long s_BaseTicks = Stopwatch.GetTimestamp();
        static long s_BaseUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        static bool s_WarnedLimit;

        /// <summary>Master switch. Off: Emit and Set return after one bool check.</summary>
        public static bool Active
        {
            get => s_Active;
            set
            {
                s_Active = value;
                UpdateLive();
            }
        }

        /// <summary>True when events are being collected (active and observed by a sink or an in-process observer).</summary>
        public static bool IsLive => s_Live;
        public static bool IsRunning => s_Worker != null;
        /// <summary>Incremented on every reset (each Play session), so observers can re-register.</summary>
        public static int Session => s_Session;

        public static IReadOnlyList<HowlEventDef> Events => s_Events;
        public static IReadOnlyList<HowlKeyDef> Keys => s_Keys;
        public static IReadOnlyList<string> Strings => s_Strings;
        public static IReadOnlyList<string> Categories => s_Categories;

        public static long TotalEmitted => s_Emitted;
        public static long DroppedEvents => s_Dropped;
        public static HowlStats Stats => s_Worker?.Stats;
        internal static long BaseTicks => s_BaseTicks;
        internal static long BaseUnixMs => s_BaseUnixMs;
        internal static bool SinkListening
        {
            set
            {
                if (s_SinkListening == value) return;
                s_SinkListening = value;
                UpdateLive();
            }
        }

        public static double TicksToSeconds(long ticks) => (ticks - s_BaseTicks) / (double)Stopwatch.Frequency;

        // ---- Lifetime ----

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Reset();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoStart()
        {
            var settings = HowlSettings.Load();
            if (settings.AutoStart) Start(settings);
        }

        /// <summary>Stops the worker and clears every registration and value.</summary>
        public static void Reset()
        {
            Shutdown();
            s_Events.Reset();
            s_EventsByName.Clear();
            s_Keys.Reset();
            s_KeysByPath.Clear();
            s_Categories.Reset();
            s_CategoryIndex.Clear();
            s_Strings.Reset();
            s_StringIndex.Clear();
            Array.Clear(s_Values, 0, s_Values.Length);
            Array.Clear(s_Seq, 0, s_Seq.Length);
            s_Emitted = 0;
            s_Dropped = 0;
            s_Observers = 0;
            s_SinkListening = false;
            s_Active = true;
            s_WarnedLimit = false;
            s_Session++;
            UpdateLive();
        }

        /// <summary>Starts the worker thread and sinks. Called automatically before the first scene loads.</summary>
        public static void Start(HowlSettings settings)
        {
            if (s_Worker != null) return;
            s_BaseTicks = Stopwatch.GetTimestamp();
            s_BaseUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            s_Ring = new HowlRing(settings.RingCapacity);
            s_Worker = new HowlWorker(settings, s_Ring);
            s_Worker.Start();

            Application.quitting += Shutdown;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
#endif
            UpdateLive();
        }

        /// <summary>Drains what's queued, flushes sinks and stops the worker. Safe to call repeatedly.</summary>
        public static void Shutdown()
        {
            Application.quitting -= Shutdown;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
#endif
            var worker = s_Worker;
            s_Worker = null;
            UpdateLive();
            worker?.Stop();
        }

#if UNITY_EDITOR
        static void OnPlayModeChanged(UnityEditor.PlayModeStateChange change)
        {
            if (change == UnityEditor.PlayModeStateChange.ExitingPlayMode) Shutdown();
        }
#endif

        static void UpdateLive() => s_Live = s_Active && s_Worker != null && (s_Observers > 0 || s_SinkListening);

        /// <summary>An in-process consumer (e.g. a debug window) wants events even when no sink is connected.</summary>
        public static void AddObserver()
        {
            s_Observers++;
            UpdateLive();
        }

        public static void RemoveObserver()
        {
            if (s_Observers > 0) s_Observers--;
            UpdateLive();
        }

        /// <summary>Wakes the worker to send now instead of at its next tick (e.g. on match end).</summary>
        public static void Flush() => s_Worker?.Wake();

        /// <summary>Turns the JSONL file sink on or off at runtime.</summary>
        public static void SetFileSink(bool enabled) => s_Worker?.RequestFile(enabled);

        // ---- Registration (main thread; allocates; call at startup or rarely) ----

        public static EventId RegisterEvent(string name,
            string aName = null, HowlField aKind = HowlField.None,
            string bName = null, HowlField bKind = HowlField.None,
            string xName = null, HowlVectorField xKind = HowlVectorField.None)
        {
            if (s_EventsByName.TryGetValue(name, out var existing)) return new EventId(existing);
            if (s_Events.Count >= MaxEvents) return Limit("events", MaxEvents, new EventId(0));
            var id = (ushort)s_Events.Add(new HowlEventDef { Name = name, AName = aName, AKind = aKind, BName = bName, BKind = bKind, XName = xName, XKind = xKind });
            s_EventsByName.Add(name, id);
            return new EventId(id);
        }

        public static KeyId RegisterKey(string category, string key, HowlValueKind kind)
        {
            string path = category + "." + key;
            if (s_KeysByPath.TryGetValue(path, out var existing)) return new KeyId(existing);
            if (s_Keys.Count >= MaxKeys) return Limit("info keys", MaxKeys, new KeyId(0));
            if (!s_CategoryIndex.TryGetValue(category, out int categoryIndex))
            {
                categoryIndex = s_Categories.Add(category);
                s_CategoryIndex.Add(category, categoryIndex);
            }
            var id = (ushort)s_Keys.Add(new HowlKeyDef { Category = category, Key = key, Kind = kind, CategoryIndex = categoryIndex });
            s_KeysByPath.Add(path, id);
            return new KeyId(id);
        }

        /// <summary>Adds a string to the string table (deduplicated) and returns its index. Call rarely, never per frame.</summary>
        public static int RegisterString(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            if (s_StringIndex.TryGetValue(value, out var index)) return index;
            index = s_Strings.Add(value);
            s_StringIndex.Add(value, index);
            return index;
        }

        static T Limit<T>(string what, int max, T invalid)
        {
            if (!s_WarnedLimit) Debug.LogWarning($"[Howl] More than {max} {what} registered; extra registrations are ignored.");
            s_WarnedLimit = true;
            return invalid;
        }

        // ---- Hot path (main thread; no allocation, no locks, no I/O) ----

        public static void Emit(EventId id, int a = 0, int b = 0, float x = 0f, float y = 0f, float z = 0f)
        {
            if (!s_Live || id.Value == 0) return;
            var ring = s_Ring;
            long head = ring.Head;
            if (head - ring.CachedTail >= ring.Capacity)
            {
                ring.CachedTail = Volatile.Read(ref ring.Tail);
                if (head - ring.CachedTail >= ring.Capacity)
                {
                    s_Dropped++;
                    return;
                }
            }
            ref var e = ref ring.Buffer[head & ring.Mask];
            e.Id = id.Value;
            e.Ticks = Stopwatch.GetTimestamp();
            e.A = a;
            e.B = b;
            e.X = x;
            e.Y = y;
            e.Z = z;
            Volatile.Write(ref ring.Head, head + 1);
            s_Emitted++;
        }

        public static void Emit(EventId id, int a, int b, Vector3 v) => Emit(id, a, b, v.x, v.y, v.z);

        // Info is kept current whenever Howl is active (even with nobody listening), so a sink that connects later
        // gets the full state.
        public static void Set(KeyId key, float value) => Write(key, HowlValueKind.Float, 0, value, 0f, 0f);
        public static void Set(KeyId key, int value) => Write(key, HowlValueKind.Int, value, 0f, 0f, 0f);
        public static void Set(KeyId key, bool value) => Write(key, HowlValueKind.Bool, value ? 1 : 0, 0f, 0f, 0f);
        public static void Set(KeyId key, Vector3 value) => Write(key, HowlValueKind.Vector3, 0, value.x, value.y, value.z);
        public static void SetString(KeyId key, int stringIndex) => Write(key, HowlValueKind.String, stringIndex, 0f, 0f, 0f);
        /// <summary>Drops the key's value; consumers see it removed (GEP: info set to null).</summary>
        public static void Clear(KeyId key) => Write(key, HowlValueKind.None, 0, 0f, 0f, 0f);

        static void Write(KeyId key, HowlValueKind kind, int i, float x, float y, float z)
        {
            if (!s_Active || key.Value == 0) return;
            int k = key.Value;
            ref var v = ref s_Values[k];
            // Unchanged writes cost a compare, not a version bump: setting the same value every frame is free.
            if (v.Kind == kind && v.I == i && v.X == x && v.Y == y && v.Z == z) return;
            int seq = Interlocked.Increment(ref s_Seq[k]); // odd: write in progress (full fence)
            v.Kind = kind;
            v.I = i;
            v.X = x;
            v.Y = y;
            v.Z = z;
            Volatile.Write(ref s_Seq[k], seq + 1);        // even: stable
        }

        /// <summary>Consistent read of an info slot from any thread. False if a write kept racing it.</summary>
        internal static bool TryReadSlot(int key, out HowlValue value, out int seq)
        {
            for (int attempt = 0; attempt < 64; attempt++)
            {
                int s1 = Volatile.Read(ref s_Seq[key]);
                if ((s1 & 1) != 0)
                {
                    Thread.SpinWait(4);
                    continue;
                }
                value = s_Values[key];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref s_Seq[key]) == s1)
                {
                    seq = s1;
                    return true;
                }
            }
            value = default;
            seq = 0;
            return false;
        }

        // ---- Inspection (debug UI; may allocate) ----

        public static long GetEventCount(ushort id) => s_Worker?.GetEventCount(id) ?? 0;
        public static long HistoryTotal => s_Worker?.HistoryTotal ?? 0;

        public static HowlValue GetValue(ushort key)
        {
            TryReadSlot(key, out var value, out _);
            return value;
        }

        public static uint GetVersion(ushort key) => (uint)(Volatile.Read(ref s_Seq[key]) >> 1);

        public static string GetString(int index) => index >= 0 && index < s_Strings.Count ? s_Strings[index] : "?";

        /// <summary>Copies up to buffer.Length most recent events seen by the worker, newest first.</summary>
        public static int CopyRecent(HowlEvent[] buffer) => s_Worker?.CopyRecent(buffer) ?? 0;

        /// <summary>Clears the worker's recent-event history and per-event counts.</summary>
        public static void ClearHistory() => s_Worker?.RequestClearHistory();

        public static string FormatEvent(in HowlEvent e)
        {
            var def = e.Id < s_Events.Count ? s_Events[e.Id] : null;
            if (def == null) return "?";
            var sb = new System.Text.StringBuilder(def.Name);
            AppendField(sb, def.AName, def.AKind, e.A);
            AppendField(sb, def.BName, def.BKind, e.B);
            if (def.XKind == HowlVectorField.Scalar) sb.Append(' ').Append(def.XName).Append('=').Append(e.X.ToString("0.##"));
            else if (def.XKind == HowlVectorField.Vector) sb.Append(' ').Append(def.XName).Append("=(").Append(e.X.ToString("0.#")).Append(", ").Append(e.Y.ToString("0.#")).Append(", ").Append(e.Z.ToString("0.#")).Append(')');
            return sb.ToString();
        }

        public static string FormatValue(in HowlValue v)
        {
            switch (v.Kind)
            {
                case HowlValueKind.Float: return v.X.ToString("0.##");
                case HowlValueKind.Int: return v.I.ToString();
                case HowlValueKind.Bool: return v.I != 0 ? "true" : "false";
                case HowlValueKind.String: return GetString(v.I);
                case HowlValueKind.Vector3: return v.X.ToString("0.0") + "," + v.Y.ToString("0.0") + "," + v.Z.ToString("0.0");
                default: return "";
            }
        }

        static void AppendField(System.Text.StringBuilder sb, string name, HowlField kind, int value)
        {
            if (kind == HowlField.None) return;
            sb.Append(' ').Append(name).Append('=');
            if (kind == HowlField.String) sb.Append(GetString(value));
            else sb.Append(value);
        }
    }
}
