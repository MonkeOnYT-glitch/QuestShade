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
        private const float HandleReach = 0.16f, BodyReach = 0.09f, SnapTime = 0.07f;
        private Vector3 _grabPosC; private Quaternion _grabRotC = Quaternion.identity;

        private void UpdateGrab(Config cfg, Camera head, bool gripL, bool gripR)
        {
            bool downL = gripL && !_prevGripL, downR = gripR && !_prevGripR;
            _prevGripL = gripL; _prevGripR = gripR;
            float now = Time.unscaledTime;

            if (_held >= 0)
            {
                if (now < _grabCheckUntil && HandBusySafe(_held + 1)) { DropHold(true); return; }
                if (Hidden || !(_held == 0 ? gripL : gripR)) { DropHold(false); return; }
                int other = 1 - _held;
                if ((other == 0 ? downL : downR) && Reach(head, other, out int handle) < float.MaxValue) StartHold(cfg, head, other, handle);
                return;
            }
            if (Hidden || !_hasDevicePose || (!downL && !downR)) return;

            int bestHand = -1, bestHandle = 0; float best = float.MaxValue;
            for (int h = 0; h < 2; h++)
            {
                if (h == 0 ? !downL : !downR) continue;
                if (HandBusySafe(h + 1)) continue;
                float d = Reach(head, h, out int handle);
                if (d < best) { best = d; bestHand = h; bestHandle = handle; }
            }
            if (bestHand >= 0) StartHold(cfg, head, bestHand, bestHandle);
        }

        private float Reach(Camera head, int h, out int handle)
        {
            handle = h == 0 ? 1 : 0;
            if (!TryHandPose(head, h == 0 ? XRNode.LeftHand : XRNode.RightHand, out var hp, out _)) return float.MaxValue;
            var local = Quaternion.Inverse(_devRot) * (hp - _devPos);
            if (_model != null)
            {
                float dR = local.magnitude, dL = (local - _modelHandleL).magnitude;
                handle = dL < dR ? 1 : 0;
                float d = Mathf.Min(dR, dL);
                if (d <= HandleReach) return d;
                float db = (_modelBounds.ClosestPoint(local) - local).magnitude;
                return db <= BodyReach ? HandleReach + db : float.MaxValue;
            }
            var body = new Bounds(Vector3.zero, new Vector3(0.3f, 0.2f, 0.05f));
            float dd = (body.ClosestPoint(local) - local).magnitude;
            return dd <= BodyReach ? dd : float.MaxValue;
        }

        private void StartHold(Config cfg, Camera head, int h, int handle)
        {
            if (!TryHandPose(head, h == 0 ? XRNode.LeftHand : XRNode.RightHand, out var hp, out var hr)) return;
            Quaternion rigRot; Vector3 rigPos;
            if (_model != null)
            {
                rigRot = hr * Quaternion.Euler(cfg.GripAngle, 0f, 0f);
                rigPos = hp - rigRot * (handle == 1 ? _modelHandleL : Vector3.zero);
            }
            else { rigRot = _devRot; rigPos = _devPos; }

            var inv = Quaternion.Inverse(hr);
            _grabRotC = inv * rigRot; _grabPosC = inv * (rigPos - hp);
            _grabOnHand = false;
            Transform ht = null;
            try { ht = HandTransform?.Invoke(h + 1); } catch { }
            if (ht != null)
            {
                var hinv = Quaternion.Inverse(ht.rotation);
                _grabRot = hinv * rigRot; _grabPos = hinv * (rigPos - ht.position);
                _grabOnHand = true;
            }
            bool handOver = _held >= 0;
            if (!handOver) { _blendPos = _devPos; _blendRot = _devRot; }
            _held = h;
            _pinned = false;
            _grabAt = Time.unscaledTime;
            _grabCheckUntil = _grabAt + 0.3f;
            Mod.Log.Msg($"Camera grabbed ({(h == 0 ? "left" : "right")} hand, {(handle == 1 ? "left" : "right")} handle{(_grabOnHand ? "" : ", controller pose")})");
            Feedback?.Invoke(null, h == 0);
        }

        private void DropHold(bool cancel)
        {
            bool left = _held == 0;
            _held = -1;
            _pinned = true;
            if (cancel) { _devPos = _blendPos; _devRot = _blendRot; Mod.Log.Msg("Camera grab skipped (your hand took an item)"); return; }
            Mod.Log.Msg("Camera placed");
            Feedback?.Invoke(null, left);
        }

        private bool HandBusySafe(int hand) { try { return HandBusy != null && HandBusy(hand); } catch { return false; } }

        private bool HeldPose(Camera head, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = default;
            Transform ht = null;
            if (_grabOnHand) { try { ht = HandTransform?.Invoke(_held + 1); } catch { } }
            if (ht != null) { rot = ht.rotation * _grabRot; pos = ht.position + ht.rotation * _grabPos; }
            else if (TryHandPose(head, _held == 0 ? XRNode.LeftHand : XRNode.RightHand, out var hp, out var hr)) { rot = hr * _grabRotC; pos = hp + hr * _grabPosC; }
            else return false;
            float t = (Time.unscaledTime - _grabAt) / SnapTime;
            if (t < 1f)
            {
                t = t * t * (3f - 2f * t);
                pos = Vector3.Lerp(_devPos, pos, t);
                rot = Quaternion.Slerp(_devRot, rot, t);
            }
            return true;
        }

        private void LayoutScreen()
        {
            if (_layoutMode == (int)Mode) return;
            _layoutMode = (int)Mode;

            bool hide = Detached && _screenLayer >= 0;
            _cam.cullingMask = (_screenLayer >= 0 ? _fullMask & ~(1 << _screenLayer) : _fullMask) & ~UiLayers;

            bool selfie = Mode == CamMode.Selfie;
            if (_model != null)
            {
                _screenMat.mainTextureScale = new Vector2(selfie ? 1f : -1f, -1f);
                _screenMat.mainTextureOffset = new Vector2(selfie ? 0f : 1f, 1f);
                return;
            }
            bool flip = selfie && !PropActive;
            var face = flip ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            _screen.transform.localRotation = face;
            _bezel.transform.localRotation = face;
            _screen.transform.localPosition = Vector3.zero;
            _bezel.transform.localPosition = new Vector3(0f, 0f, flip ? -0.002f : 0.002f);
            var s = _screen.transform.localScale;
            _screen.transform.localScale = new Vector3(selfie ? -Mathf.Abs(s.x) : Mathf.Abs(s.x), s.y, 1f);
        }

        private bool TryHandPose(Camera head, XRNode node, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = default;
            float now = Time.unscaledTime;
            if (now >= _nextDev || !_headDev.isValid || !_leftDev.isValid || !_rightDev.isValid)
            {
                _nextDev = now + 2f;
                _headDev = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
                if (!_headDev.isValid) _headDev = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                _leftDev = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                _rightDev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            }
            var dev = node == XRNode.RightHand ? _rightDev : _leftDev;
            var ht = head.transform;
            Vector3 hp = default; Quaternion hr = default;
            bool xr = _headDev.isValid && dev.isValid &&
                      (_headDev.TryGetFeatureValue(CommonUsages.centerEyePosition, out hp) || _headDev.TryGetFeatureValue(CommonUsages.devicePosition, out hp)) &&
                      (_headDev.TryGetFeatureValue(CommonUsages.centerEyeRotation, out hr) || _headDev.TryGetFeatureValue(CommonUsages.deviceRotation, out hr));
            if (!xr || !dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 lp) ||
                !dev.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion lr))
            {
                if (node == XRNode.RightHand) return false;
                pos = ht.position + ht.forward * 0.45f - ht.up * 0.15f;
                rot = ht.rotation * Quaternion.Euler(-25f, 0f, 0f);
                return true;
            }
            var R = ht.rotation * Quaternion.Inverse(hr);
            pos = ht.position + R * (lp - hp);
            rot = R * lr;
            return true;
        }
    }
}
