using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ShadowResolution = UnityEngine.Rendering.Universal.ShadowResolution;
using LightRenderingMode = UnityEngine.Rendering.Universal.LightRenderingMode;

namespace QuestShade
{
    internal static class Lighting
    {
        private static float _applyAt = -1f;
        private static UniversalRenderPipelineAsset _urp;
        private static bool _saved;
        private static bool _oShadows, _oSoft, _oMixed;
        private static float _oDistance;
        private static int _oCascades, _oAddLimit;
        private static ShadowResolution _oRes;
        private static LightRenderingMode _oAddMode;
        private static float _oReflection = -1f;

        private static Light _sun;
        private static LightShadows _sunShadows;
        private static float _sunStrength;
        private static LightBakingOutput _sunBaking;
        private static readonly List<Renderer> _casters = new List<Renderer>();
        private static readonly Dictionary<IntPtr, (ReflectionProbe p, float i)> _probes = new Dictionary<IntPtr, (ReflectionProbe, float)>();
        private static bool _loggedPipeline;

        public static void OnLevel()
        {
            _sun = null;
            _casters.Clear();
            _probes.Clear();
            _oReflection = -1f;
            _applyAt = Time.unscaledTime + 1.5f;
        }

        public static void Reapply() => _applyAt = Time.unscaledTime;

        public static void Tick(Config cfg)
        {
            if (_applyAt < 0f || Time.unscaledTime < _applyAt) return;
            _applyAt = -1f;
            try { Apply(cfg); }
            catch (Exception e) { Mod.Log.Warning("Lighting: " + e.Message); }
        }

        private static void Apply(Config cfg)
        {
            Pipeline(cfg);
            Sun(cfg);
            Casters(cfg);
            Shine(cfg);
        }

        private static void Pipeline(Config cfg)
        {
            if (_urp == null)
            {
                var rp = QualitySettings.renderPipeline ?? GraphicsSettings.currentRenderPipeline;
                _urp = rp != null ? rp.TryCast<UniversalRenderPipelineAsset>() : null;
            }
            if (_urp == null) { Mod.Log.Warning("Lighting: no URP asset found"); return; }
            if (!_saved)
            {
                _saved = true;
                _oShadows = _urp.m_MainLightShadowsSupported; _oSoft = _urp.m_SoftShadowsSupported; _oMixed = _urp.m_MixedLightingSupported;
                _oDistance = _urp.m_ShadowDistance; _oCascades = _urp.m_ShadowCascadeCount; _oRes = _urp.m_MainLightShadowmapResolution;
                _oAddMode = _urp.m_AdditionalLightsRenderingMode; _oAddLimit = _urp.m_AdditionalLightsPerObjectLimit;
            }
            if (!_loggedPipeline)
            {
                _loggedPipeline = true;
                Mod.Log.Msg($"Lighting: BONELAB settings were shadows {_oShadows} ({_oRes}, {_oDistance:0} m), extra lights {_oAddMode} x{_oAddLimit}, mixed {_oMixed}");
            }
            if (cfg.Shadows)
            {
                _urp.m_MainLightShadowsSupported = true;
                _urp.m_SoftShadowsSupported = true;
                _urp.m_MixedLightingSupported = true;
                _urp.m_ShadowDistance = cfg.ShadowDistance;
                _urp.m_ShadowCascadeCount = 1;
                _urp.m_MainLightShadowmapResolution = ShadowResolution._1024;
            }
            else
            {
                _urp.m_MainLightShadowsSupported = _oShadows; _urp.m_SoftShadowsSupported = _oSoft; _urp.m_MixedLightingSupported = _oMixed;
                _urp.m_ShadowDistance = _oDistance; _urp.m_ShadowCascadeCount = _oCascades; _urp.m_MainLightShadowmapResolution = _oRes;
            }
            if (cfg.RealLights)
            {
                _urp.m_AdditionalLightsRenderingMode = LightRenderingMode.PerPixel;
                _urp.m_AdditionalLightsPerObjectLimit = Math.Max(_oAddLimit, 4);
            }
            else
            {
                _urp.m_AdditionalLightsRenderingMode = _oAddMode;
                _urp.m_AdditionalLightsPerObjectLimit = _oAddLimit;
            }
        }

        private static void Sun(Config cfg)
        {
            if (_sun == null)
            {
                var s = RenderSettings.sun;
                if (s == null) foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>()) if (l != null && l.type == LightType.Directional && l.enabled) { s = l; break; }
                if (s == null) { Mod.Log.Msg("Lighting: no sun in this map (indoor maps get no sun shadows)"); return; }
                _sun = s; _sunShadows = s.shadows; _sunStrength = s.shadowStrength; _sunBaking = s.bakingOutput;
            }
            if (cfg.Shadows)
            {
                _sun.shadows = LightShadows.Soft;
                _sun.shadowStrength = 0.75f;
                var bo = _sun.bakingOutput;
                if (bo.lightmapBakeType == LightmapBakeType.Baked)
                {
                    bo.isBaked = true;
                    bo.lightmapBakeType = LightmapBakeType.Mixed;
                    bo.mixedLightingMode = MixedLightingMode.Subtractive;
                    _sun.bakingOutput = bo;
                }
                RenderSettings.subtractiveShadowColor = new Color(0.40f, 0.43f, 0.52f);
                Mod.Log.Msg($"Lighting: sun shadows on ('{_sun.name}', was {_sunBaking.lightmapBakeType}/{_sunBaking.mixedLightingMode})");
            }
            else
            {
                _sun.shadows = _sunShadows; _sun.shadowStrength = _sunStrength; _sun.bakingOutput = _sunBaking;
            }
        }

        private static void Casters(Config cfg)
        {
            if (!cfg.Shadows)
            {
                foreach (var r in _casters) if (r != null) r.shadowCastingMode = ShadowCastingMode.Off;
                _casters.Clear();
                return;
            }
            if (_sun == null) return;
            int n = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || r.lightmapIndex >= 0 || r.shadowCastingMode != ShadowCastingMode.Off) continue;
                if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
                r.shadowCastingMode = ShadowCastingMode.On;
                _casters.Add(r);
                if (++n >= 4000) break;
            }
            Mod.Log.Msg($"Lighting: {n} moving things now cast shadows");
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
            Mod.Log.Msg($"Lighting: reflections at {k * 100f:0}% ({_probes.Count} probes)");
        }
    }
}
