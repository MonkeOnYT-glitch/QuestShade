using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    internal static class EyeAdaptation
    {
        public static float Ev { get; private set; }
        public static float Measured { get; private set; } = -1f;
        public static float Reference { get; private set; } = -1f;
        public static bool Hold;
        public static float DarkMap { get; private set; }
        private const float NormalMapLight = 0.12f;

        private const float MaxUp = 2.0f, MaxDown = 0.5f;
        private const float DeadZone = 0.6f;
        private static int _probeCount = -1;
        private static float _nextSample, _nextMapCheck, _target, _smoothLum = -1f;
        private static bool _logged;

        public static void Tick(Transform head, bool on, float strength, float speed, float dt)
        {
            if (Time.unscaledTime >= _nextMapCheck) { _nextMapCheck = Time.unscaledTime + 1f; CheckMap(); }
            if (!on || head == null || strength <= 0f)
            {
                Ev = Mathf.MoveTowards(Ev, 0f, dt * 2f);
                return;
            }
            float now = Time.unscaledTime;
            if (now >= _nextSample)
            {
                _nextSample = now + 0.1f;
                Sample(head, strength);
            }
            if (Hold) { Ev += (0f - Ev) * (1f - Mathf.Exp(-9f * dt)); return; }
            float rate = (_target > Ev ? 0.55f : 0.9f) * Mathf.Max(0.05f, speed);
            Ev += (_target - Ev) * (1f - Mathf.Exp(-rate * dt));
        }

        public static void Reset() { _probeCount = -1; DarkMap = 0f; _smoothLum = -1f; Ev = 0f; _target = 0f; _logged = false; }

        private static void Sample(Transform head, float strength)
        {
            try
            {
                var lp = LightmapSettings.lightProbes;
                int count = lp != null ? lp.count : 0;
                if (count != _probeCount) CheckMap();
                if (Reference <= 0f) { _target = 0f; return; }

                LightProbes.GetInterpolatedProbe(head.position, null, out var sh);
                float amb = Lum(sh);
                if (!(amb > Reference * 0.005f) || amb > Reference * 200f || float.IsNaN(amb)) return;
                float dir = Mathf.Clamp(0.25f * Directional(sh, head.forward), -0.3f * amb, 0.3f * amb);
                float lum = Mathf.Max(1e-5f, amb + dir);
                float l = Mathf.Log(lum, 2f);
                _smoothLum = _smoothLum < 0f ? lum : Mathf.Pow(2f, Mathf.Lerp(Mathf.Log(_smoothLum, 2f), l, 0.18f));
                Measured = _smoothLum;
                float e = Mathf.Log(Reference / _smoothLum, 2f);
                e = Mathf.Sign(e) * Mathf.Max(0f, Mathf.Abs(e) - DeadZone);
                if (e < 0f) e *= 0.4f;
                _target = Mathf.Clamp(strength * e, -MaxDown, MaxUp);
                if (!_logged) { _logged = true; Mod.Log.Msg($"Eye adaptation: {count} light probes, map light {Reference:0.###}, here {_smoothLum:0.###}"); }
            }
            catch (Exception e)
            {
                if (!_logged) { _logged = true; Mod.Log.Warning("Eye adaptation off for this map: " + e.Message); }
                Reference = -1f; _target = 0f;
            }
        }

        private static void CheckMap()
        {
            try
            {
                var lp = LightmapSettings.lightProbes;
                int count = lp != null ? lp.count : 0;
                if (count == _probeCount) return;
                _probeCount = count; _smoothLum = -1f; _logged = false;
                Reference = MapReference(lp);
                float light = Reference > 0f ? Reference : Lum(RenderSettings.ambientProbe);
                DarkMap = light > 0f ? Mathf.Clamp01(Mathf.Log(NormalMapLight / light, 2f) / 2.3f) : 0f;
                Mod.Log.Msg($"Map light {light:0.###} ({(Reference > 0f ? count + " probes" : "ambient")}): " +
                            (DarkMap > 0f ? $"dark map, lifting shadows {DarkMap * 100f:0}%" : "normally lit"));
            }
            catch (Exception e) { DarkMap = 0f; Mod.Log.Warning("Dark map check: " + e.Message); }
        }

        private static float MapReference(LightProbes lp)
        {
            if (lp == null || lp.count == 0) return -1f;
            var probes = lp.bakedProbes;
            if (probes == null || probes.Length == 0) return -1f;
            int n = probes.Length, step = Math.Max(1, n / 4096);
            var lums = new List<float>(Math.Min(n, 4096) + 1);
            for (int i = 0; i < n; i += step)
            {
                float l = Lum(probes[i]);
                if (l > 1e-4f) lums.Add(l);
            }
            if (lums.Count == 0) return -1f;
            lums.Sort();
            return lums[(int)(lums.Count * 0.6f)];
        }

        private static float Lum(SphericalHarmonicsL2 s) => 0.2126f * s.shr0 + 0.7152f * s.shg0 + 0.0722f * s.shb0;
        private static float Directional(SphericalHarmonicsL2 s, Vector3 d)
        {
            float r = s.shr3 * d.x + s.shr1 * d.y + s.shr2 * d.z;
            float g = s.shg3 * d.x + s.shg1 * d.y + s.shg2 * d.z;
            float b = s.shb3 * d.x + s.shb1 * d.y + s.shb2 * d.z;
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }
    }
}
