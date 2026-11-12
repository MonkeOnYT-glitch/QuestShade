using System;
using System.Collections.Generic;
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
        public static bool CameraOpen;

        private const int MaxParts = 12;
        private const float Reach = 40f, Lift = 0.012f;
        private const string PartName = "QuestShade_ShadowPart";
        private static readonly int KId = Shader.PropertyToID("_QS_ShadowK"), MaskId = Shader.PropertyToID("_QS_Mask"),
            CompId = Shader.PropertyToID("_QS_Comp"), PassId = Shader.PropertyToID("_QS_Pass"), ZTestId = Shader.PropertyToID("_QS_ZTest"),
            PointId = Shader.PropertyToID("_QS_ShPoint"), NormalId = Shader.PropertyToID("_QS_ShNormal"), DirId = Shader.PropertyToID("_QS_ShDir");

        private static GameObject _root, _sheet, _sheetLens;
        private static Material _shade, _none, _clear, _shadeLens, _noneLens, _clearLens;
        private static readonly List<GameObject> _skins = new List<GameObject>(), _skinsLens = new List<GameObject>();
        private static readonly List<GameObject> _parts = new List<GameObject>(), _partsLens = new List<GameObject>();
        private static readonly Vector3 Slant = new Vector3(0.42f, -1f, 0.3f).normalized;
        private static Mesh _quad;
        private static int _avatarId, _itemsKey, _mask, _maskAll, _layer = -1, _lensLayer = -2, _seenAvatar;
        private static float _nextItems, _sunAt, _sunSeenAt, _fade, _k = 0.6f, _wait;
        private static Light _sun;
        private static Vector3 _dir = new Vector3(0.42f, -1f, 0.3f).normalized, _planeP, _planeN = Vector3.up;
        private static bool _plane, _failed, _logged, _checked, _sunSeen, _partsOn;

        private static string StepFile => System.IO.Path.Combine(Config.Root, "shadow5-step.txt");

        public static void OnLevel()
        {
            _sun = null; _sunAt = Time.unscaledTime + 2f; _fade = 0f; _logged = false; _plane = false; _sunSeen = false;
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
                Mod.Log.Warning($"Shadows: last time the game died at shadow step \"{last}\". Body shadow is OFF now so you can play. Please send this log. (Turn Body shadow off and on in Lighting > Body shadow to try again.)");
            }
            catch { }
            return _failed;
        }

        public static void Retry()
        {
            _checked = true; _failed = false; _logged = false; _wait = 0f;
            try { if (System.IO.File.Exists(StepFile)) System.IO.File.Delete(StepFile); } catch { }
            Mod.Log.Msg("Shadows: trying the body shadow again");
        }

        public static void Tick(Config cfg, Camera head)
        {
            bool want = cfg.Shadows && cfg.Enabled && !_failed && ShaderPack.Shadow != null && head != null && Avatar != null;
            if (want && DiedLastTime()) want = false;
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
            if (_partsOn == on) return;
            _partsOn = on;
            foreach (var g in _parts) if (g != null && g.activeSelf != on) g.SetActive(on);
        }

        private static Material Mat(string name, int queue, float mask, float comp, float pass, float ztest, bool stereo = true)
        {
            var m = new Material(ShaderPack.Shadow) { name = "QuestShade_" + name, hideFlags = HideFlags.HideAndDontSave, renderQueue = queue };
            m.SetFloat(MaskId, mask); m.SetFloat(CompId, comp); m.SetFloat(PassId, pass); m.SetFloat(ZTestId, ztest);
            ShaderPack.SetStereo(m, stereo);
            return m;
        }

        private static void Ensure()
        {
            if (_shade == null) _shade = Mat("Shadow", 2510, 15f, 6f, 2f, 4f);
            if (_none == null) _none = Mat("ShadowNone", 2510, 0f, 1f, 0f, 4f);
            if (_clear == null) _clear = Mat("ShadowClear", 2511, 0f, 8f, 1f, 8f);
            if (_shadeLens == null) _shadeLens = Mat("ShadowLens", 2510, 15f, 6f, 2f, 4f, false);
            if (_clearLens == null) _clearLens = Mat("ShadowClearLens", 2511, 0f, 8f, 1f, 8f, false);
            if (_noneLens == null) _noneLens = Mat("ShadowNoneLens", 2510, 0f, 1f, 0f, 4f, false);
            if (_quad == null)
            {
                _quad = new Mesh { name = "QuestShade_ShadowSheet", hideFlags = HideFlags.HideAndDontSave };
                _quad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) };
                _quad.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            }
            if (_root != null) return;
            _skins.Clear(); _skinsLens.Clear(); _lensLayer = -2;
            _avatarId = 0; _layer = -1;
            _root = new GameObject("QuestShade_Shadow");
            _root.SetActive(false);
            _sheet = new GameObject("Sheet");
            _sheet.transform.SetParent(_root.transform, false);
            _sheet.AddComponent<MeshFilter>().sharedMesh = _quad;
            Plain(_sheet.AddComponent<MeshRenderer>()).sharedMaterial = _clear;
            _sheetLens = new GameObject("SheetLens");
            _sheetLens.transform.SetParent(_sheet.transform, false);
            _sheetLens.AddComponent<MeshFilter>().sharedMesh = _quad;
            Plain(_sheetLens.AddComponent<MeshRenderer>()).sharedMaterial = _clearLens;
            _sheetLens.SetActive(false);
        }

        private static Renderer Plain(Renderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return r;
        }

        private static readonly bool[] _solid = new bool[16];

        private static bool ReadSolid(Renderer src, int subs)
        {
            subs = Math.Clamp(subs, 1, 16);
            for (int i = 0; i < 16; i++) _solid[i] = true;
            int k = 0;
            foreach (var m in src.sharedMaterials)
            {
                if (k >= 16) break;
                _solid[k++] = m == null || m.renderQueue < 2450;
            }
            for (int i = 0; i < subs; i++) if (_solid[i]) return true;
            return false;
        }

        private static void Shade(Renderer r, int subs, Material mat, Material none)
        {
            subs = Math.Clamp(subs, 1, 16);
            if (subs == 1) { r.sharedMaterial = _solid[0] ? mat : none; return; }
            var a = new Material[subs];
            for (int i = 0; i < subs; i++) a[i] = _solid[i] ? mat : none;
            r.sharedMaterials = a;
        }

        private static SkinnedMeshRenderer Skin(string name, SkinnedMeshRenderer src, Mesh mesh, int subs, Material mat, Material none)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var copy = go.AddComponent<SkinnedMeshRenderer>();
            copy.sharedMesh = mesh;
            S("copy body: share bones");
            copy.bones = src.bones;
            S("copy body: set root bone");
            var rb = src.rootBone;
            copy.rootBone = rb != null ? rb : src.transform;
            copy.quality = SkinQuality.Bone2;
            copy.updateWhenOffscreen = false;
            S("copy body: set bounds");
            var lb = src.localBounds;
            copy.localBounds = new Bounds(lb.center, lb.size + Vector3.one * 8f);
            Shade(copy, subs, mat, none);
            Plain(copy);
            return copy;
        }

        private static void Build(Transform av)
        {
            foreach (var g in _skins) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (var g in _skinsLens) if (g != null) UnityEngine.Object.Destroy(g);
            _skins.Clear(); _skinsLens.Clear(); _lensLayer = -2; _layer = -1;
            _avatarId = av.GetInstanceID();

            S("copy body: player layers");
            int pl = 0; try { pl = PlayerLayers != null ? PlayerLayers() : 0; } catch { }
            pl &= ~1;
            int st = LayerMask.NameToLayer("Static");
            _mask = (1 | (st >= 0 ? 1 << st : 0)) & ~pl;
            _maskAll = ~(1 << 2) & ~pl;

            S("copy body: find meshes");
            foreach (var smr in av.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (smr == null) continue;
                S("copy body: read mesh");
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                int subs = mesh.subMeshCount;
                S("copy body: check see-through parts");
                if (!ReadSolid(smr, subs)) continue;
                S("copy body: make copy");
                _skins.Add(Skin("Skin", smr, mesh, subs, _shade, _none).gameObject);
                var lens = Skin("SkinLens", smr, mesh, subs, _shadeLens, _noneLens).gameObject;
                lens.SetActive(false);
                _skinsLens.Add(lens);
            }
            Mod.Log.Msg($"Shadows: body copied ({_skins.Count} meshes, bones shared with the avatar)");
        }

        private static readonly List<Transform> _roots = new List<Transform>();
        private static readonly List<(MeshRenderer r, Mesh m, float size)> _found = new List<(MeshRenderer, Mesh, float)>();

        private static GameObject Part(string name, Transform parent, Mesh mesh, int subs, Material mat, Material none)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var copy = go.AddComponent<MeshRenderer>();
            Shade(copy, subs, mat, none);
            Plain(copy);
            return go;
        }

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
            foreach (var g in _parts) if (g != null) UnityEngine.Object.Destroy(g);
            _parts.Clear(); _partsLens.Clear(); _found.Clear(); _lensLayer = -2; _layer = -1;
            foreach (var root in _roots)
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (mr == null || !mr.enabled) continue;
                    if (mr.gameObject.name.StartsWith("QuestShade", StringComparison.Ordinal)) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    var e = mr.bounds.extents;
                    if (Math.Min(e.x, Math.Min(e.y, e.z)) < 0.0015f) continue;
                    _found.Add((mr, mesh, e.x * e.y * e.z));
                }
            _found.Sort((x, y) => y.size.CompareTo(x.size));
            for (int i = 0; i < _found.Count && _parts.Count < MaxParts; i++)
            {
                var (mr, mesh, _) = _found[i];
                int subs = mesh.subMeshCount;
                if (!ReadSolid(mr, subs)) continue;
                var go = Part(PartName, mr.transform, mesh, subs, _shade, _none);
                var lens = Part(PartName + "Lens", go.transform, mesh, subs, _shadeLens, _noneLens);
                lens.SetActive(false);
                go.SetActive(_partsOn);
                _parts.Add(go); _partsLens.Add(lens);
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
            if (avId != _avatarId) { S("copy body"); Build(av); }
            if (now >= _nextItems) { _nextItems = now + 0.25f; S("held items"); Items(); }
            if (_skins.Count == 0) { Show(false); return; }

            S("sun");
            if (now >= _sunAt) { _sunAt = now + 6f; if (_sun == null || !_sun.isActiveAndEnabled) FindSun(); }
            Vector3 headAt = head.transform.position;
            Vector3 want = Slant; float wantK = 0.7f;
            if (_sun != null && _sun.isActiveAndEnabled && _sun.intensity > 0.05f)
            {
                var sd = _sun.transform.forward;
                if (sd.y < -0.2f)
                {
                    if (now >= _sunSeenAt) { _sunSeenAt = now + 0.2f; _sunSeen = !Physics.Raycast(headAt, -sd, 80f, _mask, QueryTriggerInteraction.Ignore); }
                    if (_sunSeen) { want = sd; wantK = 1f; }
                }
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
            Shader.SetGlobalVector(PointId, new Vector4(_planeP.x, _planeP.y, _planeP.z, 0f));
            Shader.SetGlobalVector(NormalId, new Vector4(n.x, n.y, n.z, Lift));
            Shader.SetGlobalVector(DirId, new Vector4(d.x, d.y, d.z, 0f));
            var a = Vector3.Cross(n, Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.forward).normalized;
            var at = origin - d * (Vector3.Dot(origin - _planeP, n) / dn);
            _sheet.transform.SetPositionAndRotation(at, Quaternion.LookRotation(n, a));
            _sheet.transform.localScale = new Vector3(60f, 60f, 1f);
            _shade.SetFloat(KId, dark); _shadeLens.SetFloat(KId, dark);

            int layer = FlashBeams.HeadsetLayer != null ? FlashBeams.HeadsetLayer() : -1;
            if (layer < 0) layer = 0;
            if (layer != _layer)
            {
                _layer = layer;
                _sheet.layer = layer;
                foreach (var g in _skins) if (g != null) g.layer = layer;
                foreach (var g in _parts) if (g != null) g.layer = layer;
            }
            int lensLayer = CameraOpen && FlashBeams.LensLayer != null ? FlashBeams.LensLayer() : -1;
            if (lensLayer != _lensLayer)
            {
                _lensLayer = lensLayer;
                bool on = lensLayer >= 0;
                if (_sheetLens != null) { if (on) _sheetLens.layer = lensLayer; _sheetLens.SetActive(on); }
                foreach (var g in _skinsLens) if (g != null) { if (on) g.layer = lensLayer; g.SetActive(on); }
                foreach (var g in _partsLens) if (g != null) { if (on) g.layer = lensLayer; g.SetActive(on); }
            }

            Show(true);
            if (!_logged)
            {
                S("first frame finished");
                try { System.IO.File.WriteAllText(StepFile, "ok"); } catch { }
                _logged = true;
                Mod.Log.Msg($"Shadows: body shadow on ({_skins.Count} meshes, {_parts.Count} held parts, {(_sun != null ? "sun '" + _sun.name + "'" : "no sun on this map, shadow falls straight down")}, {SystemInfo.graphicsDeviceType})");
            }
        }
    }
}
