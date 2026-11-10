using UnityEngine;
using UnityEngine.XR;

namespace QuestShade
{
    internal static class Perf
    {
        public static int Level { get; private set; }
        private static float _ema = -1f, _overFor, _underFor, _target = 1f / 72f;
        private static float _nextRefresh, _ignoreUntil;
        public static void OnSceneLoaded() { _ignoreUntil = Time.unscaledTime + 20f; _ema = -1f; _overFor = _underFor = 0f; }
        private static readonly float[] Trip = { 3f, 4f, 5f };
        private static readonly float[] Hold = { 4f, 4f, 6f };

        public static void Tick(float dt)
        {
            if (dt <= 0f || dt > 0.5f) return;
            if (Time.unscaledTime < _ignoreUntil) return;
            float now = Time.unscaledTime;
            if (now > _nextRefresh)
            {
                _nextRefresh = now + 5f;
                float hz = 72f;
                try { if (XRDevice.refreshRate > 30f) hz = XRDevice.refreshRate; } catch { }
                _target = 1f / hz;
            }
            _ema = _ema < 0f ? dt : Mathf.Lerp(_ema, dt, 0.03f);
            float ratio = _ema / _target;
            if (Level < 3 && ratio > Trip[Level]) { _overFor += dt; _underFor = 0f; }
            else if (Level > 0 && ratio < Trip[Level - 1] * 0.75f) { _underFor += dt; _overFor = 0f; }
            else { _overFor = 0f; _underFor = 0f; }
            if (Level < 3 && _overFor > Hold[Level]) { Level++; _overFor = 0f; Mod.Log.Msg($"Perf: game far behind ({_ema * 1000f:0.0} ms vs {_target * 1000f:0.0}) - QuestShade load level {Level}"); }
            else if (_underFor > 5f && Level > 0) { Level--; _underFor = 0f; Mod.Log.Msg($"Perf: recovered - QuestShade load level {Level}"); }
        }

        public static int PreviewFps(int wanted, bool inUse) => Mathf.Max(24, wanted);
        public static bool PreviewExtras => Level < 3;
        public static bool RecordingExtras => Level < 3;
    }
}
