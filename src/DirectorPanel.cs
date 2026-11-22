using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    // The Timeline director panel (beta): floats in front of you, controls the Timeline mod and pins camera shots to moments.
    // Point with your right controller, pull the trigger to press a button, or hold it on the bar to scrub through the take.
    // It sits on the layer the in-game camera never draws, so it never shows up in your videos.
    internal static class DirectorPanel
    {
        public static bool IsOpen => _root != null && _root.activeSelf;
        public static Func<int> HiddenLayer;
        public static Action<string, float> Notify;

        // Hooks into the rest of QuestShade (set by Mod).
        public static Action<ShotKind> AddShot;
        public static Action ToggleRecordVideo;
        public static Func<bool> RecordVideoOn;
        public static bool Preview; // show the shot at the playhead while paused

        private const float W = 0.92f, H = 0.46f, BarW = 0.82f, BarY = 0.11f;
        private static GameObject _root, _bar, _playhead, _cursor, _markers;
        private static Il2CppTMPro.TextMeshPro _title, _time, _shotInfo;
        private sealed class Btn { public string Label; public Vector2 C, S; public Action Act; public GameObject Bg; public Il2CppTMPro.TextMeshPro Txt; public Color Col; public Func<string> Live; }
        private static readonly List<Btn> _btns = new List<Btn>();
        private static readonly Dictionary<Color, Material> _mats = new Dictionary<Color, Material>();
        private static Mesh _quad;
        private static bool _prevTrig, _dragging;
        private static float _nextScrub, _lastScrubT = -1f;
        private static int _markersFor = -1;
        private static Btn _hover;

        public static void Toggle(Camera head) { if (IsOpen) Close(); else Open(head); }

        public static void Open(Camera head)
        {
            if (head == null) return;
            if (_root == null) Build();
            if (_root == null) return;
            var ht = head.transform;
            var fwd = Vector3.ProjectOnPlane(ht.forward, Vector3.up); if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward; fwd.Normalize();
            _root.transform.SetPositionAndRotation(ht.position + fwd * 0.7f - Vector3.up * 0.18f, Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(18f, 0f, 0f));
            _root.SetActive(true);
            ApplyLayer();
        }

        public static void Close() { if (_root != null) _root.SetActive(false); _dragging = false; }
        public static void OnLevel() { _root = null; _btns.Clear(); _markersFor = -1; _dragging = false; }

        private static void ApplyLayer()
        {
            int l = HiddenLayer != null ? HiddenLayer() : -1;
            if (l < 0 || _root == null) return;
            foreach (var t in _root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = l;
        }

        // ---------- building ----------

        private static Material Mat(Color c)
        {
            if (_mats.TryGetValue(c, out var m) && m != null) return m;
            m = new Material(ShaderPack.Screen) { hideFlags = HideFlags.HideAndDontSave, mainTexture = Texture2D.whiteTexture };
            m.SetColor("_Color", c);
            ShaderPack.SetStereo(m, ShaderPack.HeadsetActive);
            _mats[c] = m;
            return m;
        }

        private static GameObject Quad(string name, Transform parent, Vector2 center, Vector2 size, Color c, float z = 0f)
        {
            _quad ??= MakeQuad();
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(center.x, center.y, z);
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Mat(c);
            r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
            return go;
        }

        private static Il2CppTMPro.TextMeshPro Text(Transform parent, Vector2 center, Vector2 size, string s, float maxSize, Il2CppTMPro.TextAlignmentOptions align = Il2CppTMPro.TextAlignmentOptions.Center)
        {
            var go = new GameObject("Text");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(center.x, center.y, -0.003f);
            var t = go.AddComponent<Il2CppTMPro.TextMeshPro>();
            t.rectTransform.sizeDelta = size;
            t.enableAutoSizing = true; t.fontSizeMin = 0.05f; t.fontSizeMax = maxSize;
            t.enableWordWrapping = false; t.richText = false;
            t.alignment = align;
            t.color = Color.white;
            t.text = s;
            var r = go.GetComponent<MeshRenderer>(); if (r != null) r.shadowCastingMode = ShadowCastingMode.Off;
            return t;
        }

        private static void Build()
        {
            if (ShaderPack.Screen == null) { Notify?.Invoke("Director panel unavailable (shader missing)", 3f); return; }
            try
            {
                _root = new GameObject("QuestShade_Director");
                var rt = _root.transform;
                Quad("Back", rt, Vector2.zero, new Vector2(W, H), new Color(0.07f, 0.07f, 0.09f), 0.004f);
                Quad("Edge", rt, Vector2.zero, new Vector2(W + 0.012f, H + 0.012f), new Color(1f, 0.55f, 0.12f), 0.006f);
                _title = Text(rt, new Vector2(-0.2f, 0.195f), new Vector2(0.48f, 0.04f), "QuestShade Director", 0.32f, Il2CppTMPro.TextAlignmentOptions.Left);
                _time = Text(rt, new Vector2(0.24f, 0.195f), new Vector2(0.38f, 0.04f), "0:00.0 / 0:00.0", 0.3f, Il2CppTMPro.TextAlignmentOptions.Right);
                _shotInfo = Text(rt, new Vector2(0f, 0.155f), new Vector2(0.84f, 0.03f), "", 0.22f);
                _bar = Quad("Bar", rt, new Vector2(0f, BarY), new Vector2(BarW, 0.035f), new Color(0.2f, 0.2f, 0.24f));
                _markers = new GameObject("Markers"); _markers.transform.SetParent(rt, false);
                _playhead = Quad("Playhead", rt, new Vector2(0f, BarY), new Vector2(0.006f, 0.06f), Color.white, -0.002f);
                _cursor = Quad("Cursor", rt, Vector2.zero, new Vector2(0.012f, 0.012f), new Color(1f, 0.8f, 0.3f), -0.005f);

                float y1 = 0.035f, y2 = -0.045f, y3 = -0.125f, y4 = -0.195f, bw = 0.165f, bh = 0.06f, gap = 0.174f;
                AddBtn("Show people", new Vector2(-2 * gap, y1), bw, bh, new Color(0.2f, 0.42f, 0.55f), () => { TimelineLink.ShowPeople(); Notify?.Invoke("Showing the recorded people", 1.2f); });
                AddBtn("Play", new Vector2(-gap, y1), bw, bh, new Color(0.18f, 0.45f, 0.25f), () => TimelineLink.PlayFrom(TimelineLink.PlayHead >= TimelineLink.Length - 0.05f ? 0f : TimelineLink.PlayHead), () => TimelineLink.Playing && !TimelineLink.Paused ? "Playing" : "Play");
                AddBtn("Pause", new Vector2(0f, y1), bw, bh, new Color(0.3f, 0.3f, 0.34f), () => TimelineLink.Pause());
                AddBtn("< Shot", new Vector2(gap, y1), bw, bh, new Color(0.3f, 0.3f, 0.34f), () => TimelineLink.Scrub(Director.Prev(TimelineLink.PlayHead)));
                AddBtn("Shot >", new Vector2(2 * gap, y1), bw, bh, new Color(0.3f, 0.3f, 0.34f), () => TimelineLink.Scrub(Director.Next(TimelineLink.PlayHead, TimelineLink.Length)));
                for (int k = 0; k < 5; k++)
                {
                    var kind = (ShotKind)k;
                    AddBtn("+ " + Director.KindName[k], new Vector2((k - 2) * gap, y2), bw, bh, Director.KindColor[k] * 0.55f + new Color(0, 0, 0, 1), () => AddShot?.Invoke(kind));
                }
                AddBtn("Delete shot", new Vector2(-2 * gap, y3), bw, bh, new Color(0.5f, 0.18f, 0.18f), () => Notify?.Invoke(Director.RemoveNear(TimelineLink.PlayHead) ? "Shot deleted" : "No shot at the playhead", 1.2f));
                AddBtn("Clear shots", new Vector2(-gap, y3), bw, bh, new Color(0.4f, 0.22f, 0.15f), () => { Director.Clear(); Notify?.Invoke("All shots cleared", 1.2f); });
                AddBtn("Preview", new Vector2(0f, y3), bw, bh, new Color(0.3f, 0.3f, 0.34f), () => { Preview = !Preview; Notify?.Invoke(Preview ? "Preview on: the camera shows the shot at the playhead" : "Preview off: aim the camera yourself", 2f); }, () => Preview ? "Preview: on" : "Preview: off");
                AddBtn("Record", new Vector2(gap, y3), bw, bh, new Color(0.45f, 0.15f, 0.15f), () => ToggleRecordVideo?.Invoke(), () => RecordVideoOn != null && RecordVideoOn() ? "Rec video: on" : "Rec video: off");
                AddBtn("Close", new Vector2(2 * gap, y3), bw, bh, new Color(0.3f, 0.3f, 0.34f), Close);
                Text(rt, new Vector2(0f, y4), new Vector2(0.86f, 0.03f), "Point with your right hand: trigger to press, hold on the bar to scrub. Free cam shots use where your camera is now.", 0.16f);
                _root.SetActive(false);
                ApplyLayer();
            }
            catch (Exception e) { Mod.Log.Warning("Director panel failed to build: " + e.Message); _root = null; }
        }

        private static void AddBtn(string label, Vector2 c, float w, float h, Color col, Action act, Func<string> live = null)
        {
            var b = new Btn { Label = label, C = c, S = new Vector2(w, h), Act = act, Col = col, Live = live };
            b.Bg = Quad("Btn", _root.transform, c, b.S, col, -0.001f);
            b.Txt = Text(_root.transform, c, b.S * 0.9f, label, 0.22f);
            _btns.Add(b);
        }

        private static Mesh MakeQuad()
        {
            var m = new Mesh { name = "QuestShade_PanelQuad" };
            m.vertices = new[] { new Vector3(-.5f, -.5f, 0), new Vector3(-.5f, .5f, 0), new Vector3(.5f, .5f, 0), new Vector3(.5f, -.5f, 0) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        // ---------- every frame while open ----------

        public static void Tick(PhotoCamera cam, Camera head, bool trigger)
        {
            if (!IsOpen || head == null) { _prevTrig = trigger; return; }
            float len = Mathf.Max(0f, TimelineLink.Length), ph = TimelineLink.PlayHead;
            _time.text = $"{Director.Clock(ph)} / {Director.Clock(len)}";
            _shotInfo.text = !TimelineLink.Installed ? "Timeline mod not installed" : len <= 0f ? "Record something in Timeline first" : "Now: " + Director.Describe(ph) + $"   ({Director.Shots.Count} shot{(Director.Shots.Count == 1 ? "" : "s")})";
            float u = len > 0f ? Mathf.Clamp01(ph / len) : 0f;
            _playhead.transform.localPosition = new Vector3(-BarW / 2f + u * BarW, BarY, -0.002f);
            if (_markersFor != Director.Version) RebuildMarkers(len);
            foreach (var b in _btns) if (b.Live != null) { var s = b.Live(); if (b.Txt.text != s) b.Txt.text = s; }

            // Pointing: right controller ray onto the panel.
            Btn hover = null; bool onBar = false; Vector3 local = default; bool hit = false;
            if (cam.PointerPose(head, out var pp, out var pr))
            {
                var rt = _root.transform;
                var plane = new Plane(rt.forward, rt.position);
                var ray = new Ray(pp, pr * Vector3.forward);
                if (plane.Raycast(ray, out float d) && d < 3f)
                {
                    local = rt.InverseTransformPoint(ray.GetPoint(d));
                    hit = Mathf.Abs(local.x) <= W / 2f && Mathf.Abs(local.y) <= H / 2f;
                    if (hit)
                    {
                        onBar = Mathf.Abs(local.x) <= BarW / 2f + 0.01f && Mathf.Abs(local.y - BarY) <= 0.035f;
                        foreach (var b in _btns) if (Mathf.Abs(local.x - b.C.x) <= b.S.x / 2f && Mathf.Abs(local.y - b.C.y) <= b.S.y / 2f) { hover = b; break; }
                    }
                }
            }
            _cursor.SetActive(hit);
            if (hit) _cursor.transform.localPosition = new Vector3(local.x, local.y, -0.005f);
            if (hover != _hover)
            {
                if (_hover != null) _hover.Bg.GetComponent<MeshRenderer>().sharedMaterial = Mat(_hover.Col);
                if (hover != null) hover.Bg.GetComponent<MeshRenderer>().sharedMaterial = Mat(hover.Col * 1.6f + new Color(0.1f, 0.1f, 0.1f, 0));
                _hover = hover;
            }

            bool down = trigger && !_prevTrig, up = !trigger && _prevTrig;
            _prevTrig = trigger;
            if (down && hit)
            {
                if (onBar && len > 0f) _dragging = true;
                else if (hover != null) { try { hover.Act?.Invoke(); } catch (Exception e) { Mod.Log.Warning("Director: " + e.Message); } }
            }
            if (up) _dragging = false;
            if (_dragging && hit && len > 0f && Time.unscaledTime >= _nextScrub)
            {
                float t = Mathf.Clamp01((local.x + BarW / 2f) / BarW) * len;
                if (Mathf.Abs(t - _lastScrubT) > 0.01f) { _nextScrub = Time.unscaledTime + 0.05f; _lastScrubT = t; TimelineLink.Scrub(t); }
            }
        }

        private static void RebuildMarkers(float len)
        {
            _markersFor = Director.Version;
            if (_markers == null) return;
            for (int i = _markers.transform.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_markers.transform.GetChild(i).gameObject);
            if (len <= 0f) return;
            foreach (var s in Director.Shots)
            {
                float x = -BarW / 2f + Mathf.Clamp01(s.T / len) * BarW;
                Quad("Shot", _markers.transform, new Vector2(x, BarY), new Vector2(0.01f, 0.045f), Director.KindColor[(int)s.Kind], -0.0015f);
            }
            ApplyLayer();
        }
    }
}
