using System;
using UnityEngine;

namespace QuestShade
{
    public enum StampMode { Off, PoliceBodycam, AllModes }

    internal sealed partial class PhotoCamera
    {
        private GameObject _stamp;
        private Il2CppTMPro.TextMeshPro _stampTmp;
        private int _overlayLayer = -2;
        private float _stampNext;
        private string _stampText;

        internal int OverlayMask => _overlayLayer >= 0 ? 1 << _overlayLayer : 0;

        private int PickOverlayLayer()
        {
            for (int l = 31; l >= 8; l--)
                if (l != _screenLayer && string.IsNullOrEmpty(LayerMask.LayerToName(l))) return l;
            return -1;
        }

        private void UpdateStamp(Config cfg, Camera head)
        {
            bool want = Open && !Hidden && (cfg.Timestamp == StampMode.AllModes || (cfg.Timestamp == StampMode.PoliceBodycam && Mode == CamMode.PoliceCam));
            if (!want) { if (_stamp != null && _stamp.activeSelf) _stamp.SetActive(false); return; }
            if (_overlayLayer == -2)
            {
                _overlayLayer = PickOverlayLayer();
                Mod.Log.Msg(_overlayLayer >= 0 ? $"Timestamp: lens-only layer {_overlayLayer}" : "Timestamp: no free layer, timestamp off");
                _layoutMode = -1;
            }
            if (_overlayLayer < 0 || _lensGo == null || _cam == null) return;
            int bit = 1 << _overlayLayer;
            if (head != null && (head.cullingMask & bit) != 0) head.cullingMask &= ~bit;
            if ((_cam.cullingMask & bit) == 0) _cam.cullingMask |= bit;
            if (_stamp == null)
            {
                try
                {
                    _stamp = new GameObject("QuestShade_Timestamp");
                    _stamp.layer = _overlayLayer;
                    _stamp.transform.SetParent(_lensGo.transform, false);
                    _stampTmp = _stamp.AddComponent<Il2CppTMPro.TextMeshPro>();
                    _stampTmp.alignment = Il2CppTMPro.TextAlignmentOptions.TopRight;
                    _stampTmp.enableWordWrapping = false;
                    _stampTmp.richText = false;
                    _stampTmp.color = new Color(1f, 1f, 1f, 0.92f);
                    _stampTmp.outlineWidth = 0.2f;
                    _stampTmp.outlineColor = new Color32(0, 0, 0, 200);
                    _stampTmp.rectTransform.pivot = new Vector2(1f, 1f);
                    var r = _stamp.GetComponent<MeshRenderer>();
                    if (r != null) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                catch (Exception e)
                {
                    Mod.Log.Warning("Timestamp unavailable: " + e.Message);
                    if (_stamp != null) UnityEngine.Object.Destroy(_stamp);
                    _stamp = null; _stampTmp = null; _overlayLayer = -1;
                    return;
                }
            }
            if (!_stamp.activeSelf) _stamp.SetActive(true);
            float d = Mathf.Max(_cam.nearClipPlane * 2f, 0.1f);
            float hh = d * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float hw = hh * Mathf.Max(0.5f, _cam.aspect);
            _stamp.transform.localPosition = new Vector3(hw * 0.94f, hh * 0.9f, d);
            _stamp.transform.localRotation = Quaternion.identity;
            _stampTmp.fontSize = hh * 2f * 0.38f;
            _stampTmp.rectTransform.sizeDelta = new Vector2(hw * 2f, hh);
            if (Time.unscaledTime >= _stampNext)
            {
                _stampNext = Time.unscaledTime + 0.25f;
                var now = DateTime.Now;
                string t = now.ToString("yyyy-MM-dd  HH:mm:ss") + (Mode == CamMode.PoliceCam ? "\nQS BODY CAM  X1" : "");
                if (t != _stampText) { _stampText = t; _stampTmp.text = t; }
            }
        }

        private void HideStamp() { if (_stamp != null) _stamp.SetActive(false); }
    }
}
