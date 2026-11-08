using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace QuestShade
{
    internal static class Lut
    {
        public static Texture2D Load(string path, int outSize, string flipMode)
        {
            var (cube, n) = LoadCube(path, flipMode);
            return Bake(cube, n, outSize);
        }

        public static (float[] cube, int n) LoadCube(string path, string flipMode)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("not found", path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".cube") return ParseCube(File.ReadAllText(path));
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg") return ParseStrip(File.ReadAllBytes(path), flipMode);
            throw new Exception("unsupported LUT type (use .cube or .png)");
        }

        public static F3 LookupF(float[] cube, int n, F3 c)
        {
            float s = n - 1;
            Sample(cube, n, F3.Clamp01(c.x) * s, F3.Clamp01(c.y) * s, F3.Clamp01(c.z) * s, out var r, out var g, out var b);
            return new F3(r, g, b);
        }

        public static Vector3 Lookup(float[] cube, int n, Vector3 c)
        {
            float s = n - 1;
            Sample(cube, n, Mathf.Clamp01(c.x) * s, Mathf.Clamp01(c.y) * s, Mathf.Clamp01(c.z) * s, out var r, out var g, out var b);
            return new Vector3(r, g, b);
        }

        private static (float[], int) ParseCube(string text)
        {
            int n = 0; float[] data = null; int idx = 0;
            Vector3 dmin = Vector3.zero, dmax = Vector3.one;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (char.IsLetter(line[0]))
                {
                    var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts[0] == "LUT_3D_SIZE") { n = int.Parse(parts[1], CultureInfo.InvariantCulture); data = new float[n * n * n * 3]; }
                    else if (parts[0] == "DOMAIN_MIN" && parts.Length >= 4) dmin = V(parts);
                    else if (parts[0] == "DOMAIN_MAX" && parts.Length >= 4) dmax = V(parts);
                    else if (parts[0] == "LUT_1D_SIZE") throw new Exception("1D .cube LUTs aren't supported");
                    continue;
                }
                if (data == null) continue;
                var v = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (v.Length < 3 || idx + 3 > data.Length) continue;
                for (int i = 0; i < 3; i++)
                {
                    float f = float.Parse(v[i], CultureInfo.InvariantCulture);
                    float lo = i == 0 ? dmin.x : i == 1 ? dmin.y : dmin.z, hi = i == 0 ? dmax.x : i == 1 ? dmax.y : dmax.z;
                    data[idx++] = (f - lo) / Mathf.Max(1e-6f, hi - lo);
                }
            }
            if (data == null || idx != data.Length) throw new Exception("malformed .cube file");
            return (data, n);

            static Vector3 V(string[] p) => new Vector3(
                float.Parse(p[1], CultureInfo.InvariantCulture), float.Parse(p[2], CultureInfo.InvariantCulture), float.Parse(p[3], CultureInfo.InvariantCulture));
        }

        private static (float[], int) ParseStrip(byte[] bytes, string flipMode)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!ImageConversion.LoadImage(tex, bytes, false)) throw new Exception("couldn't decode image");
                int w = tex.width, h = tex.height, n = h;
                if (w != n * n) throw new Exception($"strip must be (N*N) x N, got {w}x{h}");
                var px = tex.GetPixels32();
                var raw = new byte[w * h * 4];
                Marshal.Copy(IntPtr.Add(px.Pointer, IntPtr.Size * 4), raw, 0, raw.Length);

                bool flip = flipMode.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                            (flipMode.Equals("auto", StringComparison.OrdinalIgnoreCase) && Luma(raw, w, 0, h - 1) < Luma(raw, w, 0, 0));
                var data = new float[n * n * n * 3];
                for (int b = 0; b < n; b++)
                for (int g = 0; g < n; g++)
                for (int r = 0; r < n; r++)
                {
                    int y = flip ? (h - 1 - g) : g;
                    int o = (y * w + b * n + r) * 4;
                    int d = ((b * n + g) * n + r) * 3;
                    data[d] = raw[o] / 255f; data[d + 1] = raw[o + 1] / 255f; data[d + 2] = raw[o + 2] / 255f;
                }
                return (data, n);
            }
            finally { UnityEngine.Object.Destroy(tex); }

            static int Luma(byte[] raw, int w, int x, int y) { int o = (y * w + x) * 4; return raw[o] + raw[o + 1] + raw[o + 2]; }
        }

        private static Texture2D Bake(float[] cube, int n, int size)
        {
            int w = size * size, h = size;
            var bytes = new byte[w * h * 4];
            float s = (n - 1f) / (size - 1f);
            for (int b = 0; b < size; b++)
            for (int g = 0; g < size; g++)
            for (int r = 0; r < size; r++)
            {
                Sample(cube, n, r * s, g * s, b * s, out var cr, out var cg, out var cb);
                int o = (g * w + b * size + r) * 4;
                bytes[o] = ToByte(cr); bytes[o + 1] = ToByte(cg); bytes[o + 2] = ToByte(cb); bytes[o + 3] = 255;
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
            {
                name = "QuestShade_LUT", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                anisoLevel = 0, hideFlags = HideFlags.HideAndDontSave
            };
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { tex.LoadRawTextureData(handle.AddrOfPinnedObject(), bytes.Length); }
            finally { handle.Free(); }
            tex.Apply(false, true);
            return tex;
        }

        private static byte ToByte(float f) => (byte)Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);

        private static void Sample(float[] c, int n, float r, float g, float b, out float or, out float og, out float ob)
        {
            int r0 = Math.Min((int)r, n - 1), g0 = Math.Min((int)g, n - 1), b0 = Math.Min((int)b, n - 1);
            int r1 = Math.Min(r0 + 1, n - 1), g1 = Math.Min(g0 + 1, n - 1), b1 = Math.Min(b0 + 1, n - 1);
            float fr = r - r0, fg = g - g0, fb = b - b0;
            or = og = ob = 0;
            for (int i = 0; i < 8; i++)
            {
                int ri = (i & 1) != 0 ? r1 : r0, gi = (i & 2) != 0 ? g1 : g0, bi = (i & 4) != 0 ? b1 : b0;
                float wgt = ((i & 1) != 0 ? fr : 1 - fr) * ((i & 2) != 0 ? fg : 1 - fg) * ((i & 4) != 0 ? fb : 1 - fb);
                int d = ((bi * n + gi) * n + ri) * 3;
                or += c[d] * wgt; og += c[d + 1] * wgt; ob += c[d + 2] * wgt;
            }
        }
    }
}
