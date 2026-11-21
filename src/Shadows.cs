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

        private static GameObject _root, _sheet, _sheetLens, _sheetWall, _sheetWallLens;
        private static Material _shade, _none, _clear, _shadeLens, _noneLens, _clearLens;
        private static readonly List<GameObject> _skins = new List<GameObject>(), _skinsLens = new List<GameObject>();
        private static readonly List<GameObject> _skinsWall = new List<GameObject>(), _skinsWallLens = new List<GameObject>();
        private static readonly List<GameObject> _parts = new List<GameObject>(), _partsLens = new List<GameObject>();
        private static readonly List<GameObject> _partsWall = new List<GameObject>(), _partsWallLens = new List<GameObject>();
        private static readonly Vector3 Slant = new Vector3(0.42f, -1f, 0.3f).normalized;
        private static Mesh _quad;
        private static int _avatarId, _itemsKey, _mask, _maskAll, _layer = -1, _lensLayer = -2, _seenAvatar;
        private static float _nextItems, _sunAt, _sunSeenAt, _fade, _k = 0.6f, _wait;
        private static Light _sun;
        private static Vector3 _dir = new Vector3(0.42f, -1f, 0.3f).normalized, _planeP, _planeN = Vector3.up, _wallP, _wallN = Vector3.up;
        private static bool _plane, _wall, _failed, _logged, _checked, _sunSeen, _partsOn, _stepsOk;

        private static string StepFile => System.IO.Path.Combine(Config.Root, "shadow5-step.txt");

        public static void OnLevel()
        {
            _sun = null; _sunAt = Time.unscaledTime + 2f; _fade = 0f; _logged = false; _plane = false; _wall = false; _sunSeen = false;
            _wait = Time.unscaledTime + 4f;
        }

        private static void S(string s)
        {
            if (_logged || _stepsOk) return;
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
            if (_othersOn != on) { _othersOn = on; foreach (var g in _others) if (g != null && g.activeSelf != on) g.SetActive(on); }
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
            _sheetWall = new GameObject("SheetWall");
            _sheetWall.transform.SetParent(_root.transform, false);
            _sheetWall.AddComponent<MeshFilter>().sharedMesh = _quad;
            Plain(_sheetWall.AddComponent<MeshRenderer>()).sharedMaterial = _clear;
            _sheetWall.SetActive(false);
            _sheetWallLens = new GameObject("SheetWallLens");
            _sheetWallLens.transform.SetParent(_sheetWall.transform, false);
            _sheetWallLens.AddComponent<MeshFilter>().sharedMesh = _quad;
            Plain(_sheetWallLens.AddComponent<MeshRenderer>()).sharedMaterial = _clearLens;
            _sheetWallLens.SetActive(false);
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
        private static readonly Texture[] _cutTex = new Texture[16];
        private static readonly float[] _cutAt = new float[16];
        private static readonly Dictionary<long, Material> _cutMats = new Dictionary<long, Material>();
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex"), ClipId = Shader.PropertyToID("_QS_Clip");

        private static bool ReadSolid(Renderer src, int subs, bool cutouts = false)
        {
            subs = Math.Clamp(subs, 1, 16);
            for (int i = 0; i < 16; i++) { _solid[i] = true; _cutTex[i] = null; }
            int k = 0;
            foreach (var m in src.sharedMaterials)
            {
                if (k >= 16) break;
                int i = k++;
                if (m == null || m.renderQueue < 2450) continue;
                _solid[i] = false;
                if (!cutouts || m.renderQueue >= 3500) continue;
                var tex = AlphaTexture(m);
                if (tex == null) continue;
                _cutTex[i] = tex;
                _cutAt[i] = m.HasProperty("_Cutoff") ? Mathf.Clamp(m.GetFloat("_Cutoff"), 0.1f, 0.9f) : 0.5f;
                _solid[i] = true;
            }
            for (int i = 0; i < subs; i++) if (_solid[i]) return true;
            return false;
        }

        private static Texture AlphaTexture(Material m)
        {
            try
            {
                foreach (var name in new[] { "_MainTex", "_BaseMap", "_AlbedoMap", "_Albedo" })
                    if (m.HasProperty(name)) { var t = m.GetTexture(name); if (t != null) return t; }
            }
            catch { }
            return null;
        }

        private static Material Cut(Material baseMat, Texture tex, float at, bool lens)
        {
            long key = ((long)tex.GetInstanceID() << 2) ^ (lens ? 1 : 0) ^ ((long)Mathf.RoundToInt(at * 100f) << 40);
            if (_cutMats.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(baseMat) { name = baseMat.name + "_Cut", hideFlags = HideFlags.HideAndDontSave };
            m.SetTexture(MainTexId, tex);
            m.SetFloat(ClipId, at);
            _cutMats[key] = m;
            return m;
        }

        private static void Shade(Renderer r, int subs, Material mat, Material none)
        {
            subs = Math.Clamp(subs, 1, 16);
            bool lens = mat == _shadeLens;
            Material Pick(int i) => !_solid[i] ? none : _cutTex[i] != null ? Cut(mat, _cutTex[i], _cutAt[i], lens) : mat;
            if (subs == 1) { r.sharedMaterial = Pick(0); return; }
            var a = new Material[subs];
            for (int i = 0; i < subs; i++) a[i] = Pick(i);
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
            foreach (var g in _skinsWall) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (var g in _skinsWallLens) if (g != null) UnityEngine.Object.Destroy(g);
            _skins.Clear(); _skinsLens.Clear(); _skinsWall.Clear(); _skinsWallLens.Clear(); _lensLayer = -2; _layer = -1;
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
                if (!ReadSolid(smr, subs, true)) continue;
                int cut = 0; for (int i = 0; i < Math.Min(subs, 16); i++) if (_cutTex[i] != null) cut++;
                if (cut > 0) Mod.Log.Msg($"Shadows: {smr.name} has {cut} see-through part(s) (hair, lashes...): shadow cut to their shape");
                S("copy body: make copy");
                _skins.Add(Skin("Skin", smr, mesh, subs, _shade, _none).gameObject);
                var lens = Skin("SkinLens", smr, mesh, subs, _shadeLens, _noneLens).gameObject;
                lens.SetActive(false);
                _skinsLens.Add(lens);
                var wall = Skin("SkinWall", smr, mesh, subs, _shade, _none).gameObject;
                wall.SetActive(false);
                _skinsWall.Add(wall);
                var wallLens = Skin("SkinWallLens", smr, mesh, subs, _shadeLens, _noneLens).gameObject;
                wallLens.SetActive(false);
                _skinsWallLens.Add(wallLens);
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
            foreach (var g in _partsWall) if (g != null) UnityEngine.Object.Destroy(g);
            _parts.Clear(); _partsLens.Clear(); _partsWall.Clear(); _partsWallLens.Clear(); _found.Clear(); _lensLayer = -2; _layer = -1; _heldGroups.Clear();
            foreach (var root in _roots) _heldGroups.Add(new Group { Root = root });
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
                var wallGo = Part(PartName + "Wall", mr.transform, mesh, subs, _shade, _none);
                var wallLens = Part(PartName + "WallLens", wallGo.transform, mesh, subs, _shadeLens, _noneLens);
                wallLens.SetActive(false);
                wallGo.SetActive(false);
                _partsWall.Add(wallGo); _partsWallLens.Add(wallLens);
                Group grp = null;
                foreach (var hg in _heldGroups) if (mr.transform.IsChildOf(hg.Root)) { grp = hg; break; }
                if (grp != null)
                {
                    grp.Src ??= mr;
                    grp.Copies.Add(go.GetComponent<Renderer>()); grp.Copies.Add(lens.GetComponent<Renderer>());
                    grp.WallCopies.Add(wallGo.GetComponent<Renderer>()); grp.WallCopies.Add(wallLens.GetComponent<Renderer>());
                }
            }
            _found.Clear();
        }


        // ---- NPCs, spawned props and loose physics props near the player ----
        // Kept cheap on purpose: a local overlap check instead of scanning every object in the level, one shadow copy
        // (plus its camera copy) per mesh, and only a couple of objects re-cast onto their floor each frame.
        private const int MaxOthers = 6, MaxOtherMeshes = 16, MaxOtherSkins = 6, PlacePerFrame = 2;
        private const float OthersReach = 12f;
        private static readonly List<GameObject> _others = new List<GameObject>(), _othersLens = new List<GameObject>();
        private static readonly List<(Transform t, float d)> _near = new List<(Transform, float)>();
        private static readonly HashSet<int> _seenRoots = new HashSet<int>();
        private static readonly Collider[] _cols = new Collider[96];
        private static int _othersKey, _placeNext;
        private static float _nextOthers;
        private static bool _othersOn;
        private static Vector3 _placedDir;
        private sealed class Group
        {
            public Transform Root; public Renderer Src;
            public readonly List<Renderer> Copies = new List<Renderer>(), WallCopies = new List<Renderer>();
            public bool Placed, WallPlaced; public Vector3 P, N, WP, WN;
        }
        private static readonly List<Group> _groups = new List<Group>();
        private static readonly List<Group> _heldGroups = new List<Group>();
        private static readonly RaycastHit[] _hits = new RaycastHit[12];
        private static MaterialPropertyBlock _mpb;

        private static void ClearOthers()
        {
            foreach (var g in _others) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (var g in _othersLens) if (g != null) UnityEngine.Object.Destroy(g);
            _others.Clear(); _othersLens.Clear(); _othersKey = 0; _groups.Clear(); _placeNext = 0;
        }

        private static void Others(Transform av, Vector3 at)
        {
            _near.Clear(); _seenRoots.Clear();
            Transform rig = av; for (int i = 0; i < 6 && rig.parent != null; i++) rig = rig.parent;
            int n = Physics.OverlapSphereNonAlloc(at, OthersReach, _cols, _maskAll, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var col = _cols[i];
                if (col == null) continue;
                Transform root = null;
                var pool = col.GetComponentInParent<Il2CppSLZ.Marrow.Pool.Poolee>();
                if (pool != null) root = pool.transform;
                else { var rb = col.attachedRigidbody; if (rb != null && !rb.isKinematic) root = rb.transform; }
                if (root == null || !_seenRoots.Add(root.GetInstanceID())) continue;
                if (root.name.StartsWith("QuestShade", StringComparison.Ordinal) || root.IsChildOf(rig) || _roots.Contains(root)) continue;
                bool inside = false;
                foreach (var r in _roots) if (root.IsChildOf(r)) { inside = true; break; }
                if (inside) continue;
                _near.Add((root, (root.position - at).sqrMagnitude));
            }
            _near.Sort((x, y) => x.d.CompareTo(y.d));
            if (_near.Count > MaxOthers) _near.RemoveRange(MaxOthers, _near.Count - MaxOthers);
            int key = 17;
            foreach (var e in _near) key = key * 31 + e.t.GetInstanceID();
            if (key == _othersKey) return;
            ClearOthers();
            _othersKey = key;
            _lensLayer = -2; _layer = -1;
            int meshes = 0, skins = 0;
            foreach (var (root, _) in _near)
            {
                if (meshes >= MaxOtherMeshes) break;
                var grp = new Group { Root = root };
                HashSet<int> lodKeep = null;
                var lodg = root.GetComponentInChildren<LODGroup>();
                if (lodg != null) { var lods = lodg.GetLODs(); if (lods != null && lods.Length > 1) { lodKeep = new HashSet<int>(); foreach (var lr in lods[0].renderers) if (lr != null) lodKeep.Add(lr.GetInstanceID()); } }
                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (meshes >= MaxOtherMeshes || skins >= MaxOtherSkins) break;
                    if (smr == null || !smr.enabled || smr.sharedMesh == null) continue;
                    if (lodKeep != null && !lodKeep.Contains(smr.GetInstanceID())) continue;
                    var mesh = smr.sharedMesh; int subs = mesh.subMeshCount;
                    if (!ReadSolid(smr, subs, true)) continue;
                    var go = Skin("NpcSkin", smr, mesh, subs, _shade, _none).gameObject;
                    var lens = Skin("NpcSkinLens", smr, mesh, subs, _shadeLens, _noneLens).gameObject;
                    lens.SetActive(false);
                    grp.Src ??= smr; grp.Copies.Add(go.GetComponent<Renderer>()); grp.Copies.Add(lens.GetComponent<Renderer>());
                    _others.Add(go); _othersLens.Add(lens); meshes++; skins++;
                }
                foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(false))
                {
                    if (meshes >= MaxOtherMeshes) break;
                    if (mr == null || !mr.enabled || mr.gameObject.name.StartsWith("QuestShade", StringComparison.Ordinal)) continue;
                    if (lodKeep != null && !lodKeep.Contains(mr.GetInstanceID())) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    var mesh = mf != null ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    var ext = mr.bounds.extents;
                    if (Math.Min(ext.x, Math.Min(ext.y, ext.z)) < 0.0015f || Math.Max(ext.x, Math.Max(ext.y, ext.z)) > 6f) continue;
                    int subs = mesh.subMeshCount;
                    if (!ReadSolid(mr, subs)) continue;
                    var go = Part(PartName, mr.transform, mesh, subs, _shade, _none);
                    var lens = Part(PartName + "Lens", go.transform, mesh, subs, _shadeLens, _noneLens);
                    lens.SetActive(false);
                    go.SetActive(_othersOn);
                    grp.Src ??= mr; grp.Copies.Add(go.GetComponent<Renderer>()); grp.Copies.Add(lens.GetComponent<Renderer>());
                    _others.Add(go); _othersLens.Add(lens); meshes++;
                }
                if (grp.Copies.Count > 0) { foreach (var r in grp.Copies) if (r != null) r.enabled = false; _groups.Add(grp); }
            }
        }

        private static bool BadFloorHit(RaycastHit hit)
        {
            var col = hit.collider;
            if (col == null) return true;
            var go = col.gameObject;
            if (go == null) return true;
            // Only skip obvious non-floors. The old "see-through material" test (which also looked at parent objects)
            // threw out floors like Void G114's grid, so shadows never found the ground.
            if (col.GetComponent<SpriteRenderer>() != null) return true;
            if (go.name.IndexOf("decal", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Finds the floor (and a wall, if wanted) under an object along the light, ignoring the object itself.
        private static void Cast(Group g, Vector3 d, Transform skip, bool wantWall, out bool floor, out RaycastHit fh, out bool wall, out RaycastHit wh)
        {
            floor = wall = false; fh = wh = default;
            int n = Physics.RaycastNonAlloc(g.Src.bounds.center, d, _hits, 20f, _maskAll, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i]; var col = h.collider;
                if (col == null) continue;
                var t = col.transform;
                if (t.IsChildOf(g.Root) || (skip != null && t.IsChildOf(skip)) || BadFloorHit(h)) continue;
                if (h.normal.y > 0.5f) { if (Vector3.Dot(d, h.normal) < -0.1f && (!floor || h.distance < fh.distance)) { fh = h; floor = true; } }
                else if (wantWall && Mathf.Abs(h.normal.y) < 0.5f && Vector3.Dot(d, h.normal) < -0.35f && h.distance <= 12f && (!wall || h.distance < wh.distance)) { wh = h; wall = true; }
            }
        }

        private static void Apply(List<Renderer> copies, bool on, Vector3 p, Vector3 nrm, Vector3 d)
        {
            if (on)
            {
                _mpb.Clear();
                _mpb.SetVector(PointId, new Vector4(p.x, p.y, p.z, 0f));
                _mpb.SetVector(NormalId, new Vector4(nrm.x, nrm.y, nrm.z, Lift));
                _mpb.SetVector(DirId, new Vector4(d.x, d.y, d.z, 0f));
            }
            foreach (var r in copies)
            {
                if (r == null) continue;
                if (r.enabled != on) r.enabled = on;
                if (on) r.SetPropertyBlock(_mpb);
            }
        }

        private static bool Moved(Vector3 a, Vector3 b) => (a - b).sqrMagnitude > 0.0004f;

        private static void PlaceOthers(Vector3 d)
        {
            if (_groups.Count == 0) return;
            _mpb ??= new MaterialPropertyBlock();
            bool dirChanged = Vector3.Dot(d, _placedDir) < 0.9999f;
            if (dirChanged) { _placedDir = d; foreach (var g in _groups) g.Placed = false; }
            // Groups that were never placed go first, then a couple per frame in rotation.
            int budget = PlacePerFrame;
            foreach (var g in _groups) if (!g.Placed && budget > 0 && g.Root != null && g.Src != null) { PlaceOne(g, d); budget--; }
            for (int k = 0; k < _groups.Count && budget > 0; k++)
            {
                _placeNext = (_placeNext + 1) % _groups.Count;
                var g = _groups[_placeNext];
                if (g.Root == null || g.Src == null) continue;
                PlaceOne(g, d); budget--;
            }
        }

        private static void PlaceOne(Group g, Vector3 d)
        {
            Cast(g, d, null, false, out bool floor, out var fh, out _, out _);
            if (!g.Placed || floor != g.Copies[0].enabled || (floor && (Moved(fh.point, g.P) || Moved(fh.normal, g.N))))
            {
                Apply(g.Copies, floor, fh.point, fh.normal, d);
                if (floor) { g.P = fh.point; g.N = fh.normal; }
            }
            g.Placed = true;
        }

        // Held items move fast, so they re-cast every frame (there are at most two).
        private static void PlaceHeld(Vector3 d, Transform av)
        {
            if (_heldGroups.Count == 0) return;
            _mpb ??= new MaterialPropertyBlock();
            foreach (var g in _heldGroups)
            {
                if (g.Root == null || g.Src == null || g.Copies.Count == 0) continue;
                Cast(g, d, av, true, out bool floor, out var fh, out bool wall, out var wh);
                Apply(g.Copies, floor, fh.point, fh.normal, d);
                Apply(g.WallCopies, wall, wh.point, wh.normal, d);
            }
        }

        private static void SetList(List<GameObject> list, bool on)
        {
            foreach (var g in list) if (g != null && g.activeSelf != on) g.SetActive(on);
        }

        private static void SetBlock(List<GameObject> list)
        {
            foreach (var g in list)
            {
                if (g == null) continue;
                var r = g.GetComponent<Renderer>();
                if (r != null) r.SetPropertyBlock(_mpb);
            }
        }

        private static void SetWallBlocks(Vector3 d)
        {
            if (!_wall) return;
            _mpb ??= new MaterialPropertyBlock();
            _mpb.Clear();
            _mpb.SetVector(PointId, new Vector4(_wallP.x, _wallP.y, _wallP.z, 0f));
            _mpb.SetVector(NormalId, new Vector4(_wallN.x, _wallN.y, _wallN.z, Lift));
            _mpb.SetVector(DirId, new Vector4(d.x, d.y, d.z, 0f));
            SetBlock(_skinsWall); SetBlock(_skinsWallLens);
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
            if (now >= _nextOthers) { _nextOthers = now + 1f; S("npcs and props"); Others(av, head.transform.position); }
            // The risky part (copying meshes) is done. Mark it so a later crash elsewhere (e.g. a recording) is never blamed on shadows,
            // and stop rewriting the step file every frame while there's no floor to cast onto.
            if (!_stepsOk) { _stepsOk = true; try { System.IO.File.WriteAllText(StepFile, "ok"); } catch { } }
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
            bool hitAny = false, wallAny = false;
            RaycastHit hit = default, wallHit = default;
            int count = Physics.RaycastNonAlloc(origin, d, _hits, Reach, _mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                if (BadFloorHit(_hits[i])) continue;
                if (_hits[i].normal.y > 0.5f)
                {
                    if (!hitAny || _hits[i].distance < hit.distance) { hit = _hits[i]; hitAny = true; }
                }
                else if (Mathf.Abs(_hits[i].normal.y) < 0.5f && Vector3.Dot(d, _hits[i].normal) < -0.35f && _hits[i].distance <= 12f)
                {
                    if (!wallAny || _hits[i].distance < wallHit.distance) { wallHit = _hits[i]; wallAny = true; }
                }
            }
            bool hadFloor = hitAny, hadWall = wallAny;
            if (!hitAny || !wallAny)
            {
                count = Physics.RaycastNonAlloc(origin, d, _hits, Reach, _maskAll, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    if (BadFloorHit(_hits[i])) continue;
                    if (!hadFloor && _hits[i].normal.y > 0.5f)
                    {
                        if (!hitAny || _hits[i].distance < hit.distance) { hit = _hits[i]; hitAny = true; }
                    }
                    else if (!hadWall && Mathf.Abs(_hits[i].normal.y) < 0.5f && Vector3.Dot(d, _hits[i].normal) < -0.35f && _hits[i].distance <= 12f)
                    {
                        if (!wallAny || _hits[i].distance < wallHit.distance) { wallHit = _hits[i]; wallAny = true; }
                    }
                }
            }
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
            if (wallAny)
            {
                Vector3 hp = wallHit.point, hn = wallHit.normal;
                if (!_wall || Vector3.Dot(hn, _wallN) < 0.95f) { _wallP = hp; _wallN = hn; }
                else
                {
                    float k = 1f - Mathf.Exp(-dt * 20f);
                    _wallN = Vector3.Slerp(_wallN, hn, k).normalized;
                    _wallP = Vector3.Lerp(_wallP, hp, k);
                }
                _wall = true;
            }
            else _wall = false;
            if (!_plane && !_wall) { Show(false); return; }

            Vector3 n = _plane ? _planeN : _wallN;
            float dn = Vector3.Dot(d, n);
            float dark = Mathf.Clamp01(cfg.ShadowDarkness) * _k * _fade * Mathf.Clamp01((-dn - 0.35f) / 0.2f);
            if (dark < 0.01f) { Show(false); return; }

            S("place");
            if (_plane)
            {
                Shader.SetGlobalVector(PointId, new Vector4(_planeP.x, _planeP.y, _planeP.z, 0f));
                Shader.SetGlobalVector(NormalId, new Vector4(n.x, n.y, n.z, Lift));
                Shader.SetGlobalVector(DirId, new Vector4(d.x, d.y, d.z, 0f));
                var a = Vector3.Cross(n, Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.forward).normalized;
                var at = origin - d * (Vector3.Dot(origin - _planeP, n) / dn);
                _sheet.transform.SetPositionAndRotation(at, Quaternion.LookRotation(n, a));
                _sheet.transform.localScale = new Vector3(60f, 60f, 1f);
            }
            if (_sheet != null && _sheet.activeSelf != _plane) _sheet.SetActive(_plane);
            if (_wall)
            {
                var wa = Vector3.Cross(_wallN, Mathf.Abs(_wallN.x) < 0.9f ? Vector3.right : Vector3.forward).normalized;
                float wdn = Vector3.Dot(d, _wallN);
                var wat = origin - d * (Vector3.Dot(origin - _wallP, _wallN) / wdn);
                _sheetWall.transform.SetPositionAndRotation(wat, Quaternion.LookRotation(_wallN, wa));
                _sheetWall.transform.localScale = new Vector3(60f, 60f, 1f);
            }
            if (_sheetWall != null && _sheetWall.activeSelf != _wall) _sheetWall.SetActive(_wall);
            _shade.SetFloat(KId, dark); _shadeLens.SetFloat(KId, dark);
            foreach (var cm in _cutMats.Values) if (cm != null) cm.SetFloat(KId, dark);
            S("place npcs and props"); PlaceOthers(d); PlaceHeld(d, av);

            int layer = FlashBeams.HeadsetLayer != null ? FlashBeams.HeadsetLayer() : -1;
            if (layer < 0) layer = 0;
            if (layer != _layer)
            {
                _layer = layer;
                _sheet.layer = layer;
                if (_sheetWall != null) _sheetWall.layer = layer;
                foreach (var g in _skins) if (g != null) g.layer = layer;
                foreach (var g in _skinsWall) if (g != null) g.layer = layer;
                foreach (var g in _parts) if (g != null) g.layer = layer;
                foreach (var g in _partsWall) if (g != null) g.layer = layer;
                foreach (var g in _others) if (g != null) g.layer = layer;
            }
            int lensLayer = CameraOpen && FlashBeams.LensLayer != null ? FlashBeams.LensLayer() : -1;
            if (lensLayer != _lensLayer)
            {
                _lensLayer = lensLayer;
                bool on = lensLayer >= 0;
                if (_sheetLens != null) { if (on) _sheetLens.layer = lensLayer; _sheetLens.SetActive(on); }
                if (_sheetWallLens != null && on) _sheetWallLens.layer = lensLayer;
                foreach (var g in _skinsLens) if (g != null) { if (on) g.layer = lensLayer; g.SetActive(on); }
                foreach (var g in _skinsWallLens) if (g != null && on) g.layer = lensLayer;
                foreach (var g in _partsLens) if (g != null) { if (on) g.layer = lensLayer; g.SetActive(on); }
                foreach (var g in _partsWallLens) if (g != null && on) g.layer = lensLayer;
                foreach (var g in _othersLens) if (g != null) { if (on) g.layer = lensLayer; g.SetActive(on); }
            }

            Show(true);
            SetWallBlocks(d);
            bool lensOn = _lensLayer >= 0;
            SetList(_skins, _plane);
            SetList(_skinsWall, _wall);
            SetList(_skinsLens, lensOn && _plane);
            SetList(_skinsWallLens, lensOn && _wall);
            SetList(_parts, _partsOn);
            SetList(_partsWall, _partsOn);
            SetList(_partsLens, lensOn && _partsOn);
            SetList(_partsWallLens, lensOn && _partsOn);
            if (_sheetWallLens != null && _sheetWallLens.activeSelf != (lensOn && _wall)) _sheetWallLens.SetActive(lensOn && _wall);
            if (!_logged)
            {
                S("first frame finished");
                try { System.IO.File.WriteAllText(StepFile, "ok"); } catch { }
                _logged = true;
                Mod.Log.Msg($"Shadows: body shadow on ({_skins.Count} meshes, {_parts.Count} held parts, {_others.Count} NPC/prop meshes, {(_sun != null ? "sun '" + _sun.name + "'" : "no sun on this map, shadow falls straight down")}, {SystemInfo.graphicsDeviceType})");
            }
        }
    }
}
