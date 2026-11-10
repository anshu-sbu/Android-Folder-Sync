// Minimal QR code encoder (byte mode, error correction level M, versions 1-6).
// Enough for a pairing link of up to ~100 characters. C# 5.
using System;
using System.Collections.Generic;
using System.Text;

namespace PhoneSync
{
    static class Qr
    {
        static readonly int[] DataCw = { 0, 16, 28, 44, 64, 86, 108 };
        static readonly int[] EcPerBlock = { 0, 10, 16, 26, 18, 24, 16 };
        static readonly int[] Blocks = { 0, 1, 1, 1, 2, 2, 4 };

        /// <summary>Returns the module matrix, [row, column], true = dark.</summary>
        public static bool[,] Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int ver = 0;
            for (int v = 1; v <= 6; v++) if (data.Length + 2 <= DataCw[v]) { ver = v; break; }
            if (ver == 0) throw new ArgumentException("Text too long for the QR code.");

            // ---- data bits
            var bits = new List<bool>();
            Append(bits, 4, 4);                  // byte mode
            Append(bits, data.Length, 8);
            foreach (byte b in data) Append(bits, b, 8);
            int cap = DataCw[ver] * 8;
            for (int i = 0; i < 4 && bits.Count < cap; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);
            for (int pad = 0xEC; bits.Count < cap; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);
            var cw = new byte[DataCw[ver]];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) cw[i >> 3] |= (byte)(0x80 >> (i & 7));

            // ---- error correction + interleave
            int nb = Blocks[ver], per = DataCw[ver] / nb, ecn = EcPerBlock[ver];
            var divisor = RsDivisor(ecn);
            var dataBlocks = new byte[nb][];
            var ecBlocks = new byte[nb][];
            for (int b = 0; b < nb; b++)
            {
                dataBlocks[b] = new byte[per];
                Array.Copy(cw, b * per, dataBlocks[b], 0, per);
                ecBlocks[b] = RsRemainder(dataBlocks[b], divisor);
            }
            var all = new List<byte>();
            for (int i = 0; i < per; i++) for (int b = 0; b < nb; b++) all.Add(dataBlocks[b][i]);
            for (int i = 0; i < ecn; i++) for (int b = 0; b < nb; b++) all.Add(ecBlocks[b][i]);

            // ---- matrix
            int size = 17 + 4 * ver;
            var mod = new bool[size, size];      // [y, x]
            var fn = new bool[size, size];
            for (int i = 0; i < size; i++) { SetFn(mod, fn, 6, i, i % 2 == 0); SetFn(mod, fn, i, 6, i % 2 == 0); }
            Finder(mod, fn, 3, 3); Finder(mod, fn, size - 4, 3); Finder(mod, fn, 3, size - 4);
            if (ver >= 2) { int a = 4 * ver + 10; Align(mod, fn, a, a); }
            DrawFormat(mod, fn, 0);              // reserve the format areas

            int idx = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!fn[y, x] && idx < all.Count * 8)
                        {
                            mod[y, x] = ((all[idx >> 3] >> (7 - (idx & 7))) & 1) != 0;
                            idx++;
                        }
                    }
            }

            int best = 0, bestPenalty = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                Mask(mod, fn, m);
                DrawFormat(mod, fn, m);
                int pen = Penalty(mod, size);
                if (pen < bestPenalty) { bestPenalty = pen; best = m; }
                Mask(mod, fn, m);                // undo
            }
            Mask(mod, fn, best);
            DrawFormat(mod, fn, best);
            return mod;
        }

        static void Append(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        static void SetFn(bool[,] mod, bool[,] fn, int x, int y, bool dark)
        {
            mod[y, x] = dark;
            fn[y, x] = true;
        }

        static void Finder(bool[,] mod, bool[,] fn, int cx, int cy)
        {
            int size = mod.GetLength(0);
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    int xx = cx + dx, yy = cy + dy;
                    if (xx >= 0 && xx < size && yy >= 0 && yy < size) SetFn(mod, fn, xx, yy, dist != 2 && dist != 4);
                }
        }

        static void Align(bool[,] mod, bool[,] fn, int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    SetFn(mod, fn, cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        static void DrawFormat(bool[,] mod, bool[,] fn, int mask)
        {
            int size = mod.GetLength(0);
            int data = (0 << 3) | mask;          // error correction level M = 00
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;
            for (int i = 0; i <= 5; i++) SetFn(mod, fn, 8, i, Bit(bits, i));
            SetFn(mod, fn, 8, 7, Bit(bits, 6));
            SetFn(mod, fn, 8, 8, Bit(bits, 7));
            SetFn(mod, fn, 7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) SetFn(mod, fn, 14 - i, 8, Bit(bits, i));
            for (int i = 0; i < 8; i++) SetFn(mod, fn, size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) SetFn(mod, fn, 8, size - 15 + i, Bit(bits, i));
            SetFn(mod, fn, 8, size - 8, true);   // always dark
        }

        static bool Bit(int v, int i) { return ((v >> i) & 1) != 0; }

        static void Mask(bool[,] mod, bool[,] fn, int m)
        {
            int size = mod.GetLength(0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (fn[y, x]) continue;
                    bool inv;
                    switch (m)
                    {
                        case 0: inv = (x + y) % 2 == 0; break;
                        case 1: inv = y % 2 == 0; break;
                        case 2: inv = x % 3 == 0; break;
                        case 3: inv = (x + y) % 3 == 0; break;
                        case 4: inv = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: inv = x * y % 2 + x * y % 3 == 0; break;
                        case 6: inv = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: inv = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (inv) mod[y, x] = !mod[y, x];
                }
        }

        static int Penalty(bool[,] m, int n)
        {
            int p = 0;
            // runs of 5+ in rows and columns
            for (int a = 0; a < n; a++)
                for (int dir = 0; dir < 2; dir++)
                {
                    int run = 1;
                    for (int b = 1; b < n; b++)
                    {
                        bool cur = dir == 0 ? m[a, b] : m[b, a];
                        bool prev = dir == 0 ? m[a, b - 1] : m[b - 1, a];
                        if (cur == prev) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                        else run = 1;
                    }
                }
            // 2x2 blocks
            for (int y = 0; y < n - 1; y++)
                for (int x = 0; x < n - 1; x++)
                    if (m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) p += 3;
            // finder-like patterns
            int[] pa = { 1, 0, 1, 1, 1, 0, 1, 0, 0, 0, 0 };
            int[] pb = { 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1 };
            for (int a = 0; a < n; a++)
                for (int b = 0; b <= n - 11; b++)
                    for (int dir = 0; dir < 2; dir++)
                    {
                        bool okA = true, okB = true;
                        for (int k = 0; k < 11 && (okA || okB); k++)
                        {
                            bool v = dir == 0 ? m[a, b + k] : m[b + k, a];
                            if (v != (pa[k] == 1)) okA = false;
                            if (v != (pb[k] == 1)) okB = false;
                        }
                        if (okA) p += 40;
                        if (okB) p += 40;
                    }
            // dark / light balance
            int dark = 0;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (m[y, x]) dark++;
            int total = n * n;
            int k5 = (int)Math.Ceiling(Math.Abs(dark * 20 - total * 10) / (double)total) - 1;
            if (k5 > 0) p += k5 * 10;
            return p;
        }

        // ---- Reed-Solomon over GF(256), polynomial 0x11D
        static byte Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        static byte[] RsDivisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = Mul(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = Mul(root, 0x02);
            }
            return result;
        }

        static byte[] RsRemainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= Mul(divisor[i], factor);
            }
            return result;
        }
    }
}
