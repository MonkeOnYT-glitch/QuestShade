using System;
using System.Collections.Generic;
using System.Globalization;

namespace QuestShade
{
    internal static class ReShadeColor
    {
        public static readonly HashSet<string> Exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Vibrance.fx", "Tonemap.fx", "LiftGammaGain.fx", "Curves.fx", "Levels.fx", "DPX.fx", "Technicolor.fx",
          "Technicolor2.fx", "Sepia.fx", "Monochrome.fx", "ColorMatrix.fx" };

        internal struct Op { public string Fx; public Dictionary<string, string> P; }

        public sealed class Chain
        {
            internal readonly List<Op> Ops = new List<Op>();
            public bool Empty => Ops.Count == 0;
        }

        public static Chain Read(Ini p)
        {
            var ch = new Chain();
            var order = p.Str("ReShadeChain", "Order", "");
            if (string.IsNullOrEmpty(order)) return ch;
            foreach (var raw in order.Split(','))
            {
                var fx = raw.Trim();
                if (fx.Length == 0 || !Exact.Contains(fx)) continue;
                ch.Ops.Add(new Op { Fx = fx, P = p.Section("RS " + fx) });
            }
            return ch;
        }

        public static void Apply(Chain ch, ref float r, ref float g, ref float b)
        {
            foreach (var op in ch.Ops)
            {
                var c = new[] { r, g, b };
                switch (op.Fx.ToLowerInvariant())
                {
                    case "vibrance.fx": Vibrance(op.P, c); break;
                    case "tonemap.fx": Tonemap(op.P, c); break;
                    case "liftgammagain.fx": Lgg(op.P, c); break;
                    case "curves.fx": Curves(op.P, c); break;
                    case "levels.fx": Levels(op.P, c); break;
                    case "dpx.fx": Dpx(op.P, c); break;
                    case "technicolor.fx": Technicolor(op.P, c); break;
                    case "technicolor2.fx": Technicolor2(op.P, c); break;
                    case "sepia.fx": Sepia(op.P, c); break;
                    case "monochrome.fx": Monochrome(op.P, c); break;
                    case "colormatrix.fx": ColorMatrix(op.P, c); break;
                }
                r = Clamp01(c[0]); g = Clamp01(c[1]); b = Clamp01(c[2]);
            }
        }

        private static void Vibrance(Dictionary<string, string> p, float[] c)
        {
            float vib = F(p, "Vibrance", 0.15f); var bal = V(p, "VibranceRGBBalance", 1, 1, 1);
            float luma = 0.212656f * c[0] + 0.715158f * c[1] + 0.072186f * c[2];
            float sat = Math.Max(c[0], Math.Max(c[1], c[2])) - Math.Min(c[0], Math.Min(c[1], c[2]));
            for (int i = 0; i < 3; i++)
            {
                float k = bal[i] * vib;
                c[i] = Lerp(luma, c[i], 1f + k * (1f - Math.Sign(k) * sat));
            }
        }

        private static void Tonemap(Dictionary<string, string> p, float[] c)
        {
            float gamma = F(p, "Gamma", 1), exposure = F(p, "Exposure", 0), satur = F(p, "Saturation", 0), bleach = F(p, "Bleach", 0), defog = F(p, "Defog", 0);
            var fog = V(p, "FogColor", 0, 0, 1);
            for (int i = 0; i < 3; i++) c[i] = Clamp01(c[i] - defog * fog[i] * 2.55f);
            float e = MathF.Pow(2f, exposure);
            for (int i = 0; i < 3; i++) c[i] = MathF.Pow(Math.Max(0, c[i] * e), gamma);
            float lum = 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2];
            float L = Clamp01(10f * (lum - 0.45f));
            var col = (float[])c.Clone();
            for (int i = 0; i < 3; i++)
            {
                float A2 = bleach * col[i];
                float r1 = 2f * col[i] * lum, r2 = 1f - 2f * (1f - lum) * (1f - col[i]);
                float nc = Lerp(r1, r2, L);
                c[i] = col[i] + (1f - A2) * (A2 * nc);
            }
            float mg = (c[0] + c[1] + c[2]) / 3f;
            for (int i = 0; i < 3; i++) { float d = c[i] - mg; c[i] = (c[i] + d * satur) / (1f + d * satur); }
        }

        private static void Lgg(Dictionary<string, string> p, float[] c)
        {
            var lift = V(p, "RGB_Lift", 1, 1, 1); var gam = V(p, "RGB_Gamma", 1, 1, 1); var gain = V(p, "RGB_Gain", 1, 1, 1);
            for (int i = 0; i < 3; i++)
            {
                float x = c[i] * (1.5f - 0.5f * lift[i]) + 0.5f * lift[i] - 0.5f;
                x = Clamp01(x) * gain[i];
                c[i] = Clamp01(MathF.Pow(Math.Abs(x), 1f / Math.Max(1e-4f, gam[i])));
            }
        }

        private static void Curves(Dictionary<string, string> p, float[] c)
        {
            int mode = (int)F(p, "Mode", 0), formula = (int)F(p, "Formula", 4);
            float contrast = F(p, "Contrast", 0.65f);
            float luma = 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2];
            var chroma = new[] { c[0] - luma, c[1] - luma, c[2] - luma };
            var x = new float[3];
            for (int i = 0; i < 3; i++) x[i] = mode == 0 ? luma : mode == 1 ? chroma[i] * 0.5f + 0.5f : c[i];
            float blend = contrast;
            for (int i = 0; i < 3; i++)
            {
                float v = x[i];
                switch (formula)
                {
                    case 0: v = MathF.Sin(MathF.PI * 0.5f * v); v *= v; break;
                    case 1: v -= 0.5f; v = v / (0.5f + Math.Abs(v)) + 0.5f; break;
                    case 2: v = v * v * (3f - 2f * v); break;
                    case 3: v = (1.0524f * MathF.Exp(6f * v) - 1.05248f) / (MathF.Exp(6f * v) + 20.0855f); break;
                    case 4: v = v * (v * (1.5f - v) + 0.5f); blend = contrast * 2f; break;
                    case 5: v = v * v * v * (v * (v * 6f - 15f) + 10f); break;
                    case 6: v -= 0.5f; v = v / (Math.Abs(v) * 1.25f + 0.375f) + 0.5f; break;
                    case 7: v = (v * (v * (v * (v * (v * (v * (1.6f * v - 7.2f) + 10.8f) - 4.2f) - 3.6f) + 2.7f) - 1.8f) + 2.7f) * v * v; break;
                    case 8: v = -0.5f * (v * 2f - 1f) * (Math.Abs(v * 2f - 1f) - 2f) + 0.5f; break;
                    case 9:
                    {
                        float st = v <= 0.5f ? 1f : 0f, sh = st - 0.5f, sx = v + sh;
                        v = Math.Abs(st - MathF.Sqrt(Math.Max(0, -sx * sx + sx))) - sh; blend = contrast * 0.5f; break;
                    }
                    case 10: v = v < 0.5f ? v * v * 2f : (2f * -v + 4f) * v - 1f; break;
                }
                x[i] = v;
            }
            for (int i = 0; i < 3; i++)
            {
                if (mode == 0) c[i] = Lerp(luma, x[i], blend) + chroma[i];
                else if (mode == 1) c[i] = Lerp(c[i], luma + (x[i] * 2f - 1f), blend);
                else c[i] = Lerp(c[i], x[i], blend);
            }
        }

        private static void Levels(Dictionary<string, string> p, float[] c)
        {
            float bp = F(p, "BlackPoint", 16), wp = F(p, "WhitePoint", 235);
            float bpf = bp / 255f, wpf = wp == bp ? 255f / 0.00025f : 255f / (wp - bp);
            for (int i = 0; i < 3; i++) c[i] = c[i] * wpf - bpf * wpf;
        }

        private static readonly float[,] DpxRgb = { { 2.6714711726599600f, -1.2672360578624100f, -0.4109956021722270f },
            { -1.0251070293466400f, 1.9840911624108900f, 0.0439502493584124f }, { 0.0610009456429445f, -0.2236707508128630f, 1.1590210416706100f } };
        private static readonly float[,] DpxXyz = { { 0.5003033835433160f, 0.3380975732227390f, 0.1645897795458570f },
            { 0.2579688942747580f, 0.6761952591447060f, 0.0658358459823868f }, { 0.0234517888692628f, 0.1126992737203000f, 0.8668396731242010f } };

        private static void Dpx(Dictionary<string, string> p, float[] c)
        {
            var curve = V(p, "RGB_Curve", 8, 8, 8); var C = V(p, "RGB_C", 0.36f, 0.36f, 0.34f);
            float contrast = F(p, "Contrast", 0.1f), saturation = F(p, "Saturation", 3f), colorful = F(p, "Colorfulness", 2.5f), strength = F(p, "Strength", 0.2f);
            var input = (float[])c.Clone(); var B = new float[3];
            for (int i = 0; i < 3; i++)
            {
                float bb = input[i] * (1f - contrast) + 0.5f * contrast;
                float bt = 1f / (1f + MathF.Exp(curve[i] / 2f));
                B[i] = (1f / (1f + MathF.Exp(-curve[i] * (bb - C[i])))) / (-2f * bt + 1f) + (-bt / (-2f * bt + 1f));
            }
            float value = Math.Max(Math.Max(B[0], B[1]), B[2]);
            if (Math.Abs(value) < 1e-6f) value = 1e-6f;
            var c0 = new float[3];
            for (int i = 0; i < 3; i++) c0[i] = MathF.Pow(Math.Abs(B[i] / value), 1f / colorful) * value;
            c0 = MulM(DpxXyz, c0);
            float luma = c0[0] * 0.30f + c0[1] * 0.59f + c0[2] * 0.11f;
            for (int i = 0; i < 3; i++) c0[i] = (1f - saturation) * luma + saturation * c0[i];
            c0 = MulM(DpxRgb, c0);
            for (int i = 0; i < 3; i++) c[i] = Lerp(input[i], c0[i], strength);
        }

        private static void Technicolor(Dictionary<string, string> p, float[] c)
        {
            float power = F(p, "Power", 4f), strength = F(p, "Strength", 0.4f); var neg = V(p, "RGBNegativeAmount", 0.88f, 0.88f, 0.88f);
            float[] cyan = { 0, 1.30f, 1 }, magenta = { 1, 0, 1.05f }, yellow = { 1.6f, 1.6f, 0.05f };
            float nr = (c[0] * 1.05f + c[1] * 0.620f) / (neg[0] * power);
            float ng = (c[0] * 0.30f + c[1] * 1.0f) / (neg[1] * power);
            float nb = (c[0] * 1.0f + c[2] * 1.05f) / (neg[2] * power);
            for (int i = 0; i < 3; i++)
            {
                float o = (nr + cyan[i]) * (ng + magenta[i]) * (nb + yellow[i]);
                c[i] = Lerp(c[i], o, strength);
            }
        }

        private static void Technicolor2(Dictionary<string, string> p, float[] c)
        {
            var cs = V(p, "ColorStrength", 0.2f, 0.2f, 0.2f);
            float bright = F(p, "Brightness", 1f), sat = F(p, "Saturation", 1f), strength = F(p, "Strength", 1f);
            var col = new[] { Clamp01(c[0]), Clamp01(c[1]), Clamp01(c[2]) };
            var temp = new[] { 1 - col[0], 1 - col[1], 1 - col[2] };
            var target = new[] { temp[1], temp[0], temp[1] }; var target2 = new[] { temp[2], temp[2], temp[0] };
            var temp2 = new float[3];
            for (int i = 0; i < 3; i++) temp2[i] = col[i] * target[i] * target2[i];
            for (int i = 0; i < 3; i++) temp[i] = temp2[i] * cs[i];
            for (int i = 0; i < 3; i++) temp2[i] *= bright;
            target = new[] { temp[1], temp[0], temp[1] }; target2 = new[] { temp[2], temp[2], temp[0] };
            var outc = new float[3];
            for (int i = 0; i < 3; i++) outc[i] = Lerp(col[i], col[i] - target[i] + temp2[i] - target2[i], strength);
            float avg = (outc[0] + outc[1] + outc[2]) * 0.333f;
            for (int i = 0; i < 3; i++) c[i] = Lerp(avg, outc[i], sat);
        }

        private static void Sepia(Dictionary<string, string> p, float[] c)
        {
            var tint = V(p, "Tint", 0.55f, 0.43f, 0.42f); float strength = F(p, "Strength", 0.58f);
            for (int i = 0; i < 3; i++) c[i] = Lerp(c[i], c[i] * tint[i] * 2.55f, strength);
        }

        private static readonly float[][] MonoPresets = {
            null, new[] { 0.21f, 0.72f, 0.07f }, new[] { 0.3333333f, 0.3333334f, 0.3333333f }, new[] { 0.18f, 0.41f, 0.41f },
            new[] { 0.25f, 0.39f, 0.36f }, new[] { 0.21f, 0.40f, 0.39f }, new[] { 0.20f, 0.41f, 0.39f }, new[] { 0.21f, 0.42f, 0.37f },
            new[] { 0.22f, 0.42f, 0.36f }, new[] { 0.31f, 0.36f, 0.33f }, new[] { 0.28f, 0.41f, 0.31f }, new[] { 0.23f, 0.37f, 0.40f },
            new[] { 0.33f, 0.36f, 0.31f }, new[] { 0.36f, 0.31f, 0.33f }, new[] { 0.21f, 0.42f, 0.37f }, new[] { 0.24f, 0.37f, 0.39f },
            new[] { 0.27f, 0.36f, 0.37f }, new[] { 0.25f, 0.35f, 0.40f } };

        private static void Monochrome(Dictionary<string, string> p, float[] c)
        {
            int preset = Math.Clamp((int)F(p, "Monochrome_preset", 0), 0, MonoPresets.Length - 1);
            var k = preset == 0 ? V(p, "Monochrome_conversion_values", 0.21f, 0.72f, 0.07f) : MonoPresets[preset];
            float grey = k[0] * c[0] + k[1] * c[1] + k[2] * c[2], s = F(p, "Monochrome_color_saturation", 0);
            for (int i = 0; i < 3; i++) c[i] = Clamp01(Lerp(grey, c[i], s));
        }

        private static void ColorMatrix(Dictionary<string, string> p, float[] c)
        {
            var r = V(p, "ColorMatrix_Red", 0.817f, 0.183f, 0); var g = V(p, "ColorMatrix_Green", 0.333f, 0.667f, 0);
            var b = V(p, "ColorMatrix_Blue", 0, 0.125f, 0.875f); float s = F(p, "Strength", 1f);
            var m = new[] { Dot(r, c), Dot(g, c), Dot(b, c) };
            for (int i = 0; i < 3; i++) c[i] = Clamp01(Lerp(c[i], m[i], s));
        }

        private static float[] MulM(float[,] m, float[] v)
            => new[] { m[0, 0] * v[0] + m[0, 1] * v[1] + m[0, 2] * v[2], m[1, 0] * v[0] + m[1, 1] * v[1] + m[1, 2] * v[2], m[2, 0] * v[0] + m[2, 1] * v[1] + m[2, 2] * v[2] };
        private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static float Clamp01(float x) => float.IsNaN(x) ? 0 : Math.Clamp(x, 0f, 1f);

        private static float F(Dictionary<string, string> p, string k, float def)
            => p != null && p.TryGetValue(k, out var v) && float.TryParse(v.Split(',')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;

        private static float[] V(Dictionary<string, string> p, string k, float a, float b, float c)
        {
            if (p == null || !p.TryGetValue(k, out var v)) return new[] { a, b, c };
            var s = v.Split(',');
            float P(int i, float d) => i < s.Length && float.TryParse(s[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : d;
            return s.Length == 1 ? new[] { P(0, a), P(0, a), P(0, a) } : new[] { P(0, a), P(1, b), P(2, c) };
        }
    }
}
