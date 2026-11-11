using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    internal static class FlashBeams
    {
        public static Func<int> HeadsetLayer, LensLayer;

        private sealed class Beam { public Light L; public GameObject Head, Lens; public MeshRenderer HeadR, LensR; }
        private static readonly Dictionary<IntPtr, Beam> _beams = new Dictionary<IntPtr, Beam>();
        private static Mesh _mesh;
        private static Material _matHead, _matLens;
        private static MaterialPropertyBlock _mpb;
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public static void Track(Light l)
        {
            if (l != null && !_beams.ContainsKey(l.Pointer)) _beams[l.Pointer] = new Beam { L = l };
        }

        public static void Reset()
        {
            foreach (var b in _beams.Values) { Kill(b.Head); Kill(b.Lens); }
            _beams.Clear();
        }

        public static void Tick(bool on)
        {
            if (_beams.Count == 0 || ShaderPack.Beam == null) return;
            if (!Ensure()) return;
            int hl = HeadsetLayer != null ? HeadsetLayer() : -1;
            int ll = LensLayer != null ? LensLayer() : -1;
            List<IntPtr> dead = null;
            foreach (var kv in _beams)
            {
                var b = kv.Value;
                if (b.L == null) { Kill(b.Head); Kill(b.Lens); (dead ??= new List<IntPtr>()).Add(kv.Key); continue; }
                bool lit = on && b.L.isActiveAndEnabled && b.L.intensity > 0.01f;
                Place(ref b.Head, ref b.HeadR, b.L, _matHead, hl >= 0 ? hl : b.L.gameObject.layer, lit);
                Place(ref b.Lens, ref b.LensR, b.L, _matLens, ll, lit && ll >= 0);
            }
            if (dead != null) foreach (var k in dead) _beams.Remove(k);
        }

        private static bool Ensure()
        {
            if (_mesh == null) _mesh = Cone(24);
            if (_matHead == null)
            {
                _matHead = new Material(ShaderPack.Beam) { hideFlags = HideFlags.HideAndDontSave };
                ShaderPack.SetStereo(_matHead, true);
            }
            if (_matLens == null)
            {
                _matLens = new Material(ShaderPack.Beam) { hideFlags = HideFlags.HideAndDontSave };
                ShaderPack.SetStereo(_matLens, false);
            }
            _mpb ??= new MaterialPropertyBlock();
            return true;
        }

        private static void Place(ref GameObject go, ref MeshRenderer r, Light l, Material mat, int layer, bool show)
        {
            if (!show) { if (go != null && go.activeSelf) go.SetActive(false); return; }
            if (go == null || r == null)
            {
                Kill(go);
                go = new GameObject("QuestShade_Beam");
                go.transform.SetParent(l.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = _mesh;
                r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            if (!go.activeSelf) go.SetActive(true);
            if (go.layer != layer) go.layer = layer;
            float len = Mathf.Clamp(l.range, 1f, 8f);
            float rad = len * Mathf.Tan(Mathf.Clamp(l.spotAngle, 5f, 120f) * 0.5f * Mathf.Deg2Rad) * 0.85f;
            var s = l.transform.lossyScale;
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(rad / Safe(s.x), rad / Safe(s.y), len / Safe(s.z));
            var c = l.color;
            float k = Mathf.Clamp(l.intensity * 0.05f, 0.05f, 0.3f);
            _mpb.SetColor(ColorId, new Color(c.r, c.g, c.b, k));
            r.SetPropertyBlock(_mpb);
        }

        private static float Safe(float v) => Mathf.Abs(v) < 1e-4f ? 1f : Mathf.Abs(v);

        private static void Kill(GameObject go) { if (go != null) UnityEngine.Object.Destroy(go); }

        private static Mesh Cone(int seg)
        {
            var v = new Vector3[(seg + 1) * 2];
            var n = new Vector3[v.Length];
            var uv = new Vector2[v.Length];
            var tri = new int[seg * 6];
            for (int i = 0; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                var nrm = new Vector3(dir.x, dir.y, -1f).normalized;
                v[i * 2] = dir * 0.02f; n[i * 2] = nrm; uv[i * 2] = new Vector2(i / (float)seg, 0f);
                v[i * 2 + 1] = dir + Vector3.forward; n[i * 2 + 1] = nrm; uv[i * 2 + 1] = new Vector2(i / (float)seg, 1f);
            }
            for (int i = 0; i < seg; i++)
            {
                int a = i * 2, t = i * 6;
                tri[t] = a; tri[t + 1] = a + 1; tri[t + 2] = a + 2;
                tri[t + 3] = a + 2; tri[t + 4] = a + 1; tri[t + 5] = a + 3;
            }
            var m = new Mesh { name = "QuestShade_BeamCone", vertices = v, normals = n, uv = uv, triangles = tri };
            m.bounds = new Bounds(new Vector3(0f, 0f, 0.5f), new Vector3(2f, 2f, 1f));
            m.hideFlags = HideFlags.HideAndDontSave;
            return m;
        }
    }
}
