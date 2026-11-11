using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    internal static class SunShadows
    {
        public static bool Active { get; private set; }
        public static Func<Renderer[]> AvatarRenderers;

        private const int Res = 1024;
        private const float Half = 9f, Range = 60f, ScanRadius = 10f;
        private static RenderTexture _rt;
        private static Material _mat;
        private static CommandBuffer _cmd;
        private static readonly List<(Renderer r, int subs)> _casters = new List<(Renderer, int)>();
        private static readonly HashSet<IntPtr> _seen = new HashSet<IntPtr>();
        private static float _nextScan, _sunLook;
        private static Light _sun;
        private static bool _logged, _failed;
        private static float _strength = 0.55f;

        private static readonly int IdMap = Shader.PropertyToID("_QS_ShadowMap"), IdVP = Shader.PropertyToID("_QS_ShadowVP"),
            IdVPGpu = Shader.PropertyToID("_QS_ShadowVPGpu"), IdO = Shader.PropertyToID("_QS_ShadowO"), IdD = Shader.PropertyToID("_QS_ShadowD"),
            IdP = Shader.PropertyToID("_QS_ShadowP"), IdInv0 = Shader.PropertyToID("_QS_ShInvVP0"), IdInv1 = Shader.PropertyToID("_QS_ShInvVP1");

        public static void OnLevel()
        {
            _sun = null; _sunLook = 0f; _nextScan = 0f; _casters.Clear(); _logged = false;
        }

        public static void Tick(Config cfg, Camera head)
        {
            bool want = cfg.Shadows && !_failed && ShaderPack.ShadowDepth != null && head != null && FindSun() != null;
            if (!want) { Active = false; return; }
            try
            {
                if (!Ensure()) { Active = false; return; }
                float now = Time.unscaledTime;
                if (now >= _nextScan) { _nextScan = now + 0.5f; Scan(head.transform.position); }
                _strength = Mathf.Clamp01(cfg.ShadowStrength);
                Render(head.transform.position);
                Active = true;
                if (!_logged) { _logged = true; Mod.Log.Msg($"Sun shadows: QuestShade renderer on ({_casters.Count} casters, {Res}px, sun '{_sun.name}')"); }
            }
            catch (Exception e)
            {
                _failed = true; Active = false;
                Mod.Log.Warning("Sun shadows off: " + e.Message);
            }
        }

        private static Light FindSun()
        {
            if (_sun != null && _sun.isActiveAndEnabled) return _sun;
            if (Time.unscaledTime < _sunLook) return null;
            _sunLook = Time.unscaledTime + 5f;
            _sun = null;
            var s = RenderSettings.sun;
            if (s != null && s.isActiveAndEnabled) { _sun = s; return s; }
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                if (l != null && l.type == LightType.Directional && l.isActiveAndEnabled && l.intensity > 0.05f) { _sun = l; break; }
            return _sun;
        }

        private static bool Ensure()
        {
            if (_rt == null)
            {
                var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RHalf) ? RenderTextureFormat.RHalf
                        : SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat) ? RenderTextureFormat.RFloat : RenderTextureFormat.ARGBHalf;
                _rt = new RenderTexture(Res, Res, 16, fmt) { name = "QuestShade_SunShadow", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false };
                _rt.hideFlags = HideFlags.HideAndDontSave;
                if (!_rt.Create()) { _failed = true; Mod.Log.Warning("Sun shadows: couldn't make the shadow map"); return false; }
            }
            if (_mat == null) _mat = new Material(ShaderPack.ShadowDepth) { hideFlags = HideFlags.HideAndDontSave };
            if (_cmd == null) _cmd = new CommandBuffer { name = "QuestShade sun shadows" };
            return true;
        }

        private static void Add(Renderer r)
        {
            if (r == null || !_seen.Add(r.Pointer)) return;
            if (r.TryCast<MeshRenderer>() == null && r.TryCast<SkinnedMeshRenderer>() == null) return;
            int subs = 1;
            try { var m = r.sharedMaterials; subs = Math.Max(1, m != null ? m.Length : 1); } catch { }
            _casters.Add((r, Math.Min(subs, 8)));
        }

        private static void Scan(Vector3 pos)
        {
            _casters.Clear(); _seen.Clear();
            try { var av = AvatarRenderers?.Invoke(); if (av != null) foreach (var r in av) Add(r); } catch { }
            var bodies = new HashSet<IntPtr>();
            foreach (var c in Physics.OverlapSphere(pos, ScanRadius, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c == null) continue;
                var rb = c.attachedRigidbody;
                if (rb == null || !bodies.Add(rb.Pointer)) continue;
                foreach (var r in rb.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || r.lightmapIndex >= 0) continue;
                    Add(r);
                    if (_casters.Count >= 220) return;
                }
            }
        }

        private static void Render(Vector3 headPos)
        {
            Vector3 dir = _sun.transform.forward;
            var rot = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.99f ? Vector3.forward : Vector3.up);
            var centre = headPos + Vector3.down * 0.8f;
            var ls = Quaternion.Inverse(rot) * centre;
            float texel = 2f * Half / Res;
            ls.x = Mathf.Round(ls.x / texel) * texel;
            ls.y = Mathf.Round(ls.y / texel) * texel;
            centre = rot * ls;
            var origin = centre - dir * (Range * 0.5f);
            var view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(origin, rot, Vector3.one).inverse;
            var proj = Matrix4x4.Ortho(-Half, Half, -Half, Half, 0f, Range);
            Shader.SetGlobalMatrix(IdVP, proj * view);
            Shader.SetGlobalMatrix(IdVPGpu, GL.GetGPUProjectionMatrix(proj, true) * view);
            Shader.SetGlobalVector(IdO, new Vector4(origin.x, origin.y, origin.z, 1f / Range));
            Shader.SetGlobalVector(IdD, new Vector4(dir.x, dir.y, dir.z, 0f));
            Shader.SetGlobalTexture(IdMap, _rt);

            _cmd.Clear();
            _cmd.SetRenderTarget(_rt);
            _cmd.ClearRenderTarget(true, true, Color.white);
            for (int i = 0; i < _casters.Count; i++)
            {
                var (r, subs) = _casters[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                for (int s = 0; s < subs; s++) _cmd.DrawRenderer(r, _mat, s, 0);
            }
            Graphics.ExecuteCommandBuffer(_cmd);
        }

        public static void Apply(Material m, Camera cam, bool stereo, bool on)
        {
            if (m == null) return;
            if (!on || !Active || cam == null) { m.SetVector(IdP, Vector4.zero); return; }
            Matrix4x4 a, b;
            if (stereo)
            {
                a = Inv(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left));
                b = Inv(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right), cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right));
            }
            else a = b = Inv(cam.projectionMatrix, cam.worldToCameraMatrix);
            m.SetMatrix(IdInv0, a);
            m.SetMatrix(IdInv1, b);
            m.SetVector(IdP, new Vector4(_strength, 0.05f / Range, 0.75f / Res, 1f));
        }

        private static Matrix4x4 Inv(Matrix4x4 p, Matrix4x4 v) => (GL.GetGPUProjectionMatrix(p, true) * v).inverse;
    }
}
