using System;
using System.IO;

namespace QuestShade
{
    // QuestShade watermark for livestreams only (never the headset or saved recordings): the icon + "QuestShade"
    // on a dark glass pill. Blended straight into the NV12 frame on its way to the encoder, so it costs nothing on the GPU.
    // The artwork (Assets/watermark.bin, straight RGBA) is drawn once on PC at 1080p size and scaled to the stream here.
    internal static class StreamWatermark
    {
        private static byte[] _rgba; private static int _mw, _mh;
        // Scaled for the current stream: per-pixel Y and alpha, per-2x2-block U, V and alpha.
        private static byte[] _y, _a, _u, _v, _ca; private static int _sw, _sh, _forW, _forH;
        private static bool _loaded, _failed;

        private static bool Load()
        {
            if (_loaded) return !_failed;
            _loaded = true;
            try
            {
                using var s = typeof(StreamWatermark).Assembly.GetManifestResourceStream("QuestShade.Assets.watermark.bin");
                if (s == null) throw new Exception("missing resource");
                using var r = new BinaryReader(s);
                if (new string(r.ReadChars(4)) != "QSW2") throw new Exception("bad file");
                _mw = r.ReadInt32(); _mh = r.ReadInt32();
                _rgba = r.ReadBytes(_mw * _mh * 4);
                return true;
            }
            catch (Exception e) { _failed = true; Mod.Log.Warning("Stream watermark unavailable: " + e.Message); return false; }
        }

        private static void Prepare(int w, int h)
        {
            if (_forW == w && _forH == h && _y != null) return;
            _forW = w; _forH = h;
            // Same size relative to the picture at 480p / 720p / 1080p; smaller on landscape streams, where it would cover more.
            float k = Math.Min(w, h) / 1080f * (w > h ? 0.68f : 1f);
            _sw = Math.Max(16, (int)(_mw * k)) & ~1; _sh = Math.Max(8, (int)(_mh * k)) & ~1;
            int n = _sw * _sh;
            var pr = new float[n]; var pg = new float[n]; var pb = new float[n]; var pa = new float[n];
            for (int y = 0; y < _sh; y++)
                for (int x = 0; x < _sw; x++)
                {
                    // Bilinear on premultiplied colour so the soft edges don't go dark or fringed.
                    float fx = (x + 0.5f) / k - 0.5f, fy = (y + 0.5f) / k - 0.5f;
                    int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy); float tx = fx - x0, ty = fy - y0;
                    float r = 0, g = 0, b = 0, a = 0;
                    Acc(x0, y0, (1 - tx) * (1 - ty), ref r, ref g, ref b, ref a);
                    Acc(x0 + 1, y0, tx * (1 - ty), ref r, ref g, ref b, ref a);
                    Acc(x0, y0 + 1, (1 - tx) * ty, ref r, ref g, ref b, ref a);
                    Acc(x0 + 1, y0 + 1, tx * ty, ref r, ref g, ref b, ref a);
                    int i = y * _sw + x; pr[i] = r; pg[i] = g; pb[i] = b; pa[i] = a;
                }
            _y = new byte[n]; _a = new byte[n];
            for (int i = 0; i < n; i++)
            {
                float a = pa[i];
                float r = a > 1e-4f ? pr[i] / a : 0, g = a > 1e-4f ? pg[i] / a : 0, b = a > 1e-4f ? pb[i] / a : 0;
                _y[i] = (byte)Math.Clamp(16f + 65.481f * r + 128.553f * g + 24.966f * b, 0f, 255f); // BT.601 video range, like the recorder
                _a[i] = (byte)Math.Clamp(a * 255f + 0.5f, 0f, 255f);
            }
            int cw = _sw / 2, ch = _sh / 2;
            _u = new byte[cw * ch]; _v = new byte[cw * ch]; _ca = new byte[cw * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int j = 0; j < 2; j++) for (int i2 = 0; i2 < 2; i2++) { int i = (y * 2 + j) * _sw + x * 2 + i2; r += pr[i]; g += pg[i]; b += pb[i]; a += pa[i]; }
                    float inv = a > 1e-4f ? 1f / a : 0f; r *= inv; g *= inv; b *= inv;
                    int c = y * cw + x;
                    _u[c] = (byte)Math.Clamp(128f - 37.797f * r - 74.203f * g + 112f * b, 0f, 255f);
                    _v[c] = (byte)Math.Clamp(128f + 112f * r - 93.786f * g - 18.214f * b, 0f, 255f);
                    _ca[c] = (byte)Math.Clamp(a / 4f * 255f + 0.5f, 0f, 255f);
                }
        }

        private static void Acc(int x, int y, float wgt, ref float r, ref float g, ref float b, ref float a)
        {
            if (x < 0 || y < 0 || x >= _mw || y >= _mh || wgt <= 0f) return;
            int p = (y * _mw + x) * 4;
            float al = _rgba[p + 3] / 255f * wgt;
            r += _rgba[p] / 255f * al; g += _rgba[p + 1] / 255f * al; b += _rgba[p + 2] / 255f * al; a += al;
        }

        // frame: NV12, Y plane rows of `stride` bytes (`slice` rows), then interleaved UV at stride * slice.
        public static unsafe void Apply(byte* frame, long bytes, int w, int h, int stride, int slice)
        {
            if (!Load()) return;
            Prepare(w, h);
            int margin = Math.Max(10, Math.Min(w, h) / 36) & ~1;
            int x0 = (w - _sw - margin) & ~1, y0 = (h - _sh - margin) & ~1; // bottom-right corner
            if (x0 < 0 || y0 < 0) return;
            long uvBase = (long)stride * slice;
            if (uvBase + (long)stride * (h / 2) > bytes) return;
            for (int y = 0; y < _sh; y++)
            {
                byte* row = frame + (long)(y0 + y) * stride + x0;
                int m = y * _sw;
                for (int x = 0; x < _sw; x++)
                {
                    int a = _a[m + x];
                    if (a == 0) continue;
                    row[x] = (byte)((row[x] * (255 - a) + _y[m + x] * a + 127) / 255);
                }
            }
            int cw = _sw / 2;
            for (int y = 0; y < _sh / 2; y++)
            {
                byte* uv = frame + uvBase + (long)(y0 / 2 + y) * stride + x0;
                int m = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    int a = _ca[m + x];
                    if (a == 0) continue;
                    uv[x * 2] = (byte)((uv[x * 2] * (255 - a) + _u[m + x] * a + 127) / 255);
                    uv[x * 2 + 1] = (byte)((uv[x * 2 + 1] * (255 - a) + _v[m + x] * a + 127) / 255);
                }
            }
        }
    }
}
