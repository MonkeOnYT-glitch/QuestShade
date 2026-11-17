using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using InputDevice = UnityEngine.XR.InputDevice;

namespace QuestShade
{
    internal sealed partial class PhotoCamera
    {

        private float _handBack;
        private bool _handNear;
        private bool _vertical, _photoVertical;
        private Vector4 _portraitSet = new Vector4(-1f, 0f, 0f, 0f);
        private static readonly int PortraitId = Shader.PropertyToID("_QS_Portrait");

        private float BackOffForHands(Vector3 pivot, Vector3 fwd, float want, float minDist, float dt)
        {
            float limit = float.MaxValue;
            if (!_following && HandTransform != null)
            {
                for (int h = 1; h <= 2; h++)
                {
                    Transform t = null;
                    try { t = HandTransform(h); } catch { }
                    if (t == null) continue;
                    foreach (var p in new[] { t.position, t.position + t.forward * 0.1f })
                    {
                        var rel = p - pivot;
                        float depth = Vector3.Dot(rel, fwd);
                        if (depth < -0.05f || depth > want + 0.2f) continue;
                        float side = (rel - fwd * depth).magnitude;
                        if (side > 0.25f + Mathf.Max(0f, depth) * 0.9f) continue;
                        limit = Mathf.Min(limit, depth - 0.08f);
                    }
                }
            }
            float back = limit == float.MaxValue ? 0f : Mathf.Clamp(want - Mathf.Max(minDist, limit), 0f, want - minDist);
            _handNear = limit != float.MaxValue && limit < want + 0.05f;
            _handBack = back > _handBack ? back : Mathf.MoveTowards(_handBack, back, dt * 0.4f);
            return Mathf.Max(minDist, want - _handBack);
        }
        private float _fullFar = 1000f;
        private const float PreviewFar = 200f;
        private void SetFar(float f) { if (_cam != null && Mathf.Abs(_cam.farClipPlane - f) > 0.5f) _cam.farClipPlane = f; }

        private bool ScreenInView(Camera head)
        {
            if (head == null) return true;
            var ht = head.transform;
            var sp = _modelScreen != null ? _modelScreen.position : _devPos;
            var to = sp - ht.position;
            float d = to.magnitude;
            if (d < 0.7f) return true;
            if (d > 12f) return false;
            return Vector3.Dot(ht.forward, to / d) > 0.26f;
        }
        private float SmoothK(Config cfg, float dt) => _hasLensPose && cfg.Smoothing > 0f ? 1f - Mathf.Pow(cfg.Smoothing, dt * 10f) : 1f;

        private void UpdatePoses(Config cfg, Camera head, float dt)
        {
            var ht = head.transform;
            _following = false;
            if (FollowActor && ActorPose != null && (Mode == CamMode.Chest || Mode == CamMode.ThirdPerson)
                && ActorPose(ActorIndex, out var ap, out var ar))
            {
                if (_actorAnchor == null) { _actorAnchor = new GameObject("QuestShade_ActorAnchor"); UnityEngine.Object.DontDestroyOnLoad(_actorAnchor); _actorAnchor.hideFlags = HideFlags.HideAndDontSave; }
                _actorAnchor.transform.SetPositionAndRotation(ap, ar);
                ht = _actorAnchor.transform;
                _following = true;
            }

            if (PropActive) FollowProp(cfg, head);
            else if (_held >= 0 && HeldPose(head, out var gp, out var gr))
            {
                _devRot = gr;
                _devPos = gp;
                _hasDevicePose = true;
            }
            else if (!(_pinned && _hasDevicePose) && TryHandPose(head, XRNode.LeftHand, out var hp, out var hr))
            {
                var rot = hr * Quaternion.Euler(cfg.HandTilt, 0f, 0f);
                var pos = hp + rot * cfg.HandOffset;
                if (Mode == CamMode.Selfie && _model == null)
                {
                    var face = ht.position - Vector3.up * 0.06f;
                    var toFace = face - pos;
                    rot = toFace.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toFace, Vector3.up) : rot * Quaternion.Euler(0f, 180f, 0f);
                }
                bool lensOnDevice = Mode == CamMode.Hand || Mode == CamMode.Selfie;
                float k = lensOnDevice && _hasDevicePose && cfg.Smoothing > 0f ? 1f - Mathf.Pow(cfg.Smoothing, dt * 10f) : 1f;
                _devPos = _hasDevicePose ? Vector3.Lerp(_devPos, pos, k) : pos;
                _devRot = _hasDevicePose ? Quaternion.Slerp(_devRot, rot, k) : rot;
                _hasDevicePose = true;
            }
            _device.transform.SetPositionAndRotation(_devPos, _devRot);
            LayoutScreen();

            Vector3 lp; Quaternion lr;
            switch (Mode)
            {
                case CamMode.ThirdPerson:
                {
                    var fwd = Vector3.ProjectOnPlane(ht.forward, Vector3.up);
                    if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(ht.up, Vector3.up);
                    fwd.Normalize();
                    var right = Vector3.Cross(Vector3.up, fwd);
                    lp = ht.position - fwd * cfg.ThirdPersonDistance + Vector3.up * cfg.ThirdPersonHeight + right * cfg.ThirdPersonSide;
                    lp = AvoidWalls(ht.position, lp);
                    var target = ht.position + fwd * 1.5f;
                    lr = Quaternion.LookRotation(target - lp, Vector3.up);
                    break;
                }
                case CamMode.Helmet:
                {
                    lp = ht.position + ht.right * 0.17f + ht.up * 0.07f + ht.forward * 0.10f;
                    lr = ht.rotation * Quaternion.Euler(6f, 4f, 0f);
                    break;
                }
                case CamMode.FirstPerson:
                {
                    lp = ht.position + ht.forward * 0.06f;
                    lr = ht.rotation;
                    break;
                }
                case CamMode.Chest:
                case CamMode.PoliceCam:
                    ChestPose(cfg, head, out lp, out lr, out var chestPivot, _following ? ht : null);
                    if (Mode == CamMode.PoliceCam) lp += lr * new Vector3(-cfg.ChestSide, -0.07f, 0f);
                    lp = AvoidWalls(chestPivot, lp);
                    {
                        var off = lp - chestPivot; float d = off.magnitude;
                        if (d > 1e-3f) BackOffForHands(chestPivot, off / d, d, d, dt);
                    }
                    break;
                default:
                    if (Mode == CamMode.Selfie && (PropActive || _model != null))
                    {
                        lp = _model != null && _modelScreen != null
                            ? _modelScreen.position - (_devRot * Vector3.forward) * 0.03f
                            : _devPos + _devRot * new Vector3(0f, 0f, -0.02f);
                        lr = _devRot * Quaternion.Euler(0f, 180f, 0f);
                    }
                    else
                    {
                        lp = _model != null && _modelLens != null
                            ? _modelLens.position + (_devRot * Vector3.forward) * 0.01f
                            : _devPos + _devRot * new Vector3(0f, 0f, 0.02f);
                        lr = _devRot;
                    }
                    break;
            }
            _vertical = cfg.Vertical;
            if (_vertical) lr *= Quaternion.Euler(0f, 0f, 90f);
            bool onDevice = Mode == CamMode.Hand || Mode == CamMode.Selfie;
            float lk = onDevice && _held < 0 ? 1f : SmoothK(cfg, dt);
            bool bodyLocked = BodyMode;
            _lensPos = _hasLensPose && !bodyLocked ? Vector3.Lerp(_lensPos, lp, lk) : lp;
            _lensRot = _hasLensPose ? Quaternion.Slerp(_lensRot, lr, lk) : lr;
            _hasLensPose = true;
            _lensGo.transform.SetPositionAndRotation(_lensPos, _lensRot);
            if (_hoverMat != null && _hoverWanted)
            {
                _hoverMat.mainTexture = _screenMat.mainTexture;
                bool sm = Mode == CamMode.Selfie;
                _hoverMat.mainTextureScale = new Vector2(sm ? -1f : 1f, 1f);
                _hoverMat.mainTextureOffset = new Vector2(sm ? 1f : 0f, 0f);
            }
            float ratio = cfg.PreviewWidth > 0 ? cfg.PreviewHeight / (float)cfg.PreviewWidth : 0.5625f;
            var portrait = new Vector4(_vertical ? 1f : 0f, ratio * ratio, 0f, 0f);
            if (portrait != _portraitSet && _screenMat != null)
            {
                _portraitSet = portrait;
                _screenMat.SetVector(PortraitId, portrait);
                if (_hoverMat != null) _hoverMat.SetVector(PortraitId, portrait);
            }
            float fov = Mode == CamMode.PoliceCam ? Mathf.Max(cfg.ChestFov, 115f) : BodyMode ? cfg.ChestFov : cfg.Fov;
            if (fov != _appliedFov) { _appliedFov = fov; _cam.fieldOfView = fov; }
            float near = BodyMode ? 0.05f : 0.015f;
            if (_handNear && BodyMode) near = 0.01f;
            if (near != _appliedNear) { _appliedNear = near; _cam.nearClipPlane = near; }

            bool stereo = ShaderPack.HeadsetActive;
            if (stereo != _screenStereo && ShaderPack.Screen != null && _screenMat != null && _screenMat.shader == ShaderPack.Screen)
            {
                _screenStereo = stereo;
                ShaderPack.SetStereo(_screenMat, stereo);
                ShaderPack.SetStereo(_bezelMat, stereo);
                foreach (var m in _modelMats) if (m != null) ShaderPack.SetStereo(m, stereo);
                if (_hoverMat != null) ShaderPack.SetStereo(_hoverMat, stereo);
            }
        }
    }
}
