using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace QuestShade
{
    internal static class ReShadeLut
    {
        public const int N = 17;
        private const int Bytes = N * N * N * 3;

        public static readonly Dictionary<string, (string res, int count)> BuiltIn = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["pd80_cinelut.png"] = ("pd80_cinelut", 31),
            ["pd80_example-lut.png"] = ("pd80_example-lut", 50),
        };

        public static byte[] FromBuiltIn(string texture, int index)
        {
            if (!BuiltIn.TryGetValue(Path.GetFileName(texture), out var b)) return null;
            index = Math.Clamp(index, 0, b.count - 1);
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream("QuestShade.Luts." + b.res + ".bin");
            if (s == null) return null;
            using var z = new GZipStream(s, CompressionMode.Decompress);
            var skip = new byte[Bytes];
            for (int i = 0; i < index; i++) ReadAll(z, skip);
            var o = new byte[Bytes];
            ReadAll(z, o);
            return o;
        }

        private static void ReadAll(Stream s, byte[] b)
        {
            int got = 0, n;
            while (got < b.Length && (n = s.Read(b, got, b.Length - got)) > 0) got += n;
            if (got < b.Length) throw new Exception("built-in LUT data is cut short");
        }

        public static bool Layout(int w, int h, out int tile, out int count)
        {
            tile = (int)Math.Round(Math.Sqrt(w)); count = 0;
            if (tile < 2 || tile * tile != w || h % tile != 0) return false;
            count = h / tile;
            return true;
        }

        public static byte[] FromPicture(string path, int index)
        {
            var rgb = LutPng.DecodeRgb(File.ReadAllBytes(path), out int w, out int h);
            if (!Layout(w, h, out int tile, out int count)) throw new Exception($"{Path.GetFileName(path)} isn't a LUT strip ({w}x{h})");
            return FromAtlas(rgb, w, tile, Math.Clamp(index, 0, count - 1));
        }

        public static byte[] FromAtlas(byte[] rgb, int w, int tile, int index)
        {
            var o = new byte[Bytes];
            float s = (tile - 1f) / (N - 1f);
            int Px(int r, int g, int b) => ((index * tile + g) * w + b * tile + r) * 3;
            for (int b = 0; b < N; b++)
            for (int g = 0; g < N; g++)
            for (int r = 0; r < N; r++)
            {
                float fr = r * s, fg = g * s, fb = b * s;
                int r0 = Math.Min((int)fr, tile - 1), g0 = Math.Min((int)fg, tile - 1), b0 = Math.Min((int)fb, tile - 1);
                int r1 = Math.Min(r0 + 1, tile - 1), g1 = Math.Min(g0 + 1, tile - 1), b1 = Math.Min(b0 + 1, tile - 1);
                float tr = fr - r0, tg = fg - g0, tb = fb - b0;
                int d = ((b * N + g) * N + r) * 3;
                for (int c = 0; c < 3; c++)
                {
                    float v = 0;
                    for (int i = 0; i < 8; i++)
                    {
                        int ri = (i & 1) != 0 ? r1 : r0, gi = (i & 2) != 0 ? g1 : g0, bi = (i & 4) != 0 ? b1 : b0;
                        float wt = ((i & 1) != 0 ? tr : 1 - tr) * ((i & 2) != 0 ? tg : 1 - tg) * ((i & 4) != 0 ? tb : 1 - tb);
                        v += rgb[Px(ri, gi, bi) + c] * wt;
                    }
                    o[d + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
            }
            return o;
        }

        public static byte[] FromCube(string path)
        {
            int n = 0, idx = 0; float[] data = null;
            float[] lo = { 0, 0, 0 }, hi = { 1, 1, 1 };
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (char.IsLetter(line[0]))
                {
                    if (p[0] == "LUT_3D_SIZE") { n = int.Parse(p[1], CultureInfo.InvariantCulture); data = new float[n * n * n * 3]; }
                    else if (p[0] == "DOMAIN_MIN" && p.Length >= 4) for (int i = 0; i < 3; i++) lo[i] = float.Parse(p[i + 1], CultureInfo.InvariantCulture);
                    else if (p[0] == "DOMAIN_MAX" && p.Length >= 4) for (int i = 0; i < 3; i++) hi[i] = float.Parse(p[i + 1], CultureInfo.InvariantCulture);
                    else if (p[0] == "LUT_1D_SIZE") throw new Exception("1D .cube LUTs aren't supported");
                    continue;
                }
                if (data == null || p.Length < 3 || idx + 3 > data.Length) continue;
                for (int i = 0; i < 3; i++) data[idx++] = (float.Parse(p[i], CultureInfo.InvariantCulture) - lo[i]) / Math.Max(1e-6f, hi[i] - lo[i]);
            }
            if (data == null || idx != data.Length) throw new Exception("malformed .cube file");
            var rgb = new byte[n * n * n * 3];
            for (int b = 0; b < n; b++)
            for (int g = 0; g < n; g++)
            for (int r = 0; r < n; r++)
            {
                int src = ((b * n + g) * n + r) * 3, dst = (g * n * n + b * n + r) * 3;
                for (int c = 0; c < 3; c++) rgb[dst + c] = (byte)Math.Clamp((int)Math.Round(data[src + c] * 255f), 0, 255);
            }
            return FromAtlas(rgb, n * n, n, 0);
        }

        public static void Sample(byte[] cube, float r, float g, float b, float[] o)
        {
            float s = N - 1;
            float fr = Math.Clamp(r, 0, 1) * s, fg = Math.Clamp(g, 0, 1) * s, fb = Math.Clamp(b, 0, 1) * s;
            int r0 = Math.Min((int)fr, N - 1), g0 = Math.Min((int)fg, N - 1), b0 = Math.Min((int)fb, N - 1);
            int r1 = Math.Min(r0 + 1, N - 1), g1 = Math.Min(g0 + 1, N - 1), b1 = Math.Min(b0 + 1, N - 1);
            float tr = fr - r0, tg = fg - g0, tb = fb - b0;
            o[0] = o[1] = o[2] = 0;
            for (int i = 0; i < 8; i++)
            {
                int ri = (i & 1) != 0 ? r1 : r0, gi = (i & 2) != 0 ? g1 : g0, bi = (i & 4) != 0 ? b1 : b0;
                float wt = ((i & 1) != 0 ? tr : 1 - tr) * ((i & 2) != 0 ? tg : 1 - tg) * ((i & 4) != 0 ? tb : 1 - tb);
                int d = ((bi * N + gi) * N + ri) * 3;
                o[0] += cube[d] * wt; o[1] += cube[d + 1] * wt; o[2] += cube[d + 2] * wt;
            }
            o[0] /= 255f; o[1] /= 255f; o[2] /= 255f;
        }
    }
}
