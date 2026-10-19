using System.Threading;

namespace Focus.Howl
{
    /// <summary>
    /// Single-producer (main thread) / single-consumer (worker) lock-free ring of events.
    /// When full, the producer drops and counts: it never waits and the ring never grows.
    /// </summary>
    sealed class HowlRing
    {
        public readonly HowlEvent[] Buffer;
        public readonly int Mask;
        public readonly int Capacity;

        // Producer-owned. Head is published with Volatile.Write after the slot is filled.
        public long Head;
        public long CachedTail;

        // Consumer-owned, on its own cache line so the two threads don't contend.
#pragma warning disable 169
        long m_Pad1, m_Pad2, m_Pad3, m_Pad4, m_Pad5, m_Pad6, m_Pad7;
#pragma warning restore 169
        public long Tail;

        public HowlRing(int capacity)
        {
            int size = 1;
            while (size < capacity) size <<= 1;
            Buffer = new HowlEvent[size];
            Capacity = size;
            Mask = size - 1;
        }

        public int Count => (int)(Volatile.Read(ref Head) - Volatile.Read(ref Tail));

        /// <summary>Consumer: copies up to max events into dst and frees their slots.</summary>
        public int Read(HowlEvent[] dst, int max)
        {
            long tail = Tail;
            long head = Volatile.Read(ref Head);
            int n = (int)System.Math.Min(head - tail, max);
            for (int i = 0; i < n; i++) dst[i] = Buffer[(tail + i) & Mask];
            Volatile.Write(ref Tail, tail + n);
            return n;
        }
    }
}
