using System.Collections.Generic;
using UnityEngine;

namespace QuestShade
{
    // Camera timeline: drop keyframes where the camera is looking, then play them back as one smooth move
    // (a curved path through every keyframe, easing in at the start and out at the end). Each keyframe keeps its zoom.
    internal sealed partial class PhotoCamera
    {
        private struct Key { public Vector3 Pos; public Quaternion Rot; public float Fov; }
        private readonly List<Key> _keys = new List<Key>();
        private bool _pathPlaying;
        private float _zoomNow = 1f;
        public bool UsesBodyFov => BodyMode; // chest / helmet / first person use the body-cam field of view
        private float _pathT;

        public int TimelineCount => _keys.Count;
        public bool TimelinePlaying => _pathPlaying;
        public float TimelineLength(Config cfg) => Mathf.Max(0, _keys.Count - 1) * cfg.TimelineSeconds;

        public bool TimelineAdd()
        {
            if (!Open || _lensGo == null || _cam == null || _pathPlaying) return false;
            var t = _lensGo.transform;
            _keys.Add(new Key { Pos = t.position, Rot = t.rotation, Fov = _cam.fieldOfView / _zoomNow }); // without the slow-mo zoom
            return true;
        }

        public void TimelineUndo() { if (!_pathPlaying && _keys.Count > 0) _keys.RemoveAt(_keys.Count - 1); }
        public void TimelineClear() { _pathPlaying = false; _keys.Clear(); }

        public bool TimelinePlay()
        {
            if (!Open || _keys.Count < 2 || _moveRecording) return false;
            _movePlaying = false; _pathT = 0f; _pathPlaying = true;
            return true;
        }

        public void TimelineStop() { _pathPlaying = false; _movePlaying = false; _moveRecording = false; }
        public bool AnyPlayback => _pathPlaying || _movePlaying;
        public Camera FollowCamera; // set while the Timeline mod plays back: the lens follows its camera

        private void EvalPath(Config cfg, float dt, ref Vector3 pos, ref Quaternion rot, ref float fov)
        {
            int n = _keys.Count;
            if (n < 2) { _pathPlaying = false; return; }
            float total = (n - 1) * cfg.TimelineSeconds;
            _pathT += dt;
            float s = Mathf.Clamp01(_pathT / total);
            s = s * s * (3f - 2f * s);                 // ease in at the start, out at the end
            float u = s * (n - 1);
            int i = Mathf.Min((int)u, n - 2);
            float f = u - i;
            var p0 = _keys[Mathf.Max(i - 1, 0)].Pos; var p1 = _keys[i].Pos; var p2 = _keys[i + 1].Pos; var p3 = _keys[Mathf.Min(i + 2, n - 1)].Pos;
            pos = CatmullRom(p0, p1, p2, p3, f);
            rot = Quaternion.Slerp(_keys[i].Rot, _keys[i + 1].Rot, f * f * (3f - 2f * f));
            fov = Mathf.Lerp(_keys[i].Fov, _keys[i + 1].Fov, f);
            if (_pathT >= total) _pathPlaying = false;
        }

        // ---- Recorded moves: record the camera's real movement as you move it, then play it back exactly. ----
        private struct Sample { public float T; public Vector3 Pos; public Quaternion Rot; public float Fov; }
        private readonly List<Sample> _move = new List<Sample>();
        private bool _moveRecording, _movePlaying;
        private float _moveT0, _moveT; private int _moveIdx;
        private const int MaxSamples = 90 * 600; // 10 minutes at 90 fps

        public bool MoveRecording => _moveRecording;
        public bool MovePlaying => _movePlaying;
        public float MoveLength => _move.Count > 1 ? _move[_move.Count - 1].T : 0f;
        public float MoveRecordedSoFar => _moveRecording ? Time.unscaledTime - _moveT0 : MoveLength;

        public bool MoveRecordStart()
        {
            if (!Open || _pathPlaying || _movePlaying) return false;
            _move.Clear(); _moveRecording = true; _moveT0 = Time.unscaledTime;
            return true;
        }
        public void MoveRecordStop() => _moveRecording = false;

        public bool MovePlay()
        {
            if (!Open || _move.Count < 2 || _moveRecording) return false;
            _pathPlaying = false; _moveT = 0f; _moveIdx = 0; _movePlaying = true;
            return true;
        }

        // Called with the lens pose the camera would have had this frame (after smoothing).
        private void SampleMove(Vector3 pos, Quaternion rot, float fov)
        {
            if (_move.Count >= MaxSamples) { _moveRecording = false; return; }
            _move.Add(new Sample { T = Time.unscaledTime - _moveT0, Pos = pos, Rot = rot, Fov = fov });
        }

        private void EvalMove(float dt, ref Vector3 pos, ref Quaternion rot, ref float fov)
        {
            int n = _move.Count;
            if (n < 2) { _movePlaying = false; return; }
            _moveT += dt;
            while (_moveIdx < n - 2 && _move[_moveIdx + 1].T < _moveT) _moveIdx++;
            var a = _move[_moveIdx]; var b = _move[_moveIdx + 1];
            float f = b.T > a.T ? Mathf.Clamp01((_moveT - a.T) / (b.T - a.T)) : 1f;
            pos = Vector3.Lerp(a.Pos, b.Pos, f);
            rot = Quaternion.Slerp(a.Rot, b.Rot, f);
            fov = Mathf.Lerp(a.Fov, b.Fov, f);
            if (_moveT >= _move[n - 1].T) _movePlaying = false;
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }
    }
}
