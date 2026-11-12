using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    internal static class Shadows
    {
        public static Func<List<Renderer>, bool> Collect;
        public static Func<int> PlayerLayers;

        private const int N = 11, Q = N - 1, Res = 256;
        private const float Ahead = 1.5f, Reach = 14f;
        private static readonly int TexId = Shader.PropertyToID("_QS_ShadowTex"), KId = Shader.PropertyToID("_QS_ShadowK");

        private struct Caster { public Renderer R; public int Subs; }
        private static readonly List<Renderer> _found = new List<Renderer>();
        private static readonly List<Caster> _casters = new List<Caster>();
        private static RenderTexture _rt;
        private static Material _castMat, _headMat, _lensMat;
        private static CommandBuffer _cb;
        private static GameObject _headGo, _lensGo;
        private static Mesh _mesh;
        private static Il2CppStructArray<Vector3> _verts;
        private static readonly Vector3[] _p = new Vector3[N * N], _n = new Vector3[N * N];
        private static readonly bool[] _ok = new bool[N * N];
        private static Light _sun;
        private static float _sunAt, _scanAt, _fade, _k = 0.6f;
        private static Vector3 _dir = Vector3.down;
        private static int _mask;
        private static bool _perfOff, _failed, _logged;

        public static void OnLevel()
        {
            _casters.Clear(); _sun = null; _sunAt = Time.unscaledTime + 2f; _scanAt = 0f; _mask = 0;
            _perfOff = false; _fade = 0f; _logged = false;
        }

        public static void Tick(Config cfg, Camera head)
        {
            bool want = cfg.Shadows && cfg.Enabled && !_perfOff && !_failed && ShaderPack.Shadow != null && head != null && Collect != null;
            if (want && DiedLastTime()) want = false;
            if (want && Perf.Level >= 2) { _perfOff = true; want = false; Mod.Log.Msg("Shadows: game too slow, body shadow off for this level"); }
            if (!want) { Show(false); return; }
            try { Run(cfg, head); }
            catch (Exception e) { _failed = true; Show(false); Mod.Log.Warning("Shadows: turned off, " + e.Message); }
        }

        private static void Show(bool on)
        {
            if (_headGo != null && _headGo.activeSelf != on) _headGo.SetActive(on);
            if (_lensGo != null && _lensGo.activeSelf != (on && _lensGo.layer != 0)) _lensGo.SetActive(on && _lensGo.layer != 0);
        }

        private static void Scan()
        {
            _found.Clear(); _casters.Clear();
            bool got; try { got = Collect(_found); } catch { got = false; }
            if (!got) return;
            foreach (var r in _found)
            {
                if (r == null) continue;
                int subs = 0;
                var smr = r.TryCast<SkinnedMeshRenderer>();
                if (smr != null) { var m = smr.sharedMesh; subs = m != null ? m.subMeshCount : 0; }
                else if (r.TryCast<MeshRenderer>() != null)
                {
                    var mf = r.GetComponent<MeshFilter>();
                    var m = mf != null ? mf.sharedMesh : null;
                    subs = m != null ? m.subMeshCount : 0;
                }
                if (subs > 0) _casters.Add(new Caster { R = r, Subs = Mathf.Min(subs, 8) });
            }
            if (_mask == 0)
            {
                int st = LayerMask.NameToLayer("Static");
                int m = 1;
                if (st >= 0) m |= 1 << st;
                int pl = 0; try { pl = PlayerLayers != null ? PlayerLayers() : 0; } catch { }
                m &= ~pl;
                _mask = m != 0 ? m : 1;
            }
        }

        private static void FindSun()
        {
            Light best = null;
            try { best = RenderSettings.sun; } catch { }
            if (best == null || !best.isActiveAndEnabled)
            {
                best = null; float top = 0.05f;
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                    if (l != null && l.type == LightType.Directional && l.isActiveAndEnabled && l.intensity > top) { top = l.intensity; best = l; }
            }
            _sun = best;
        }

        private static void Ensure()
        {
            if (_rt == null)
            {
                _rt = new RenderTexture(Res, Res, 0, RenderTextureFormat.ARGB32) { name = "QuestShade_Shadow", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false, hideFlags = HideFlags.HideAndDontSave };
                _rt.Create();
            }
            if (_castMat == null) _castMat = new Material(ShaderPack.Shadow) { hideFlags = HideFlags.HideAndDontSave };
            if (_headMat == null) { _headMat = new Material(ShaderPack.Shadow) { hideFlags = HideFlags.HideAndDontSave }; ShaderPack.SetStereo(_headMat, true); }
            if (_lensMat == null) { _lensMat = new Material(ShaderPack.Shadow) { hideFlags = HideFlags.HideAndDontSave }; ShaderPack.SetStereo(_lensMat, false); }
            _headMat.SetTexture(TexId, _rt); _lensMat.SetTexture(TexId, _rt);
            _cb ??= new CommandBuffer { name = "QuestShade_Shadow" };
            if (_mesh == null)
            {
                int vc = Q * Q * 4;
                var uv = new Vector2[vc];
                var tri = new int[Q * Q * 6];
                for (int j = 0; j < Q; j++)
                    for (int i = 0; i < Q; i++)
                    {
                        int q = j * Q + i, v = q * 4, t = q * 6;
                        float u0 = i / (float)Q, u1 = (i + 1) / (float)Q, v0 = j / (float)Q, v1 = (j + 1) / (float)Q;
                        uv[v] = new Vector2(u0, v0); uv[v + 1] = new Vector2(u1, v0); uv[v + 2] = new Vector2(u0, v1); uv[v + 3] = new Vector2(u1, v1);
                        tri[t] = v; tri[t + 1] = v + 2; tri[t + 2] = v + 1; tri[t + 3] = v + 1; tri[t + 4] = v + 2; tri[t + 5] = v + 3;
                    }
                _verts = new Il2CppStructArray<Vector3>(vc);
                _mesh = new Mesh { name = "QuestShade_Shadow", hideFlags = HideFlags.HideAndDontSave };
                _mesh.MarkDynamic();
                _mesh.vertices = _verts; _mesh.uv = uv; _mesh.triangles = tri;
            }
            if (_headGo == null) _headGo = Make(_headMat);
            if (_lensGo == null) _lensGo = Make(_lensMat);
        }

        private static GameObject Make(Material mat)
        {
            var go = new GameObject("QuestShade_Shadow");
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return go;
        }

        private static string StepFile => System.IO.Path.Combine(Config.Root, "shadow-step.txt");
        private static bool _checked;
        private static void S(string s)
        {
            if (_logged) return;
            Mod.Log.Msg("Shadows step: " + s);
            try { System.IO.File.WriteAllText(StepFile, s); } catch { }
        }

        private static bool DiedLastTime()
        {
            if (_checked) return _failed;
            _checked = true;
            try
            {
                if (!System.IO.File.Exists(StepFile)) return false;
                var last = System.IO.File.ReadAllText(StepFile).Trim();
                if (last == "ok" || last.Length == 0) return false;
                _failed = true;
                Mod.Log.Warning($"Shadows: last time the game died at shadow step \"{last}\". Body shadow is OFF now so you can play. Please send this log. (Delete UserData/QuestShade/shadow-step.txt to try again.)");
            }
            catch { }
            return _failed;
        }

        private static bool Flat(int a, int b)
        {
            var e = _p[b] - _p[a];
            float len = e.magnitude;
            if (len > 3f) return false;
            float lim = 0.3f * len + 0.03f;
            return Mathf.Abs(Vector3.Dot(_n[a], e)) < lim && Mathf.Abs(Vector3.Dot(_n[b], e)) < lim;
        }

        private static void Run(Config cfg, Camera head)
        {
            float now = Time.unscaledTime, dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (now >= _scanAt) { _scanAt = now + 3f; Scan(); }
            if (_casters.Count == 0) { Show(false); return; }
            S("ensure"); Ensure();

            S("bounds");
            bool any = false; Bounds b = default;
            for (int i = 0; i < _casters.Count; i++)
            {
                var r = _casters[i].R;
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var rb = r.bounds;
                if (!any) { b = rb; any = true; } else b.Encapsulate(rb);
            }
            if (!any) { Show(false); return; }
            Vector3 center = b.center;
            float half = Mathf.Clamp(b.extents.magnitude + 0.15f, 0.6f, 2.2f);

            S("sun");
            if (now >= _sunAt) { _sunAt = now + 6f; if (_sun == null || !_sun.isActiveAndEnabled) FindSun(); }
            Vector3 want = Vector3.down; float wantK = 0.6f;
            if (_sun != null && _sun.isActiveAndEnabled && _sun.intensity > 0.05f)
            {
                var d = _sun.transform.forward;
                if (d.y < -0.2f && !Physics.Raycast(head.transform.position, -d, 80f, _mask, QueryTriggerInteraction.Ignore)) { want = d; wantK = 1f; }
            }
            float target = 1f;
            if (Vector3.Angle(want, _dir) < 5f) { _dir = want; _k = wantK; }
            else { target = 0f; if (_fade <= 0.02f) { _dir = want; _k = wantK; } }
            _fade = Mathf.MoveTowards(_fade, target, dt * 5f);

            float dark = Mathf.Clamp01(cfg.ShadowDarkness) * _k * _fade;
            if (dark < 0.01f) { Show(false); return; }

            var rot = Quaternion.LookRotation(_dir, Mathf.Abs(_dir.y) > 0.95f ? Vector3.forward : Vector3.up);
            Vector3 right = rot * Vector3.right, up = rot * Vector3.up;

            var view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(center - _dir * 5f, rot, Vector3.one).inverse;
            var proj = Matrix4x4.Ortho(-half, half, -half, half, 0.1f, 10f);
            S($"silhouette build: {_casters.Count} parts, half {half:0.00} m, dir {_dir}, mask {_mask}, {SystemInfo.graphicsDeviceType}, threaded {SystemInfo.graphicsMultiThreaded}, sun {(_sun != null ? _sun.name : "none")}");
            var prev = RenderTexture.active;
            _cb.Clear();
            _cb.SetRenderTarget(new RenderTargetIdentifier(_rt));
            _cb.ClearRenderTarget(false, true, Color.clear);
            _cb.SetViewProjectionMatrices(view, proj);
            for (int i = 0; i < _casters.Count; i++)
            {
                var c = _casters[i];
                if (c.R == null || !c.R.enabled || !c.R.gameObject.activeInHierarchy) continue;
                if (!_logged)
                {
                    S($"draw part {i}: \"{c.R.name}\" {c.R.GetIl2CppType().Name}, {c.Subs} submesh, layer {c.R.gameObject.layer}");
                    for (int s = 0; s < c.Subs; s++) _cb.DrawRenderer(c.R, _castMat, s, 1);
                    Graphics.ExecuteCommandBuffer(_cb);
                    _cb.Clear();
                    _cb.SetRenderTarget(new RenderTargetIdentifier(_rt));
                    _cb.SetViewProjectionMatrices(view, proj);
                }
                else for (int s = 0; s < c.Subs; s++) _cb.DrawRenderer(c.R, _castMat, s, 1);
            }
            S("silhouette draw");
            Graphics.ExecuteCommandBuffer(_cb);
            S("silhouette done");
            RenderTexture.active = prev;

            S("rays");
            float step = 2f * half / Q;
            var o = center - _dir * Ahead;
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    int k = j * N + i;
                    var s = o + right * (-half + i * step) + up * (-half + j * step);
                    if (Physics.Raycast(s, _dir, out var hit, Ahead + Reach, _mask, QueryTriggerInteraction.Ignore))
                    {
                        var nrm = hit.normal;
                        _ok[k] = true; _n[k] = nrm; _p[k] = hit.point + nrm * 0.012f - center;
                    }
                    else _ok[k] = false;
                }
            S("mesh");
            int vi = 0;
            for (int j = 0; j < Q; j++)
                for (int i = 0; i < Q; i++)
                {
                    int a = j * N + i, bb = a + 1, c = a + N, d = c + 1;
                    bool good = _ok[a] && _ok[bb] && _ok[c] && _ok[d] && Flat(a, bb) && Flat(a, c) && Flat(bb, d) && Flat(c, d);
                    if (good) { _verts[vi] = _p[a]; _verts[vi + 1] = _p[bb]; _verts[vi + 2] = _p[c]; _verts[vi + 3] = _p[d]; }
                    else { _verts[vi] = Vector3.zero; _verts[vi + 1] = Vector3.zero; _verts[vi + 2] = Vector3.zero; _verts[vi + 3] = Vector3.zero; }
                    vi += 4;
                }
            S("mesh upload");
            _mesh.vertices = _verts;
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(40f, 40f, 40f));

            S("place");
            _headMat.SetFloat(KId, dark); _lensMat.SetFloat(KId, dark);
            int hl = FlashBeams.HeadsetLayer != null ? FlashBeams.HeadsetLayer() : -1;
            int ll = FlashBeams.LensLayer != null ? FlashBeams.LensLayer() : -1;
            _headGo.layer = hl >= 0 ? hl : 0;
            _lensGo.layer = ll >= 0 ? ll : 0;
            _headGo.transform.position = center; _lensGo.transform.position = center;
            Show(true);
            if (!_logged) { S("first frame finished"); try { System.IO.File.WriteAllText(StepFile, "ok"); } catch { } }
            if (!_logged) { _logged = true; Mod.Log.Msg($"Shadows: body shadow on ({_casters.Count} parts, {(_sun != null ? "sun '" + _sun.name + "'" : "no sun on this map, shadow falls straight down")})"); }
        }
    }
}
