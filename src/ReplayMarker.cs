using System;
using Il2CppTMPro;
using UnityEngine;

namespace QuestShade
{
    internal sealed class ReplayMarker
    {
        private GameObject _go;
        private TextMeshPro _t;
        private bool _broken;

        private int _shownWhich = -1, _shownCount = -1;

        public void Tick(bool show, Vector3 pos, Camera head, int which = 0, int count = 0)
        {
            if (_broken) return;
            if (!show || head == null) { if (_go != null && _go.activeSelf) _go.SetActive(false); return; }
            try
            {
                if (_go == null)
                {
                    _go = new GameObject("QuestShade_ReplayMarker") { layer = 5 };
                    UnityEngine.Object.DontDestroyOnLoad(_go);
                    _go.hideFlags = HideFlags.DontSave;
                    _t = _go.AddComponent<TextMeshPro>();
                    _t.text = "▼ FILMING";
                    _t.alignment = TextAlignmentOptions.Center;
                    _t.fontSize = 1.4f;
                    _t.enableWordWrapping = false;
                    _t.color = new Color(1f, 0.55f, 0.12f);
                    _t.outlineWidth = 0.25f;
                    _t.outlineColor = new Color32(0, 0, 0, 255);
                    _t.rectTransform.sizeDelta = new Vector2(3f, 0.6f);
                }
                if (!_go.activeSelf) _go.SetActive(true);
                if (which != _shownWhich || count != _shownCount)
                {
                    _shownWhich = which; _shownCount = count;
                    _t.text = count > 1 ? $"▼ FILMING  {which + 1}/{count}" : "▼ FILMING";
                }
                float dist = Mathf.Max(1.5f, (pos - head.transform.position).magnitude);
                _go.transform.localScale = Vector3.one * (dist * 0.6f * (1f + 0.08f * Mathf.Sin(Time.unscaledTime * 5f)));
                var toCam = pos - head.transform.position;
                _go.transform.SetPositionAndRotation(pos, toCam.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toCam, Vector3.up) : Quaternion.identity);
            }
            catch (Exception e) { _broken = true; Mod.Log.Warning("Replay marker unavailable: " + e.Message); }
        }
    }

    internal enum ReplayView { Chest, OverTheShoulder }
}
