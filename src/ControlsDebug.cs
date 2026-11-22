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
        public static bool On = false; // debug tool: turn on to trace controls (off in releases)
        public static Func<int, string> HandState; // what a hand holds / is attached to (from BoneMenuUi, needs BoneLib)
        public static Func<string> CameraState;
        public static Func<int, Vector2> GameStick;
        public static Func<int, bool> GameTouch; // BONELAB's own thumb-on-stick reading

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
            // Full-rate history (last ~4 s) for frozen-input detection and the manual marker.
            Vector2 gl = Vector2.zero, gr = Vector2.zero;
            try { if (GameStick != null) { gl = GameStick(1); gr = GameStick(2); } } catch { }
            _ring[_ringW] = new Sample { T = now, L = l, R = r, GL = gl, GR = gr, Grip = (ctl.GripL ? 1 : 0) | (ctl.GripR ? 2 : 0), Speed = _speed };
            _ringW = (_ringW + 1) % _ring.Length; if (_ringN < _ring.Length) _ringN++;
            // Frozen input: the controller reports the exact same pushed value for over 1.5 s (a thumb can't hold it that still).
            if (l.magnitude > 0.3f && l == _frozenVal) _frozenFor += dt; else { _frozenFor = 0f; _frozenVal = l; }
            if (r.magnitude > 0.3f && r == _frozenValR) _frozenForR += dt; else { _frozenForR = 0f; _frozenValR = r; }
            bool mismatch = (l - gl).magnitude > 0.3f; // left only: after a snap turn BONELAB holds the right stick at zero on purpose
            _mismatchFor = mismatch ? _mismatchFor + dt : 0f;
            // Manual marker: hold both triggers and both grips for 1 s.
            bool mark = ctl.GripL && ctl.GripR && ctl.Trigger(true) && ctl.Trigger(false);
            _markFor = mark ? _markFor + dt : 0f;
            if (now >= _dumpAt)
            {
                string why = _markFor > 1f ? "MANUAL MARK (both triggers + grips)" :
                             _frozenFor > 1.5f ? $"LEFT STICK FROZEN at ({l.x:0.000},{l.y:0.000}) for {_frozenFor:0.0}s" :
                             _frozenForR > 1.5f ? $"RIGHT STICK FROZEN at ({r.x:0.000},{r.y:0.000}) for {_frozenForR:0.0}s" :
                             _mismatchFor > 0.5f ? "BONELAB DISAGREES with the controller (left stick) for 0.5s" : null;
                if (why != null) { _dumpAt = now + 6f; Dump(why, ctl); }
            }
            _leftPushed = l.magnitude > 0.5f ? _leftPushed + dt : 0f;
            _rightPushed = Mathf.Abs(r.x) > 0.5f ? _rightPushed + dt : 0f;

            if (now >= _fileAt)
            {
                _fileAt = now + (_leftPushed > 0f || _rightPushed > 0f ? 0.25f : 1f);
                Append($"L({l.x:+0.00;-0.00},{l.y:+0.00;-0.00}) click={B(ctl.LeftClick)} grip={B(ctl.GripL)} trig={B(ctl.Trigger(true))} | " +
                       $"R({r.x:+0.00;-0.00},{r.y:+0.00;-0.00}) click={B(ctl.RightClick)} grip={B(ctl.GripR)} trig={B(ctl.Trigger(false))} | " +
                       $"touch raw L={B(ctl.LeftTouch)} R={B(ctl.RightTouch)} game L={GT(1)} R={GT(2)} | game L{GS(1)} R{GS(2)} | move {_speed:0.00} m/s turn {_yawRate:0} deg/s | held L={Hand(1)} R={Hand(2)} | cam {Cam()}");
            }

            bool moveStuck = _leftPushed > 0.4f && _speed < 0.15f;
            bool turnStuck = _rightPushed > 0.4f && _yawRate < 8f;
            if ((moveStuck || turnStuck) && now >= _stuckLogAt)
            {
                _stuckLogAt = now + 5f;
                var sb = new System.Text.StringBuilder();
                sb.Append($"[Controls] STUCK ({(moveStuck ? $"left stick pushed {_leftPushed:0.0}s, not moving ({_speed:0.00} m/s)" : "")}{(moveStuck && turnStuck ? " + " : "")}{(turnStuck ? $"right stick pushed {_rightPushed:0.0}s, not turning ({_yawRate:0} deg/s)" : "")})\n");
                sb.Append($"    sticks L({l.x:0.00},{l.y:0.00}) R({r.x:0.00},{r.y:0.00}); grips L={B(ctl.GripL)} R={B(ctl.GripR)}; triggers L={B(ctl.Trigger(true))} R={B(ctl.Trigger(false))}\n");
                sb.Append($"    BONELAB sees sticks L{GS(1)} R{GS(2)} (zero here = the game is ignoring the stick; same as above = your body is stuck)\n");
                sb.Append($"    left hand: {Hand(1)}\n    right hand: {Hand(2)}\n    camera: {Cam()}\n");
                sb.Append("    recent QuestShade events:\n");
                if (_events.Count == 0) sb.Append("      (none)\n");
                foreach (var e in _events) sb.Append("      ").Append(e).Append('\n');
                Mod.Log.Warning(sb.ToString().TrimEnd());
                Append(sb.ToString().TrimEnd());
            }
        }

        private struct Sample { public float T, Speed; public Vector2 L, R, GL, GR; public int Grip; }
        private static readonly Sample[] _ring = new Sample[360];
        private static int _ringW, _ringN;
        private static Vector2 _frozenVal, _frozenValR;
        private static float _frozenFor, _frozenForR, _mismatchFor, _markFor, _dumpAt;

        private static void Dump(string why, Controls ctl)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[Controls] {why} - last {_ringN} frames (raw controller vs what BONELAB sees):\n");
            sb.Append($"    left hand: {Hand(1)} | right hand: {Hand(2)} | touch raw L={B(ctl.LeftTouch)} R={B(ctl.RightTouch)} game L={GT(1)} R={GT(2)} | camera: {Cam()}\n");
            int start = (_ringW - _ringN + _ring.Length) % _ring.Length;
            float t0 = _ring[start].T;
            for (int i = 0; i < _ringN; i += 3) // every 3rd frame keeps it readable (~24 lines per second)
            {
                var s = _ring[(start + i) % _ring.Length];
                sb.Append($"    {s.T - t0:0.00}s L({s.L.x:+0.000;-0.000},{s.L.y:+0.000;-0.000}) game({s.GL.x:+0.00;-0.00},{s.GL.y:+0.00;-0.00})  R({s.R.x:+0.000;-0.000},{s.R.y:+0.000;-0.000}) game({s.GR.x:+0.00;-0.00},{s.GR.y:+0.00;-0.00})  grip {(s.Grip & 1)}{(s.Grip >> 1)}  move {s.Speed:0.00}\n");
            }
            sb.Append("    recent QuestShade events:\n");
            foreach (var e in _events) sb.Append("      ").Append(e).Append('\n');
            Mod.Log.Warning(sb.ToString().TrimEnd());
            Append(sb.ToString().TrimEnd());
        }

        private static string B(bool b) => b ? "1" : "0";
        private static string GT(int h) { try { return GameTouch == null ? "?" : (GameTouch(h) ? "1" : "0"); } catch { return "err"; } }
        private static string GS(int h) { try { if (GameStick == null) return "(?)"; var v = GameStick(h); return $"({v.x:+0.00;-0.00},{v.y:+0.00;-0.00})"; } catch { return "(err)"; } }
        private static string Hand(int h) { try { return HandState != null ? HandState(h) : "?"; } catch (Exception e) { return "err " + e.Message; } }
        private static string Cam() { try { return CameraState != null ? CameraState() : "?"; } catch { return "?"; } }
    }
}
