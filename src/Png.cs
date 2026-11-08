using System;
using System.IO;
using System.IO.Compression;

namespace QuestShade
{
    internal static class Png
    {
        private static readonly uint[] Crc = BuildCrc();

        public static byte[] EncodeBottomUp(byte[] rgb, int w, int h, int bpp = 3)
        {
            int stride = w * 3, src = w * bpp;
            using var idat = new MemoryStream(rgb.Length / 2);
            using (var z = new ZLibStream(idat, CompressionLevel.Fastest, true))
            {
                var row = new byte[stride + 1];
                row[0] = 1;
                for (int y = h - 1; y >= 0; y--)
                {
                    int o = y * src;
                    if (bpp == 4) for (int x = 0, d = 1; x < w; x++, d += 3) { int s4 = o + x * 4; row[d] = rgb[s4]; row[d + 1] = rgb[s4 + 1]; row[d + 2] = rgb[s4 + 2]; }
                    else Buffer.BlockCopy(rgb, o, row, 1, stride);
                    for (int i = stride; i > 3; i--) row[i] = (byte)(row[i] - row[i - 3]);
                    z.Write(row, 0, row.Length);
                }
            }

            using var png = new MemoryStream((int)idat.Length + 64);
            png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            BE(ihdr, 0, (uint)w); BE(ihdr, 4, (uint)h);
            ihdr[8] = 8; ihdr[9] = 2;
            Chunk(png, "IHDR", ihdr, ihdr.Length);
            Chunk(png, "IDAT", idat.GetBuffer(), (int)idat.Length);
            Chunk(png, "IEND", Array.Empty<byte>(), 0);
            return png.ToArray();
        }

        private static void Chunk(Stream s, string type, byte[] data, int len)
        {
            var hdr = new byte[8];
            BE(hdr, 0, (uint)len);
            for (int i = 0; i < 4; i++) hdr[4 + i] = (byte)type[i];
            s.Write(hdr, 0, 8);
            s.Write(data, 0, len);
            uint c = 0xFFFFFFFF;
            for (int i = 4; i < 8; i++) c = Crc[(c ^ hdr[i]) & 0xFF] ^ (c >> 8);
            for (int i = 0; i < len; i++) c = Crc[(c ^ data[i]) & 0xFF] ^ (c >> 8);
            var crc = new byte[4];
            BE(crc, 0, c ^ 0xFFFFFFFF);
            s.Write(crc, 0, 4);
        }

        private static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        private static uint[] BuildCrc()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }
    }
}
