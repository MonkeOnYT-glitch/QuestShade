using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace QuestShade
{
    // Timeline mod support (beta). Timeline records you, NPCs, props and your voice and plays them back with camera keyframes.
    // QuestShade watches it: when Timeline playback starts, QuestShade's camera follows Timeline's keyframed camera and records
    // the replay with QuestShade's look (shaders, bloom, shadows); recording stops when playback ends.
    // Everything goes through reflection, so QuestShade runs the same without Timeline installed.
    internal static class TimelineLink
    {
        private static bool _searched, _found, _camWarned;
        private static FieldInfo _playing, _recording, _paused;
        private static Type _ccType; private static FieldInfo _ccCamera;
        private static MethodInfo _tryCast;
        private static Camera _cam; private static float _nextCamFind;

        public static bool Installed => Find();

        private static bool Find()
        {
            if (_searched) return _found;
            _searched = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Timeline") continue;
                    const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var wp = asm.GetType("Timeline.WorldRecording.WorldPlayer");
                    _playing = wp?.GetField("playing", S); _recording = wp?.GetField("recording", S); _paused = wp?.GetField("paused", S);
                    _ccType = asm.GetType("Timeline.CameraRelated.CameraController");
                    _ccCamera = _ccType?.GetField("camera", I);
                    // Director controls: play / pause / scrub, the take length, and the recorded you (playback avatar).
                    _playHead = wp?.GetField("playHead", S); _instance = wp?.GetField("Instance", S);
                    _length = wp?.GetField("totalSceneLength", I);
                    _play = wp?.GetMethod("Play", I); _pause = wp?.GetMethod("Pause", I); _updateScene = wp?.GetMethod("UpdateScene", I);
                    _rigRec = wp?.GetField("currentRigmanagerRecorder", S);
                    var rr = asm.GetType("Timeline.WorldRecording.Recorders.RigmanagerRecorder");
                    _getBone = rr?.GetMethod("GetBoneTransform", I);
                    _playbackAvatar = rr?.GetField("currentPlaybackAvatar", I);
                    _found = _playing != null;
                    break;
                }
                if (_ccType != null) _tryCast = typeof(Il2CppObjectBase).GetMethod("TryCast")?.MakeGenericMethod(_ccType);
            }
            catch (Exception e) { Mod.Log.Warning("Timeline support: couldn't read Timeline: " + e.Message); _found = false; }
            Mod.Log.Msg(_found ? $"Timeline support: Timeline found{(_ccCamera != null ? " (with its camera)" : "")}" : "Timeline support: Timeline not installed");
            return _found;
        }

        private static FieldInfo _playHead, _instance, _length, _rigRec, _playbackAvatar;
        private static MethodInfo _play, _pause, _updateScene, _getBone;
        private static object Instance { get { try { return _instance?.GetValue(null); } catch { return null; } } }

        public static bool CanDirect => Find() && _playHead != null && _play != null && _pause != null && _updateScene != null;

        public static float PlayHead
        {
            get { try { return _playHead != null ? (float)_playHead.GetValue(null) : 0f; } catch { return 0f; } }
        }

        public static float Length
        {
            get { try { var w = Instance; return w != null && _length != null ? (float)_length.GetValue(w) : 0f; } catch { return 0f; } }
        }

        // The recorded people only exist while playback is set up; "show people" plays and pauses straight away.
        public static bool PeopleShown => Playing || Paused;

        public static void PlayFrom(float t)
        {
            try { var w = Instance; if (w != null) _play.Invoke(w, new object[] { t, true, null }); }
            catch (Exception e) { Mod.Log.Warning("Timeline support: play failed: " + Inner(e)); }
        }

        public static void Pause()
        {
            try { var w = Instance; if (w != null) _pause.Invoke(w, null); }
            catch (Exception e) { Mod.Log.Warning("Timeline support: pause failed: " + Inner(e)); }
        }

        public static void ShowPeople() => Scrub(0.01f); // going to the very start spawns the recorded people
        private static void ShowPeopleOld()
        {
            if (PeopleShown) return;
            float t = PlayHead;
            PlayFrom(t);
            Pause();
            Scrub(t);
        }

        // Jump to a moment: the recorded people and objects pose themselves there (paused).
        public static void Scrub(float t)
        {
            try
            {
                var w = Instance; if (w == null) return;
                if (!PeopleShown) { PlayFrom(t); Pause(); }
                else if (Playing && !Paused) Pause();
                t = Mathf.Clamp(t, 0f, Mathf.Max(0f, Length));
                _playHead.SetValue(null, t);
                _updateScene.Invoke(w, new object[] { t });
            }
            catch (Exception e) { Mod.Log.Warning("Timeline support: scrub failed: " + Inner(e)); }
        }

        // A bone of the recorded you (Timeline's playback avatar), or null if there isn't one right now.
        public const int Head = 10, Chest = 8; // Unity HumanBodyBones values
        public static Transform PlaybackBone(int bone)
        {
            try
            {
                if (_rigRec == null || _getBone == null) return null;
                var rr = _rigRec.GetValue(null);
                if (rr == null) return null;
                if (_playbackAvatar != null && _playbackAvatar.GetValue(rr) == null) return null;
                var p = _getBone.GetParameters();
                object arg = p.Length == 1 && p[0].ParameterType.IsEnum ? Enum.ToObject(p[0].ParameterType, bone) : (object)bone;
                return _getBone.Invoke(rr, new[] { arg }) as Transform;
            }
            catch { return null; }
        }

        private static string Inner(Exception e) => (e.InnerException ?? e).Message;

        private static bool Flag(FieldInfo f) { try { return f != null && (bool)f.GetValue(null); } catch { return false; } }
        public static bool Playing => Find() && Flag(_playing);
        public static bool Recording => Find() && Flag(_recording);
        public static bool Paused => Find() && Flag(_paused);

        // Timeline's keyframed playback camera (null if Timeline has none in this scene).
        public static Camera PlaybackCamera()
        {
            if (!Find() || _ccType == null || _ccCamera == null) return null;
            bool alive; try { alive = _cam != null && _cam.gameObject != null; } catch { alive = false; }
            if (alive) return _cam;
            float now = Time.unscaledTime;
            if (now < _nextCamFind) return null;
            _nextCamFind = now + 2f;
            try
            {
                var objs = UnityEngine.Object.FindObjectsOfType(Il2CppType.From(_ccType));
                foreach (var o in objs)
                {
                    if (o == null) continue;
                    object inst = _tryCast != null ? _tryCast.Invoke(o, null) : null;
                    if (inst == null) continue;
                    if (_ccCamera.GetValue(inst) is Camera c && c != null) { _cam = c; Mod.Log.Msg($"Timeline support: following Timeline's camera '{c.gameObject.name}'"); return c; }
                }
            }
            catch (Exception e) { if (!_camWarned) { _camWarned = true; Mod.Log.Warning("Timeline support: couldn't find Timeline's camera: " + e.Message); } }
            return null;
        }
    }
}
