namespace Focus.Howl
{
    public readonly struct EventId
    {
        public readonly ushort Value;
        public EventId(ushort value) { Value = value; }
        public bool IsValid => Value != 0;
    }

    public readonly struct KeyId
    {
        public readonly ushort Value;
        public KeyId(ushort value) { Value = value; }
        public bool IsValid => Value != 0;
    }

    /// <summary>How an event's A/B int field is written on the wire.</summary>
    public enum HowlField : byte { None, Int, String }

    /// <summary>How an event's X/Y/Z floats are written on the wire.</summary>
    public enum HowlVectorField : byte { None, Scalar, Vector }

    public enum HowlValueKind : byte { None, Float, Int, Bool, String, Vector3 }

    /// <summary>Fixed-size event record (40 bytes). String fields hold string table indices.</summary>
    public struct HowlEvent
    {
        public ushort Id;
        /// <summary>Stopwatch timestamp taken in Emit. See <see cref="Howl.TicksToSeconds"/>.</summary>
        public long Ticks;
        public int A, B;
        public float X, Y, Z;
    }

    public struct HowlValue
    {
        public HowlValueKind Kind;
        public int I;
        public float X, Y, Z;
    }

    public sealed class HowlEventDef
    {
        public string Name;
        public string AName;
        public HowlField AKind;
        public string BName;
        public HowlField BKind;
        public string XName;
        public HowlVectorField XKind;
    }

    public sealed class HowlKeyDef
    {
        public string Category;
        public string Key;
        public HowlValueKind Kind;
        public int CategoryIndex;
    }

    /// <summary>Bridge telemetry, published by the worker once per second (also sent as bridge.* info).</summary>
    public sealed class HowlStats
    {
        public float EventsPerSecond;
        public float BytesPerSecond;
        public float BatchesPerSecond;
        public long Emitted;
        public long DroppedEvents;
        public long DroppedBatches;
        public int RingHighWater;
        public int RingCapacity;
        public float SerializeUsAvg;
        public float SendUsAvg;
        public string WebSocket = "off";
        public string File = "off";
    }
}
