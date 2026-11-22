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
        private float _pathT;

        public int TimelineCount => _keys.Count;
        public bool TimelinePlaying => _pathPlaying;
        public float TimelineLength(Config cfg) => Mathf.Max(0, _keys.Count - 1) * cfg.TimelineSeconds;

        public bool TimelineAdd()
        {
            if (!Open || _lensGo == null || _cam == null || _pathPlaying) return false;
            var t = _lensGo.transform;
            _keys.Add(new Key { Pos = t.position, Rot = t.rotation, Fov = _cam.fieldOfView });
            return true;
        }

        public void TimelineUndo() { if (!_pathPlaying && _keys.Count > 0) _keys.RemoveAt(_keys.Count - 1); }
        public void TimelineClear() { _pathPlaying = false; _keys.Clear(); }

        public bool TimelinePlay()
        {
            if (!Open || _keys.Count < 2) return false;
            _pathT = 0f; _pathPlaying = true;
            return true;
        }

        public void TimelineStop() => _pathPlaying = false;

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

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }
    }
}
