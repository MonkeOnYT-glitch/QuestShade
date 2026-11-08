using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using BoneLib.BoneMenu;
using UnityEngine;

namespace QuestShade
{
    internal static class PresetEditorUi
    {
        private static Mod _mod;
        private static Config _cfg;
        private static Page _editor, _light, _color, _lens, _retro;

        private sealed class Knob
        {
            public string Label, Section, Key;
            public float Min, Max, Step;
            public float Def;
            public bool Vec4W;
            public float? Off;
        }

        private static readonly Dictionary<string, string> Primary = new Dictionary<string, string>
        {
            ["Sharpen"] = "Amount", ["Bloom"] = "Intensity", ["Vignette"] = "Intensity", ["FilmGrain"] = "Intensity",
            ["ChromaticAberration"] = "Intensity", ["Scanlines"] = "Intensity", ["CRT"] = "Curvature", ["Noise"] = "Intensity",
            ["Glitch"] = "Amount", ["Posterize"] = "Levels",
        };

        private static readonly Knob[] LightKnobs =
        {
            new Knob { Label = "Exposure",   Section = "Color", Key = "Exposure",   Min = -2f,   Max = 2f,   Step = 0.1f },
            new Knob { Label = "Contrast",   Section = "Color", Key = "Contrast",   Min = -60f,  Max = 60f,  Step = 2f },
            new Knob { Label = "Shadows",    Section = "LiftGammaGain", Key = "Lift",  Min = -0.2f, Max = 0.2f, Step = 0.01f, Vec4W = true },
            new Knob { Label = "Midtones",   Section = "LiftGammaGain", Key = "Gamma", Min = -0.5f, Max = 0.5f, Step = 0.02f, Vec4W = true },
            new Knob { Label = "Highlights", Section = "LiftGammaGain", Key = "Gain",  Min = -0.4f, Max = 0.4f, Step = 0.02f, Vec4W = true },
        };
        private static readonly Knob[] ColorKnobs =
        {
            new Knob { Label = "Saturation", Section = "Color", Key = "Saturation", Min = -100f, Max = 100f, Step = 5f },
            new Knob { Label = "Warmth",     Section = "WhiteBalance", Key = "Temperature", Min = -60f, Max = 60f, Step = 2f },
            new Knob { Label = "Tint (green / pink)", Section = "WhiteBalance", Key = "Tint", Min = -60f, Max = 60f, Step = 2f },
            new Knob { Label = "Hue shift",  Section = "Color", Key = "HueShift",   Min = -180f, Max = 180f, Step = 5f },
        };
        private static readonly Knob[] LensKnobs =
        {
            new Knob { Label = "Sharpness",     Section = "Sharpen",  Key = "Amount",    Min = 0f, Max = 1f, Step = 0.05f, Def = 0.5f },
            new Knob { Label = "Bloom (glow)",  Section = "Bloom",    Key = "Intensity", Min = 0f, Max = 3f, Step = 0.1f },
            new Knob { Label = "Vignette",      Section = "Vignette", Key = "Intensity", Min = 0f, Max = 1f, Step = 0.05f },
            new Knob { Label = "Colour fringe", Section = "ChromaticAberration", Key = "Intensity", Min = 0f, Max = 1f, Step = 0.05f },
        };
        private static readonly Knob[] RetroKnobs =
        {
            new Knob { Label = "Film grain",     Section = "FilmGrain", Key = "Intensity", Min = 0f, Max = 1f, Step = 0.05f },
            new Knob { Label = "Grain size",     Section = "FilmGrain", Key = "Size", Min = 0.5f, Max = 4f, Step = 0.25f, Def = 1.5f },
            new Knob { Label = "CRT scanlines",  Section = "Scanlines", Key = "Intensity", Min = 0f, Max = 1f, Step = 0.05f, Def = 0.3f },
            new Knob { Label = "Scanline count", Section = "Scanlines", Key = "Count", Min = 100f, Max = 720f, Step = 20f, Def = 240f },
            new Knob { Label = "Rolling bar",    Section = "Scanlines", Key = "RollingBar", Min = 0f, Max = 1f, Step = 0.05f },
            new Knob { Label = "CRT curve",      Section = "CRT", Key = "Curvature", Min = 0f, Max = 1f, Step = 0.05f, Def = 0.3f },
            new Knob { Label = "TV noise",       Section = "Noise", Key = "Intensity", Min = 0f, Max = 0.5f, Step = 0.02f, Def = 0.1f },
            new Knob { Label = "Glitch",         Section = "Glitch", Key = "Amount", Min = 0f, Max = 1f, Step = 0.05f, Def = 0.1f },
            new Knob { Label = "Posterize (0 = off)", Section = "Posterize", Key = "Levels", Min = 0f, Max = 32f, Step = 1f },
        };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Build(Page effects, Mod mod, Config cfg)
        {
            _mod = mod; _cfg = cfg;
            _editor = effects.CreatePage("Edit preset", Color.yellow);
            _light = _editor.CreatePage("Light", Color.white, 0, false);
            _color = _editor.CreatePage("Colour", Color.white, 0, false);
            _lens = _editor.CreatePage("Lens", Color.white, 0, false);
            _retro = _editor.CreatePage("Retro (CRT, grain)", Color.white, 0, false);
            BrowserUi.Build(effects, mod, cfg);
            Refresh();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Refresh()
        {
            if (_editor == null) return;
            _editor.RemoveAll();
            bool builtIn = Config.IsBuiltIn(_cfg.PresetName);
            _editor.CreateFunction("New preset", Color.green, () => _mod.NewPreset(false));
            if (builtIn)
                _editor.CreateFunction($"\"{_cfg.PresetName}\" (built-in: Copy it to rename)", Color.gray,
                    () => BoneMenuUi.Notify("Built-in presets keep their name. Press \"Copy this preset\" and name the copy.", 3f));
            else
                _editor.CreateString("Name", Color.yellow, _cfg.PresetName, v => _mod.RenamePreset(v));
            _editor.CreateFunction("Copy this preset", Color.cyan, () => _mod.NewPreset(true));
            _editor.CreatePageLink(_light);
            _editor.CreatePageLink(_color);
            _editor.CreatePageLink(_lens);
            _editor.CreatePageLink(_retro);
            _editor.CreateFunction("Share publicly", new Color(0.35f, 0.4f, 0.95f), SharePressed);
            _editor.CreateFunction("Delete this preset", Color.red, DeletePressed);

            Fill(_light, LightKnobs);
            Fill(_color, ColorKnobs);
            Fill(_lens, LensKnobs);
            Fill(_retro, RetroKnobs);
        }

        private static void DeletePressed()
        {
            var name = _cfg.PresetName;
            if (Config.IsBuiltIn(name)) { BoneMenuUi.Notify($"\"{name}\" comes with QuestShade and can't be deleted", 2.5f); return; }
            Menu.DisplayDialog("Delete preset?", $"\"{name}\" will be gone for good.", null, () => _mod.DeletePreset());
        }

        private static void SharePressed()
        {
            var name = _cfg.PresetName;
            if (Config.IsBuiltIn(name)) { BoneMenuUi.Notify("That one already comes with QuestShade. Share your own presets.", 3f); return; }
            if (Sharing.CleanIni(_cfg.CurrentPresetText()).Length == 0) { BoneMenuUi.Notify("Move a few sliders first: this preset doesn't do anything yet", 3f); return; }
            var author = string.IsNullOrWhiteSpace(_cfg.ShareAuthor) ? "Anonymous" : _cfg.ShareAuthor.Trim();
            Menu.DisplayDialog("Share publicly?",
                $"\"{name}\" by {author} will show up in everyone's Preset browser. (Set your creator name in Effects > Preset browser.)",
                null, () => _mod.SharePreset());
        }

        private static void Fill(Page page, Knob[] knobs)
        {
            page.RemoveAll();
            var p = _cfg.Preset ?? Ini.Parse("");
            page.CreateFunction("Editing: " + _cfg.PresetName, Color.gray,
                () => BoneMenuUi.Notify($"Changes save to Presets/{_cfg.PresetName}.ini as you go.", 3f));
            foreach (var k in knobs)
            {
                var knob = k;
                page.CreateFloat(knob.Label, Color.white, Current(p, knob), knob.Step, knob.Min, knob.Max, v => Changed(knob, v));
            }
        }

        private static bool IsPrimary(Knob k) => !Primary.TryGetValue(k.Section, out var key) || key == k.Key;

        private static float Current(Ini p, Knob k)
        {
            if (!p.Enabled(k.Section))
            {
                if (k.Section == "Bloom" && _cfg.BloomDofAll) return 0.6f;
                return IsPrimary(k) ? 0f : k.Def;
            }
            if (k.Vec4W) return p.Vec4(k.Section, k.Key, new Vector4(1, 1, 1, 0)).w;
            return p.Float(k.Section, k.Key, k.Def);
        }

        private static void Changed(Knob k, float v)
        {
            v = (float)Math.Round(Mathf.Clamp(v, k.Min, k.Max), 3);
            var p = _cfg.Preset ?? Ini.Parse("");
            if (!p.Has(k.Section) && !IsPrimary(k)) _mod.EditPreset(k.Section, Primary[k.Section], "0");
            string value;
            if (k.Vec4W)
            {
                var cur = p.Vec4(k.Section, k.Key, new Vector4(1, 1, 1, 0));
                value = $"{F(cur.x)}, {F(cur.y)}, {F(cur.z)}, {F(v)}";
            }
            else value = F(v);
            _mod.EditPreset(k.Section, k.Key, value);
        }

        private static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
    }

    internal static class BrowserUi
    {
        private enum Sort { Newest, Top }
        private static Page _page;
        private static Mod _mod;
        private static Config _cfg;
        private static Sort _sort = Sort.Newest;
        private static string _search = "";
        private static List<Sharing.Entry> _results;
        private static string _status = "Press Refresh to load presets";

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Build(Page effects, Mod mod, Config cfg)
        {
            _mod = mod; _cfg = cfg;
            _page = effects.CreatePage("Preset browser", new Color(0.35f, 0.4f, 0.95f));
            Draw();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Load()
        {
            if (_page == null) return;
            _status = "Loading...";
            Draw();
            Sharing.List(_sort == Sort.Top ? "top" : "new", _search, (list, err) =>
            {
                _results = list;
                _status = err != null ? "Couldn't load: " + err : list.Count == 0 ? "Nothing here yet. Be the first: Edit preset > Share publicly" : $"{list.Count} presets";
                Draw();
            });
        }

        private static void Draw()
        {
            _page.RemoveAll();
            _page.CreateFunction("Refresh", Color.green, Load);
            _page.CreateEnum("Sort", Color.white, _sort, v => { _sort = (Sort)v; Load(); });
            _page.CreateString("Search", Color.white, _search, v => { _search = v ?? ""; Load(); });
            _page.CreateString("Your creator name", Color.yellow, _cfg.ShareAuthor, v => _mod.SetShareAuthor(v));
            _page.CreateFunction("Like the preset you're using", Color.magenta, () => _mod.LikeCurrentPreset());
            _page.CreateFunction("Report the preset you're using", Color.red, () => _mod.ReportCurrentPreset());
            _page.CreateFunction(_status, Color.gray, () => { });
            if (_results == null) return;
            foreach (var e in _results)
            {
                var entry = e;
                string label = $"{Short(entry.Name, 24)}  by {Short(entry.Author, 14)}  ({entry.Likes} likes)";
                _page.CreateFunction(label, Color.white, () =>
                    Menu.DisplayDialog(Short(entry.Name, 30),
                        $"by {Short(entry.Author, 24)} - {entry.Likes} likes - {entry.Downloads} downloads\nGet it and switch to it?",
                        null, () => _mod.DownloadShared(entry)));
            }
        }

        private static string Short(string s, int n)
        {
            s = (s ?? "").Replace("<", "").Replace(">", "").Replace((char)10, (char)32).Replace((char)13, (char)32).Trim();
            return s.Length <= n ? s : s.Substring(0, n - 3) + "...";
        }
    }
}
