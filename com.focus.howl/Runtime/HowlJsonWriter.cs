using System;
using System.Text;

namespace Focus.Howl
{
    /// <summary>Allocation-free UTF-8 JSON building into a reusable buffer (worker thread only).</summary>
    sealed class HowlJsonWriter
    {
        public byte[] Buffer;
        public int Length;

        static readonly long[] Pow10 = { 1, 10, 100, 1000, 10000, 100000, 1000000 };

        public HowlJsonWriter(int capacity) { Buffer = new byte[capacity]; }

        public void Clear() => Length = 0;

        void Ensure(int extra)
        {
            if (Length + extra <= Buffer.Length) return;
            Array.Resize(ref Buffer, Math.Max(Buffer.Length * 2, Length + extra));
        }

        public void Byte(byte b)
        {
            Ensure(1);
            Buffer[Length++] = b;
        }

        public void Raw(byte[] bytes)
        {
            Ensure(bytes.Length);
            System.Buffer.BlockCopy(bytes, 0, Buffer, Length, bytes.Length);
            Length += bytes.Length;
        }

        public void Long(long value)
        {
            Ensure(20);
            if (value < 0)
            {
                Buffer[Length++] = (byte)'-';
                value = -value;
            }
            int start = Length;
            do
            {
                Buffer[Length++] = (byte)('0' + value % 10);
                value /= 10;
            } while (value > 0);
            Array.Reverse(Buffer, start, Length - start);
        }

        /// <summary>Fixed-point with up to <paramref name="decimals"/> digits, trailing zeros trimmed (74.500 -> 74.5).</summary>
        public void Number(double value, int decimals)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 9e12)
            {
                Byte((byte)'0');
                return;
            }
            long scale = Pow10[decimals];
            long scaled = (long)Math.Round(Math.Abs(value) * scale);
            if (value < 0 && scaled != 0) Byte((byte)'-');
            Long(scaled / scale);
            long frac = scaled % scale;
            if (frac == 0) return;
            int digits = decimals;
            while (frac % 10 == 0)
            {
                frac /= 10;
                digits--;
            }
            Ensure(digits + 1);
            Buffer[Length++] = (byte)'.';
            for (int i = digits - 1; i >= 0; i--)
            {
                Buffer[Length + i] = (byte)('0' + frac % 10);
                frac /= 10;
            }
            Length += digits;
        }

        /// <summary>
        /// UTF-8 bytes of <paramref name="s"/> JSON-escaped <paramref name="levels"/> times (2 = for text inside a JSON
        /// string that is itself inside JSON, as in GEP's event "data"). No surrounding quotes. Allocates; cache it.
        /// </summary>
        public static byte[] Escaped(string s, int levels)
        {
            for (int i = 0; i < levels; i++) s = EscapeOnce(s ?? "");
            return Encoding.UTF8.GetBytes(s ?? "");
        }

        public static byte[] Ascii(string s) => Encoding.UTF8.GetBytes(s);

        static string EscapeOnce(string s)
        {
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                string rep = c == '"' ? "\\\"" : c == '\\' ? "\\\\" : c < 0x20 ? "\\u" + ((int)c).ToString("x4") : null;
                if (rep == null)
                {
                    sb?.Append(c);
                    continue;
                }
                if (sb == null) sb = new StringBuilder(s, 0, i, s.Length + 8);
                sb.Append(rep);
            }
            return sb == null ? s : sb.ToString();
        }
    }
}
