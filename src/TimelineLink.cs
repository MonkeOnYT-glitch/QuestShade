using System;
using System.Reflection;
using UnityEngine;

namespace QuestShade
{
    internal static class TimelineLink
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static FieldInfo _playing, _recording, _paused, _instance, _recorders, _avatarField, _playHead;
        private static MethodInfo _play, _pause, _updateScene;
        private static Type _rigType, _menuType;
        private static bool _searched;
        private static float _nextSearch, _nextPoll;

        public static bool Present { get; private set; }
        public static bool Playing { get; private set; }
        public static bool Recording { get; private set; }
        public static bool Paused { get; private set; }
        public static bool PlayingTake => Playing && !Paused && !Recording && _preview == 0;

        private static int _preview;
        private static float _previewUntil, _nextReadyCheck;
        private static int _extraPoses;
        public static bool Previewing => _preview != 0;

        public static bool Preview()
        {
            if (!Present || Playing || Recording || _play == null || _pause == null) return false;
            if (!Call(_play)) return false;
            _preview = 1;
            _previewUntil = Time.unscaledTime + 6f;
            _extraPoses = -1;
            return true;
        }

        private static void PreviewTick(float now)
        {
            if (_preview == 1)
            {
                Call(_pause);
                _preview = 2;
                return;
            }
            bool paused = false;
            try { paused = _paused != null && (bool)_paused.GetValue(null); } catch { }
            if (!paused || now > _previewUntil) { _preview = 0; return; }
            PoseScene();
            if (_extraPoses > 0) { if (--_extraPoses == 0) _preview = 0; return; }
            if (now >= _nextReadyCheck) { _nextReadyCheck = now + 0.2f; if (AllAvatarsLoaded()) _extraPoses = 3; }
        }

        private static readonly object[] _poseArgs = new object[1];
        private static void PoseScene()
        {
            try
            {
                var inst = _instance?.GetValue(null);
                if (inst == null || _updateScene == null || _playHead == null) return;
                _poseArgs[0] = _playHead.GetValue(null);
                _updateScene.Invoke(inst, _poseArgs);
            }
            catch (Exception e) { Mod.Log.Warning("Timeline preview pose failed: " + e.Message); _preview = 0; }
        }

        private static bool AllAvatarsLoaded()
        {
            try
            {
                var inst = _instance?.GetValue(null);
                if (inst == null || _rigType == null || !(_recorders.GetValue(inst) is System.Collections.IList list)) return true;
                foreach (var rec in list)
                    if (rec != null && _rigType.IsInstanceOfType(rec) && _avatarField.GetValue(rec) == null) return false;
                return true;
            }
            catch { return true; }
        }

        public static void PlaybackAvatars(System.Collections.Generic.List<Il2CppSLZ.VRMK.Avatar> into)
        {
            into.Clear();
            if (!Present || _recorders == null || _avatarField == null || _rigType == null) return;
            try
            {
                var inst = _instance?.GetValue(null);
                if (inst == null || !(_recorders.GetValue(inst) is System.Collections.IList list)) return;
                foreach (var rec in list)
                {
                    if (rec == null || !_rigType.IsInstanceOfType(rec)) continue;
                    if (_avatarField.GetValue(rec) is Il2CppSLZ.VRMK.Avatar av && av != null && av.gameObject.activeInHierarchy && av.neck2 != null) into.Add(av);
                }
            }
            catch { }
        }

        public static bool Populate(string section, object page)
        {
            if (_menuType == null || page == null) return false;
            try
            {
                var m = _menuType.GetMethod(section, Any);
                if (m == null) { Mod.Log.Warning("Timeline menu section missing: " + section); return false; }
                m.Invoke(null, new[] { page });
                return true;
            }
            catch (Exception e) { Mod.Log.Warning($"Timeline menu section {section} failed: {(e.InnerException ?? e).Message}"); return false; }
        }

        private static bool Call(MethodInfo m)
        {
            try
            {
                var inst = _instance?.GetValue(null);
                if (inst == null || m == null) return false;
                var ps = m.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                m.Invoke(inst, args);
                return true;
            }
            catch (Exception e) { Mod.Log.Warning("Timeline call failed: " + e.Message); return false; }
        }

        private static void Find()
        {
            float now = Time.unscaledTime;
            if (_searched && now < _nextSearch) return;
            _searched = true; _nextSearch = now + 10f;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "Timeline") continue;
                var t = asm.GetType("Timeline.WorldRecording.WorldPlayer");
                if (t == null) return;
                _playing = t.GetField("playing", Any);
                _recording = t.GetField("recording", Any);
                _paused = t.GetField("paused", Any);
                _playHead = t.GetField("playHead", Any);
                _instance = t.GetField("Instance", Any);
                _recorders = t.GetField("playbackRecorders", Any);
                _rigType = asm.GetType("Timeline.WorldRecording.Recorders.RigmanagerRecorder");
                _avatarField = _rigType?.GetField("currentPlaybackAvatar", Any);
                _play = t.GetMethod("Play", Any);
                _pause = t.GetMethod("Pause", Any);
                _updateScene = t.GetMethod("UpdateScene", Any);
                _menuType = asm.GetType("Timeline.Menu.BonemenuHandler");
                Present = _playing != null;
                if (Present) Mod.Log.Msg("Timeline found: its controls are in QuestShade > Timeline");
                return;
            }
        }

        public static bool JustFound { get; private set; }

        public static void Tick()
        {
            JustFound = false;
            if (!Present) { Find(); if (!Present) return; JustFound = true; }
            float now = Time.unscaledTime;
            if (_preview != 0) PreviewTick(now);
            if (now < _nextPoll) return;
            _nextPoll = now + 0.1f;
            try
            {
                Playing = (bool)_playing.GetValue(null);
                Recording = _recording != null && (bool)_recording.GetValue(null);
                Paused = _paused != null && (bool)_paused.GetValue(null);
            }
            catch { Present = false; }
        }
    }
}
