using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace QuestShade
{
    // Wrist chat (beta): your live chat floating just above your left or right wrist, turned to face you.
    // It's on the layer the in-game camera never draws, so it never shows up on stream or in recordings.
    internal static class WristChat
    {
        public static Func<int, Transform> Hand; // 1 = left, 2 = right
        public static Func<int> HiddenLayer;
        private static GameObject _go;
        private static Il2CppTMPro.TextMeshPro _tmp;
        private static readonly List<(string site, string user, string text, float at)> _lines = new List<(string, string, string, float)>();
        private static string _shown;
        private static float _nextText;

        public static void OnLevel() { _go = null; _tmp = null; _shown = null; }

        public static void Tick(Config cfg, Camera head, bool on)
        {
            // Always drain, so old messages don't pile up while it's hidden.
            while (ChatReader.Incoming.TryDequeue(out var m))
            {
                _lines.Add((m.Site, Clean(m.User, 24), Clean(m.Text, 90), Time.unscaledTime));
                if (_lines.Count > 30) _lines.RemoveAt(0);
            }
            if (!on || head == null || Hand == null) { if (_go != null && _go.activeSelf) _go.SetActive(false); return; }
            Transform hand = null;
            try { hand = Hand(cfg.ChatRightWrist ? 2 : 1); } catch { }
            if (hand == null) { if (_go != null && _go.activeSelf) _go.SetActive(false); return; }
            if (_go == null && !Build()) return;
            if (!_go.activeSelf) _go.SetActive(true);
            int l = HiddenLayer != null ? HiddenLayer() : -1;
            if (l >= 0 && _go.layer != l) _go.layer = l;

            // Just above the back of the wrist, facing your eyes.
            var pos = hand.position + Vector3.up * 0.09f;
            var toEye = pos - head.transform.position;
            _go.transform.SetPositionAndRotation(pos, toEye.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toEye, Vector3.up) : hand.rotation);

            if (Time.unscaledTime < _nextText) return;
            _nextText = Time.unscaledTime + 0.25f;
            string text = Compose(cfg);
            if (text != _shown) { _shown = text; _tmp.text = text; }
        }

        private static string Compose(Config cfg)
        {
            int n = Mathf.Clamp(cfg.ChatLines, 2, 12);
            var sb = new StringBuilder();
            if (_lines.Count == 0)
            {
                sb.Append("<color=#ffb060>Chat</color>\n<size=70%><color=#bbbbbb>");
                if (ChatReader.YouTubeStatus != "off") sb.Append("YouTube: ").Append(ChatReader.YouTubeStatus).Append('\n');
                if (ChatReader.TwitchStatus != "off") sb.Append("Twitch: ").Append(ChatReader.TwitchStatus).Append('\n');
                if (ChatReader.YouTubeStatus == "off" && ChatReader.TwitchStatus == "off") sb.Append("Add ChatYouTube or ChatTwitch under [Stream] in QuestShade.ini");
                sb.Append("</color></size>");
                return sb.ToString();
            }
            int start = Math.Max(0, _lines.Count - n);
            for (int i = start; i < _lines.Count; i++)
            {
                var (site, user, txt, at) = _lines[i];
                string col = site == "Twitch" ? "#b58cff" : "#ff7070";
                float age = Time.unscaledTime - at;
                string alpha = age > 45f ? "<alpha=#88>" : "";
                sb.Append(alpha).Append("<color=").Append(col).Append('>').Append(user).Append("</color>: ").Append(txt).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        // No rich-text tags from chat, and keep lines short.
        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("<", "‹").Replace(">", "›").Replace("\n", " ");
            return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
        }

        private static bool Build()
        {
            try
            {
                _go = new GameObject("QuestShade_WristChat");
                _tmp = _go.AddComponent<Il2CppTMPro.TextMeshPro>();
                _tmp.rectTransform.sizeDelta = new Vector2(0.24f, 0.14f);
                _tmp.enableAutoSizing = true; _tmp.fontSizeMin = 0.05f; _tmp.fontSizeMax = 0.16f;
                _tmp.enableWordWrapping = true; _tmp.richText = true;
                _tmp.alignment = Il2CppTMPro.TextAlignmentOptions.BottomLeft;
                _tmp.color = Color.white;
                _tmp.outlineWidth = 0.25f; _tmp.outlineColor = new Color32(0, 0, 0, 220);
                var r = _go.GetComponent<MeshRenderer>(); if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                return true;
            }
            catch (Exception e) { Mod.Log.Warning("Wrist chat unavailable: " + e.Message); _go = null; return false; }
        }
    }
}
