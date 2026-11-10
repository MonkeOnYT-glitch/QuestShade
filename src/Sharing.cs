using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnityEngine.Networking;

namespace QuestShade
{
    internal static class Sharing
    {
        public const string DefaultServer = "https://questshade-presets.questshade.workers.dev";

        public sealed class Entry
        {
            public string Id, Name, Author, Ini;
            public int Likes, Downloads;
        }

        private static Config _cfg;
        private static readonly List<(UnityWebRequest req, Action<string, string> done)> _pending = new List<(UnityWebRequest, Action<string, string>)>();

        public static void Init(Config cfg) => _cfg = cfg;

        private static readonly System.Text.RegularExpressions.Regex Slop = new System.Text.RegularExpressions.Regex(
            @"^(my preset|new preset|preset|untitled|shared preset|test|testing|copy|custom|reshade|bodycam|cctv|cinematic|clean cam|found footage|natural|night ops|night vision|noir|punchy|vivid|warm)( edit)?( ?\(?\d+\)?)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        public static bool IsDefaultName(string name)
        {
            var s = System.Text.RegularExpressions.Regex.Replace((name ?? "").Trim(), @"\s+", " ");
            return s.Length < 3 || Slop.IsMatch(s);
        }
        public static bool Busy => _pending.Count > 0;

        public static void Tick()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var (req, done) = _pending[i];
                if (!req.isDone) continue;
                _pending.RemoveAt(i);
                string body = null, err = null;
                try
                {
                    body = req.downloadHandler?.text;
                    if (req.result != UnityWebRequest.Result.Success)
                        err = ErrorText(body) ?? (req.responseCode == 0 ? "Can't reach the preset server (no internet?)" : $"Server error {req.responseCode}");
                }
                catch (Exception e) { err = e.Message; }
                finally { try { req.Dispose(); } catch { } }
                try { done(err == null ? body : null, err); }
                catch (Exception e) { Mod.Log.Warning("Preset browser: " + e.Message); }
            }
        }

        private static string ErrorText(string body)
        {
            try { using var d = JsonDocument.Parse(body ?? ""); return d.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null; }
            catch { return null; }
        }

        private static void Send(string method, string path, string json, Action<string, string> done)
        {
            var server = _cfg?.ShareServer ?? DefaultServer;
            string url = server + path;
            if (json != null)
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                url += (path.Contains("?") ? "&" : "?") + "d=" + b64;
            }
            var req = UnityWebRequest.Get(url);
            req.timeout = 20;
            req.SetRequestHeader("User-Agent", "QuestShade");
            req.SetRequestHeader("X-QuestShade-Client", _cfg?.ShareClientId ?? "");
            req.SendWebRequest();
            _pending.Add((req, done));
        }


        public static void List(string sort, string search, Action<List<Entry>, string> done)
        {
            string q = "/api/presets?sort=" + Uri.EscapeDataString(sort ?? "new");
            if (!string.IsNullOrWhiteSpace(search)) q += "&q=" + Uri.EscapeDataString(search.Trim());
            Send("GET", q, null, (body, err) =>
            {
                if (err != null) { done(null, err); return; }
                var list = new List<Entry>();
                using var d = JsonDocument.Parse(body);
                foreach (var e in d.RootElement.GetProperty("presets").EnumerateArray()) list.Add(Read(e));
                done(list, null);
            });
        }

        public static void Get(string id, Action<Entry, string> done)
            => Send("GET", "/api/presets/" + Uri.EscapeDataString(id), null, (body, err) =>
            {
                if (err != null) { done(null, err); return; }
                using var d = JsonDocument.Parse(body);
                done(Read(d.RootElement), null);
            });

        public static void Publish(string name, string author, string ini, Action<string, string> done)
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = name, ["author"] = author, ["ini"] = CleanIni(ini) });
            Send("GET", "/api/presets/share", json, (body, err) =>
            {
                if (err != null) { done(null, err); return; }
                using var d = JsonDocument.Parse(body);
                done(d.RootElement.GetProperty("id").GetString(), null);
            });
        }

        public static void Like(string id, Action<int, string> done)
            => Send("GET", "/api/presets/" + Uri.EscapeDataString(id) + "/like", null, (body, err) =>
            {
                if (err != null) { done(0, err); return; }
                using var d = JsonDocument.Parse(body);
                done(d.RootElement.TryGetProperty("likes", out var l) ? l.GetInt32() : 0, null);
            });

        public static void Report(string id, Action<string> done)
            => Send("GET", "/api/presets/" + Uri.EscapeDataString(id) + "/report", null, (body, err) => done(err));

        private static Entry Read(JsonElement e) => new Entry
        {
            Id = Str(e, "id"), Name = Str(e, "name"), Author = Str(e, "author"), Ini = Str(e, "ini"),
            Likes = e.TryGetProperty("likes", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0,
            Downloads = e.TryGetProperty("downloads", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
        };
        private static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;


        private static readonly Dictionary<string, string[]> Allowed = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Color"] = new[] { "Enabled", "Exposure", "Contrast", "Saturation", "HueShift", "ColorFilter" },
            ["WhiteBalance"] = new[] { "Enabled", "Temperature", "Tint" },
            ["LiftGammaGain"] = new[] { "Enabled", "Lift", "Gamma", "Gain" },
            ["ShadowsMidtonesHighlights"] = new[] { "Enabled", "Shadows", "Midtones", "Highlights", "ShadowsStart", "ShadowsEnd", "HighlightsStart", "HighlightsEnd" },
            ["SplitToning"] = new[] { "Enabled", "Shadows", "Highlights", "Balance" },
            ["ChannelMixer"] = new[] { "Enabled", "Red", "Green", "Blue" },
            ["Tonemapping"] = new[] { "Enabled", "Mode" },
            ["Sharpen"] = new[] { "Enabled", "Amount" },
            ["FilmGrain"] = new[] { "Enabled", "Intensity", "Response", "Size", "Colored" },
            ["ChromaticAberration"] = new[] { "Enabled", "Intensity" },
            ["LensDistortion"] = new[] { "Enabled", "Intensity", "Scale" },
            ["Vignette"] = new[] { "Enabled", "Intensity", "Smoothness", "Rounded", "Color", "Center" },
            ["Bloom"] = new[] { "Enabled", "Intensity", "Threshold", "SoftKnee", "Scatter" },
            ["DepthOfField"] = new[] { "Enabled", "Strength", "FocusDistance", "MaxBlur", "FarCurve", "NearCurve", "NearStrength", "BokehIntensity", "AutofocusRadius" },
            ["Scanlines"] = new[] { "Enabled", "Intensity", "Count", "Speed", "RollingBar" },
            ["CRT"] = new[] { "Enabled", "Curvature" },
            ["Noise"] = new[] { "Enabled", "Intensity" },
            ["Glitch"] = new[] { "Enabled", "Amount", "Rows", "Speed" },
            ["Posterize"] = new[] { "Enabled", "Levels" },
            ["EyeAdaptation"] = new[] { "Enabled", "Strength" },
            ["LensFlare"] = new[] { "Enabled", "Intensity", "Spacing", "Halo", "Fringe" },
            ["LensDirt"] = new[] { "Enabled", "Intensity" },
            ["GodRays"] = new[] { "Enabled", "Intensity", "Length", "Threshold" },
        };
        private static readonly HashSet<string> Words = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "true", "false", "on", "off", "aces", "neutral", "none" };

        public static string CleanIni(string text)
        {
            var ini = Ini.Parse(text ?? "");
            var sb = new StringBuilder();
            foreach (var kv in Allowed)
            {
                if (!ini.Has(kv.Key)) continue;
                var sec = ini.Section(kv.Key);
                var lines = new StringBuilder();
                foreach (var key in kv.Value)
                    if (sec.TryGetValue(key, out var v) && SafeValue(v)) lines.Append(key).Append(" = ").Append(v.Trim()).Append('\n');
                if (lines.Length > 0) sb.Append('[').Append(kv.Key).Append("]\n").Append(lines);
            }
            return sb.ToString();
        }

        private static bool SafeValue(string v)
        {
            v = (v ?? "").Trim();
            if (v.Length == 0 || v.Length > 64) return false;
            if (Words.Contains(v)) return true;
            if (v[0] == '#') { for (int i = 1; i < v.Length; i++) if (!Uri.IsHexDigit(v[i])) return false; return v.Length is 7 or 9; }
            foreach (var part in v.Split(','))
                if (!float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || float.IsNaN(f) || float.IsInfinity(f) || Math.Abs(f) > 100000f)
                    return false;
            return true;
        }
    }
}
