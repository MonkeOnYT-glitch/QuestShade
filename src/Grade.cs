using System;
using System.Runtime.InteropServices;
using UnityEngine;
using V3 = QuestShade.F3;

namespace QuestShade
{
    internal static class Grade
    {
        public const int Size = 32;

        public static bool HasGrading(Ini p) =>
            p.Enabled("Color") || p.Enabled("WhiteBalance") || p.Enabled("LiftGammaGain") || p.Enabled("ShadowsMidtonesHighlights") ||
            p.Enabled("SplitToning") || p.Enabled("ChannelMixer") || p.Enabled("LUT") || p.Has("ReShadeChain");

        public static Texture2D Bake(Ini p, float[] userCube, int userN, float userContribution, Texture2D reuse)
        {
            bool col = p.Enabled("Color");
            float contrast = col ? 1f + p.Float("Color", "Contrast", 0) / 100f : 1f;
            float sat = col ? 1f + p.Float("Color", "Saturation", 0) / 100f : 1f;
            float hue = col ? p.Float("Color", "HueShift", 0) / 360f : 0f;
            Color filter = col ? p.Color("Color", "ColorFilter", Color.white) : Color.white;
            var fl = filter.linear; V3 filt = new V3(fl.r, fl.g, fl.b);

            V3 wb = V3.One;
            if (p.Enabled("WhiteBalance")) wb = WhiteBalanceCoeffs(p.Float("WhiteBalance", "Temperature", 0), p.Float("WhiteBalance", "Tint", 0));

            bool mix = p.Enabled("ChannelMixer");
            V3 mr = mix ? F3.From(p.Vec3("ChannelMixer", "Red", new Vector3(100, 0, 0))) / 100f : new V3(1, 0, 0);
            V3 mg = mix ? F3.From(p.Vec3("ChannelMixer", "Green", new Vector3(0, 100, 0))) / 100f : new V3(0, 1, 0);
            V3 mb = mix ? F3.From(p.Vec3("ChannelMixer", "Blue", new Vector3(0, 0, 100))) / 100f : new V3(0, 0, 1);

            bool split = p.Enabled("SplitToning");
            Color stS = split ? p.Color("SplitToning", "Shadows", Color.gray) : Color.gray;
            Color stH = split ? p.Color("SplitToning", "Highlights", Color.gray) : Color.gray;
            float stBal = split ? p.Float("SplitToning", "Balance", 0) / 100f : 0f;
            V3 stSv = new V3(stS.r, stS.g, stS.b), stHv = new V3(stH.r, stH.g, stH.b);

            bool smh = p.Enabled("ShadowsMidtonesHighlights");
            const string S = "ShadowsMidtonesHighlights";
            V3 smhS = smh ? Smh(p.Vec4(S, "Shadows", new Vector4(1, 1, 1, 0))) : V3.One;
            V3 smhM = smh ? Smh(p.Vec4(S, "Midtones", new Vector4(1, 1, 1, 0))) : V3.One;
            V3 smhH = smh ? Smh(p.Vec4(S, "Highlights", new Vector4(1, 1, 1, 0))) : V3.One;
            float sS = p.Float(S, "ShadowsStart", 0f), sE = p.Float(S, "ShadowsEnd", 0.3f);
            float hS = p.Float(S, "HighlightsStart", 0.55f), hE = p.Float(S, "HighlightsEnd", 1f);

            bool lgg = p.Enabled("LiftGammaGain");
            Vector4 vl = lgg ? p.Vec4("LiftGammaGain", "Lift", new Vector4(1, 1, 1, 0)) : new Vector4(1, 1, 1, 0);
            Vector4 vg = lgg ? p.Vec4("LiftGammaGain", "Gamma", new Vector4(1, 1, 1, 0)) : new Vector4(1, 1, 1, 0);
            Vector4 vn = lgg ? p.Vec4("LiftGammaGain", "Gain", new Vector4(1, 1, 1, 0)) : new Vector4(1, 1, 1, 0);
            V3 lift = new V3(vl.x - 1 + vl.w, vl.y - 1 + vl.w, vl.z - 1 + vl.w);
            V3 gain = new V3(vn.x + vn.w, vn.y + vn.w, vn.z + vn.w);
            V3 gam = new V3(1f / MathF.Max(0.01f, vg.x + vg.w), 1f / MathF.Max(0.01f, vg.y + vg.w), 1f / MathF.Max(0.01f, vg.z + vg.w));

            var rsChain = ReShadeColor.Read(p);

            int n = Size, w = n * n;
            var bytes = new byte[w * n * 4];
            float inv = 1f / (n - 1);
            for (int b = 0; b < n; b++)
            for (int g = 0; g < n; g++)
            for (int r = 0; r < n; r++)
            {
                var c = new V3(ToLin(r * inv), ToLin(g * inv), ToLin(b * inv));

                if (!wb.IsOne) c = Lms2Lin(Mul(Lin2Lms(c), wb));
                if (contrast != 1f)
                {
                    c = new V3(LogC(c.x, contrast), LogC(c.y, contrast), LogC(c.z, contrast));
                }
                c = Mul(c, filt);
                if (mix) c = new V3(V3.Dot(c, mr), V3.Dot(c, mg), V3.Dot(c, mb));
                if (split)
                {
                    var cg = Pow(Max0(c), 1f / 2.2f);
                    float luma = F3.Clamp01(Luma(Sat01(cg)) + stBal);
                    var s1 = V3.Lerp(new V3(.5f, .5f, .5f), stSv, 1f - luma);
                    var s2 = V3.Lerp(new V3(.5f, .5f, .5f), stHv, luma);
                    cg = SoftLight(cg, s1); cg = SoftLight(cg, s2);
                    c = Pow(Max0(cg), 2.2f);
                }
                if (smh)
                {
                    float l = Luma(c);
                    float ws = 1f - Smooth(sS, sE, l), wh = Smooth(hS, hE, l), wm = 1f - ws - wh;
                    c = Mul(c, smhS) * ws + Mul(c, smhM) * wm + Mul(c, smhH) * wh;
                }
                if (hue != 0f || sat != 1f)
                {
                    if (hue != 0f)
                    {
                        F3.RgbToHsv(c, out var hh, out var ss, out var vv);
                        c = F3.HsvToRgb((hh + hue) - MathF.Floor(hh + hue), ss, vv);
                    }
                    float l = Luma(c);
                    c = new V3(l, l, l) + (c - new V3(l, l, l)) * sat;
                }

                var d = new V3(ToSrgb(c.x), ToSrgb(c.y), ToSrgb(c.z));
                if (lgg)
                {
                    d = new V3(gain.x * d.x + lift.x * (1 - d.x), gain.y * d.y + lift.y * (1 - d.y), gain.z * d.z + lift.z * (1 - d.z));
                    d = new V3(MathF.Pow(MathF.Max(0, d.x), gam.x), MathF.Pow(MathF.Max(0, d.y), gam.y), MathF.Pow(MathF.Max(0, d.z), gam.z));
                }
                d = Sat01(d);
                if (!rsChain.Empty) { float rr = d.x, gg = d.y, bb = d.z; ReShadeColor.Apply(rsChain, ref rr, ref gg, ref bb); d = new V3(rr, gg, bb); }
                if (userCube != null && userContribution > 0f) d = V3.Lerp(d, Lut.LookupF(userCube, userN, d), userContribution);

                int o = (g * w + b * n + r) * 4;
                bytes[o] = B(d.x); bytes[o + 1] = B(d.y); bytes[o + 2] = B(d.z); bytes[o + 3] = 255;
            }

            var tex = reuse;
            if (tex == null)
            {
                tex = new Texture2D(w, n, TextureFormat.RGBA32, false, true)
                { name = "QuestShade_GradeLUT", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, anisoLevel = 0, hideFlags = HideFlags.HideAndDontSave };
            }
            var h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try { tex.LoadRawTextureData(h.AddrOfPinnedObject(), bytes.Length); }
            finally { h.Free(); }
            tex.Apply(false, false);
            return tex;
        }

        private static byte B(float f) => (byte)Math.Clamp((int)MathF.Round(f * 255f), 0, 255);
        
        private static V3 Mul(V3 a, V3 b) => new V3(a.x * b.x, a.y * b.y, a.z * b.z);
        private static V3 Max0(V3 a) => new V3(MathF.Max(0, a.x), MathF.Max(0, a.y), MathF.Max(0, a.z));
        private static V3 Sat01(V3 a) => new V3(F3.Clamp01(a.x), F3.Clamp01(a.y), F3.Clamp01(a.z));
        private static V3 Pow(V3 a, float e) => new V3(MathF.Pow(a.x, e), MathF.Pow(a.y, e), MathF.Pow(a.z, e));
        private static float Luma(V3 c) => c.x * 0.2126729f + c.y * 0.7151522f + c.z * 0.0721750f;
        private static float Smooth(float a, float b, float x) { float t = F3.Clamp01((x - a) / MathF.Max(1e-5f, b - a)); return t * t * (3 - 2 * t); }
        private static float ToLin(float s) => s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
        private static float ToSrgb(float l) { l = MathF.Max(0, l); return l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1f / 2.4f) - 0.055f; }
        private static float LogC(float x, float k)
        {
            const float mid = -2.4739312f;
            float l = MathF.Log(MathF.Max(x, 1e-6f), 2f);
            return MathF.Pow(2f, (l - mid) * k + mid);
        }
        private static V3 Smh(Vector4 v) => new V3(MathF.Max(0, v.x + v.w), MathF.Max(0, v.y + v.w), MathF.Max(0, v.z + v.w));
        private static float SoftLight(float a, float b) => b < 0.5f ? 2 * a * b + a * a * (1 - 2 * b) : MathF.Sqrt(a) * (2 * b - 1) + 2 * a * (1 - b);
        private static V3 SoftLight(V3 a, V3 b) => new V3(SoftLight(a.x, b.x), SoftLight(a.y, b.y), SoftLight(a.z, b.z));

        private static V3 Lin2Lms(V3 c) => new V3(
            3.90405e-1f * c.x + 5.49941e-1f * c.y + 8.92632e-3f * c.z,
            7.08416e-2f * c.x + 9.63172e-1f * c.y + 1.35775e-3f * c.z,
            2.31082e-2f * c.x + 1.28021e-1f * c.y + 9.36245e-1f * c.z);
        private static V3 Lms2Lin(V3 c) => new V3(
            2.85847e+0f * c.x - 1.62879e+0f * c.y - 2.48910e-2f * c.z,
            -2.10182e-1f * c.x + 1.15820e+0f * c.y + 3.24281e-4f * c.z,
            -4.18120e-2f * c.x - 1.18169e-1f * c.y + 1.06867e+0f * c.z);
        private static V3 WhiteBalanceCoeffs(float temperature, float tint)
        {
            float t1 = temperature / 65f, t2 = tint / 65f;
            float x = 0.31271f - t1 * (t1 < 0f ? 0.1f : 0.05f);
            float y = 2.87f * x - 3f * x * x - 0.27509507f + t2 * 0.05f;
            var w1 = new V3(0.949237f, 1.03542f, 1.08728f);
            float X = x / y, Z = (1 - x - y) / y;
            var w2 = new V3(0.7328f * X + 0.4296f - 0.1624f * Z, -0.7036f * X + 1.6975f + 0.0061f * Z, 0.0030f * X + 0.0136f + 0.9834f * Z);
            return new V3(w1.x / w2.x, w1.y / w2.y, w1.z / w2.z);
        }
    }
}

namespace QuestShade
{
    internal readonly struct F3
    {
        public readonly float x, y, z;
        public F3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static readonly F3 One = new F3(1, 1, 1);
        public bool IsOne => x == 1f && y == 1f && z == 1f;
        public static F3 From(UnityEngine.Vector3 v) => new F3(v.x, v.y, v.z);
        public static F3 operator +(F3 a, F3 b) => new F3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static F3 operator -(F3 a, F3 b) => new F3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static F3 operator *(F3 a, float k) => new F3(a.x * k, a.y * k, a.z * k);
        public static F3 operator /(F3 a, float k) => new F3(a.x / k, a.y / k, a.z / k);
        public static float Dot(F3 a, F3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static F3 Lerp(F3 a, F3 b, float t) => new F3(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, a.z + (b.z - a.z) * t);
        public static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        public static void RgbToHsv(F3 c, out float h, out float s, out float v)
        {
            float mx = MathF.Max(c.x, MathF.Max(c.y, c.z)), mn = MathF.Min(c.x, MathF.Min(c.y, c.z)), d = mx - mn;
            v = mx; s = mx > 0 ? d / mx : 0;
            if (d <= 0) { h = 0; return; }
            if (mx == c.x) h = ((c.y - c.z) / d) / 6f;
            else if (mx == c.y) h = (2f + (c.z - c.x) / d) / 6f;
            else h = (4f + (c.x - c.y) / d) / 6f;
            if (h < 0) h += 1;
        }

        public static F3 HsvToRgb(float h, float s, float v)
        {
            float r = MathF.Abs(h * 6 - 3) - 1, g = 2 - MathF.Abs(h * 6 - 2), b = 2 - MathF.Abs(h * 6 - 4);
            var k = new F3(Clamp01(r), Clamp01(g), Clamp01(b));
            return Lerp(One, k, s) * v;
        }
    }
}
