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

        private sealed class Glow { public Transform Npc; public readonly List<(SkinnedMeshRenderer head, SkinnedMeshRenderer lens)> Copies = new List<(SkinnedMeshRenderer, SkinnedMeshRenderer)>(); }
        private static readonly Dictionary<IntPtr, Glow> _glows = new Dictionary<IntPtr, Glow>();
        private static Material _matHead, _matLens;
        private static float _nextScan;
        private static bool _on, _logged, _namesLogged;
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

        private static bool Named(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            return s.Contains("nullbody") || s.Contains("null body") || s.Contains("null_body") || s.Contains("null-body");
        }

        // BONELAB barcodes are mostly ID strings, not names, so check everything that can carry the name: the object,
        // the crate's barcode and title, and the body's mesh names.
        private static bool IsNullbody(Transform root, Poolee p)
        {
            try
            {
                if (Named(root.name)) return true;
                if (p != null)
                {
                    if (Named(p.gameObject.name)) return true;
                    try { var crate = p.SpawnableCrate; if (crate != null && (Named(crate.Barcode.ID) || Named(crate.Title))) return true; } catch { }
                }
                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                    if (smr != null && smr.sharedMesh != null && (Named(smr.sharedMesh.name) || Named(smr.gameObject.name))) return true;
            }
            catch { }
            return false;
        }

        private static void Scan(Vector3 player, int hl, int ll)
        {
            try
            {
                // Forget NPCs that were destroyed.
                List<IntPtr> gone = null;
                foreach (var kv in _glows) { bool dead; try { dead = kv.Value.Npc == null; } catch { dead = true; } if (dead) (gone ??= new List<IntPtr>()).Add(kv.Key); }
                if (gone != null) foreach (var k in gone) _glows.Remove(k);

                // Only NPCs have an AI brain: far fewer objects to look through than every pooled item in the level.
                var found = new List<(Transform root, Poolee p, float d)>();
                var seen = new HashSet<IntPtr>();
                foreach (var brain in UnityEngine.Object.FindObjectsOfType<Il2CppSLZ.Marrow.AI.AIBrain>())
                {
                    if (brain == null || !brain.gameObject.activeInHierarchy) continue;
                    var p = brain.GetComponentInParent<Poolee>();
                    var root = p != null ? p.transform : brain.transform.root;
                    if (root == null || !seen.Add(root.Pointer) || _glows.ContainsKey(root.Pointer)) continue;
                    if (!IsNullbody(root, p)) continue;
                    found.Add((root, p, (root.position - player).sqrMagnitude));
                }
                if (found.Count == 0 && seen.Count > 0 && !_namesLogged)
                {
                    _namesLogged = true;
                    var names = new List<string>();
                    foreach (var brain in UnityEngine.Object.FindObjectsOfType<Il2CppSLZ.Marrow.AI.AIBrain>()) { if (brain == null) continue; var pp = brain.GetComponentInParent<Poolee>(); names.Add(pp != null ? pp.gameObject.name : brain.transform.root.name); if (names.Count >= 8) break; }
                    Mod.Log.Msg("Nullbody glow: NPCs nearby but none look like a Nullbody: " + string.Join(", ", names));
                }
                found.Sort((a, b) => a.d.CompareTo(b.d));
                foreach (var (root, p, _) in found)
                {
                    if (_glows.Count >= MaxNpcs) break;
                    var g = new Glow { Npc = root };
                    foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                    {
                        if (smr == null || smr.sharedMesh == null || !smr.enabled) continue;
                        var head = Copy(smr, _matHead, hl >= 0 ? hl : smr.gameObject.layer);
                        var lens = ll >= 0 ? Copy(smr, _matLens, ll) : null;
                        g.Copies.Add((head, lens));
                    }
                    if (g.Copies.Count > 0)
                    {
                        _glows[root.Pointer] = g;
                        if (!_logged) { _logged = true; Mod.Log.Msg($"Nullbody glow on ({g.Copies.Count} mesh(es) on '{root.name}')"); }
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
