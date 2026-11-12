using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    internal static class Shadows
    {
        public static Func<Transform> Avatar;
        public static Func<int, GameObject> Held;
        public static Func<GameObject, Transform> ItemRoot;
        public static Func<int> PlayerLayers;

        private const int MaxParts = 12;
        private const float Reach = 40f, Lift = 0.012f, Thin = 0.002f;
        private static readonly int KId = Shader.PropertyToID("_QS_ShadowK"), MaskId = Shader.PropertyToID("_QS_Mask"),
            CompId = Shader.PropertyToID("_QS_Comp"), PassId = Shader.PropertyToID("_QS_Pass"), ZTestId = Shader.PropertyToID("_QS_ZTest");

        private static GameObject _root, _sheet;
        private static Transform _outer, _inner, _anchor;
        private static Material _shade, _none, _clear;
        private static Mesh _quad;
        private static readonly List<Transform> _boneSrc = new List<Transform>(), _boneDst = new List<Transform>();
        private static readonly List<Transform> _partSrc = new List<Transform>(), _partDst = new List<Transform>();
        private static readonly List<GameObject> _skins = new List<GameObject>(), _partGo = new List<GameObject>();
        private static int _avatarId, _itemsKey, _mask, _maskAll, _layer = -1, _frame;
        private static float _nextItems, _sunAt, _fade, _k = 0.6f;
        private static Light _sun;
        private static Vector3 _dir = Vector3.down, _planeP, _planeN = Vector3.up;
        private static bool _plane, _perfOff, _failed, _logged, _checked, _fresh;

        private static float _wait;
        private static int _seenAvatar;

        private static string StepFile => System.IO.Path.Combine(Config.Root, "shadow3-step.txt");

        public static void OnLevel()
        {
            _sun = null; _sunAt = Time.unscaledTime + 2f; _perfOff = false; _fade = 0f; _logged = false; _plane = false;
            _wait = Time.unscaledTime + 4f;
        }

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
                Mod.Log.Warning($"Shadows: last time the game died at shadow step \"{last}\". Body shadow is OFF now so you can play. Please send this log. (Turn Body shadow off and on in Effects > Lighting to try again.)");
            }
            catch { }
            return _failed;
        }

        public static void Retry()
        {
            _checked = true; _failed = false; _logged = false;
            try { if (System.IO.File.Exists(StepFile)) System.IO.File.Delete(StepFile); } catch { }
            Mod.Log.Msg("Shadows: trying the body shadow again");
        }

        public static void Tick(Config cfg, Camera head)
        {
            bool want = cfg.Shadows && cfg.Enabled && !_perfOff && !_failed && ShaderPack.Shadow != null && head != null && Avatar != null;
            if (want && DiedLastTime()) want = false;
            if (want && Perf.Level >= 2) { _perfOff = true; want = false; Mod.Log.Msg("Shadows: game too slow, body shadow off for this level"); }
            if (!want) { Show(false); return; }
            try { Run(cfg, head); }
            catch (Exception e)
            {
                _failed = true; Show(false);
                Mod.Log.Warning("Shadows: turned off after an error (not a crash): " + e);
                try { System.IO.File.WriteAllText(StepFile, "ok"); } catch { }
            }
        }

        private static void Show(bool on)
        {
            if (_root != null && _root.activeSelf != on) _root.SetActive(on);
        }

        private static Material Mat(string name, int queue, float mask, float comp, float pass, float ztest)
        {
            var m = new Material(ShaderPack.Shadow) { name = "QuestShade_" + name, hideFlags = HideFlags.HideAndDontSave, renderQueue = queue };
            m.SetFloat(MaskId, mask); m.SetFloat(CompId, comp); m.SetFloat(PassId, pass); m.SetFloat(ZTestId, ztest);
            ShaderPack.SetStereo(m, true);
            return m;
        }

        private static void Ensure()
        {
            if (_shade == null) _shade = Mat("Shadow", 2510, 15f, 6f, 2f, 4f);
            if (_none == null) _none = Mat("ShadowNone", 2510, 0f, 1f, 0f, 4f);
            if (_clear == null) _clear = Mat("ShadowClear", 2511, 0f, 8f, 1f, 8f);
            if (_quad == null)
            {
                _quad = new Mesh { name = "QuestShade_ShadowSheet", hideFlags = HideFlags.HideAndDontSave };
                _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
                _quad.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            }
            if (_root != null) return;
            _boneSrc.Clear(); _boneDst.Clear(); _partSrc.Clear(); _partDst.Clear(); _skins.Clear(); _partGo.Clear();
            _avatarId = 0; _itemsKey = 0; _layer = -1;
            _root = new GameObject("QuestShade_Shadow");
            _root.SetActive(false);
            _outer = new GameObject("Outer").transform; _outer.SetParent(_root.transform, false);
            _inner = new GameObject("Inner").transform; _inner.SetParent(_outer, false);
            _anchor = new GameObject("Anchor").transform; _anchor.SetParent(_root.transform, false);
            _sheet = new GameObject("Sheet");
            _sheet.transform.SetParent(_root.transform, false);
            _sheet.AddComponent<MeshFilter>().sharedMesh = _quad;
            Plain(_sheet.AddComponent<MeshRenderer>()).sharedMaterial = _clear;
        }

        private static Renderer Plain(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return r;
        }

        private static Il2CppReferenceArray<Material> Mats(Il2CppReferenceArray<Material> original, int subs)
        {
            subs = Mathf.Clamp(subs, 1, 16);
            var a = new Il2CppReferenceArray<Material>(subs);
            bool any = false;
            for (int i = 0; i < subs; i++)
            {
                var m = original != null && i < original.Length ? original[i] : null;
                bool solid = m == null || m.renderQueue < 2450;
                a[i] = solid ? _shade : _none;
                any |= solid;
            }
            return any ? a : null;
        }

        private static void Build(Transform av)
        {
            foreach (var g in _skins) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (var t in _boneDst) if (t != null) UnityEngine.Object.Destroy(t.gameObject);
            _skins.Clear(); _boneSrc.Clear(); _boneDst.Clear();
            _avatarId = av.GetInstanceID();
            _fresh = true; _layer = -1;

            S("copy body: player layers");
            int pl = 0; try { pl = PlayerLayers != null ? PlayerLayers() : 0; } catch { }
            S("copy body: find meshes");
            pl &= ~1;
            int st = LayerMask.NameToLayer("Static");
            _mask = (1 | (st >= 0 ? 1 << st : 0)) & ~pl;
            _maskAll = ~(1 << 2) & ~pl;

            var index = new Dictionary<int, int>();
            int meshes = 0;
            foreach (var smr in av.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (smr == null) continue;
                S("copy body: read mesh");
                var mesh = smr.sharedMesh;
                S("copy body: read bones");
                var bones = smr.bones;
                if (mesh == null || bones == null || bones.Length == 0) continue;
                S("copy body: read materials");
                var mats = Mats(smr.sharedMaterials, mesh.subMeshCount);
                S("copy body: materials ok");
                if (mats == null) continue;
                S($"copy mesh \"{smr.name}\": {bones.Length} bones, {mesh.subMeshCount} submesh");
                var mapped = new Il2CppReferenceArray<Transform>(bones.Length);
                for (int i = 0; i < bones.Length; i++)
                {
                    var b = bones[i];
                    if (b == null) { mapped[i] = _anchor; continue; }
                    int id = b.GetInstanceID();
                    if (!index.TryGetValue(id, out int at))
                    {
                        at = _boneSrc.Count; index[id] = at;
                        var p = new GameObject("Bone").transform;
                        p.SetParent(_inner, false);
                        p.localScale = b.lossyScale;
                        _boneSrc.Add(b); _boneDst.Add(p);
                    }
                    mapped[i] = _boneDst[at];
                }
                S("copy body: make copy");
                var go = new GameObject("Skin");
                go.transform.SetParent(_root.transform, false);
                var copy = go.AddComponent<SkinnedMeshRenderer>();
                copy.sharedMesh = mesh;
                S("copy body: set bones");
                copy.bones = mapped;
                S("copy body: set materials");
                copy.rootBone = _anchor;
                copy.quality = SkinQuality.Bone2;
                copy.updateWhenOffscreen = false;
                copy.localBounds = new Bounds(Vector3.zero, Vector3.one * 30f);
                copy.sharedMaterials = mats;
                Plain(copy);
                _skins.Add(go);
                meshes++;
            }
            Mod.Log.Msg($"Shadows: body copied ({meshes} meshes, {_boneSrc.Count} bones)");
        }

        private static readonly List<Transform> _roots = new List<Transform>();
        private static readonly List<(MeshRenderer r, Mesh m, float size)> _found = new List<(MeshRenderer, Mesh, float)>();

        private static void Items()
        {
            _roots.Clear();
            for (int h = 1; h <= 2; h++)
            {
                GameObject go = null;
                try { go = Held != null ? Held(h) : null; } catch { }
                if (go == null) continue;
                Transform root = null;
                try { root = ItemRoot != null ? ItemRoot(go) : null; } catch { }
                root ??= go.transform;
                if (!_roots.Contains(root)) _roots.Add(root);
            }
            int key = 17;
            foreach (var r in _roots) key = key * 31 + r.GetInstanceID();
            if (key == _itemsKey) return;
            _itemsKey = key;
            foreach (var g in _partGo) if (g != null) UnityEngine.Object.Destroy(g);
            _partGo.Clear(); _partSrc.Clear(); _partDst.Clear(); _found.Clear();
            _fresh = true; _layer = -1;
            foreach (var root in _roots)
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (mr == null || !mr.enabled) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    var e = mr.bounds.extents;
                    _found.Add((mr, mesh, e.x * e.y * e.z));
                }
            _found.Sort((x, y) => y.size.CompareTo(x.size));
            for (int i = 0; i < _found.Count && _partSrc.Count < MaxParts; i++)
            {
                var (mr, mesh, _) = _found[i];
                var mats = Mats(mr.sharedMaterials, mesh.subMeshCount);
                S("copy body: materials ok");
                if (mats == null) continue;
                var go = new GameObject("Part");
                go.transform.SetParent(_inner, false);
                go.transform.localScale = mr.transform.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var copy = go.AddComponent<MeshRenderer>();
                copy.sharedMaterials = mats;
                Plain(copy);
                _partSrc.Add(mr.transform); _partDst.Add(go.transform); _partGo.Add(go);
            }
            _found.Clear();
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

        private static void Run(Config cfg, Camera head)
        {
            float now = Time.unscaledTime, dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            Transform av = null;
            try { av = Avatar(); } catch { }
            if (av == null) { Show(false); return; }
            int avId = av.GetInstanceID();
            if (avId != _seenAvatar) { _seenAvatar = avId; _wait = Mathf.Max(_wait, now + 3f); }
            if (now < _wait) { Show(false); return; }
            S("setup"); Ensure();
            if (av.GetInstanceID() != _avatarId) { S("copy body"); Build(av); }
            if (now >= _nextItems) { _nextItems = now + 0.25f; S("held items"); Items(); }
            if (_boneDst.Count == 0) { Show(false); return; }

            S("sun");
            if (now >= _sunAt) { _sunAt = now + 6f; if (_sun == null || !_sun.isActiveAndEnabled) FindSun(); }
            Vector3 headAt = head.transform.position;
            Vector3 want = Vector3.down; float wantK = 0.6f;
            if (_sun != null && _sun.isActiveAndEnabled && _sun.intensity > 0.05f)
            {
                var sd = _sun.transform.forward;
                if (sd.y < -0.2f && !Physics.Raycast(headAt, -sd, 80f, _mask, QueryTriggerInteraction.Ignore)) { want = sd; wantK = 1f; }
            }
            float target = 1f;
            if (Vector3.Angle(want, _dir) < 5f) { _dir = want; _k = wantK; }
            else { target = 0f; if (_fade <= 0.02f) { _dir = want; _k = wantK; _plane = false; } }
            _fade = Mathf.MoveTowards(_fade, target, dt * 5f);

            S("ground");
            Vector3 origin = headAt - Vector3.up * 0.75f;
            Vector3 d = _dir;
            bool hitAny = Physics.Raycast(origin, d, out var hit, Reach, _mask, QueryTriggerInteraction.Ignore)
                       || Physics.Raycast(origin, d, out hit, Reach, _maskAll, QueryTriggerInteraction.Ignore);
            if (hitAny)
            {
                Vector3 hp = hit.point, hn = hit.normal;
                if (!_plane || Vector3.Dot(hn, _planeN) < 0.95f) { _planeP = hp; _planeN = hn; }
                else
                {
                    float k = 1f - Mathf.Exp(-dt * 20f);
                    _planeN = Vector3.Slerp(_planeN, hn, k).normalized;
                    _planeP = Vector3.Lerp(_planeP, hp, k);
                }
                _plane = true;
            }
            else _plane = false;
            if (!_plane) { Show(false); return; }

            Vector3 n = _planeN;
            float dn = Vector3.Dot(d, n);
            float dark = Mathf.Clamp01(cfg.ShadowDarkness) * _k * _fade * Mathf.Clamp01((-dn - 0.1f) / 0.2f);
            if (dark < 0.01f) { Show(false); return; }

            S("place");
            var a = Vector3.Cross(n, d);
            if (a.sqrMagnitude < 1e-6f) a = Vector3.Cross(n, Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.forward);
            a.Normalize();
            var b2 = Vector3.Cross(d, a).normalized;
            var t = b2 - d * (Vector3.Dot(b2, n) / dn);
            float stretch = t.magnitude;
            t /= stretch;
            var at2 = origin - d * (Vector3.Dot(origin - _planeP, n) / dn) + n * Lift;
            _outer.SetPositionAndRotation(at2, Quaternion.LookRotation(Vector3.Cross(a, t), t));
            _outer.localScale = new Vector3(1f, Mathf.Min(stretch, 12f), Thin);
            _inner.localRotation = Quaternion.Inverse(Quaternion.LookRotation(d, b2));
            _anchor.position = at2;
            _sheet.transform.SetPositionAndRotation(at2, Quaternion.LookRotation(n, a));
            _sheet.transform.localScale = new Vector3(60f, 60f, 1f);
            _shade.SetFloat(KId, dark);

            int layer = FlashBeams.HeadsetLayer != null ? FlashBeams.HeadsetLayer() : -1;
            if (layer < 0) layer = 0;
            if (layer != _layer)
            {
                _layer = layer;
                _sheet.layer = layer;
                foreach (var g in _skins) if (g != null) g.layer = layer;
                foreach (var g in _partGo) if (g != null) g.layer = layer;
            }

            S("follow");
            bool scale = _fresh || (++_frame & 7) == 0;
            bool active = _fresh || (_frame & 3) == 0;
            for (int i = 0; i < _boneSrc.Count; i++)
            {
                var src = _boneSrc[i];
                if (src == null) continue;
                var dst = _boneDst[i];
                dst.localPosition = src.position - origin;
                dst.localRotation = src.rotation;
                if (scale) dst.localScale = src.lossyScale;
            }
            for (int i = 0; i < _partSrc.Count; i++)
            {
                var src = _partSrc[i];
                var go = _partGo[i];
                if (src == null) { if (go != null && go.activeSelf) go.SetActive(false); continue; }
                if (active) { bool on = src.gameObject.activeInHierarchy; if (go.activeSelf != on) go.SetActive(on); }
                var dst = _partDst[i];
                dst.localPosition = src.position - origin;
                dst.localRotation = src.rotation;
                if (scale) dst.localScale = src.lossyScale;
            }
            _fresh = false;
            Show(true);
            if (!_logged)
            {
                S("first frame finished");
                try { System.IO.File.WriteAllText(StepFile, "ok"); } catch { }
                _logged = true;
                Mod.Log.Msg($"Shadows: body shadow on ({_skins.Count} meshes, {_boneSrc.Count} bones, {_partSrc.Count} held parts, {(_sun != null ? "sun '" + _sun.name + "'" : "no sun on this map, shadow falls straight down")}, {SystemInfo.graphicsDeviceType})");
            }
        }
    }
}
