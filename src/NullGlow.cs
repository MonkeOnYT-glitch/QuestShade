using System;
using System.Collections.Generic;
using Il2CppSLZ.Marrow.Pool;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    // Glowing Nullbodies: every Nullbody NPC gets a glowing rim, drawn on a copy of its skinned mesh that shares its bones
    // (so it animates with it). Two copies each: one for the headset (stereo) and one for the in-game camera (mono),
    // on layers only that view draws, like the flashlight beams.
    internal static class NullGlow
    {
        public static Func<int> HeadsetLayer, LensLayer;
        private const int MaxNpcs = 6;

        private sealed class Glow { public Poolee Npc; public readonly List<(SkinnedMeshRenderer head, SkinnedMeshRenderer lens)> Copies = new List<(SkinnedMeshRenderer, SkinnedMeshRenderer)>(); }
        private static readonly Dictionary<IntPtr, Glow> _glows = new Dictionary<IntPtr, Glow>();
        private static Material _matHead, _matLens;
        private static float _nextScan;
        private static bool _on, _logged;
        private static Color _color;

        public static void OnLevel() { _glows.Clear(); _nextScan = Time.unscaledTime + 4f; }

        public static void Tick(Config cfg, Vector3 player, bool beta)
        {
            bool want = beta && cfg.NullbodyGlow && cfg.Enabled && ShaderPack.Glow != null;
            if (!want) { if (_on) { Clear(); _on = false; } return; }
            _on = true;
            if (!Ensure(cfg)) return;
            float now = Time.unscaledTime;
            int hl = HeadsetLayer != null ? HeadsetLayer() : -1, ll = LensLayer != null ? LensLayer() : -1;
            // Keep the camera copies on whatever layer the camera currently draws alone (it changes when the camera opens / closes).
            foreach (var g in _glows.Values)
                foreach (var (h, l) in g.Copies)
                {
                    try
                    {
                        if (h != null && hl >= 0 && h.gameObject.layer != hl) h.gameObject.layer = hl;
                        if (l != null) { bool show = ll >= 0; if (show && l.gameObject.layer != ll) l.gameObject.layer = ll; if (l.enabled != show) l.enabled = show; }
                    }
                    catch { }
                }
            if (now < _nextScan) return;
            _nextScan = now + 3f;
            Scan(player, hl, ll);
        }

        private static bool Ensure(Config cfg)
        {
            if (_matHead == null) { _matHead = new Material(ShaderPack.Glow) { hideFlags = HideFlags.HideAndDontSave }; ShaderPack.SetStereo(_matHead, true); _color = default; }
            if (_matLens == null) { _matLens = new Material(ShaderPack.Glow) { hideFlags = HideFlags.HideAndDontSave }; ShaderPack.SetStereo(_matLens, false); _color = default; }
            if (_color != cfg.NullbodyColor) { _color = cfg.NullbodyColor; _matHead.SetColor("_Color", _color); _matLens.SetColor("_Color", _color); }
            return true;
        }

        private static bool IsNullbody(Poolee p)
        {
            string n = null;
            try { n = p.SpawnableCrate != null ? p.SpawnableCrate.Barcode.ID : null; } catch { }
            if (string.IsNullOrEmpty(n)) { try { n = p.gameObject.name; } catch { return false; } }
            n = n.ToLowerInvariant();
            return n.Contains("nullbody") || n.Contains("null body") || n.Contains("null_body");
        }

        private static void Scan(Vector3 player, int hl, int ll)
        {
            try
            {
                // Forget NPCs that were destroyed.
                List<IntPtr> gone = null;
                foreach (var kv in _glows) { bool dead; try { dead = kv.Value.Npc == null; } catch { dead = true; } if (dead) (gone ??= new List<IntPtr>()).Add(kv.Key); }
                if (gone != null) foreach (var k in gone) _glows.Remove(k);

                var found = new List<(Poolee p, float d)>();
                foreach (var p in UnityEngine.Object.FindObjectsOfType<Poolee>())
                {
                    if (p == null || !p.gameObject.activeInHierarchy || _glows.ContainsKey(p.Pointer)) continue;
                    if (!IsNullbody(p)) continue;
                    found.Add((p, (p.transform.position - player).sqrMagnitude));
                }
                found.Sort((a, b) => a.d.CompareTo(b.d));
                foreach (var (p, _) in found)
                {
                    if (_glows.Count >= MaxNpcs) break;
                    var g = new Glow { Npc = p };
                    foreach (var smr in p.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                    {
                        if (smr == null || smr.sharedMesh == null || !smr.enabled) continue;
                        var head = Copy(smr, _matHead, hl >= 0 ? hl : smr.gameObject.layer);
                        var lens = ll >= 0 ? Copy(smr, _matLens, ll) : null;
                        g.Copies.Add((head, lens));
                    }
                    if (g.Copies.Count > 0)
                    {
                        _glows[p.Pointer] = g;
                        if (!_logged) { _logged = true; Mod.Log.Msg($"Nullbody glow on ({g.Copies.Count} mesh(es) on '{p.gameObject.name}')"); }
                    }
                }
            }
            catch (Exception e) { Mod.Log.Warning("Nullbody glow scan failed: " + e.Message); _nextScan = Time.unscaledTime + 15f; }
        }

        private static SkinnedMeshRenderer Copy(SkinnedMeshRenderer src, Material mat, int layer)
        {
            var go = new GameObject("QuestShade_NullGlow") { layer = layer };
            var t = go.transform;
            t.SetParent(src.transform.parent, false);
            t.localPosition = src.transform.localPosition; t.localRotation = src.transform.localRotation; t.localScale = src.transform.localScale;
            var r = go.AddComponent<SkinnedMeshRenderer>();
            r.sharedMesh = src.sharedMesh;
            r.bones = src.bones;
            r.rootBone = src.rootBone;
            r.localBounds = src.localBounds;
            r.quality = src.quality;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            int n = Mathf.Max(1, src.sharedMesh.subMeshCount);
            var mats = new Material[n];
            for (int i = 0; i < n; i++) mats[i] = mat;
            r.sharedMaterials = mats;
            return r;
        }

        private static void Clear()
        {
            foreach (var g in _glows.Values)
                foreach (var (h, l) in g.Copies)
                {
                    try { if (h != null) UnityEngine.Object.Destroy(h.gameObject); } catch { }
                    try { if (l != null) UnityEngine.Object.Destroy(l.gameObject); } catch { }
                }
            _glows.Clear();
            _logged = false;
        }
    }
}
