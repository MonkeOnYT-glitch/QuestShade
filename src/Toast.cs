using System;
using Il2CppTMPro;
using UnityEngine;

namespace QuestShade
{
    internal sealed class Toast
    {
        private GameObject _go;
        public GameObject Object => _go;
        private TextMeshPro _tmp;
        private float _hideAt;
        private bool _broken;

        public void Show(Config cfg, Camera head, string text, float seconds = 1.6f)
        {
            Mod.Log.Msg(text);
            if (!cfg.ShowToasts || _broken || head == null) return;
            try
            {
                if (_go == null)
                {
                    _go = new GameObject("QuestShade_Toast");
                    _go.layer = 5;
                    UnityEngine.Object.DontDestroyOnLoad(_go);
                    _go.hideFlags = HideFlags.DontSave;
                    _tmp = _go.AddComponent<TextMeshPro>();
                    _tmp.alignment = TextAlignmentOptions.Center;
                    _tmp.fontSize = 0.6f;
                    _tmp.enableWordWrapping = false;
                    _tmp.color = Color.white;
                    _tmp.outlineWidth = 0.2f;
                    _tmp.outlineColor = new Color32(0, 0, 0, 255);
                    _tmp.rectTransform.sizeDelta = new Vector2(2f, 0.4f);
                }
                _tmp.text = text;
                _go.SetActive(true);
                _hideAt = Time.unscaledTime + seconds;
                Follow(head);
            }
            catch (Exception e) { _broken = true; Mod.Log.Warning("Toast unavailable: " + e.Message); }
        }

        public void LateTick(Camera head)
        {
            if (_go == null || !_go.activeSelf) return;
            if (Time.unscaledTime >= _hideAt || head == null) { _go.SetActive(false); return; }
            Follow(head);
        }

        private void Follow(Camera head)
        {
            var t = head.transform;
            _go.transform.SetPositionAndRotation(t.position + t.forward * 1.2f - t.up * 0.25f, t.rotation);
        }
    }
}
