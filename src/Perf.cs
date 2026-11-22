using UnityEngine;
using UnityEngine.XR;

namespace QuestShade
{
    // Keeps the game at its frame rate. When frames run long for a few seconds, QuestShade gives back GPU time in steps:
    //   level 1: shadow map at half resolution, flashlight "engine" shadows off, camera preview slower
    //   level 2: shadows update every other frame, flashlight shadow map off, real lights off for this level
    //   level 3: shadows off (and the scene-depth pass they need), preview / recording extras off
    // It steps back up once the game has had spare time for a while. A level that keeps re-tripping is held for 2 minutes.
    internal static class Perf
    {
        public static int Level { get; private set; }
        private static float _ema = -1f, _overFor, _underFor, _target = 1f / 72f;
        private static float _nextRefresh, _ignoreUntil, _holdUntil;
        private static readonly float[] _recoveredAt = new float[4];
        private const float Behind = 1.25f, Spare = 1.08f;
        private static readonly float[] TripAfter = { 3f, 4f, 4f };

        public static void OnSceneLoaded() { _ignoreUntil = Time.unscaledTime + 20f; _ema = -1f; _overFor = _underFor = 0f; }

        public static void Tick(float dt)
        {
            if (dt <= 0f || dt > 0.5f) return;
            float now = Time.unscaledTime;
            if (now < _ignoreUntil) return;
            if (now > _nextRefresh)
            {
                _nextRefresh = now + 5f;
                float hz = 72f;
                try { if (XRDevice.refreshRate > 30f) hz = XRDevice.refreshRate; } catch { }
                _target = 1f / hz;
            }
            _ema = _ema < 0f ? dt : Mathf.Lerp(_ema, dt, 0.05f);
            float ratio = _ema / _target;
            if (Level < 3 && ratio > Behind) { _overFor += dt; _underFor = 0f; }
            else if (Level > 0 && ratio < Spare) { _underFor += dt; _overFor = 0f; }
            else { _overFor = Mathf.Max(0f, _overFor - dt); _underFor = 0f; }

            if (Level < 3 && _overFor > TripAfter[Level])
            {
                Level++; _overFor = 0f;
                // Came straight back to a level we just left: the game needs it, so stay here for a while.
                if (now - _recoveredAt[Level] < 60f) _holdUntil = now + 120f;
                Mod.Log.Msg($"Perf: game behind ({_ema * 1000f:0.0} ms vs {_target * 1000f:0.0}) - QuestShade load level {Level}: {Describe(Level)}");
            }
            else if (Level > 0 && _underFor > 12f && now >= _holdUntil)
            {
                _recoveredAt[Level] = now;
                Level--; _underFor = 0f;
                Mod.Log.Msg($"Perf: game has spare time - QuestShade load level {Level}{(Level > 0 ? ": " + Describe(Level) : " (everything back on)")}");
            }
        }

        private static string Describe(int l) => l switch
        {
            1 => "half-res shadows, flashlight engine shadows off",
            2 => "shadows every other frame, flashlight shadow map off",
            _ => "shadows off",
        };

        // What the shadow system may use at the current level.
        public static int ShadowRes(int wanted) => Level >= 1 ? Mathf.Min(wanted, 1024) : wanted;
        public static bool FlashEngineShadows => Level < 1;
        public static bool FlashShadowMap => Level < 2;
        public static bool ShadowEveryOtherFrame => Level >= 2;
        public static bool ShadowsAllowed => Level < 3;

        public static int PreviewFps(int wanted, bool inUse)
        {
            int fps = Mathf.Max(1, wanted);
            if (!inUse) fps = Mathf.Min(fps, 18);
            if (Level >= 1) fps = Mathf.Min(fps, 15);
            if (Level >= 2) fps = Mathf.Min(fps, 10);
            return fps;
        }
        public static bool PreviewExtras => Level < 3;
        public static bool RecordingExtras => Level < 3;
    }
}
