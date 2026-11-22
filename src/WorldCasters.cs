using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    // Walls, floors, pillars and fixed furniture as shadow casters. The shadow map only draws copies, and those were only
    // made for things with a physics body (you, held items, props, NPCs), so the level itself never blocked the sun.
    // This copies the level's solid collision shapes near you: boxes and low-poly collision meshes, which follow the walls
    // exactly and are cheap to draw. Skipped: anything entirely above your head (roofs / ceilings: the baked lighting
    // already darkens indoors) and colliders with nothing visible on them (invisible map barriers).
    internal static class WorldCasters
    {
        private const float Reach = 10f, KeepReach = 14f, Rescan = 3f; // the shadow map covers 12 m around you
        private const int Max = 300;

        private static readonly Dictionary<IntPtr, GameObject> _copies = new Dictionary<IntPtr, GameObject>();
        private static GameObject _root;
        private static Material _mat;
        private static Mesh _cube;
        private static Vector3 _lastAt = new Vector3(1e9f, 0, 0);
        private static float _nextScan;
        private static bool _visible = true, _logged;
        private static int _layer = -1;

        public static void OnLevel() { _copies.Clear(); _root = null; _lastAt = new Vector3(1e9f, 0, 0); _nextScan = Time.unscaledTime + 3f; _logged = false; }

        public static void SetVisible(bool on)
        {
            if (_visible == on) return;
            _visible = on;
            if (_root != null) _root.SetActive(on);
        }

        public static void Clear()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null; _copies.Clear(); _lastAt = new Vector3(1e9f, 0, 0);
        }

        public static void Tick(Vector3 head, int layer)
        {
            if (layer < 0 || ShaderPack.ShadowCaster == null) return;
            if (_root == null) { _root = new GameObject("QuestShade_WorldCasters"); _root.SetActive(_visible); _copies.Clear(); }
            if (layer != _layer) { _layer = layer; foreach (var g in _copies.Values) if (g != null) g.layer = layer; }
            float now = Time.unscaledTime;
            if (now < _nextScan || (head - _lastAt).sqrMagnitude < Rescan * Rescan && now < _nextScan + 5f) return;
            _nextScan = now + 1f;
            _lastAt = head;
            try { Scan(head); }
            catch (Exception e) { Mod.Log.Warning("Wall shadows: scan failed: " + e.Message); _nextScan = now + 10f; }
        }

        private static void Scan(Vector3 head)
        {
            _mat ??= new Material(ShaderPack.ShadowCaster) { name = "QuestShade_WorldCaster", hideFlags = HideFlags.HideAndDontSave };
            var cols = Physics.OverlapSphere(head, Reach, ~(1 << 2), QueryTriggerInteraction.Ignore);
            var want = new List<(Collider c, float d)>();
            int roof = 0, invisible = 0;
            foreach (var c in cols)
            {
                if (c == null || !c.enabled || c.attachedRigidbody != null) continue; // things with a body are props / NPCs / you
                if (!(c is BoxCollider) && !(c is MeshCollider)) continue;
                var b = c.bounds;
                if (b.size.x < 0.15f && b.size.y < 0.15f && b.size.z < 0.15f) continue;
                if (b.min.y > head.y + 0.3f) { roof++; continue; }
                if (c.gameObject.name.StartsWith("QuestShade", StringComparison.Ordinal)) continue;
                if (!HasVisible(c)) { invisible++; continue; }
                want.Add((c, (b.ClosestPoint(head) - head).sqrMagnitude));
            }
            want.Sort((x, y) => x.d.CompareTo(y.d));
            if (want.Count > Max) want.RemoveRange(Max, want.Count - Max);

            // Drop copies that are now far away or whose collider is gone; build the newcomers.
            var keep = new HashSet<IntPtr>();
            foreach (var (c, _) in want) keep.Add(c.Pointer);
            var drop = new List<IntPtr>();
            foreach (var kv in _copies)
            {
                if (keep.Contains(kv.Key)) continue;
                bool far = kv.Value == null || (kv.Value.transform.position - head).sqrMagnitude > KeepReach * KeepReach;
                if (far) { if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value); drop.Add(kv.Key); }
            }
            foreach (var k in drop) _copies.Remove(k);
            int built = 0;
            foreach (var (c, _) in want)
            {
                if (_copies.ContainsKey(c.Pointer) || _copies.Count >= Max) continue;
                var go = Build(c);
                if (go != null) { _copies[c.Pointer] = go; built++; }
            }
            if (!_logged && _copies.Count > 0) { _logged = true; Mod.Log.Msg($"Wall shadows: {_copies.Count} wall / floor / fixed object casters (skipped {roof} overhead, {invisible} invisible)"); }
        }

        private static bool HasVisible(Collider c)
        {
            try
            {
                var r = c.GetComponent<Renderer>();
                if (r != null && r.enabled) return true;
                var p = c.transform.parent;
                if (p != null) { r = p.GetComponent<Renderer>(); if (r != null && r.enabled) return true; }
                r = c.GetComponentInChildren<Renderer>(false);
                return r != null && r.enabled;
            }
            catch { return false; }
        }

        private static GameObject Build(Collider c)
        {
            Mesh mesh; Vector3 pos; Quaternion rot; Vector3 scale;
            var t = c.transform;
            if (c is BoxCollider box)
            {
                mesh = _cube ??= MakeCube();
                pos = t.TransformPoint(box.center); rot = t.rotation; scale = Vector3.Scale(t.lossyScale, box.size);
            }
            else if (c is MeshCollider mc && mc.sharedMesh != null)
            {
                mesh = mc.sharedMesh; pos = t.position; rot = t.rotation; scale = t.lossyScale;
            }
            else return null;
            var go = new GameObject("QuestShade_WallCaster") { layer = _layer };
            go.transform.SetParent(_root.transform, false);
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            int n = Mathf.Max(1, mesh.subMeshCount);
            var mats = new Material[n];
            for (int i = 0; i < n; i++) mats[i] = _mat;
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        private static Mesh MakeCube()
        {
            var m = new Mesh { name = "QuestShade_CasterCube" };
            m.vertices = new[]
            {
                new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f),
                new Vector3(-.5f,-.5f,.5f),  new Vector3(.5f,-.5f,.5f),  new Vector3(.5f,.5f,.5f),  new Vector3(-.5f,.5f,.5f),
            };
            m.triangles = new[] { 0,2,1, 0,3,2,  4,5,6, 4,6,7,  0,1,5, 0,5,4,  3,7,6, 3,6,2,  0,4,7, 0,7,3,  1,2,6, 1,6,5 };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
