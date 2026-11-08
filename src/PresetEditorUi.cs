using System;
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
        private static Page _light, _color, _lens;

        private sealed class Knob
        {
            public string Label, Section, Key;
            public float Min, Max, Step;
            public int Vec4W;
            public Func<Ini, float> Off;
        }

        private static readonly Knob[] LightKnobs =
        {
            new Knob { Label = "Exposure",   Section = "Color", Key = "Exposure",   Min = -2f,   Max = 2f,   Step = 0.1f },
            new Knob { Label = "Contrast",   Section = "Color", Key = "Contrast",   Min = -60f,  Max = 60f,  Step = 2f },
            new Knob { Label = "Shadows",    Section = "LiftGammaGain", Key = "Lift", Min = -0.2f, Max = 0.2f, Step = 0.01f, Vec4W = 1 },
            new Knob { Label = "Midtones",   Section = "LiftGammaGain", Key = "Gamma", Min = -0.5f, Max = 0.5f, Step = 0.02f, Vec4W = 1 },
            new Knob { Label = "Highlights", Section = "LiftGammaGain", Key = "Gain", Min = -0.4f, Max = 0.4f, Step = 0.02f, Vec4W = 1 },
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
            new Knob { Label = "Sharpness",   Section = "Sharpen",  Key = "Amount",    Min = 0f, Max = 1f, Step = 0.05f },
            new Knob { Label = "Bloom (glow)", Section = "Bloom",   Key = "Intensity", Min = 0f, Max = 3f, Step = 0.1f,
                       Off = p => 0.6f },
            new Knob { Label = "Vignette",    Section = "Vignette", Key = "Intensity", Min = 0f, Max = 1f, Step = 0.05f },
            new Knob { Label = "Film grain",  Section = "FilmGrain", Key = "Intensity", Min = 0f, Max = 1f, Step = 0.05f },
            new Knob { Label = "Colour fringe", Section = "ChromaticAberration", Key = "Intensity", Min = 0f, Max = 1f, Step = 0.05f },
        };

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Build(Page effects, Mod mod, Config cfg)
        {
            _mod = mod; _cfg = cfg;
            var ed = effects.CreatePage("Edit preset", Color.yellow);
            ed.CreateFunction("New preset", Color.green, () => mod.NewPreset(false));
            ed.CreateFunction("Copy this preset", Color.cyan, () => mod.NewPreset(true));
            _light = ed.CreatePage("Light", Color.white);
            _color = ed.CreatePage("Colour", Color.white);
            _lens = ed.CreatePage("Lens", Color.white);
            ed.CreateFunction("Delete this preset", Color.red, DeletePressed);
            Refresh();
        }

        private static float _deleteArmedAt = -10f;
        private static void DeletePressed()
        {
            float now = Time.realtimeSinceStartup;
            if (Config.IsBuiltIn(_cfg.PresetName)) { BoneMenuUi.Notify($"\"{_cfg.PresetName}\" comes with QuestShade and can't be deleted", 2.5f); return; }
            if (now - _deleteArmedAt > 3f)
            {
                _deleteArmedAt = now;
                BoneMenuUi.Notify($"Press again to delete \"{_cfg.PresetName}\"", 3f);
                return;
            }
            _deleteArmedAt = -10f;
            _mod.DeletePreset();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Refresh()
        {
            if (_light == null) return;
            Fill(_light, LightKnobs);
            Fill(_color, ColorKnobs);
            Fill(_lens, LensKnobs);
        }

        private static void Fill(Page page, Knob[] knobs)
        {
            page.RemoveAll();
            var p = _cfg.Preset ?? Ini.Parse("");
            page.CreateFunction("Editing: " + _cfg.PresetName, Color.gray,
                () => BoneMenuUi.Notify($"Changes save to Presets/{_cfg.PresetName}.ini as you go. Effects > Edit preset > New preset starts a fresh one.", 4f));
            foreach (var k in knobs)
            {
                var knob = k;
                page.CreateFloat(knob.Label, Color.white, Current(p, knob), knob.Step, knob.Min, knob.Max, v => Changed(knob, v));
            }
        }

        private static float Current(Ini p, Knob k)
        {
            if (!p.Enabled(k.Section)) return k.Off != null && _cfg.BloomDofAll ? k.Off(p) : 0f;
            if (k.Vec4W == 1) return p.Vec4(k.Section, k.Key, new Vector4(1, 1, 1, 0)).w;
            float def = k.Section == "Sharpen" ? 0.5f : 0f;
            return p.Float(k.Section, k.Key, def);
        }

        private static void Changed(Knob k, float v)
        {
            v = Mathf.Clamp(v, k.Min, k.Max);
            v = (float)Math.Round(v, 3);
            string value;
            if (k.Vec4W == 1)
            {
                var cur = (_cfg.Preset ?? Ini.Parse("")).Vec4(k.Section, k.Key, new Vector4(1, 1, 1, 0));
                value = $"{F(cur.x)}, {F(cur.y)}, {F(cur.z)}, {F(v)}";
            }
            else value = F(v);
            _mod.EditPreset(k.Section, k.Key, value);
        }

        private static string F(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
