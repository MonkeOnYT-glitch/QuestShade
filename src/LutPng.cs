using System;
using System.IO;
using System.IO.Compression;

namespace QuestShade
{
    internal static class LutPng
    {
        public static bool Size(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                using var f = File.OpenRead(path);
                var b = new byte[24];
                if (f.Read(b, 0, 24) != 24 || b[0] != 0x89 || b[1] != (byte)'P') return false;
                w = BE(b, 16); h = BE(b, 20);
                return w > 0 && h > 0;
            }
            catch { return false; }
        }

        public static byte[] DecodeRgb(byte[] png, out int w, out int h)
        {
            w = h = 0;
            if (png.Length < 33 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G') throw new Exception("not a PNG");
            int depth = 0, type = 0, interlace = 0;
            byte[] pal = null, trns = null;
            var idat = new MemoryStream();
            int p = 8;
            while (p + 8 <= png.Length)
            {
                int len = BE(png, p);
                string t = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
                int d = p + 8;
                if (len < 0 || d + len > png.Length) break;
                if (t == "IHDR") { w = BE(png, d); h = BE(png, d + 4); depth = png[d + 8]; type = png[d + 9]; interlace = png[d + 12]; }
                else if (t == "PLTE") { pal = new byte[len]; Buffer.BlockCopy(png, d, pal, 0, len); }
                else if (t == "tRNS") { trns = new byte[len]; Buffer.BlockCopy(png, d, trns, 0, len); }
                else if (t == "IDAT") idat.Write(png, d, len);
                else if (t == "IEND") break;
                p = d + len + 4;
            }
            if (w <= 0 || h <= 0) throw new Exception("bad PNG header");
            if (interlace != 0) throw new Exception("interlaced PNGs aren't supported");
            if (depth != 8 && depth != 16) throw new Exception($"{depth}-bit PNGs aren't supported");
            int ch = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new Exception("unknown PNG colour type") };
            int bpp = ch * depth / 8, stride = w * bpp;
            var raw = new byte[(stride + 1) * h];
            idat.Position = 2;
            using (var z = new DeflateStream(idat, CompressionMode.Decompress))
            {
                int got = 0, n;
                while (got < raw.Length && (n = z.Read(raw, got, raw.Length - got)) > 0) got += n;
                if (got < raw.Length) throw new Exception("PNG data is cut short");
            }
            var cur = new byte[stride]; var prev = new byte[stride];
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                int f = raw[y * (stride + 1)], o = y * (stride + 1) + 1;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= bpp ? cur[x - bpp] : 0, b = prev[x], c = x >= bpp ? prev[x - bpp] : 0, v = raw[o + x];
                    cur[x] = (byte)(f switch { 1 => v + a, 2 => v + b, 3 => v + ((a + b) >> 1), 4 => v + Paeth(a, b, c), _ => v });
                }
                int step = depth / 8;
                for (int x = 0; x < w; x++)
                {
                    int s = x * bpp, q = (y * w + x) * 3;
                    if (type == 3)
                    {
                        int i = cur[s] * 3;
                        if (pal == null || i + 2 >= pal.Length) continue;
                        rgb[q] = pal[i]; rgb[q + 1] = pal[i + 1]; rgb[q + 2] = pal[i + 2];
                    }
                    else if (type == 0 || type == 4) { rgb[q] = rgb[q + 1] = rgb[q + 2] = cur[s]; }
                    else { rgb[q] = cur[s]; rgb[q + 1] = cur[s + step]; rgb[q + 2] = cur[s + 2 * step]; }
                }
                var t2 = prev; prev = cur; cur = t2;
            }
            return rgb;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static int BE(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
    }
}
