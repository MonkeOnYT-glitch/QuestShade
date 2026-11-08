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
        public Action<string, Vector3, Quaternion, Action<GameObject>> SpawnCrate;
        public Func<GameObject, int> IsHeldBy;
        public Func<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Collider>> PlayerColliders;
        private PropBody _prop;
        private bool _propSpawning, _propWasHeld, _propWasActive;
        private float _propRetryAt, _propSpawnTime;
        private Vector3 _propSpawnPos;
        private int _propHand;
        private bool PropActive => _prop != null && _prop.Alive;
        public bool HasBody => PropActive;
        public int HoldingHand => !Open ? 0 : PropActive ? _propHand : _held >= 0 ? _held + 1 : 0;

        private void RequestProp(Config cfg, Camera head)
        {
            if (!cfg.PhysicalCamera || SuppressBody || SpawnCrate == null || _propSpawning || PropActive || head == null) return;
            if (Time.unscaledTime < _propRetryAt) return;
            _propRetryAt = Time.unscaledTime + 5f;
            Vector3 pos; Quaternion rot;
            if (_hasDevicePose) { pos = _devPos; rot = _devRot; } else SpawnPose(head, out pos, out rot);
            try
            {
                _propSpawning = true;
                _propSpawnTime = Time.unscaledTime;
                _propSpawnPos = pos;
                SpawnCrate(cfg.BodyBarcode, pos, rot, go => OnPropSpawned(go, cfg));
            }
            catch (Exception e) { _propSpawning = false; Mod.Log.Warning("Camera body spawn failed (using the floating camera): " + e.Message); }
        }

        private void PollForSpawnedProp(Config cfg)
        {
            if (!_propSpawning) return;
            if (SuppressBody) { _propSpawning = false; return; }
            if (Time.unscaledTime < _propSpawnTime + 0.15f) return;
            if (Time.unscaledTime > _propSpawnTime + 1.5f && Time.frameCount % 10 != 0) return;
            var go = PropBody.FindSpawnedNear(_propSpawnPos, 1.5f, cfg.BodyBarcode);
            if (go != null) { Mod.Log.Msg("Camera body: found it without the spawn callback"); OnPropSpawned(go, cfg); return; }
            if (Time.unscaledTime > _propSpawnTime + 6f) { _propSpawning = false; Mod.Log.Warning("Camera body didn't appear - using the floating camera"); }
        }

        private void OnPropSpawned(GameObject go, Config cfg)
        {
            if (SuppressBody) { _propSpawning = false; return; }
            CamDiag.Step("body spawned");
            if (_prop != null && _prop.Root == go) return;
            _propSpawning = false;
            if (go == null) { Mod.Log.Warning($"Camera body '{cfg.BodyBarcode}' didn't spawn - using the floating camera"); return; }
            float h = cfg.ScreenSize, w = h * cfg.PreviewWidth / (float)cfg.PreviewHeight;
            var p = _model != null
                ? PropBody.Adapt(go, 0f, _modelBounds.size + new Vector3(0.02f, 0.02f, 0.02f), _modelBounds.center)
                : PropBody.Adapt(go, cfg.BodyMountHeight, new Vector3(w + 0.03f, h + 0.03f, 0.06f), Vector3.zero);
            if (!Open) { p.Despawn(); return; }
            _prop = p;
            _propWasHeld = false;
            _mountSet = false;
            try { if (PlayerColliders != null) _prop.IgnoreCollisions(PlayerColliders()); } catch { }
            if (_hasDevicePose) _prop.PlaceMountAt(_devPos, _devRot);
            _prop.SetFrozen(true);
            _anchorSet = false; _settling = false; _cupAt = 0; _braked = false; _notHeldFor = 0f; _propHand = 0;
            _layoutMode = -1;
        }

        private Vector3 _mountPos; private Quaternion _mountRot = Quaternion.identity; private bool _mountSet;

        private void FollowProp(Config cfg, Camera head)
        {
            var t = _prop.Root.transform;
            if (!_mountSet) { _mountPos = _prop.MountLocal; _mountRot = Quaternion.Inverse(t.rotation) * _devRot; _mountSet = true; SetAnchor(); }
            int rawHand = 0;
            try { rawHand = IsHeldBy != null ? IsHeldBy(_prop.Root) : 0; } catch { }
            if (rawHand != 0)
            {
                if (_braked) { _braked = false; _prop.Brake(false); }
                _propHand = rawHand; _notHeldFor = 0f;
            }
            else
            {
                if (_propWasHeld && !_braked && cfg.FreezeOnRelease) { _braked = true; _prop.Brake(true); }
                if (_propWasHeld && _notHeldFor == 0f) ScheduleIgnore();
                _notHeldFor += Time.unscaledDeltaTime;
                if (!_propWasHeld || _notHeldFor > 0.12f) _propHand = 0;
            }
            bool held = _propHand != 0;
            float now = Time.unscaledTime;

            if (!held) GrabAssist(head, t); else _assistHand = 0;

            if (!held && _prop.Frozen && _anchorSet) _prop.HoldAt(_anchorPos, _anchorRot);

            if (held && !_propWasHeld) _settleUntil = now + 0.35f;
            var node = _propHand == 1 ? XRNode.LeftHand : XRNode.RightHand;
            if (held && now < _settleUntil && TryHandPose(head, node, out var hp, out var hr))
            {
                var rigRot = hr * Quaternion.Euler(cfg.GripAngle, 0f, 0f);
                _devRot = rigRot;
                _devPos = hp - rigRot * (_propHand == 1 ? _modelHandleL : Vector3.zero);
                _settling = true;
            }
            else
            {
                if (_settling)
                {
                    _settling = false;
                    _mountPos = t.InverseTransformPoint(_devPos);
                    _mountRot = Quaternion.Inverse(t.rotation) * _devRot;
                }
                _devPos = t.TransformPoint(_mountPos);
                _devRot = t.rotation * _mountRot;
            }
            _hasDevicePose = true;
            if (held == _propWasHeld) return;
            _propWasHeld = held;
            ScheduleIgnore();
            if (held) { _prop.SetFrozen(false); _prop.Brake(false); _braked = false; _anchorSet = false; }
            else if (cfg.FreezeOnRelease) { _prop.SetFrozen(true); SetAnchor(); _cupAt = -1; _braked = false; }
            else { _prop.Brake(false); _braked = false; }
            Mod.Log.Msg(held ? "Camera grabbed" : (cfg.FreezeOnRelease ? "Camera placed" : "Camera dropped"));
            Feedback?.Invoke(null, false);
        }

        private float _settleUntil; private bool _settling;
        private float _notHeldFor; private bool _braked;

        public delegate bool PoseFn(int index, out Vector3 pos, out Quaternion rot);
        public PoseFn ActorPose;
        public Action<bool> HideHeadOf;
        public bool FollowActor;
        public int ActorIndex;
        private bool _following;
        private GameObject _actorAnchor;
        public bool SuppressBody;
        public bool Following => _following;

        public void ParkBody()
        {
            if (_prop == null) return;
            try { _prop.Despawn(); } catch { }
            _prop = null; _propSpawning = false;
            _pinned = true;
        }

        private readonly float[] _ignoreAt = new float[4]; private int _ignoreLeft;
        private void ScheduleIgnore()
        {
            float t = Time.unscaledTime;
            _ignoreAt[0] = t; _ignoreAt[1] = t + 0.3f; _ignoreAt[2] = t + 0.8f; _ignoreAt[3] = t + 1.5f; _ignoreLeft = 4;
        }
        private void RunIgnore(float now)
        {
            if (_ignoreLeft == 0 || PlayerColliders == null) return;
            int idx = 4 - _ignoreLeft;
            if (now < _ignoreAt[idx]) return;
            _ignoreLeft--;
            try { _prop.IgnoreCollisions(PlayerColliders(), quiet: true); } catch { }
        }
        private int _cupAt;

        public Func<int, GameObject, bool> AttachToHand;
        private bool _gL, _gR, _gLWas, _gRWas;
        private int _assistHand, _assistFrames; private float _assistHoldUntil;

        private void GrabAssist(Camera head, Transform cup)
        {
            if (_model == null) { _gLWas = _gL; _gRWas = _gR; return; }
            var rig = Matrix4x4.TRS(_devPos, _devRot, Vector3.one);
            Vector3 hR = _devPos, hL = rig.MultiplyPoint3x4(_modelHandleL);
            for (int side = 1; side <= 2; side++)
            {
                bool down = side == 1 ? _gL : _gR, was = side == 1 ? _gLWas : _gRWas;
                if (!down || was || _assistHand != 0) continue;
                if (!TryHandPose(head, side == 1 ? XRNode.LeftHand : XRNode.RightHand, out var hp, out _)) continue;
                float dR = Vector3.Distance(hp, hR), dL = Vector3.Distance(hp, hL);
                if (Mathf.Min(dR, dL) > 0.3f) continue;
                _assistHand = side; _assistFrames = 3;
            }
            _gLWas = _gL; _gRWas = _gR;
            if (_assistHand == 0) return;
            if (!(_assistHand == 1 ? _gL : _gR)) { _assistHand = 0; return; }
            if (--_assistFrames > 0) return;
            int hand = _assistHand; _assistHand = 0;
            try
            {
                if (TryHandPose(head, hand == 1 ? XRNode.LeftHand : XRNode.RightHand, out var hp2, out _))
                {
                    var delta = hp2 - cup.TransformPoint(_prop.MountLocal);
                    _anchorPos += delta;
                    _prop.HoldAt(_anchorPos, _anchorRot);
                    _mountPos = cup.InverseTransformPoint(_devPos);
                    _mountRot = Quaternion.Inverse(cup.rotation) * _devRot;
                    _cupAt = -1; _assistHoldUntil = Time.unscaledTime + 1f;
                    Mod.Log.Msg($"Camera: grab assist ({(hand == 1 ? "left" : "right")} hand)");
                }
            }
            catch (Exception e) { Mod.Log.Warning("Grab assist failed: " + e.Message); }
        }

        private void ShiftCupToReachedHandle(Camera head, Transform cup)
        {
            var rig = Matrix4x4.TRS(_devPos, _devRot, Vector3.one);
            Vector3 hR = _devPos, hL = rig.MultiplyPoint3x4(_modelHandleL);
            float best = 0.30f; int want = _cupAt;
            foreach (var node in new[] { XRNode.LeftHand, XRNode.RightHand })
            {
                if (!TryHandPose(head, node, out var hp, out _)) continue;
                float dR = Vector3.Distance(hp, hR), dL = Vector3.Distance(hp, hL);
                if (dR < best) { best = dR; want = 0; }
                if (dL < best) { best = dL; want = 1; }
            }
            if (want == _cupAt) return;
            MoveCupTo(want, cup, hR, hL);
        }

        private void MoveCupTo(int want, Transform cup, Vector3 hR, Vector3 hL)
        {
            _cupAt = want;
            var target = want == 0 ? hR : hL;
            var delta = target - cup.TransformPoint(_prop.MountLocal);
            _anchorPos += delta;
            _prop.HoldAt(_anchorPos, _anchorRot);
            _mountPos = cup.InverseTransformPoint(_devPos);
            _mountRot = Quaternion.Inverse(cup.rotation) * _devRot;
        }
        private Vector3 _anchorPos; private Quaternion _anchorRot; private bool _anchorSet;
        private void SetAnchor()
        {
            try { var t = _prop.Root.transform; _anchorPos = t.position; _anchorRot = t.rotation; _anchorSet = true; } catch { _anchorSet = false; }
        }
    }
}
