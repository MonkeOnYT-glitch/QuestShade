using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestShade
{
    internal static class Flashlights
    {
        public static Func<int, GameObject> Held;
        public static Func<GameObject, Transform> ItemRoot;

        private struct Orig { public Light L; public Transform Root; public float I, R, A, IA; }
        private const float Near = 1.4f;
        private static readonly Dictionary<IntPtr, float> _dim = new Dictionary<IntPtr, float>();
        private static readonly List<IntPtr> _keys = new List<IntPtr>();
        private static float _nextDim;
        private static readonly Dictionary<IntPtr, Orig> _lights = new Dictionary<IntPtr, Orig>();
        private static IntPtr _lastL, _lastR;
        private static float _next, _applied = -1f;
        private static bool _logged, _diag;
        public static bool AnyOn { get; private set; }

        public static void Tick(float boost, bool beams)
        {
            if (Held == null) return;
            float now = Time.unscaledTime;
            if (now >= _nextDim) { _nextDim = now + 0.05f; try { Dim(boost); } catch { } }
            if (now < _next) return;
            _next = now + 0.25f;
            try { FlashBeams.Tick(beams); } catch { }
            if (!Mathf.Approximately(boost, _applied))
            {
                _applied = boost;
                var dead = new List<IntPtr>();
                foreach (var kv in _lights) { if (kv.Value.L == null) dead.Add(kv.Key); else Apply(kv.Value, boost); }
                foreach (var k in dead) _lights.Remove(k);
            }
            bool any = false;
            foreach (var kv in _lights) { var l = kv.Value.L; if (l != null && l.isActiveAndEnabled && l.intensity > 0.01f) { any = true; break; } }
            AnyOn = any;
            Check(1, ref _lastL, boost);
            Check(2, ref _lastR, boost);
        }

        public static void Reset() { FlashBeams.Reset(); _lights.Clear(); _dim.Clear(); AnyOn = false; _lastL = _lastR = IntPtr.Zero; }

        private static void Check(int hand, ref IntPtr last, float boost)
        {
            GameObject go;
            try { go = Held(hand); } catch { return; }
            IntPtr p = go != null ? go.Pointer : IntPtr.Zero;
            if (p == last) return;
            last = p;
            if (go == null) return;
            try
            {
                Transform root = null;
                try { root = ItemRoot?.Invoke(go); } catch { }
                if (root == null) root = go.transform;
                var found = root.GetComponentsInChildren<Light>(true);
                if (found == null) return;
                int n = 0;
                foreach (var l in found)
                {
                    if (l == null || l.type != LightType.Spot) continue;
                    if (_lights.TryGetValue(l.Pointer, out var known) && known.L != null) continue;
                    if (!_diag) { _diag = true; Mod.Log.Msg($"Flashlight light: mode {l.renderMode}, bake {l.bakingOutput.lightmapBakeType}, intensity {l.intensity:0.##}, range {l.range:0.#}, mask {l.cullingMask}"); }
                    l.renderMode = LightRenderMode.ForcePixel;
                    var o = new Orig { L = l, Root = root, I = l.intensity, R = l.range, A = l.spotAngle, IA = l.innerSpotAngle };
                    _lights[l.Pointer] = o;
                    Apply(o, boost);
                    FlashBeams.Track(l);
                    n++;
                }
                if (n > 0 && !_logged) { _logged = true; Mod.Log.Msg($"Flashlight boost: {n} light(s) on '{root.name}'"); }
            }
            catch (Exception e) { if (!_logged) { _logged = true; Mod.Log.Warning("Flashlight boost: " + e.Message); } }
        }

        private static void Dim(float boost)
        {
            if (!AnyOn) return;
            _keys.Clear();
            foreach (var key in _lights.Keys) _keys.Add(key);
            foreach (var key in _keys)
            {
                var o = _lights[key];
                var l = o.L;
                if (l == null || !l.isActiveAndEnabled) continue;
                var t = l.transform;
                Vector3 f = t.forward, p = t.position + f * 0.05f;
                float target = 1f, reach = Near;
                for (int tries = 0; tries < 3 && reach > 0.05f; tries++)
                {
                    if (!Physics.SphereCast(p, 0.035f, f, out var hit, reach, ~(1 << 2), QueryTriggerInteraction.Ignore)) break;
                    var c = hit.collider;
                    if (c != null && o.Root != null && c.transform.IsChildOf(o.Root)) { float step = hit.distance + 0.08f; p += f * step; reach -= step; continue; }
                    float d = Near - reach + hit.distance + 0.05f;
                    target = Mathf.Clamp(d * d / (Near * Near), 0.03f, 1f);
                    break;
                }
                float cur = _dim.TryGetValue(key, out var have) && have > 0f ? have : 1f;
                cur = Mathf.Lerp(cur, target, target < cur ? 0.6f : 0.3f);
                _dim[key] = cur;
                l.intensity = o.I * (1f + Mathf.Max(0f, boost)) * cur;
            }
        }

        private static void Apply(Orig o, float b)
        {
            try
            {
                b = Mathf.Max(0f, b);
                o.L.intensity = o.I * (1f + 1.0f * b) * (_dim.TryGetValue(o.L.Pointer, out var k) && k > 0f ? k : 1f);
                o.L.range = o.R * (1f + 0.6f * b);
                o.L.spotAngle = Mathf.Min(o.A * (1f + 0.15f * b), 130f);
                o.L.innerSpotAngle = Mathf.Min(o.IA * (1f + 0.15f * b), o.L.spotAngle);
            }
            catch { }
        }
    }
}
