using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace QuestShade
{
    internal static class ReShadeImport
    {
        public static string Dir => Path.Combine(Config.Root, "ReShade");
        public const string Suffix = " (ReShade)";
        private const int Version = 9;

        private static readonly string[] Heavy = { "MXAO", "RTGI", "SSR", "MotionBlur", "Reflective", "GloomAO", "SSDO", "HBAO",
            "SSAO", "MiAO", "XeGTAO", "NeoGI", "SCGI", "SCAO", "TurboGI", "RadiantGI", "Pirate_Depth_GI", "ZN_GI", "DAMP_RT", "LSAO", "RTAO",
            "PPFX_SSDO", "DeepFry", "AdaptiveFog", "HeightFog", "DepthHaze", "Glamarye", "Launchpad", "Solaris", "Volumetric", "Raymarch", "Pathtrac", "LongExposure" };

        private static readonly string[] NotNeeded = { "SuperDepth3D", "Information", "DisplayDepth", "UIMask", "UIDetect", "KeepUI", "SkySave",
            "FXAA", "SMAA", "TAA", "Temporal_AA", "CMAA", "DLAA", "NFAA", "DAA", "AntiAliasing", "Deband", "Debanding", "Dither", "FramerateLimiter",
            "Splitscreen", "BeforeAfter", "Compare", "Cursor", "Mouse", "Vectorscope", "Waveform", "ZebraLines", "Stats", "Depth_Tool", "Template",
            "HotsamplingHelper", "Hotsampling", "CanvasMask", "ColorMask", "Composition", "AspectRatio", "GoldenRatio", "VRS_Map", "DownsampleSSAA", "Frequency" };

        public static void Run()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                Directory.CreateDirectory(Path.Combine(Dir, "Textures"));
                var readme = Path.Combine(Dir, "README.txt");
                if (!File.Exists(readme)) File.WriteAllText(readme,
                    "Put ReShade presets (.ini) here.\n" +
                    "You don't need ReShade's .fx shader files: QuestShade rebuilds the effects it supports and lists the ones it skipped.\n" +
                    "Each preset shows up in the menu as \"<name> (ReShade)\".\n");
                foreach (var file in Directory.GetFiles(Dir, "*.ini"))
                {
                    try { ImportOne(file); }
                    catch (Exception e) { Mod.Log.Warning($"ReShade import of {Path.GetFileName(file)} failed: {e.Message}"); }
                }
            }
            catch (Exception e) { Mod.Log.Warning("ReShade import folder unavailable: " + e.Message); }
        }

        private static string OutPath(string name) => Path.Combine(Config.PresetDir, name + Suffix + ".ini");

        private static void ImportOne(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            string outFile = OutPath(name);
            if (File.Exists(outFile) && File.GetLastWriteTimeUtc(outFile) >= File.GetLastWriteTimeUtc(file) &&
                Ini.Parse(File.ReadAllText(outFile)).Float("Import", "Version", 1) >= Version)
                return;

            if (TryPort(file, outFile)) return;

            var rs = ReadReShade(File.ReadAllText(file));
            var tech = Techniques(rs);
            var o = new Out();
            var used = new List<string>(); var skipped = new List<string>(); var heavy = new List<string>(); var notNeeded = new List<string>();
            float sat = 0, contrast = 0, exposure = 0, temp = 0, tint = 0, eye = 0, lift = 0, gain = 0;
            float bloomI = 0, bloomT = 1f, bloomS = 0.7f, bloomK = 0.5f, flare = 0, dirt = 0, sharp = 0, ca = 0, grain = 0;
            var chain = new List<string>();
            void Bloom(float i, float t, float sc)
            {
                bloomI = bloomI <= 0 ? i : Math.Max(bloomI, i) + Math.Min(bloomI, i) * 0.25f;
                bloomT = Math.Min(bloomT, t); bloomS = Math.Max(bloomS, sc);
            }

            foreach (var (techName, fx) in tech)
            {
                string key = fx.ToLowerInvariant();
                rs.TryGetValue(fx, out var s); s ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                float F(string k, float def) => Num(s, k, def);
                float Any(float def, params string[] parts) => FirstNum(s, parts, def);
                bool Hit(string n) => key.Contains(n.ToLowerInvariant()) || techName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0;
                string label = techName + "@" + fx;

                if (NotNeeded.Any(Hit)) { notNeeded.Add(label); continue; }
                if (Heavy.Any(Hit)) { heavy.Add(label); continue; }

                if (ReShadeColor.Exact.Contains(fx))
                {
                    foreach (var kv in s) o.Set("RS " + fx, kv.Key, kv.Value);
                    chain.Add(fx); used.Add(label + " (exact)"); continue;
                }
                if (key.StartsWith("ambientlight"))
                {
                    float ai = F("alInt", 10.15f), at = F("alThreshold", 15f);
                    Bloom(Math.Clamp(ai / 9f, 0.2f, 2f), Math.Clamp(0.9f - at * 0.02f, 0.3f, 1f), 0.8f);
                    bloomK = Math.Max(bloomK, 0.6f);
                    float k = Math.Clamp(ai / 13.7f, 0f, 1.5f); lift += 0.01f * k; gain += 0.04f * k;
                    if (F("AL_Lens", 0) > 0) flare = Math.Max(flare, Math.Clamp(F("alLensInt", 2f) * 0.35f, 0.1f, 1.5f));
                    if (F("AL_Dirt", 0) > 0) dirt = Math.Max(dirt, Math.Clamp(F("alDirtInt", 1f), 0f, 2f));
                    if (F("AL_Adaptation", 0) > 0) eye = Math.Max(eye, Math.Clamp(F("alAdapt", 0.7f) / 0.7f, 0.3f, 1f));
                    used.Add(label + " (glow, lens flare, dirt, eye adaptation)");
                }
                else if (key.StartsWith("bloominghdr"))
                {
                    var bi = Vec(s, "Bloom_Intensity", 0.5f);
                    Bloom(Math.Clamp(bi[0] * 2f, 0.1f, 2f), 0.75f, Math.Clamp(0.6f + F("Bloom_Spread", 1f) * 0.2f, 0.5f, 0.9f));
                    var bs = Vec(s, "Saturation", 0f); sat += (bs[0] + bs[1]) * 50f;
                    contrast += (F("Contrast", 1f) - 1f) * 50f; exposure += F("Exp", 0f);
                    if (F("Auto_Exposure", 0) > 0) eye = Math.Max(eye, 1f);
                    used.Add(label + " (glow, saturation, eye adaptation)");
                }
                else if (key.Contains("lightroom"))
                {
                    const string L = "LIGHTROOM_GLOBAL_";
                    exposure += F(L + "EXPOSURE", 0) * 1.5f; contrast += F(L + "CONTRAST", 0) * 60f;
                    sat += F(L + "SATURATION", 0) * 100f + F(L + "VIBRANCE", 0) * 50f;
                    temp += F(L + "TEMPERATURE", 0) * 50f; tint += F(L + "TINT", 0) * 50f;
                    float bl = F(L + "BLACK_LEVEL", 0), wl = F(L + "WHITE_LEVEL", 255);
                    if (wl > bl) contrast += (255f / (wl - bl) - 1f) * 100f;
                    lift -= F(L + "BLACKS_CURVE", 0) * 0.05f + F(L + "SHADOWS_CURVE", 0) * 0.03f;
                    gain += F(L + "HIGHLIGHTS_CURVE", 0) * 0.05f + F(L + "WHITES_CURVE", 0) * 0.05f;
                    used.Add(label + " (global sliders)");
                }
                else if (Hit("autoexposure") || Hit("eyeadaption") || Hit("eyeadaptation") || Hit("adaptivetonemapper") || Hit("filmic_adaptation"))
                {
                    eye = Math.Max(eye, 1f);
                    if (key.Contains("bloom")) Bloom(0.5f, 0.8f, 0.7f);
                    used.Add(label + " (eye adaptation)");
                }
                else if (Hit("godrays") || Hit("lightshaft") || Hit("sunshaft"))
                {
                    o.Set("GodRays", "Intensity", Fmt(Math.Clamp(Any(1f, "intensity", "amount", "strength") * 0.6f, 0.2f, 1.5f))); o.Set("GodRays", "Length", "0.6"); o.Set("GodRays", "Threshold", "0.75");
                    used.Add(label + " (god rays)");
                }
                else if (Hit("flare") || Hit("unreallens"))
                {
                    flare = Math.Max(flare, Math.Clamp(Any(1f, "intensity", "amount", "strength") * 4f, 0.15f, 1.5f));
                    used.Add(label + " (lens flare, camera)");
                }
                else if (key.StartsWith("glitch"))
                {
                    o.Set("Glitch", "Amount", Fmt(Math.Clamp(Any(0.1f, "amount", "strength", "intensity") * 0.6f, 0.03f, 0.4f)));
                    o.Set("Glitch", "Rows", "40"); o.Set("Glitch", "Speed", "12"); used.Add(label);
                }
                else if (key.Contains("crt") || key.Contains("cathode")) { o.Set("CRT", "Curvature", "0.25"); o.Set("Scanlines", "Intensity", "0.25"); o.Set("Scanlines", "Count", "300"); used.Add(label); }
                else if (key.StartsWith("retrotv") || key.StartsWith("vhs") || key.Contains("ntsc") || key.StartsWith("pal")) { o.Set("Scanlines", "Intensity", "0.2"); o.Set("Scanlines", "Count", "240"); o.Set("Noise", "Intensity", "0.08"); sat -= 15; used.Add(label); }
                else if (key.StartsWith("scanlines") || key.Contains("interlac")) { o.Set("Scanlines", "Intensity", Fmt(Math.Clamp(F("DARKNESS", 0.5f) * 0.5f, 0.1f, 0.5f))); o.Set("Scanlines", "Count", "240"); used.Add(label); }
                else if (key.StartsWith("nightvision"))
                {
                    o.Set("ChannelMixer", "Red", "8, 22, 5"); o.Set("ChannelMixer", "Green", "30, 85, 20"); o.Set("ChannelMixer", "Blue", "6, 16, 4");
                    o.Set("LiftGammaGain", "Gain", "1, 1, 1, 0.18"); o.Set("LiftGammaGain", "Gamma", "1, 1, 1, 0.1");
                    o.Set("Noise", "Intensity", "0.14"); contrast += 20; used.Add(label);
                }
                else if (key.StartsWith("pd80_04_contrast") || key.Contains("contrast_brightness_saturation"))
                {
                    contrast += Any(0f, "tcontrast", "contrast") * 100f; sat += Any(0f, "tsaturation", "saturation") * 100f;
                    exposure += Any(0f, "tbrightness", "brightness", "exposure"); used.Add(label);
                }
                else if (key.StartsWith("pd80_04_color_temperature"))
                {
                    float kel = Any(6500f, "colortemp", "temperature", "kelvin");
                    if (kel > 100f) temp += Math.Clamp((6500f - kel) / 50f, -100f, 100f) * Math.Clamp(Any(1f, "lumablend", "strength"), 0f, 1f);
                    used.Add(label + " (white balance)");
                }
                else if (key.StartsWith("pd80_04_saturation") || key.StartsWith("hdrsaturation"))
                {
                    sat += (Any(1f, "saturation") - 1f) * 50f; used.Add(label);
                }
                else if (key.StartsWith("pd80_04_blacknwhite") || key.StartsWith("greyscale") || key.Contains("monochrome"))
                {
                    sat = -100f; used.Add(label + " (black and white)");
                }
                else if (key.StartsWith("pcolors"))
                {
                    float sum = 0; int cnt = 0;
                    foreach (var kv in s) if (kv.Key.IndexOf("sat", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        float.TryParse(kv.Value.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { sum += v; cnt++; }
                    if (cnt > 0) sat += sum / cnt * 60f;
                    used.Add(label + " (saturation)");
                }
                else if (key.StartsWith("clarity") || key.Contains("localcontrast") || key.StartsWith("zenlc") || key.StartsWith("quark_local"))
                { sharp = Math.Max(sharp, Math.Clamp(Any(0.4f, "ClarityStrength", "strength", "intensity", "amount") * 0.6f, 0.1f, 0.8f)); contrast += 5; used.Add(label + " (as sharpen)"); }
                else if (key.StartsWith("fisheye") || key.StartsWith("perfectperspective") || key.StartsWith("lensdistort") || key.Contains("barrel") || key.StartsWith("curvedmonitor") || key.StartsWith("bulgepinch"))
                {
                    bool fish = key.StartsWith("fisheye") || key.StartsWith("bulge");
                    o.Set("LensDistortion", "Intensity", Fmt(fish ? Math.Clamp(Any(0.35f, "fov", "strength", "amount") * 0.6f, 0.1f, 0.6f) : 0.2f));
                    o.Set("LensDistortion", "Scale", fish ? "1.05" : "1.02");
                    if (fish) ca = Math.Max(ca, 0.3f);
                    used.Add(label + " (lens distortion)");
                }
                else if (key.StartsWith("vibrance") || key.StartsWith("vividtone")) { sat += F("Vibrance", 0.15f) * 60f; used.Add(label); }
                else if (key.StartsWith("colourfulness") || key.StartsWith("colorfulness")) { sat += F("colourfulness", 0.4f) * 40f; used.Add(label); }
                else if (key.StartsWith("tonemap") || key.StartsWith("reinhard") || key.StartsWith("piecewisefilmic") || key.StartsWith("watchdogs"))
                {
                    exposure += F("Exposure", 0f); sat += F("Saturation", 0f) * 100f;
                    float g = F("Gamma", 1f); if (Math.Abs(g - 1f) > 0.01f) o.Set("LiftGammaGain", "Gamma", V(1, 1, 1, (1f - g) * 0.5f));
                    used.Add(label);
                }
                else if (key.StartsWith("liftgammagain"))
                {
                    o.Set("LiftGammaGain", "Lift", V3w(Vec(s, "RGB_Lift", 1), 0)); o.Set("LiftGammaGain", "Gamma", V3w(Vec(s, "RGB_Gamma", 1), 0));
                    o.Set("LiftGammaGain", "Gain", V3w(Vec(s, "RGB_Gain", 1), 0)); used.Add(label);
                }
                else if (key.StartsWith("curves") || key.StartsWith("scurve")) { contrast += Any(0.65f, "Contrast", "intensity", "strength") * 40f; used.Add(label); }
                else if (key.StartsWith("levels") || key.StartsWith("pd80_03_levels") || key.StartsWith("extendedlevels") || key.StartsWith("levelio"))
                {
                    float bp = Any(16, "BlackPoint", "ib_", "black"), wp = Any(235, "WhitePoint", "iw_", "white");
                    if (bp <= 1f && wp <= 1f) { bp *= 255f; wp *= 255f; }
                    if (wp > bp) contrast += ((235f - 16f) / (wp - bp) - 1f) * 100f; used.Add(label);
                }
                else if (key.StartsWith("technicolor") || key.StartsWith("pd80_04_technicolor")) { sat += (F("Saturation", 1f) - 1f) * 100f; used.Add(label); }
                else if (key.StartsWith("dpx"))
                {
                    float k = Math.Clamp(F("Strength", 0.2f), 0f, 1f);
                    sat += (F("Saturation", 3f) / 3f - 1f) * 50f * k; contrast += (F("Contrast", 0.1f) - 0.1f) * 100f * k; used.Add(label);
                }
                else if (key.StartsWith("vignette") && (int)F("Type", 0) == 0)
                {
                    o.Set("Vignette", "Mode", "reshade");
                    o.Set("Vignette", "Radius", Fmt(F("Radius", 2f))); o.Set("Vignette", "Amount", Fmt(F("Amount", -1f)));
                    o.Set("Vignette", "Slope", Fmt(F("Slope", 2f))); o.Set("Vignette", "Ratio", Fmt(F("Ratio", 1f)));
                    var vc = Vec(s, "Center", 0.5f); o.Set("Vignette", "Center", $"{Fmt(vc[0])}, {Fmt(vc.Length > 1 ? vc[1] : 0.5f)}");
                    used.Add(label + " (exact)");
                }
                else if (key.Contains("vignette"))
                {
                    float amt = Any(-1f, "Amount", "intensity", "strength");
                    o.Set("Vignette", "Intensity", Fmt(Math.Clamp(Math.Abs(amt) * 0.45f / Math.Max(1f, F("Radius", 2f) * 0.6f), 0.1f, 1f)));
                    o.Set("Vignette", "Smoothness", Fmt(Math.Clamp(F("Slope", 2f) * 0.2f, 0.1f, 1f)));
                    used.Add(label);
                }
                else if (key.Contains("chromaticaberration") || key == "ca.fx" || key.StartsWith("prism") || key.Contains("flexibleca") || key.Contains("chromatic_aberration"))
                { ca = Math.Max(ca, Math.Clamp(Any(0.5f, "Strength", "intensity", "amount", "aberration") * 0.4f, 0.1f, 1f)); used.Add(label); }
                else if (key.Contains("grain") || key.StartsWith("crystallis") || (key.Contains("noise") && !key.Contains("denoise") && !key.Contains("noiseblur")))
                { grain = Math.Max(grain, Math.Clamp(Any(0.5f, "Intensity", "amount", "strength") * 0.6f, 0.1f, 1f)); used.Add(label); }
                else if (key.Contains("sharp") || key.StartsWith("cas") || key.StartsWith("ccas") || key.StartsWith("rcas") || key.StartsWith("crcas") || key.StartsWith("unsharp") || key.StartsWith("finesharp") || key.StartsWith("jasharpen"))
                { sharp = Math.Max(sharp, Math.Clamp(Any(0.6f, "Sharpening", "sharp_strength", "curve_height", "strength", "amount", "intensity") * 0.55f, 0.1f, 0.6f)); used.Add(label + " (sharpen)"); }
                else if (key.Contains("lut") || key.Contains("colorgrading") || key.Contains("adaptivecolorgrading")) { skipped.Add(label + " (needs its LUT picture, not supported yet)"); continue; }
                else if (key.Contains("dof") || key.StartsWith("defocus") || key.StartsWith("tiltshift"))
                {
                    var c = Vec(s, "fADOF_AutofocusCenter", 0.5f);
                    if (!s.ContainsKey("fADOF_AutofocusCenter") && s.ContainsKey("AutoFocusPoint")) c = Vec(s, "AutoFocusPoint", 0.5f);
                    o.Set("DepthOfField", "Strength", "1.0");
                    o.Set("DepthOfField", "FocusDistance", "0");
                    o.Set("DepthOfField", "AutofocusCenter", $"{Fmt(c[0])}, {Fmt(c.Length > 1 ? c[1] : 0.5f)}");
                    o.Set("DepthOfField", "AutofocusRadius", Fmt(Math.Clamp(F("fADOF_AutofocusRadius", 0.6f), 0f, 1f)));
                    o.Set("DepthOfField", "MaxBlur", Fmt(Math.Clamp(F("fADOF_ShapeRadius", F("FarPlaneMaxBlur", 4f) * 3f), 2f, 40f)));
                    o.Set("DepthOfField", "FarCurve", Fmt(Math.Clamp(F("fADOF_FarBlurCurve", 1.5f), 0.1f, 8f)));
                    o.Set("DepthOfField", "NearCurve", Fmt(Math.Clamp(F("fADOF_NearBlurCurve", 6f), 0.1f, 12f)));
                    o.Set("DepthOfField", "BokehIntensity", Fmt(Math.Clamp(F("fADOF_BokehIntensity", 0.3f), 0f, 2f)));
                    used.Add(label + " (photos + video)");
                }
                else if (key.Contains("bloom") || key.Contains("glow") || key.Contains("xenon") || key.Contains("halation") || key.Contains("orton") || key.Contains("magichdr") || key.Contains("fakehdr"))
                {
                    float inten = Any(0.5f, "intensity", "amount", "strength");
                    float thr = Any(0.8f, "threshold");
                    if (thr > 4f) thr /= 255f; else if (thr > 1.5f) thr = 0.6f + thr * 0.05f;
                    Bloom(Math.Clamp(inten, 0.05f, 2f), Math.Clamp(thr, 0f, 1.5f), 0.7f);
                    used.Add(label + " (glow)");
                }
                else skipped.Add(label);
            }

            if (bloomI > 0)
            {
                o.Set("Bloom", "Intensity", Fmt(Math.Clamp(bloomI, 0.05f, 2f))); o.Set("Bloom", "Threshold", Fmt(bloomT));
                o.Set("Bloom", "SoftKnee", Fmt(bloomK)); o.Set("Bloom", "Scatter", Fmt(bloomS));
            }
            if (eye > 0) o.Set("EyeAdaptation", "Strength", Fmt(eye));
            if (sharp > 0) o.Set("Sharpen", "Amount", Fmt(sharp));
            if (ca > 0) o.Set("ChromaticAberration", "Intensity", Fmt(ca));
            if (grain > 0) o.Set("FilmGrain", "Intensity", Fmt(grain));
            if (flare > 0) { o.Set("LensFlare", "Intensity", Fmt(flare)); o.Set("LensFlare", "Spacing", "0.35"); o.Set("LensFlare", "Halo", "0.4"); o.Set("LensFlare", "Fringe", "0.5"); }
            if (dirt > 0) o.Set("LensDirt", "Intensity", Fmt(dirt));
            if (Math.Abs(temp) > 0.5f || Math.Abs(tint) > 0.5f) { o.Set("WhiteBalance", "Temperature", Fmt(Math.Clamp(temp, -100f, 100f))); o.Set("WhiteBalance", "Tint", Fmt(Math.Clamp(tint, -100f, 100f))); }
            if (Math.Abs(lift) > 0.001f && !o.Has("LiftGammaGain", "Lift")) o.Set("LiftGammaGain", "Lift", V(1, 1, 1, lift));
            if (Math.Abs(gain) > 0.001f && !o.Has("LiftGammaGain", "Gain")) o.Set("LiftGammaGain", "Gain", V(1, 1, 1, gain));
            if (Math.Abs(sat) > 0.5f || Math.Abs(contrast) > 0.5f || Math.Abs(exposure) > 0.01f)
            {
                o.Set("Color", "Saturation", Fmt(Math.Clamp(sat, -100f, 100f)));
                o.Set("Color", "Contrast", Fmt(Math.Clamp(contrast, -100f, 100f)));
                if (Math.Abs(exposure) > 0.01f) o.Set("Color", "Exposure", Fmt(exposure));
            }

            if (chain.Count > 0) o.Set("ReShadeChain", "Order", string.Join(", ", chain));
            float cost = o.Cost();
            bool tooHeavy = cost > 12f;
            string answer = "";
            if (File.Exists(outFile)) { var old = Ini.Parse(File.ReadAllText(outFile)); answer = old.Str("Import", "Answer", "") ?? ""; }
            if (!tooHeavy) answer = "";

            var sb = new StringBuilder();
            sb.AppendLine($"; Imported from ReShade preset \"{Path.GetFileName(file)}\" by QuestShade.");
            sb.AppendLine($"; Rebuilt for Quest: {(used.Count > 0 ? string.Join(", ", used) : "nothing")}");
            if (heavy.Count > 0) sb.AppendLine($"; Too heavy / no Quest version (skipped): {string.Join(", ", heavy)}");
            if (notNeeded.Count > 0) sb.AppendLine($"; Not needed on Quest (tools, anti-aliasing, overlays): {string.Join(", ", notNeeded)}");
            if (skipped.Count > 0) sb.AppendLine($"; Not supported yet (skipped): {string.Join(", ", skipped)}");
            sb.AppendLine("; Edit freely - it's only rewritten when the ReShade file itself changes.");
            sb.AppendLine("[Import]");
            sb.AppendLine("Source = " + Path.GetFileName(file));
            sb.AppendLine("Version = " + Version);
            sb.AppendLine("Heavy = " + (tooHeavy ? "true" : "false"));
            sb.AppendLine("Answer = " + answer);
            bool compress = answer == "compress";
            sb.AppendLine("Cost = " + cost.ToString("0.0", CultureInfo.InvariantCulture));
            sb.Append(o.Write(compress));
            File.WriteAllText(outFile, sb.ToString());
            Mod.Log.Msg($"ReShade: imported {Path.GetFileName(file)} -> {name}{Suffix} ({used.Count} effects rebuilt, {heavy.Count + skipped.Count} skipped{(tooHeavy ? ", heavy" : "")})");
        }

        private static string Key(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            var k = sb.ToString();
            foreach (var p in new[] { "limits", "limit" }) if (k.StartsWith(p)) { k = k.Substring(p.Length); break; }
            return k;
        }

        private static bool TryPort(string file, string outFile)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name.IndexOf("limit", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("beckcam", StringComparison.OrdinalIgnoreCase) < 0) return false;
            string want = Key(name);
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            const string prefix = "QuestShade.Ports.";
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith(prefix) || !res.EndsWith(".ini")) continue;
                string port = res.Substring(prefix.Length, res.Length - prefix.Length - 4);
                if (Key(port) != want) continue;
                string text;
                using (var s = asm.GetManifestResourceStream(res))
                using (var r = new StreamReader(s)) text = r.ReadToEnd();
                var sb = new StringBuilder();
                sb.AppendLine($"; Imported from ReShade preset \"{Path.GetFileName(file)}\": QuestShade's hand-tuned port of it.");
                sb.AppendLine("[Import]");
                sb.AppendLine("Source = " + Path.GetFileName(file));
                sb.AppendLine("Version = " + Version);
                sb.AppendLine("Heavy = false");
                sb.AppendLine("Port = " + port);
                sb.AppendLine(text.TrimStart('﻿'));
                File.WriteAllText(outFile, sb.ToString());
                Mod.Log.Msg($"ReShade: {Path.GetFileName(file)} -> hand-tuned port '{port}'");
                return true;
            }
            return false;
        }

        public static List<string> PendingHeavy(Config cfg)
        {
            var list = new List<string>();
            foreach (var p in cfg.Presets)
            {
                if (!p.EndsWith(Suffix)) continue;
                try
                {
                    var ini = Ini.Parse(File.ReadAllText(Path.Combine(Config.PresetDir, p + ".ini")));
                    if (ini.Bool("Import", "Heavy", false) && string.IsNullOrEmpty(ini.Str("Import", "Answer", ""))) list.Add(p);
                }
                catch { }
            }
            return list;
        }

        public static void Answer(string preset, bool compress)
        {
            string path = Path.Combine(Config.PresetDir, preset + ".ini");
            var lines = File.ReadAllLines(path).ToList();
            int i = lines.FindIndex(l => l.TrimStart().StartsWith("Answer", StringComparison.OrdinalIgnoreCase));
            string a = "Answer = " + (compress ? "compress" : "keep");
            if (i >= 0) lines[i] = a; else lines.Insert(0, a);
            if (compress)
            {
                var keep = new HashSet<string>(ColourSections.Concat(new[] { "Import" }), StringComparer.OrdinalIgnoreCase);
                var outL = new List<string>(); bool on = true;
                foreach (var l in lines)
                {
                    var t = l.Trim();
                    if (t.StartsWith("[") && t.EndsWith("]")) on = keep.Contains(t.Substring(1, t.Length - 2));
                    if (on || t.StartsWith(";")) outL.Add(l);
                }
                lines = outL;
            }
            File.WriteAllLines(path, lines);
        }

        private static readonly string[] ColourSections = { "Vignette", "CRT", "Scanlines", "Noise", "FilmGrain", "Glitch", "ReShadeChain", "Color", "WhiteBalance", "LiftGammaGain", "SplitToning", "ChannelMixer",
            "ShadowsMidtonesHighlights", "Vignette", "Tonemapping" };

        private static Dictionary<string, Dictionary<string, string>> ReadReShade(string text)
        {
            var d = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); d[""] = cur;
            foreach (var raw in text.Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith(";") || l.StartsWith("#")) continue;
                if (l.StartsWith("[") && l.EndsWith("]")) { var n = l.Substring(1, l.Length - 2); if (!d.TryGetValue(n, out cur)) d[n] = cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); continue; }
                int eq = l.IndexOf('='); if (eq <= 0) continue;
                cur[l.Substring(0, eq).Trim()] = l.Substring(eq + 1).Trim();
            }
            return d;
        }

        private static List<(string tech, string fx)> Techniques(Dictionary<string, Dictionary<string, string>> rs)
        {
            var list = new List<(string, string)>();
            string line = null;
            foreach (var sec in rs.Values) if (sec.TryGetValue("Techniques", out var t)) { line = t; break; }
            if (string.IsNullOrEmpty(line)) return list;
            foreach (var part in line.Split(','))
            {
                var p = part.Trim(); if (p.Length == 0) continue;
                int at = p.IndexOf('@');
                var e = at > 0 ? (p.Substring(0, at), p.Substring(at + 1)) : (p, p + ".fx");
                if (!list.Contains(e)) list.Add(e);
            }
            return list;
        }

        private static string Define(Dictionary<string, Dictionary<string, string>> rs, string name)
        {
            foreach (var sec in rs.Values)
                if (sec.TryGetValue("PreprocessorDefinitions", out var defs))
                    foreach (var part in defs.Split(','))
                    {
                        int eq = part.IndexOf('=');
                        if (eq > 0 && part.Substring(0, eq).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                            return part.Substring(eq + 1).Trim().Trim('"');
                    }
            return null;
        }

        private static string FindTexture(string tex, string near)
        {
            foreach (var dir in new[] { Path.Combine(Dir, "Textures"), Dir, near })
            {
                if (dir == null) continue;
                var p = Path.Combine(dir, Path.GetFileName(tex));
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private static float Num(Dictionary<string, string> s, string k, float def)
            => s.TryGetValue(k, out var v) && float.TryParse(v.Split(',')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;

        private static float FirstNum(Dictionary<string, string> s, string[] parts, float def)
        {
            foreach (var kv in s)
                foreach (var p in parts)
                    if (kv.Key.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 &&
                        float.TryParse(kv.Value.Split(',')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
            return def;
        }

        private static float[] Vec(Dictionary<string, string> s, string k, float def)
        {
            if (!s.TryGetValue(k, out var v)) return new[] { def, def, def };
            var parts = v.Split(',').Select(x => float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def).ToArray();
            return parts.Length >= 3 ? parts : parts.Length == 1 ? new[] { parts[0], parts[0], parts[0] } : new[] { parts[0], parts.Length > 1 ? parts[1] : def, def };
        }

        private static string Fmt(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
        private static string V(float a, float b, float c, float d) => $"{Fmt(a)}, {Fmt(b)}, {Fmt(c)}, {Fmt(d)}";
        private static string V3w(float[] v, float w) => V(v[0], v[1], v[2], w);

        private sealed class Out
        {
            private readonly List<(string sec, List<(string k, string v)> kv)> _s = new List<(string, List<(string, string)>)>();
            public void Set(string sec, string k, string v)
            {
                var e = _s.FirstOrDefault(x => x.sec == sec);
                if (e.kv == null) { e = (sec, new List<(string, string)>()); _s.Add(e); }
                int i = e.kv.FindIndex(x => x.k == k);
                if (i >= 0) e.kv[i] = (k, v); else e.kv.Add((k, v));
            }
            public bool Has(string sec, string k)
            {
                var e = _s.FirstOrDefault(x => x.sec == sec);
                return e.kv != null && e.kv.Any(x => x.k == k);
            }
            public float Cost()
            {
                float c = 0;
                foreach (var (sec, _) in _s)
                    c += sec switch
                    {
                        "Sharpen" => 2f, "ChromaticAberration" => 2f, "LensDistortion" => 1.5f, "Glitch" => 1f, "CRT" => 1f,
                        "FilmGrain" => 1f, "Noise" => 0.5f, "Scanlines" => 0.5f, "Vignette" => 0.3f,
                        _ => sec.StartsWith("RS ") || sec == "ReShadeChain" ? 0f : 0.2f,
                    };
                return c;
            }

            public string Write(bool colourOnly)
            {
                var sb = new StringBuilder();
                foreach (var (sec, kv) in _s)
                {
                    if (colourOnly && Array.IndexOf(ColourSections, sec) < 0) continue;
                    sb.AppendLine($"[{sec}]");
                    foreach (var (k, v) in kv) sb.AppendLine($"{k} = {v}");
                }
                return sb.ToString();
            }
        }
    }
}
