using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestShade
{
    internal static class Flashlights
    {
        public static Func<int, GameObject> Held;
        public static Func<GameObject, Transform> ItemRoot;

        private struct Orig { public Light L; public float I, R, A, IA; }
        private static readonly Dictionary<IntPtr, Orig> _lights = new Dictionary<IntPtr, Orig>();
        private static IntPtr _lastL, _lastR;
        private static float _next, _applied = -1f;
        private static bool _logged;

        public static void Tick(float boost)
        {
            if (Held == null) return;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 0.25f;
            if (!Mathf.Approximately(boost, _applied))
            {
                _applied = boost;
                var dead = new List<IntPtr>();
                foreach (var kv in _lights) { if (kv.Value.L == null) dead.Add(kv.Key); else Apply(kv.Value, boost); }
                foreach (var k in dead) _lights.Remove(k);
            }
            Check(1, ref _lastL, boost);
            Check(2, ref _lastR, boost);
        }

        public static void Reset() { _lights.Clear(); _lastL = _lastR = IntPtr.Zero; }

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
                    var o = new Orig { L = l, I = l.intensity, R = l.range, A = l.spotAngle, IA = l.innerSpotAngle };
                    _lights[l.Pointer] = o;
                    Apply(o, boost);
                    n++;
                }
                if (n > 0 && !_logged) { _logged = true; Mod.Log.Msg($"Flashlight boost: {n} light(s) on '{root.name}'"); }
            }
            catch (Exception e) { if (!_logged) { _logged = true; Mod.Log.Warning("Flashlight boost: " + e.Message); } }
        }

        private static void Apply(Orig o, float b)
        {
            try
            {
                b = Mathf.Max(0f, b);
                o.L.intensity = o.I * (1f + 1.0f * b);
                o.L.range = o.R * (1f + 0.6f * b);
                o.L.spotAngle = Mathf.Min(o.A * (1f + 0.15f * b), 130f);
                o.L.innerSpotAngle = Mathf.Min(o.IA * (1f + 0.15f * b), o.L.spotAngle);
            }
            catch { }
        }
    }
}
