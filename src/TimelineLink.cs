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
                    _found = _playing != null;
                    break;
                }
                if (_ccType != null) _tryCast = typeof(Il2CppObjectBase).GetMethod("TryCast")?.MakeGenericMethod(_ccType);
            }
            catch (Exception e) { Mod.Log.Warning("Timeline support: couldn't read Timeline: " + e.Message); _found = false; }
            Mod.Log.Msg(_found ? $"Timeline support: Timeline found{(_ccCamera != null ? " (with its camera)" : "")}" : "Timeline support: Timeline not installed");
            return _found;
        }

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
