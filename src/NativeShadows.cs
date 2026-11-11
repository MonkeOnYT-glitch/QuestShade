using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using ShadowResolution = UnityEngine.Rendering.Universal.ShadowResolution;

namespace QuestShade
{
    internal static class NativeShadows
    {
        private const string GameShader = "SLZ/LitMAS/LitMAS Opaque";
        private static float _applyAt = -1f, _nextCasters;
        private static bool _on, _saved, _warned;
        private static UniversalRenderPipelineAsset _urp;
        private static bool _oShadows, _oSoft, _oMixed;
        private static float _oDistance;
        private static int _oCascades;
        private static ShadowResolution _oRes;
        private static Light _sun;
        private static LightShadows _sunShadows;
        private static float _sunStrength;
        private static readonly Dictionary<IntPtr, (Material m, Shader s)> _swapped = new Dictionary<IntPtr, (Material, Shader)>();
        private static readonly Dictionary<IntPtr, Renderer> _casters = new Dictionary<IntPtr, Renderer>();

        public static void OnLevel()
        {
            _swapped.Clear(); _casters.Clear(); _sun = null; _on = false; _namesLogged = false;
            _applyAt = Time.unscaledTime + 2f;
        }

        public static void Reapply() => _applyAt = Time.unscaledTime;

        private static UniversalAdditionalCameraData _camData;
        private static bool _oCamShadows;

        private static void CameraShadows(Camera head, bool on)
        {
            if (head == null) return;
            var d = head.GetComponent<UniversalAdditionalCameraData>();
            if (d == null) return;
            if (_camData == null || d.Pointer != _camData.Pointer) { _camData = d; _oCamShadows = d.renderShadows; }
            bool want = on || _oCamShadows;
            if (d.renderShadows != want) d.renderShadows = want;
        }

        public static void Tick(Config cfg, Camera head)
        {
            float now = Time.unscaledTime;
            if (_applyAt >= 0f && now >= _applyAt)
            {
                _applyAt = -1f;
                try { if (cfg.Shadows) Enable(cfg, head); else Disable(); }
                catch (Exception e) { Mod.Log.Warning("Shadows: " + e.Message); }
            }
            if (_on && head != null && now >= _nextCasters)
            {
                _nextCasters = now + 3f;
                try { CameraShadows(head, true); NearbyCasters(head.transform.position); } catch { }
            }
        }

        private static bool Ready()
        {
            if (ShaderPack.LitShadowed != null) return true;
            if (!_warned) { _warned = true; Mod.Log.Warning("Shadows: the shadow shader isn't supported on this headset"); }
            return false;
        }

        private static void Enable(Config cfg, Camera head)
        {
            if (!Ready()) return;
            CameraShadows(head, true);
            if (_urp == null)
            {
                var rp = QualitySettings.renderPipeline ?? GraphicsSettings.currentRenderPipeline;
                _urp = rp != null ? rp.TryCast<UniversalRenderPipelineAsset>() : null;
            }
            if (_urp == null) return;
            if (!_saved)
            {
                _saved = true;
                _oShadows = _urp.m_MainLightShadowsSupported; _oSoft = _urp.m_SoftShadowsSupported; _oMixed = _urp.m_MixedLightingSupported;
                _oDistance = _urp.m_ShadowDistance; _oCascades = _urp.m_ShadowCascadeCount; _oRes = _urp.m_MainLightShadowmapResolution;
            }
            _urp.m_MainLightShadowsSupported = true;
            _urp.m_SoftShadowsSupported = true;
            _urp.m_MixedLightingSupported = true;
            _urp.m_ShadowDistance = cfg.ShadowDistance;
            _urp.m_ShadowCascadeCount = 1;
            _urp.m_MainLightShadowmapResolution = ShadowResolution._1024;

            if (_sun == null)
            {
                var s = RenderSettings.sun;
                if (s == null) foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>()) if (l != null && l.type == LightType.Directional && l.isActiveAndEnabled) { s = l; break; }
                _sun = s;
                if (s != null) { _sunShadows = s.shadows; _sunStrength = s.shadowStrength; }
            }
            if (_sun != null) { _sun.shadows = LightShadows.Soft; _sun.shadowStrength = 0.8f; }

            bool multiview = XRSettings.enabled && XRSettings.stereoRenderingMode != XRSettings.StereoRenderingMode.MultiPass && SystemInfo.supportsMultiview;
            int swaps = 0, casters = 0;
            var names = new Dictionary<string, int>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null) continue;
                var mats = r.sharedMaterials;
                if (mats == null) continue;
                bool any = false;
                foreach (var m in mats)
                {
                    if (m == null) continue;
                    var sh = m.shader;
                    if (sh == null) continue;
                    var sn = sh.name ?? "";
                    names[sn] = names.TryGetValue(sn, out var cnt) ? cnt + 1 : 1;
                    if (!IsTarget(sn)) continue;
                    any = true;
                    if (_swapped.ContainsKey(m.Pointer)) continue;
                    _swapped[m.Pointer] = (m, sh);
                    m.shader = ShaderPack.LitShadowed;
                    if (multiview) m.EnableKeyword("QS_STEREO_MULTIVIEW"); else m.DisableKeyword("QS_STEREO_MULTIVIEW");
                    swaps++;
                }
                if (r.lightmapIndex < 0 && (r.TryCast<MeshRenderer>() != null || r.TryCast<SkinnedMeshRenderer>() != null) && AddCaster(r)) casters++;
            }
            _on = true;
            if (!_namesLogged)
            {
                _namesLogged = true;
                var top = new List<KeyValuePair<string, int>>(names);
                top.Sort((a, b) => b.Value.CompareTo(a.Value));
                var parts = new List<string>();
                for (int i = 0; i < top.Count && i < 15; i++) parts.Add(top[i].Key + " x" + top[i].Value);
                Mod.Log.Msg("Shadows: shaders in this map: " + string.Join(", ", parts));
            }
            Mod.Log.Msg($"Shadows: real shadows on ({swaps} materials, {casters} casters, sun {(_sun != null ? "'" + _sun.name + "'" : "none - indoor map")}, camera shadows were {_oCamShadows})");
        }

        private static bool _namesLogged;

        private static bool IsTarget(string n)
        {
            if (n == GameShader) return true;
            if (n.IndexOf("LitMAS", StringComparison.OrdinalIgnoreCase) < 0) return false;
            foreach (var bad in new[] { "Transparent", "Alpha", "Cutout", "Clip", "Triplanar", "AudioLink", "Posespace", "Hair", "Fade", "Additive", "Water" })
                if (n.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static bool AddCaster(Renderer r)
        {
            if (_casters.ContainsKey(r.Pointer)) return false;
            if (r.shadowCastingMode != ShadowCastingMode.Off) return false;
            r.shadowCastingMode = ShadowCastingMode.On;
            _casters[r.Pointer] = r;
            return true;
        }

        private static void NearbyCasters(Vector3 pos)
        {
            foreach (var s in UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>())
                if (s != null && s.lightmapIndex < 0) AddCaster(s);
            var seen = new HashSet<IntPtr>();
            foreach (var c in Physics.OverlapSphere(pos, 12f, ~0, QueryTriggerInteraction.Ignore))
            {
                var rb = c != null ? c.attachedRigidbody : null;
                if (rb == null || !seen.Add(rb.Pointer)) continue;
                foreach (var r in rb.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || r.lightmapIndex >= 0) continue;
                    if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) continue;
                    AddCaster(r);
                }
            }
        }

        private static void Disable()
        {
            foreach (var kv in _swapped.Values) if (kv.m != null && kv.s != null) kv.m.shader = kv.s;
            _swapped.Clear();
            foreach (var r in _casters.Values) if (r != null) r.shadowCastingMode = ShadowCastingMode.Off;
            _casters.Clear();
            if (_sun != null) { _sun.shadows = _sunShadows; _sun.shadowStrength = _sunStrength; }
            if (_urp != null && _saved)
            {
                _urp.m_MainLightShadowsSupported = _oShadows; _urp.m_SoftShadowsSupported = _oSoft; _urp.m_MixedLightingSupported = _oMixed;
                _urp.m_ShadowDistance = _oDistance; _urp.m_ShadowCascadeCount = _oCascades; _urp.m_MainLightShadowmapResolution = _oRes;
            }
            if (_camData != null) { try { _camData.renderShadows = _oCamShadows; } catch { } }
            if (_on) Mod.Log.Msg("Shadows: off, original materials back");
            _on = false;
        }
    }
}
