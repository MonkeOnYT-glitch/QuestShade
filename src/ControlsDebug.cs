using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace QuestShade
{
    // DEBUG BUILD: tracks the controls to find out why the rig freezes / the joystick stops working until you regrab.
    //  - every time QuestShade touches your hands, grips or the camera prop, that's logged as an event (kept: last 25)
    //  - once a second: sticks, buttons, how fast you move/turn and what each hand holds -> UserData/QuestShade/controls_debug.txt
    //  - "STUCK": a stick pushed for over 1 s while you don't move (left) or turn (right) -> full snapshot in the main log
    internal static class ControlsDebug
    {
        public static bool On = true;
        public static Func<int, string> HandState; // what a hand holds / is attached to (from BoneMenuUi, needs BoneLib)
        public static Func<string> CameraState;

        private static readonly Queue<string> _events = new Queue<string>();
        private static Vector3 _lastPos; private static float _lastYaw, _speed, _yawRate, _lastT, _fileAt, _stuckLogAt, _leftPushed, _rightPushed;
        private static bool _have;
        private static string FilePath => Path.Combine(Config.Root, "controls_debug.txt");

        public static void Event(string what)
        {
            if (!On) return;
            string line = $"{Time.unscaledTime:0.00}s {what}";
            _events.Enqueue(line);
            while (_events.Count > 25) _events.Dequeue();
            Mod.Log.Msg("[Controls] " + what);
            Append("EVENT " + line);
        }

        private static void Append(string line)
        {
            try { File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n"); } catch { }
        }

        public static void Start()
        {
            if (!On) return;
            try { if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2_000_000) File.Delete(FilePath); } catch { }
            Append("===== session start (QuestShade controls debug) =====");
        }

        public static void Tick(Controls ctl, Camera head)
        {
            if (!On || ctl == null || head == null) return;
            float now = Time.unscaledTime, dt = Mathf.Max(now - _lastT, 1e-4f);
            _lastT = now;
            var t = head.transform;
            Vector3 p = t.position; float yaw = t.eulerAngles.y;
            if (_have)
            {
                Vector3 d = p - _lastPos; d.y = 0;
                float k = 1f - Mathf.Exp(-dt * 4f); // smooth over ~0.25 s
                _speed = Mathf.Lerp(_speed, d.magnitude / dt, k);
                _yawRate = Mathf.Lerp(_yawRate, Mathf.Abs(Mathf.DeltaAngle(_lastYaw, yaw)) / dt, k);
            }
            _have = true; _lastPos = p; _lastYaw = yaw;

            Vector2 l = ctl.LeftStick, r = ctl.RightStick;
            _leftPushed = l.magnitude > 0.6f ? _leftPushed + dt : 0f;
            _rightPushed = Mathf.Abs(r.x) > 0.6f ? _rightPushed + dt : 0f;

            if (now >= _fileAt)
            {
                _fileAt = now + 1f;
                Append($"L({l.x:+0.00;-0.00},{l.y:+0.00;-0.00}) click={B(ctl.LeftClick)} grip={B(ctl.GripL)} trig={B(ctl.Trigger(true))} | " +
                       $"R({r.x:+0.00;-0.00},{r.y:+0.00;-0.00}) click={B(ctl.RightClick)} grip={B(ctl.GripR)} trig={B(ctl.Trigger(false))} | " +
                       $"move {_speed:0.00} m/s turn {_yawRate:0} deg/s | held L={Hand(1)} R={Hand(2)} | cam {Cam()}");
            }

            bool moveStuck = _leftPushed > 1f && _speed < 0.15f;
            bool turnStuck = _rightPushed > 1f && _yawRate < 8f;
            if ((moveStuck || turnStuck) && now >= _stuckLogAt)
            {
                _stuckLogAt = now + 5f;
                var sb = new System.Text.StringBuilder();
                sb.Append($"[Controls] STUCK ({(moveStuck ? $"left stick pushed {_leftPushed:0.0}s, not moving ({_speed:0.00} m/s)" : "")}{(moveStuck && turnStuck ? " + " : "")}{(turnStuck ? $"right stick pushed {_rightPushed:0.0}s, not turning ({_yawRate:0} deg/s)" : "")})\n");
                sb.Append($"    sticks L({l.x:0.00},{l.y:0.00}) R({r.x:0.00},{r.y:0.00}); grips L={B(ctl.GripL)} R={B(ctl.GripR)}; triggers L={B(ctl.Trigger(true))} R={B(ctl.Trigger(false))}\n");
                sb.Append($"    left hand: {Hand(1)}\n    right hand: {Hand(2)}\n    camera: {Cam()}\n");
                sb.Append("    recent QuestShade events:\n");
                if (_events.Count == 0) sb.Append("      (none)\n");
                foreach (var e in _events) sb.Append("      ").Append(e).Append('\n');
                Mod.Log.Warning(sb.ToString().TrimEnd());
                Append(sb.ToString().TrimEnd());
            }
        }

        private static string B(bool b) => b ? "1" : "0";
        private static string Hand(int h) { try { return HandState != null ? HandState(h) : "?"; } catch (Exception e) { return "err " + e.Message; } }
        private static string Cam() { try { return CameraState != null ? CameraState() : "?"; } catch { return "?"; } }
    }
}
