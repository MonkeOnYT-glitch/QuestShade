using System;
using UnityEngine;

namespace QuestShade
{
    // Slow-mo modes (a little / more / full) through BONELAB's own time manager, so the player rig, hands and the game's
    // slow-mo sounds behave exactly like the built-in slow-mo. Plain Unity time scale is only a fallback if that call fails.
    // While it's on, the in-game camera can ease into a cinematic zoom.
    internal static class SlowMo
    {
        public static readonly string[] Names = { "Off", "A little", "More", "Full" };
        public static int Level { get; private set; }
        private static bool _fallback, _logged;
        private static float _baseFixed = -1f, _zoom = 1f;

        public static void Set(int level)
        {
            level = Mathf.Clamp(level, 0, 3);
            Level = level;
            if (!_fallback)
            {
                try
                {
                    if (level == 0) Il2CppSLZ.Marrow.TimeManager.RESET_TIMESCALE();
                    else
                    {
                        if (!Il2CppSLZ.Marrow.TimeManager.slowMoEnabled) Il2CppSLZ.Marrow.TimeManager.slowMoEnabled = true;
                        Il2CppSLZ.Marrow.TimeManager.STEP_TIMESCALE(level);
                    }
                    Mod.Log.Msg($"Slow-mo: {Names[level]} (game step {Il2CppSLZ.Marrow.TimeManager.CurrentTimeScaleStep})");
                    return;
                }
                catch (Exception e) { _fallback = true; Mod.Log.Warning("Slow-mo: game time manager unavailable, using plain time scale: " + e.Message); }
            }
            Mod.Log.Msg($"Slow-mo: {Names[level]} (time scale)");
        }

        public static void Cycle() => Set((Level + 1) % 4);

        public static void OnLevel() { if (Level != 0) Set(0); }

        public static void Tick(float unscaledDt)
        {
            if (_fallback)
            {
                float target = Level switch { 1 => 0.6f, 2 => 0.35f, 3 => 0.15f, _ => 1f };
                if (Time.timeScale == 0f) return; // paused
                if (Level == 0 && Mathf.Approximately(Time.timeScale, 1f)) { if (_baseFixed > 0f) { Time.fixedDeltaTime = _baseFixed; _baseFixed = -1f; } }
                else
                {
                    if (_baseFixed < 0f) _baseFixed = Time.fixedDeltaTime / Mathf.Max(0.01f, Time.timeScale);
                    float s = Mathf.MoveTowards(Time.timeScale, target, unscaledDt * 2.5f);
                    Time.timeScale = s; Time.fixedDeltaTime = _baseFixed * s;
                }
            }
            // Cinematic zoom follows how slowed the game really is (also when BONELAB's own slow-mo is used).
            float slow = Mathf.Clamp01((1f - Time.timeScale) / 0.85f);
            float want = 1f - slow * 0.3f;
            _zoom = Mathf.MoveTowards(_zoom, Time.timeScale == 0f ? _zoom : want, unscaledDt * 0.5f);
            if (!_logged && slow > 0.2f) { _logged = true; Mod.Log.Msg($"Slow-mo running (time scale {Time.timeScale:0.00})"); }
        }

        // Camera field of view multiplier: down to 0.7 at full slow-mo (a slow push in).
        public static float Zoom(Config cfg) => cfg.SlowMoZoom && Mod.BetaOn ? _zoom : 1f;
    }
}
