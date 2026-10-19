using System.Collections.Generic;
using UnityEngine;

namespace Focus.Howl
{
    // Phase 0 stub: a main-thread store so the event vocabulary and integrations can be built and watched now.
    // Phase 2 replaces the internals with the SPSC ring, versioned/seqlock info slots and the worker thread.
    // The public API (Register*/Emit/Set) is the one the real emitter keeps.
    public static partial class Howl
    {
        public const int RecentCapacity = 256;

        // No profiler markers in Emit/Set: a marker per call costs more than the call and, under Editor profiling,
        // allocates. Measure around a caller's batch instead (see HowlPerfRecorder's "Howl.Calls").

        // Index 0 is reserved so default(EventId)/default(KeyId) are invalid and string index 0 is "".
        static readonly List<HowlEventDef> s_Events = new List<HowlEventDef> { null };
        static readonly Dictionary<string, ushort> s_EventsByName = new Dictionary<string, ushort>();
        static readonly List<HowlKeyDef> s_Keys = new List<HowlKeyDef> { null };
        static readonly Dictionary<string, ushort> s_KeysByPath = new Dictionary<string, ushort>();
        static readonly List<string> s_Strings = new List<string> { "" };
        static readonly Dictionary<string, int> s_StringIndex = new Dictionary<string, int> { { "", 0 } };

        static HowlValue[] s_Values = new HowlValue[64];
        static uint[] s_Versions = new uint[64];
        static long[] s_EventCounts = new long[64];
        static readonly HowlEvent[] s_Recent = new HowlEvent[RecentCapacity];
        static int s_RecentNext;
        static long s_TotalEmitted;

        /// <summary>When false, Emit and Set return after one bool check.</summary>
        public static bool Active = true;

        public static IReadOnlyList<HowlEventDef> Events => s_Events;
        public static IReadOnlyList<HowlKeyDef> Keys => s_Keys;
        public static IReadOnlyList<string> Strings => s_Strings;
        public static long TotalEmitted => s_TotalEmitted;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void Reset()
        {
            s_Events.RemoveRange(1, s_Events.Count - 1);
            s_EventsByName.Clear();
            s_Keys.RemoveRange(1, s_Keys.Count - 1);
            s_KeysByPath.Clear();
            s_Strings.RemoveRange(1, s_Strings.Count - 1);
            s_StringIndex.Clear();
            s_StringIndex.Add("", 0);
            System.Array.Clear(s_Values, 0, s_Values.Length);
            System.Array.Clear(s_Versions, 0, s_Versions.Length);
            System.Array.Clear(s_EventCounts, 0, s_EventCounts.Length);
            System.Array.Clear(s_Recent, 0, s_Recent.Length);
            s_RecentNext = 0;
            s_TotalEmitted = 0;
            Active = true;
        }

        // ---- Registration (startup only; allocates) ----

        public static EventId RegisterEvent(string name,
            string aName = null, HowlField aKind = HowlField.None,
            string bName = null, HowlField bKind = HowlField.None,
            string xName = null, HowlVectorField xKind = HowlVectorField.None)
        {
            if (s_EventsByName.TryGetValue(name, out var existing)) return new EventId(existing);
            var id = (ushort)s_Events.Count;
            s_Events.Add(new HowlEventDef { Name = name, AName = aName, AKind = aKind, BName = bName, BKind = bKind, XName = xName, XKind = xKind });
            s_EventsByName.Add(name, id);
            if (id >= s_EventCounts.Length) System.Array.Resize(ref s_EventCounts, s_EventCounts.Length * 2);
            return new EventId(id);
        }

        public static KeyId RegisterKey(string category, string key, HowlValueKind kind)
        {
            string path = category + "." + key;
            if (s_KeysByPath.TryGetValue(path, out var existing)) return new KeyId(existing);
            var id = (ushort)s_Keys.Count;
            s_Keys.Add(new HowlKeyDef { Category = category, Key = key, Kind = kind });
            s_KeysByPath.Add(path, id);
            if (id >= s_Values.Length)
            {
                System.Array.Resize(ref s_Values, s_Values.Length * 2);
                System.Array.Resize(ref s_Versions, s_Versions.Length * 2);
            }
            return new KeyId(id);
        }

        /// <summary>Adds a string to the string table (deduplicated) and returns its index. Call rarely, never per frame.</summary>
        public static int RegisterString(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            if (s_StringIndex.TryGetValue(value, out var index)) return index;
            index = s_Strings.Count;
            s_Strings.Add(value);
            s_StringIndex.Add(value, index);
            return index;
        }

        // ---- Hot path (no allocation) ----

        public static void Emit(EventId id, int a = 0, int b = 0, float x = 0f, float y = 0f, float z = 0f)
        {
            if (!Active || !id.IsValid) return;
            ref var e = ref s_Recent[s_RecentNext];
            e.Id = id.Value;
            e.Time = Time.realtimeSinceStartupAsDouble;
            e.A = a;
            e.B = b;
            e.X = x;
            e.Y = y;
            e.Z = z;
            s_RecentNext = (s_RecentNext + 1) & (RecentCapacity - 1);
            s_EventCounts[id.Value]++;
            s_TotalEmitted++;
        }

        public static void Emit(EventId id, int a, int b, Vector3 v) => Emit(id, a, b, v.x, v.y, v.z);

        public static void Set(KeyId key, float value) => Write(key, HowlValueKind.Float, 0, value, 0f, 0f);
        public static void Set(KeyId key, int value) => Write(key, HowlValueKind.Int, value, 0f, 0f, 0f);
        public static void Set(KeyId key, bool value) => Write(key, HowlValueKind.Bool, value ? 1 : 0, 0f, 0f, 0f);
        public static void Set(KeyId key, Vector3 value) => Write(key, HowlValueKind.Vector3, 0, value.x, value.y, value.z);
        public static void SetString(KeyId key, int stringIndex) => Write(key, HowlValueKind.String, stringIndex, 0f, 0f, 0f);
        /// <summary>Drops the key's value; consumers see it removed (GEP: info set to null).</summary>
        public static void Clear(KeyId key) => Write(key, HowlValueKind.None, 0, 0f, 0f, 0f);

        static void Write(KeyId key, HowlValueKind kind, int i, float x, float y, float z)
        {
            if (!Active || !key.IsValid) return;
            ref var v = ref s_Values[key.Value];
            v.Kind = kind;
            v.I = i;
            v.X = x;
            v.Y = y;
            v.Z = z;
            s_Versions[key.Value]++;
        }

        // ---- Inspection (editor/debug UI; may allocate) ----

        /// <summary>Clears recent events and counts. Registrations and info values are kept.</summary>
        public static void ClearHistory()
        {
            System.Array.Clear(s_EventCounts, 0, s_EventCounts.Length);
            System.Array.Clear(s_Recent, 0, s_Recent.Length);
            s_RecentNext = 0;
            s_TotalEmitted = 0;
        }

        public static long GetEventCount(ushort id) => id < s_EventCounts.Length ? s_EventCounts[id] : 0;
        public static HowlValue GetValue(ushort key) => s_Values[key];
        public static uint GetVersion(ushort key) => s_Versions[key];
        public static string GetString(int index) => index >= 0 && index < s_Strings.Count ? s_Strings[index] : "?";

        /// <summary>Copies up to <paramref name="buffer"/>.Length most recent events, newest first. Returns the count.</summary>
        public static int CopyRecent(HowlEvent[] buffer)
        {
            int available = (int)System.Math.Min(s_TotalEmitted, RecentCapacity);
            int count = System.Math.Min(available, buffer.Length);
            for (int i = 0; i < count; i++)
                buffer[i] = s_Recent[(s_RecentNext - 1 - i) & (RecentCapacity - 1)];
            return count;
        }

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
