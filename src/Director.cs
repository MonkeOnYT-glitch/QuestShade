using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestShade
{
    public enum ShotKind { FreeCam, FirstPerson, ThirdPerson, Chest, TimelineCam }

    // Timeline director (beta): camera shots pinned to exact moments of a Timeline recording.
    //  - Free cam shots: where the QuestShade camera was when you added them. Several in a row glide smoothly between
    //    their times, so you always know when the camera reaches each spot (the problem with free-floating keyframes).
    //  - Mode shots: first person / third person / chest on the recorded you (Timeline's playback avatar), or hand
    //    control back to Timeline's own camera. Each runs from its time until the next shot, like cuts in an edit.
    internal static class Director
    {
        public struct Shot { public float T; public ShotKind Kind; public Vector3 Pos; public Quaternion Rot; public float Fov; }
        public static readonly List<Shot> Shots = new List<Shot>();
        public static int Version { get; private set; }

        public static Func<Transform> LiveHead;
        private static Quaternion _headFix = Quaternion.identity;
        private static float _nextLearn;

        public static readonly Color[] KindColor =
        {
            new Color(1f, 0.62f, 0.15f), new Color(0.35f, 0.85f, 1f), new Color(0.55f, 1f, 0.45f), new Color(1f, 0.45f, 0.6f), new Color(0.85f, 0.75f, 1f),
        };
        public static readonly string[] KindName = { "Free cam", "First person", "Third person", "Chest", "Timeline camera" };

        // Learn which way your avatar's head bone faces relative to your eyes, so first person on the recorded you looks
        // where you looked (head bone axes differ between avatars). Learned during normal play.
        public static void LearnHeadFix(Transform hmd, bool ragdolled)
        {
            if (ragdolled || hmd == null || LiveHead == null || Time.unscaledTime < _nextLearn) return;
            _nextLearn = Time.unscaledTime + 1f;
            try { var h = LiveHead(); if (h != null) _headFix = Quaternion.Inverse(h.rotation) * hmd.rotation; } catch { }
        }

        public static void Add(ShotKind kind, float t, Vector3 pos, Quaternion rot, float fov)
        {
            Shots.RemoveAll(s => Mathf.Abs(s.T - t) < 0.05f);
            Shots.Add(new Shot { T = Mathf.Max(0f, t), Kind = kind, Pos = pos, Rot = rot, Fov = fov });
            Shots.Sort((a, b) => a.T.CompareTo(b.T));
            Version++;
        }

        public static bool RemoveNear(float t, float within = 0.3f)
        {
            int best = -1; float bd = within;
            for (int i = 0; i < Shots.Count; i++) { float d = Mathf.Abs(Shots[i].T - t); if (d <= bd) { bd = d; best = i; } }
            if (best < 0) return false;
            Shots.RemoveAt(best); Version++;
            return true;
        }

        public static void Clear() { Shots.Clear(); Version++; }

        public static float Prev(float t) { float r = 0f; foreach (var s in Shots) if (s.T < t - 0.05f) r = s.T; return r; }
        public static float Next(float t, float end) { foreach (var s in Shots) if (s.T > t + 0.05f) return s.T; return end; }

        public static string Describe(float t)
        {
            int i = Active(t);
            return i < 0 ? "no shot" : $"{KindName[(int)Shots[i].Kind]} (from {Clock(Shots[i].T)})";
        }

        public static string Clock(float t) => $"{(int)t / 60}:{t % 60f:00.0}";

        private static int Active(float t)
        {
            int i = -1;
            for (int k = 0; k < Shots.Count; k++) { if (Shots[k].T <= t + 1e-4f) i = k; else break; }
            return i < 0 && Shots.Count > 0 ? 0 : i;
        }

        // The camera at Timeline second t. False = no shot applies (the camera does whatever it normally does).
        public static bool Eval(float t, Config cfg, Camera timelineCam, out Vector3 pos, out Quaternion rot, out float fov)
        {
            pos = default; rot = Quaternion.identity; fov = 0f;
            int i = Active(t);
            if (i < 0) return false;
            var s = Shots[i];
            switch (s.Kind)
            {
                case ShotKind.FreeCam:
                {
                    // Glide to the next free cam shot if it comes straight after; otherwise hold here.
                    if (i + 1 < Shots.Count && Shots[i + 1].Kind == ShotKind.FreeCam && t > s.T)
                    {
                        var n = Shots[i + 1];
                        float u = Mathf.Clamp01((t - s.T) / Mathf.Max(0.01f, n.T - s.T));
                        var p0 = i > 0 && Shots[i - 1].Kind == ShotKind.FreeCam ? Shots[i - 1].Pos : s.Pos;
                        var p3 = i + 2 < Shots.Count && Shots[i + 2].Kind == ShotKind.FreeCam ? Shots[i + 2].Pos : n.Pos;
                        pos = CatmullRom(p0, s.Pos, n.Pos, p3, u);
                        rot = Quaternion.Slerp(s.Rot, n.Rot, u * u * (3f - 2f * u));
                        fov = Mathf.Lerp(s.Fov, n.Fov, u);
                    }
                    else { pos = s.Pos; rot = s.Rot; fov = s.Fov; }
                    return true;
                }
                case ShotKind.TimelineCam:
                {
                    if (timelineCam == null) return false;
                    try { var ct = timelineCam.transform; pos = ct.position; rot = ct.rotation; fov = timelineCam.fieldOfView; return true; }
                    catch { return false; }
                }
                default:
                {
                    var head = TimelineLink.PlaybackBone(TimelineLink.Head);
                    if (head == null) return false; // nobody to film: press Show people / Play first
                    var hr = head.rotation * _headFix;
                    var fwd = Vector3.ProjectOnPlane(hr * Vector3.forward, Vector3.up);
                    if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                    fwd.Normalize();
                    var right = Vector3.Cross(Vector3.up, fwd);
                    fov = s.Fov;
                    if (s.Kind == ShotKind.FirstPerson)
                    {
                        pos = head.position + hr * new Vector3(0f, 0.03f, 0.09f);
                        rot = hr;
                    }
                    else if (s.Kind == ShotKind.ThirdPerson)
                    {
                        pos = head.position - fwd * cfg.ThirdPersonDistance + Vector3.up * cfg.ThirdPersonHeight + right * cfg.ThirdPersonSide;
                        rot = Quaternion.LookRotation(head.position + fwd * 1.5f - pos, Vector3.up);
                    }
                    else
                    {
                        var chest = TimelineLink.PlaybackBone(TimelineLink.Chest);
                        var c = chest != null ? chest.position : head.position - Vector3.up * 0.35f;
                        pos = c + fwd * 0.2f + Vector3.up * 0.05f;
                        rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(5f, 0f, 0f);
                    }
                    return true;
                }
            }
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }
    }
}
