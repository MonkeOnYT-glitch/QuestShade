using System;
using UnityEngine;
using UnityEngine.XR;
using InputDevice = UnityEngine.XR.InputDevice;

namespace QuestShade
{
    internal enum Gesture
    {
        None,
        LeftStickHold, LeftStickDoubleTap, RightStickHold, RightStickDoubleTap, BothSticks,
        LeftGripStickHold, RightGripStickHold,
    }

    internal readonly struct Binding
    {
        public readonly Gesture Gesture;
        private Binding(Gesture g) { Gesture = g; }
        public static Binding Parse(string s)
            => new Binding(Enum.TryParse<Gesture>(s?.Trim(), true, out var g) ? g : Gesture.None);
    }

    internal sealed class StickButton
    {
        public bool Down;
        public float PressTime;
        private float _lastTapEnd = -10f;
        private int _taps;
        private bool _holdFired, _consumed;

        public bool Hold, DoubleTap, Pressed;

        public void Update(bool down, float now, float holdTime, float dblTime)
        {
            Hold = DoubleTap = Pressed = false;
            if (down && !Down)
            {
                Pressed = true;
                PressTime = now;
                _holdFired = false;
                _consumed = false;
                if (now - _lastTapEnd > dblTime) _taps = 0;
            }
            else if (down && !_holdFired && !_consumed && now - PressTime >= holdTime)
            {
                _holdFired = true;
                _taps = 0;
                Hold = true;
            }
            else if (!down && Down)
            {
                if (!_holdFired && !_consumed && now - PressTime < holdTime)
                {
                    _taps++;
                    if (_taps >= 2) { DoubleTap = true; _taps = 0; }
                    _lastTapEnd = now;
                }
            }
            Down = down;
        }

        public void Consume() { _consumed = true; _taps = 0; }
    }

    internal sealed class Controls
    {
        private readonly StickButton _l = new StickButton(), _r = new StickButton();
        private InputDevice _left, _right;
        private float _nextDeviceRefresh;
        private bool _bothFired;
        private bool _keyboardOk = true;
        private bool _lGrip, _rGrip;
        public bool GripL => _lGrip;
        public bool GripR => _rGrip;
        private bool _lTrig, _rTrig;
        public bool Trigger(bool left) => left ? _lTrig : _rTrig;

        public void Update(Config cfg, float now)
        {
            if (now >= _nextDeviceRefresh || !_left.isValid || !_right.isValid)
            {
                _nextDeviceRefresh = now + 2f;
                _left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                _right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            }
            bool l = false, r = false;
            if (_left.isValid) _left.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out l);
            if (_right.isValid) _right.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out r);
            _lGrip = _rGrip = false;
            if (_left.isValid) _left.TryGetFeatureValue(CommonUsages.gripButton, out _lGrip);
            if (_right.isValid) _right.TryGetFeatureValue(CommonUsages.gripButton, out _rGrip);
            _lTrig = _rTrig = false;
            if (_left.isValid) _left.TryGetFeatureValue(CommonUsages.triggerButton, out _lTrig);
            if (_right.isValid) _right.TryGetFeatureValue(CommonUsages.triggerButton, out _rTrig);

            _l.Update(l, now, cfg.HoldTime, cfg.DoubleTapTime);
            _r.Update(r, now, cfg.HoldTime, cfg.DoubleTapTime);

            if (_l.Down && _r.Down)
            {
                if (!_bothFired && Mathf.Abs(_l.PressTime - _r.PressTime) < 0.15f)
                {
                    _bothFired = true;
                    _l.Consume(); _r.Consume();
                    _both = true;
                }
            }
            else if (!_l.Down && !_r.Down) _bothFired = false;
        }

        private bool _both;

        public bool Fired(Binding b)
        {
            switch (b.Gesture)
            {
                case Gesture.LeftStickHold: return _l.Hold && !_lGrip;
                case Gesture.RightStickHold: return _r.Hold && !_rGrip;
                case Gesture.LeftGripStickHold: return _l.Hold && _lGrip;
                case Gesture.RightGripStickHold: return _r.Hold && _rGrip;
                case Gesture.LeftStickDoubleTap: return _l.DoubleTap;
                case Gesture.RightStickDoubleTap: return _r.DoubleTap;
                case Gesture.BothSticks: return _both;
                default: return false;
            }
        }

        public void EndFrame() { _both = false; }

        public bool Key(Config cfg, KeyCode k)
        {
            if (!cfg.Keyboard || !_keyboardOk || Application.platform == RuntimePlatform.Android) return false;
            try { return Input.GetKeyDown(k); }
            catch { _keyboardOk = false; return false; }
        }

        public void Buzz(Config cfg, bool left, float amp = 0.4f, float dur = 0.06f)
        {
            if (!cfg.Haptics) return;
            var d = left ? _left : _right;
            try { if (d.isValid) d.SendHapticImpulse(0u, amp, dur); } catch { }
        }
    }
}
