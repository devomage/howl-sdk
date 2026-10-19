using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Focus.Howl
{
    /// <summary>
    /// Append-only list with one writer (main thread) and lock-free readers (worker). Growth copies to a new array
    /// and publishes it before the count, so a reader that sees count N always sees an array holding N items.
    /// Index 0 is reserved.
    /// </summary>
    sealed class HowlAppendList<T> : IReadOnlyList<T>
    {
        T[] m_Items = new T[64];
        int m_Count = 1;

        public int Count => Volatile.Read(ref m_Count);

        public T this[int index] => Volatile.Read(ref m_Items)[index];

        public int Add(T item)
        {
            int index = m_Count;
            if (index == m_Items.Length)
            {
                var grown = new T[m_Items.Length * 2];
                System.Array.Copy(m_Items, grown, index);
                grown[index] = item;
                Volatile.Write(ref m_Items, grown);
            }
            else
            {
                m_Items[index] = item;
            }
            Volatile.Write(ref m_Count, index + 1);
            return index;
        }

        /// <summary>Only call while no reader is running.</summary>
        public void Reset()
        {
            m_Items = new T[64];
            m_Count = 1;
        }

        public IEnumerator<T> GetEnumerator()
        {
            int count = Count;
            for (int i = 0; i < count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
