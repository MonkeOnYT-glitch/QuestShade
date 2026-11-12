using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using LightRenderingMode = UnityEngine.Rendering.Universal.LightRenderingMode;

namespace QuestShade
{
    internal static class Lighting
    {
        private static float _applyAt = -1f;
        private static UniversalRenderPipelineAsset _urp;
        private static bool _saved, _logged;
        private static int _oAddLimit;
        private static LightRenderingMode _oAddMode;
        private static float _oReflection = -1f;
        private static readonly Dictionary<IntPtr, (ReflectionProbe p, float i)> _probes = new Dictionary<IntPtr, (ReflectionProbe, float)>();

        private static bool _want, _perfOff;

        public static void OnLevel()
        {
            _probes.Clear();
            _oReflection = -1f;
            _perfOff = false;
            _applyAt = Time.unscaledTime + 1.5f;
        }

        public static void Reapply() => _applyAt = Time.unscaledTime;

        public static void Tick(Config cfg)
        {
            if (!_perfOff && _want && Perf.Level >= 2) { _perfOff = true; Mod.Log.Msg("Lighting: game too slow, real lights off for this level"); }
            bool want = cfg.RealLights && cfg.Enabled && !_perfOff;
            if (want != _want) { _want = want; if (_applyAt < 0f) _applyAt = Time.unscaledTime; }
            if (_applyAt < 0f || Time.unscaledTime < _applyAt) return;
            _applyAt = -1f;
            try { Lights(cfg); Shine(cfg); }
            catch (Exception e) { Mod.Log.Warning("Lighting: " + e.Message); }
        }

        private static void Lights(Config cfg)
        {
            if (_urp == null)
            {
                var rp = QualitySettings.renderPipeline ?? GraphicsSettings.currentRenderPipeline;
                _urp = rp != null ? rp.TryCast<UniversalRenderPipelineAsset>() : null;
            }
            if (_urp == null) return;
            if (!_saved) { _saved = true; _oAddMode = _urp.m_AdditionalLightsRenderingMode; _oAddLimit = _urp.m_AdditionalLightsPerObjectLimit; }
            if (!_logged) { _logged = true; Mod.Log.Msg($"Lighting: BONELAB extra lights were {_oAddMode} x{_oAddLimit}"); }
            if (_want)
            {
                _urp.m_AdditionalLightsRenderingMode = LightRenderingMode.PerPixel;
                _urp.m_AdditionalLightsPerObjectLimit = Math.Max(_oAddLimit, 2);
                Mod.Log.Msg($"Lighting: extra lights now {_urp.m_AdditionalLightsRenderingMode} x{_urp.m_AdditionalLightsPerObjectLimit}");
            }
            else
            {
                _urp.m_AdditionalLightsRenderingMode = _oAddMode;
                _urp.m_AdditionalLightsPerObjectLimit = _oAddLimit;
            }
        }

        private static void Shine(Config cfg)
        {
            float k = 1f - Mathf.Clamp01(cfg.LessShine) * 0.75f;
            if (_oReflection < 0f) _oReflection = RenderSettings.reflectionIntensity;
            RenderSettings.reflectionIntensity = _oReflection * k;
            if (_probes.Count == 0)
                foreach (var p in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>())
                    if (p != null) _probes[p.Pointer] = (p, p.intensity);
            foreach (var kv in _probes.Values) if (kv.p != null) kv.p.intensity = kv.i * k;
        }
    }
}
