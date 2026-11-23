using System;
using System.Linq;
using MelonLoader;
using UnityEngine;

#if QS_SHADOW_DEBUG
[assembly: MelonInfo(typeof(QuestShade.Mod), "QuestShade", "1.7.94-debug", "QuestShade")]
#else
[assembly: MelonInfo(typeof(QuestShade.Mod), "QuestShade", "1.7.93", "QuestShade")]
#endif
[assembly: MelonGame("Stress Level Zero", "BONELAB")]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]
[assembly: MelonOptionalDependencies("BoneLib")]

namespace QuestShade
{
    public sealed class Mod : MelonMod
    {
        internal static MelonLogger.Instance Log;

        private readonly Config _cfg = new Config();
        private readonly Effects _fx = new Effects();
        private readonly PhotoCamera _cam = new PhotoCamera();
        private readonly Controls _ctl = new Controls();
        private readonly Toast _toast = new Toast();
        private float _nextCamCheck;
        private string _startNotice;
        private bool _ready, _boneLib;
        private int _errors;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            if (Application.platform != RuntimePlatform.Android) { Log.Warning("QuestShade is Quest only - it does nothing on PC"); return; }
            try
            {
                Config.InitPaths();
                _cfg.LoadAll();
                BetaOn = Beta; // before the saved camera mode is applied (beta-only modes)
                if (_cfg._audioMigrated) { _cfg._audioMigrated = false; _cfg.SaveMenuSettings(); Log.Msg("Game audio + mic in recordings: on (new default)"); }
                Sharing.Init(_cfg);
                PhotoSaver.ResolveFolder(_cfg.SaveFolder);
                _cam.SetMode(_cfg.DefaultMode);
                _cam.Fx = _fx;
                _cam.SetHoverPreview(_cfg.HoverPreview);
                _cam.Feedback += (msg, left) => { _ctl.Buzz(_cfg, left, 0.5f, 0.05f); if (msg != null) Notify(msg, 1f); };
                _ready = true;
                _startNotice = VideoRecorder.RecoverInterrupted();
                Log.Msg($"Ready on {Application.platform}. Config: {Config.Root}  Presets: {string.Join(", ", _cfg.Presets)}");
            }
            catch (Exception e) { Log.Error("Init failed, QuestShade disabled: " + e); return; }

#if THUNDERSTORE
            Log.Msg("Updates: Thunderstore build, your mod manager keeps QuestShade up to date");
#else
            if (_cfg.AutoUpdate) Updater.Begin(Info.Version);
            else Log.Msg("Updates: auto-update is off ([General] AutoUpdate)");
#endif

            _boneLib = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BoneLib");
            if (_boneLib)
            {
                try { BoneMenuUi.Build(this, _cfg); _cam.ChestProvider = BoneMenuUi.Chest; Focus.PlayerRoot = BoneMenuUi.PlayerRoot; _cam.HideHead = BoneMenuUi.HideHead; _cam.RestoreHead = BoneMenuUi.RestoreHead; _cam.UiLayerMask = BoneMenuUi.UiLayerMask; _cam.ActorPose = BoneMenuUi.ActorPose; _cam.HideHeadOf = BoneMenuUi.HideHeadOf; _cam.HeadToLayer = BoneMenuUi.HeadToLayer; Effects.LensHide = BoneMenuUi.LensHide; _cam.HeadRestore = BoneMenuUi.RestoreHeadLayer; _cam.OwnBodyToLayer = BoneMenuUi.OwnBodyToLayer;
                    _cam.SpawnCrate = BoneMenuUi.SpawnCrate; _cam.IsHeldBy = BoneMenuUi.IsHeld; _cam.AttachToHand = BoneMenuUi.Attach; _cam.PlayerColliders = BoneMenuUi.PlayerColliders; _cam.HandTransform = BoneMenuUi.HandTransform; _cam.HandBusy = BoneMenuUi.HandBusy; Flashlights.Held = BoneMenuUi.HeldObject; FlashBeams.HeadsetLayer = () => _cam.LensHiddenLayer; FlashBeams.LensLayer = () => _cam.LensOnlyLayer; NullGlow.HeadsetLayer = () => _cam.LensHiddenLayer; Director.LiveHead = BoneMenuUi.LiveAvatarHead; _cam.BodyBone = BoneMenuUi.BodyBone; _cam.PlayerDying = () => _dyingNow; NullGlow.LensLayer = () => _cam.LensOnlyLayer; Flashlights.ItemRoot = BoneMenuUi.ItemRoot; Shadows.Avatar = BoneMenuUi.AvatarRoot; Shadows.PlayerRoot = BoneMenuUi.PlayerRoot; ControlsDebug.HandState = BoneMenuUi.HandState; ControlsDebug.GameStick = BoneMenuUi.GameStick; ControlsDebug.GameTouch = BoneMenuUi.GameStickTouch; Shadows.Held = BoneMenuUi.HeldObject; Shadows.ItemRoot = BoneMenuUi.ItemRoot; Shadows.PlayerLayers = BoneMenuUi.PlayerLayerMask; Log.Msg("BoneMenu page added"); }
                catch (Exception e) { _boneLib = false; Log.Warning("BoneLib found but menu failed (gestures still work): " + e.Message); }
            }
            else Log.Msg("BoneLib not found - using controller gestures only");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!_ready) return;
            // Livestreams carry on through level changes (the picture holds while it loads); recordings stop and save.
            if (_cam.Streaming) ParkStreamForLevel();
            else if (_cam.Recording) { var f = _cam.StopRecording(); if (f != null) Log.Msg("Recording stopped by level change: " + f); }
            if (_parked != null) { _parkedSince = Time.unscaledTime; _resumeAt = Time.unscaledTime + 3f; } // loading screen then the level: wait for the last one
            if (_boneLib) { try { BoneMenuUi.OutlineSelected(false); } catch { } }
            _cam.OnSceneLoaded();
            Flashlights.Reset();
            Lighting.OnLevel();
            Shadows.OnLevel();
            NullGlow.OnLevel(); WristChat.OnLevel();
            Director.Clear(); _tlManual = _tlRender = false;
            SlowMo.OnLevel();
            _fx.ResetCalibration();
            if (_startNotice != null) _noticeAt = Time.unscaledTime + 6f;
            Perf.OnSceneLoaded();
            _calibTries = 0; _nextCalibAt = Time.unscaledTime + 9f;
            _nextCamCheck = 0f;
            _fx.MarkDirty();
        }

        public override void OnUpdate()
        {
            if (!_ready) return;
            float now = Time.unscaledTime;
            _sw.Restart();
            try
            {
                if (_cfg.PollChanges(now)) { _fx.MarkDirty(); _cam.Refresh(_cfg, _fx.BoundCamera); RefreshPresetMenu(); }
                if (_cfg.PresetName != _leveledFor)
                {
                    bool first = _leveledFor == null;
                    _leveledFor = _cfg.PresetName;
                    if (!first) { _fx.ResetCalibration(); _fx.MarkDirty(); _calibTries = 0; _nextCalibAt = now + 0.5f; }
                }
                if (now >= _nextCamCheck) { _nextCamCheck = now + 1f; _fx.EnsureCamera(_cfg); }
                { long t0 = Prof.Now; _fx.Tick(_cfg); Prof.Add("effects", t0); }
                // With QuestShade's own flashlight light on, BONELAB's flashlight is turned down: it only lights some materials, and the two
                // stacked made those surfaces blow out to white next to dull ones. QuestShade's light treats every surface the same.
                bool ownLight = _cfg.ShadowMap && _cfg.FlashlightShadows && _cfg.FlashlightLight > 0.01f && ShadowMap.Available;
                Flashlights.Scale = ownLight ? 0.25f : 1f;
                Flashlights.Tick(_cfg.FlashlightBoost, _cfg.FlashlightBeams);
                { long t0 = Prof.Now; Lighting.Tick(_cfg); Prof.Add("lighting", t0); }
                { long t0 = Prof.Now; try { var hc = _fx.BoundCamera; if (hc != null) NullGlow.Tick(_cfg, hc.transform.position, BetaOn); } catch { } Prof.Add("nullbody glow", t0); }
                BetaOn = Beta;
                { long t0 = Prof.Now; TickChat(); Prof.Add("wrist chat", t0); }
                SlowMo.Tick(Time.unscaledDeltaTime);
                {
                    // Slow-mo effects: ease in as the game slows down, ease back out as it speeds up.
                    float sfx = BetaOn ? SlowMo.Intensity : 0f;
                    _slowFx += (sfx - _slowFx) * (1f - Mathf.Exp(-0.9f * Time.unscaledDeltaTime));
                    try { _fx.SetSlowMoFx(_slowFx < 0.003f && sfx == 0f ? 0f : _slowFx, _cfg); } catch { }
                }
                _fx.SetFisheye(_cam.Mode == CamMode.PoliceCam ? Mathf.Max(0.35f, _cfg.Fisheye) : _cfg.Fisheye);
                if (_boneLib) { long t0 = Prof.Now; TickHurt(); Prof.Add("hurt", t0); }
                _fx.TickHeadsetFocus(Time.unscaledDeltaTime);

                _ctl.Update(_cfg, now);
                { long t0 = Prof.Now; HandleInput(); Prof.Add("input", t0); }
                _ctl.EndFrame();

                TimerTick(now);
                FocusGuard(now);
                Updater.Tick();
                if (_boneLib)
                {
                    try { BoneMenuUi.KeepMenusOffCamera(); } catch { }
                    bool was = EyeAdaptation.Hold;
                    try { EyeAdaptation.Hold = BoneMenuUi.MenuInView(_fx.BoundTransform); } catch { EyeAdaptation.Hold = false; }
                    if (EyeAdaptation.Hold && !was && _cfg.EyeAdapt) Log.Msg($"Eye adaptation paused while menu '{BoneMenuUi.LastMenu}' is open");
                }
                Sharing.Tick();
                if (Updater.Notice != null && _noticeAt < 0f) { Notify(Updater.Notice, 6f); Updater.Notice = null; }
                if (!_justUpdatedChecked && now > 12f) { _justUpdatedChecked = true; ShowJustUpdated(); }
                if (_noticeAt > 0f && now > _noticeAt && _startNotice != null) { Notify(_startNotice, 6f); _startNotice = null; _noticeAt = -1f; }
                FusionTick();
                if (_uiDumpAt > 0f && now > _uiDumpAt) { _uiDumpAt = -1f; if (_boneLib) BoneMenuUi.DumpUi(); }
                SelfTest(now);
                var saved = PhotoSaver.PumpMainThread();
                if (saved != null) Notify(saved, saved.Contains("fail") ? 4f : 1.5f);
            }
            catch (Exception e) { Guard(e); }
            _ownMs += _sw.Elapsed.TotalMilliseconds;
        }

        private float _unfocusedSince = -1f, _noticeAt = -1f;
        private void FocusGuard(float now)
        {
            if (Application.isFocused) { _unfocusedSince = -1f; return; }
            if (!_cam.Recording) return;
            if (_unfocusedSince < 0f) { _unfocusedSince = now; return; }
            if (now - _unfocusedSince < 0.25f) return;
            _unfocusedSince = -1f;
            var f = _cam.StopRecording();
            Log.Msg("Recording saved because the game lost focus (Quest menu / headset off): " + (f ?? "nothing to save"));
        }

        public override void OnApplicationQuit()
        {
            try { if (_ready && _cam.Recording) { var f = _cam.StopRecording(); Log.Msg("Recording saved on quit: " + (f ?? "nothing to save")); } } catch { }
            try { _parked?.Dispose(); _parked = null; } catch { }
        }

        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private float _nextCalibAt; private int _calibTries; private float _uiDumpAt = -1f;
        private string _leveledFor;
        private double _ownMs, _ownMax;

        public override void OnLateUpdate()
        {
            if (!_ready) return;
            _sw.Restart();
            try
            {
                var head = _fx.BoundCamera;
                _fx.PhotoCamera = _cam.Open ? _cam.LensCamera : null;
                // A level starting to load takes the headset camera away for a moment, and the in-game camera closes when
                // that happens, which used to end the livestream. Hold the stream first; it comes back once the level is up.
                if (head == null && _cam.Streaming) { ParkStreamForLevel(); if (_parked != null) Log.Msg("Stream: headset camera gone (level loading), holding the stream"); }
                { long t0 = Prof.Now; _cam.LateTick(_cfg, head, Time.unscaledDeltaTime, _ctl.GripL, _ctl.GripR); Prof.Add("camera", t0); }
                { long t0 = Prof.Now; StreamTick(); Prof.Add("stream", t0); }
                { long t0 = Prof.Now; TimelineTick(); Prof.Add("timeline+menus", t0); }
                if (!_cam.Open)
                {
                    _cam.PrewarmTick(_cfg, head);
                    float now = Time.unscaledTime;
                    if (_fx.CalibrationWanted && !_cam.Prewarming && now > _nextCalibAt && _calibTries < 8)
                    { _calibTries++; _nextCalibAt = now + 5f; _cam.SchedulePrewarm(0f); }
                }
                Shadows.CameraOpen = _cam.Open;
                Shadows.Lens = _cam.Open ? _cam.LensCamera : null;
                Shadows.CameraProp = _cam.PropRoot;
                { long t0 = Prof.Now; Shadows.Tick(_cfg, head); Prof.Add("shadows", t0); }
                if (ControlsDebug.CameraState == null) { ControlsDebug.Start(); ControlsDebug.CameraState = () => $"open={_cam.Open} mode={_cam.Mode} recording={_cam.Recording}"; }
                ControlsDebug.Tick(_ctl, head);
                _toast.LateTick(head);
                if (_boneLib)
                {
                    BoneMenuUi.Hmd = head != null ? head.transform : null;
                }
                _cam.ToastObject = _toast.Object;
            }
            catch (Exception e) { try { if (_cam.Streaming) ParkStreamForLevel(); } catch { } _cam.Close(); Guard(e); }
            _ownMs += _sw.Elapsed.TotalMilliseconds;
            if (_ownMs > _ownMax) _ownMax = _ownMs;
            SpikeWatch();
            Perf.Tick(Time.unscaledDeltaTime);
            Prof.EndFrame();
            _ownMs = 0;
        }

        private int _gcSeen; private float _nextSpikeLog; private int _spikes;
        private void SpikeWatch()
        {
            float dt = Time.unscaledDeltaTime;
            int gc = GC.CollectionCount(0);
            bool gcRan = gc != _gcSeen; _gcSeen = gc;
            if (dt < 0.045f || Time.unscaledTime < 20f) return;
            _spikes++;
            if (Time.unscaledTime < _nextSpikeLog) return;
            _nextSpikeLog = Time.unscaledTime + 2f;
            Log.Msg($"Perf: {dt * 1000f:0} ms frame (QuestShade {_ownMs:0.0} ms{(gcRan ? ", .NET GC ran" : "")}{(_cam.Recording ? ", recording" : "")}{(_cam.Open ? ", camera open" : "")}) - {_spikes} spikes so far");
        }

        private void Guard(Exception e)
        {
            _errors++;
            if (_errors <= 5) Log.Error(e.ToString());
            if (_errors == 5) Log.Error("Further QuestShade errors suppressed.");
            _nextCamCheck = Time.unscaledTime + 3f;
        }


        internal void SetEffectsEnabled(bool on)
        {
            _cfg.Enabled = on;
            _fx.EnsureCamera(_cfg);
            Notify(on ? $"Effects ON ({_cfg.PresetName})" : "Effects OFF");
        }

        internal void StepPreset(int dir)
        {
            _cfg.StepPreset(dir);
            AfterPresetChange();
        }

        internal void SelectPreset(string name)
        {
            _cfg.SelectPreset(name);
            AfterPresetChange();
        }

        private void AfterPresetChange()
        {
            _cfg.Enabled = true;
            _fx.MarkDirty();
            RefreshPresetMenu();
            Notify($"Preset {_cfg.PresetIndex + 1}/{_cfg.Presets.Count}: {_cfg.PresetName}");
        }

        internal void EditPreset(string section, string key, string value)
        {
            _cfg.SetPresetValue(section, key, value);
            _cfg.Enabled = true;
            _fx.MarkDirty();
        }

        internal void NewPreset(bool copyCurrent)
        {
            var name = _cfg.NewPreset(copyCurrent);
            if (name == null) { Notify("Couldn't make a new preset"); return; }
            AfterPresetChange();
            Notify(copyCurrent ? $"Copied to \"{name}\": edit it in Presets > Edit preset" : $"New preset \"{name}\": edit it in Presets > Edit preset", 3f);
        }

        internal void RenamePreset(string name)
        {
            var old = _cfg.PresetName;
            var n = _cfg.RenamePreset(name, out var err);
            if (n == null) { Notify(err ?? "Couldn't rename", 2.5f); RefreshPresetMenu(); return; }
            _fx.MarkDirty();
            RefreshPresetMenu();
            Notify(Config.IsBuiltIn(old) ? $"Saved a copy as \"{n}\"" : $"Renamed to \"{n}\"", 2f);
        }

        internal void SetShareAuthor(string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length > 24) n = n.Substring(0, 24);
            _cfg.ShareAuthor = n;
            _cfg.SetKey("Sharing", "Author", n);
            Notify(n.Length == 0 ? "Your shared presets will say Anonymous" : $"Your shared presets will say \"by {n}\"", 2f);
        }

        internal void SharePreset()
        {
            var name = _cfg.PresetName;
            if (Sharing.IsDefaultName(name))
            {
                Notify($"Rename it first! \"{name}\" is a default name. Edit preset > Rename, then Share", 4f);
                return;
            }
            var author = string.IsNullOrWhiteSpace(_cfg.ShareAuthor) ? "Anonymous" : _cfg.ShareAuthor.Trim();
            Notify($"Sharing \"{name}\"...", 2f);
            Sharing.Publish(name, author, _cfg.CurrentPresetText(), (id, err) =>
            {
                if (err != null) { Notify("Couldn't share: " + err, 3f); return; }
                Notify($"\"{name}\" is public! Find it in Presets > Preset browser", 3f);
            });
        }

        internal void DownloadShared(Sharing.Entry entry)
        {
            Notify($"Getting \"{entry.Name}\"...", 2f);
            Sharing.Get(entry.Id, (full, err) =>
            {
                if (err != null || full?.Ini == null) { Notify("Couldn't download: " + (err ?? "empty preset"), 3f); return; }
                var n = _cfg.AddSharedPreset(full.Name ?? entry.Name, full.Author ?? entry.Author, full.Id ?? entry.Id, full.Ini);
                AfterPresetChange();
                Notify($"Got \"{n}\" by {full.Author ?? entry.Author}. It's on now", 3f);
            });
        }

        internal void ReportCurrentPreset()
        {
            var id = _cfg.Preset?.Str("Shared", "Id", null);
            if (string.IsNullOrEmpty(id)) { Notify("Switch to the preset you got from the browser first", 2.5f); return; }
            Sharing.Report(id, err => Notify(err != null ? "Couldn't report: " + err : "Reported. Thanks, someone will take a look", 2.5f));
        }

        internal void LikeCurrentPreset()
        {
            var id = _cfg.Preset?.Str("Shared", "Id", null);
            if (string.IsNullOrEmpty(id)) { Notify("Switch to a preset you got from the browser first", 2.5f); return; }
            Sharing.Like(id, (likes, err) => Notify(err != null ? "Couldn't like: " + err : $"Liked \"{_cfg.PresetName}\" ({likes} likes)", 2f));
        }

        internal void NewPresetFrom(string source)
        {
            var name = _cfg.NewPresetFrom(source);
            if (name == null) { Notify("Couldn't make the copy"); return; }
            AfterPresetChange();
            Notify($"\"{name}\" is yours: edit, rename and share it in Presets > Edit preset", 3f);
        }

        internal void ResetBuiltInPreset()
        {
            if (!_cfg.ResetBuiltIn()) { Notify("Only built-in presets can be reset"); return; }
            _fx.MarkDirty();
            RefreshPresetMenu();
            Notify($"\"{_cfg.PresetName}\" is back to how it shipped", 2f);
        }

        internal void DeletePreset()
        {
            var name = _cfg.PresetName;
            if (Config.IsBuiltIn(name)) { Notify($"\"{name}\" comes with QuestShade and can't be deleted", 2.5f); return; }
            if (!_cfg.DeleteCurrentPreset()) { Notify("Couldn't delete that preset"); return; }
            AfterPresetChange();
            Notify($"Deleted \"{name}\". Now on {_cfg.PresetName}", 2.5f);
        }

        internal const string DiscordInvite = "https://discord.gg/g2X4cjjEqM";

        internal void OpenDiscord()
        {
            try
            {
                Application.OpenURL(DiscordInvite);
                Notify(Application.platform == RuntimePlatform.Android
                    ? "Opening the QuestShade Discord in the browser..."
                    : "Opened the QuestShade Discord in your browser", 3f);
            }
            catch (Exception e)
            {
                Log.Warning("Couldn't open the Discord link: " + e.Message);
                Notify("Join the Discord: discord.gg/g2X4cjjEqM", 5f);
            }
        }

        private bool _justUpdatedChecked;
        private void ShowJustUpdated()
        {
            try
            {
                var f = System.IO.Path.Combine(Config.Root, "just-updated.txt");
                if (!System.IO.File.Exists(f)) return;
                var v = System.IO.File.ReadAllText(f).Trim();
                System.IO.File.Delete(f);
                if (v.Length > 0 && v.Length < 20) Notify($"QuestShade updated to {v}", 5f);
            }
            catch { }
        }

        internal void ReloadAll()
        {
            _cfg.LoadAll();
            _fx.MarkDirty();
            _cam.Refresh(_cfg, _fx.BoundCamera);
            RefreshPresetMenu();
            Notify($"Reloaded ({_cfg.Presets.Count} presets)");
        }

        internal void ToggleCamera()
        {
            if (_cam.Blocked && !_cam.Open) { Notify("Camera's off in Fusion lobbies (NoCameraInFusion in QuestShade.ini changes this)", 2f); return; }
            _cam.Toggle(_cfg, _fx.BoundCamera);
            if (_cam.Open && _cfg.RecordMic) AudioCapture.AskMicPermission();
            Notify(_cam.Open ? $"Camera: {Pretty(_cam.Mode)}" : "Camera closed", 1f);
        }

        internal void SetCameraMode(CamMode m, bool open = false)
        {
            bool changed = _cfg.DefaultMode != m || _cam.Mode != m;
            _cfg.DefaultMode = m;
            _cam.SetMode(m);
            if (open && !_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            if (changed && _cam.Open) Notify($"Camera: {Pretty(m)}", 1f);
        }

        private float _timerAt = -1f; private int _timerShown;

        internal void TakePhoto()
        {
            if (!_cam.Open) { Notify("Open the camera first", 1f); return; }
            if (_cfg.PhotoTimer > 0)
            {
                if (_timerAt >= 0f) { _timerAt = -1f; Notify("Timer cancelled", 1f); return; }
                _timerAt = Time.unscaledTime + _cfg.PhotoTimer;
                _timerShown = _cfg.PhotoTimer + 1;
                return;
            }
            TakePhotoNow();
        }

        private void TimerTick(float now)
        {
            if (_timerAt < 0f) return;
            if (!_cam.Open) { _timerAt = -1f; return; }
            float left = _timerAt - now;
            if (left <= 0f) { _timerAt = -1f; TakePhotoNow(); return; }
            int s = Mathf.CeilToInt(left);
            if (s != _timerShown) { _timerShown = s; _ctl.Buzz(_cfg, true, 0.3f, 0.04f); }
        }

        internal void TakePhotoNow()
        {
            if (!_cam.Open) { Notify("Open the camera first", 1f); return; }
            Log.Msg($"Photo requested ({_cam.Mode})");
            if (_cam.RequestPhoto(_cfg)) _ctl.Buzz(_cfg, true, 0.8f, 0.08f);
            else Log.Msg("Photo skipped (busy)");
        }

        // ---------- Livestream ----------
        // Beta features show for Testers: people running the test-builds updater, or with [Beta] Enabled = true.
        private float _slowFx;
        internal static bool BetaOn; // beta-only features check this (beta build, test-builds updater, or [Beta] Enabled = true)
        private static bool? _testUpdater;
        internal bool Beta
        {
            get
            {
#if QS_BETA
                return true; // beta build (pre-release)
#endif
                if (_cfg != null && _cfg.BetaOverride) return true;
                if (_testUpdater == null)
                {
                    _testUpdater = false;
                    try { foreach (var m in MelonBase.RegisteredMelons) if (m?.Info?.Name != null && m.Info.Name.Contains("(test builds)")) { _testUpdater = true; break; } } catch { }
                    Log.Msg(_testUpdater == true ? "Beta features on (test-builds updater found)" : "Beta features off");
                }
                return _testUpdater == true;
            }
        }

        // A stream kept connected while a level loads (the encoders restart once the new level is up).
        private RtmpClient _parked;
        private float _resumeAt, _parkedSince, _liveSince;

        internal bool Streaming => _parked != null || (_cam != null && _cam.Streaming);
        internal string StreamStatus
        {
            get
            {
                if (!Streaming) return "Not streaming";
                float t = Time.unscaledTime - _liveSince;
                string clock = $"{(int)t / 60}:{(int)t % 60:00}";
                if (_parked != null) return $"LIVE {clock}, changing level: viewers see a held picture until it loads";
                var s = _cam.Stream;
                if (s.Live) return $"LIVE {clock}{(s.SecondsBehind > 1f ? $", {s.SecondsBehind:0}s behind (slow Wi-Fi)" : "")}";
                return "Connecting: " + s.Status;
            }
        }

        private bool _wasLive;

        // Called when a level starts loading: keep the connection, stop the encoders, carry on once the level is up.
        private void ParkStreamForLevel()
        {
            if (_parked != null || !_cam.Streaming) return;
            _parked = _cam.DetachStream();
            if (_parked == null) return;
            _parkedSince = Time.unscaledTime;
            _resumeAt = Time.unscaledTime + 3f;
        }

        private void EndParked(string why)
        {
            try { _parked?.Dispose(); } catch { }
            _parked = null; _wasLive = false;
            if (why != null) { _ctl.Buzz(_cfg, false, 1f, 0.3f); Notify(why, 5f); }
        }

        internal void ToggleStreaming()
        {
            if (_parked != null) { EndParked(null); _ctl.Buzz(_cfg, false, 0.6f, 0.1f); Notify("Stream ended", 2f); return; }
            if (Streaming)
            {
                _cam.StopRecording(); _wasLive = false;
                _ctl.Buzz(_cfg, false, 0.6f, 0.1f);
                Notify("Stream ended", 2f);
                return;
            }
            if (_cam.Recording) { Notify("Stop recording first", 2f); return; }
            if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            if (_cam.StartStreaming(_cfg, out var err))
            {
                _wasLive = false;
                _ctl.Buzz(_cfg, false, 0.8f, 0.1f);
                Notify("Going live... connecting", 2f);
            }
            else Notify(err ?? "Stream failed to start (see log)", 3f);
        }

        private void StreamTick()
        {
            if (_parked != null)
            {
                float now = Time.unscaledTime;
                if (_parked.Failed) { EndParked("Stream stopped: " + (_parked.Error ?? "lost connection") ); return; }
                if (now - _parkedSince > 120f) { EndParked("Stream stopped: the level took too long to load"); return; }
                if (now < _resumeAt) return;
                _resumeAt = now + 2f; // try again shortly if the camera isn't ready yet
                if (_cam.Recording || _fx.BoundCamera == null) return; // wait for the new level's headset camera
                try
                {
                    if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
                    if (!_cam.Open) return;
                    var p = _parked;
                    _parked = null;
                    if (_cam.StartStreaming(_cfg, out var err, p)) { Log.Msg("Stream: back on after the level change"); Notify("Stream back on", 1.5f); }
                    else { _parked = p; Log.Msg("Stream: not ready to resume yet (" + (err ?? "camera not ready") + ")"); }
                }
                catch (Exception e) { Log.Warning("Stream resume failed, retrying: " + e.Message); }
                return;
            }
            if (!Streaming) { _wasLive = false; return; }
            var s = _cam.Stream;
            if (s.Failed)
            {
                string why = s.Error ?? "unknown error";
                _cam.StopRecording(); _wasLive = false;
                _ctl.Buzz(_cfg, false, 1f, 0.3f);
                Notify("Stream stopped: " + why, 5f);
                return;
            }
            if (s.Live && !_wasLive) { _wasLive = true; _liveSince = Time.unscaledTime; _ctl.Buzz(_cfg, true, 0.8f, 0.15f); Notify("You're LIVE!", 2.5f); }
        }

        // ---------- Camera timeline (beta) ----------
        private bool _timelineRec;
        internal void TimelineAdd()
        {
            if (!_cam.Open) { Notify("Open the camera first, then aim it at your first shot", 2f); return; }
            if (_cam.TimelinePlaying) { Notify("Wait for the timeline to finish", 1.5f); return; }
            if (_cam.TimelineAdd()) { _ctl.Buzz(_cfg, true, 0.5f, 0.05f); Notify($"Keyframe {_cam.TimelineCount} added" + (_cam.TimelineCount >= 2 ? $" ({_cam.TimelineLength(_cfg):0} s move)" : ": move the camera and add another"), 1.6f); }
        }
        internal void TimelineUndo() { _cam.TimelineUndo(); Notify($"{_cam.TimelineCount} keyframe(s)", 1.2f); }
        internal void TimelineClear() { _cam.TimelineClear(); Notify("Timeline cleared", 1.2f); }
        internal void TimelinePlay(bool record)
        {
            if (_cam.TimelineCount < 2) { Notify("Add at least 2 keyframes first", 2f); return; }
            if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            if (record && !_cam.Recording)
            {
                if (Streaming) { Notify("You're streaming: the move plays on the stream", 2f); record = false; }
                else { ToggleRecording(); _timelineRec = _cam.Recording; }
            }
            if (_cam.TimelinePlay()) { Log.Msg($"Timeline: playing {_cam.TimelineCount} keyframes ({_cam.TimelineLength(_cfg):0.0} s){(_timelineRec ? ", recording" : "")}"); Notify($"Playing timeline ({_cam.TimelineLength(_cfg):0} s)", 1.5f); }
        }
        internal void TimelineStop()
        {
            bool wasRec = _cam.MoveRecording;
            _cam.TimelineStop();
            if (wasRec) Notify($"Move recorded ({_cam.MoveLength:0.0} s). Press Play move to watch it back", 2.5f);
        }

        // Record the camera's real movement while you move it, then play it back exactly.
        internal void MoveRecord()
        {
            if (_cam.MoveRecording) { TimelineStop(); return; }
            if (!_cam.Open) { Notify("Open the camera first", 1.5f); return; }
            if (_cam.MoveRecordStart()) { _ctl.Buzz(_cfg, false, 0.6f, 0.08f); Log.Msg("Timeline: recording camera move"); Notify("Recording the camera's movement... move it around, then press Stop", 2.5f); }
            else Notify("Stop the playback first", 1.5f);
        }
        internal void MovePlay(bool record)
        {
            if (_cam.MoveRecording) TimelineStop();
            if (_cam.MoveLength <= 0f) { Notify("Record a camera move first", 2f); return; }
            if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            if (record && !_cam.Recording)
            {
                if (Streaming) { Notify("You're streaming: the move plays on the stream", 2f); record = false; }
                else { ToggleRecording(); _timelineRec = _cam.Recording; }
            }
            if (_cam.MovePlay()) { Log.Msg($"Timeline: playing recorded move ({_cam.MoveLength:0.0} s){(_timelineRec ? ", recording" : "")}"); Notify($"Playing your move ({_cam.MoveLength:0} s)", 1.5f); }
        }

        // The timeline page's zoom slider: changes the field of view the current camera mode uses, live.
        internal float LiveFov => _cam.UsesBodyFov ? _cfg.ChestFov : _cfg.Fov;
        internal void SetLiveFov(float v)
        {
            if (_cam.UsesBodyFov) _cfg.ChestFov = v; else _cfg.Fov = v;
            _cfg.SaveMenuSettings();
        }

        // ---------- Timeline mod support (beta) ----------
        private bool _tlFilming, _tlRec;
        internal string TimelineModStatus =>
            !TimelineLink.Installed ? "Timeline mod not installed" :
            _tlFilming ? (_cam.FollowCamera != null ? "Filming Timeline playback through its camera" : "Filming Timeline playback (no Timeline camera: QuestShade camera stays put)") :
            TimelineLink.Recording ? "Timeline is recording" : "Timeline found: press Play in Timeline and QuestShade films it";

        // ---------- Timeline (beta, needs the Timeline mod) ----------
        // QuestShade steps through Timeline's recording itself (set the time, pose the scene) instead of pressing Timeline's
        // Play, which spawned a fresh copy of the people every time. Show people, the time slider, Play preview and Render
        // all work that way.
        private bool _tlManual, _tlRender, _tlRenderRec;
        private float _tlT;

        private bool TimelineReady()
        {
            if (!TimelineLink.Installed) { Notify("Install the Timeline mod to use this", 2.5f); return false; }
            if (!TimelineLink.CanDirect) { Notify("This Timeline version isn't supported yet (see log)", 2.5f); return false; }
            return true;
        }

        internal void ShowTimelinePeople()
        {
            if (!TimelineReady()) return;
            _tlManual = false;
            TimelineLink.Scrub(0.01f);
            Notify("Showing the recorded people (at the start)", 1.5f);
        }

        internal void ScrubTo(float t)
        {
            if (BoneMenuUi.SettingTimeSlider || !TimelineReady()) return;
            _tlManual = false;
            TimelineLink.Scrub(Mathf.Max(0.01f, t));
        }
        internal void ScrubBy(float dt) { if (TimelineReady()) { _tlManual = false; TimelineLink.Scrub(Mathf.Max(0.01f, TimelineLink.PlayHead + dt)); } }

        internal void TimelinePlayPause()
        {
            if (_tlManual) { _tlManual = false; Notify("Paused", 1f); return; }
            if (!TimelineReady()) return;
            float len = TimelineLink.Length;
            if (len <= 0f) { Notify("Record something with the Timeline mod first", 2.5f); return; }
            if (!TimelineLink.PeopleShown) TimelineLink.Scrub(0.01f);
            _tlT = TimelineLink.PlayHead >= len - 0.05f ? 0.01f : TimelineLink.PlayHead;
            if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            _tlManual = true; _tlRender = false;
            Notify("Playing the preview", 1f);
        }

        // Render: plays the whole clip from the start with your shots and records it, then stops on its own.
        internal void TimelineRender()
        {
            if (!TimelineReady()) return;
            float len = TimelineLink.Length;
            if (len <= 0f) { Notify("Record something with the Timeline mod first", 2.5f); return; }
            if (_tlRender) { FinishRender(false); return; }
            if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            TimelineLink.Scrub(0.01f);
            _tlT = 0.01f; _tlManual = true; _tlRender = true; _tlRenderRec = false;
            if (!_cam.Recording && !Streaming) { ToggleRecording(); _tlRenderRec = _cam.Recording; }
            Log.Msg($"Timeline: rendering {len:0.0} s with {Director.Shots.Count} shot(s){(_tlRenderRec ? "" : " (not recording)")}");
            Notify($"Rendering your clip ({len:0} s)...", 2f);
        }

        private void FinishRender(bool done)
        {
            _tlManual = false; _tlRender = false;
            if (_tlRenderRec) { _tlRenderRec = false; if (_cam.Recording && !Streaming) ToggleRecording(); }
            Notify(done ? "Clip rendered and saved to your gallery" : "Render stopped", 2.5f);
        }

        internal string TimelineLine
        {
            get
            {
                if (!TimelineLink.Installed) return "Timeline mod: not installed";
                float len = TimelineLink.Length, t = TimelineLink.PlayHead;
                if (len <= 0f) return "Record something with the Timeline mod";
                string state = _tlRender ? "Rendering" : _tlManual ? "Playing" : TimelineLink.PeopleShown ? "Paused" : "Ready";
                return $"{state}  {Director.Clock(t)} / {Director.Clock(len)}  ({Director.Shots.Count} shot{(Director.Shots.Count == 1 ? "" : "s")})";
            }
        }

        internal string ShotList
        {
            get
            {
                if (Director.Shots.Count == 0) return "No shots yet: move the time slider, then add a shot";
                var sb = new System.Text.StringBuilder();
                foreach (var s in Director.Shots) { if (sb.Length > 0) sb.Append(",  "); sb.Append(Director.Clock(s.T)).Append(' ').Append(Director.KindName[(int)s.Kind]); }
                return sb.ToString();
            }
        }

        internal void DeleteShotHere() => Notify(Director.RemoveNear(TimelineLink.PlayHead) ? "Shot deleted" : "No shot at this time", 1.5f);
        internal void ClearShots() { Director.Clear(); Notify("All shots deleted", 1.5f); }

        internal void DirectorAddShot(ShotKind kind)
        {
            if (!TimelineLink.Installed) { Notify("The director needs the Timeline mod", 2f); return; }
            if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            float t = TimelineLink.PlayHead;
            var lt = _cam.LensTransform;
            Vector3 p = lt != null ? lt.position : Vector3.zero;
            Quaternion r = lt != null ? lt.rotation : Quaternion.identity;
            if (_cam.VerticalNow) r *= Quaternion.Euler(0f, 0f, -90f); // stored upright; vertical video adds its roll back
            if (kind == ShotKind.FreeCam && lt == null) { Notify("Open the camera and aim it first", 2f); return; }
            Director.Add(kind, t, p, r, _cam.LensFov);
            _ctl.Buzz(_cfg, true, 0.5f, 0.05f);
            Log.Msg($"Director: {Director.KindName[(int)kind]} shot at {Director.Clock(t)}");
            Notify($"{Director.KindName[(int)kind]} shot at {Director.Clock(t)}", 1.5f);
        }

        private bool DirectorLens(out Vector3 p, out Quaternion r, out float fov)
            => Director.Eval(TimelineLink.PlayHead, _cfg, TimelineLink.PlaybackCamera(), out p, out r, out fov);

        private void DirectorTick()
        {
            var head = _fx.BoundCamera;
            if (head != null) Director.LearnHeadFix(head.transform, _dyingNow || _cam.RagdollLocked);
            // Play preview / Render: step through the recording in real time.
            if (_tlManual)
            {
                float len = TimelineLink.Length;
                _tlT += Time.unscaledDeltaTime;
                if (len <= 0f || _tlT >= len) { _tlT = Mathf.Max(0.01f, len); TimelineLink.Scrub(_tlT); if (_tlRender) FinishRender(true); else { _tlManual = false; Notify("End of the clip", 1.2f); } }
                else TimelineLink.Scrub(_tlT);
            }
            // Shots drive the camera while the clip plays (preview, render, or the Timeline mod's own Play), or while
            // scrubbing with "Preview shots while scrubbing" on.
            bool drive = Director.Shots.Count > 0 && TimelineLink.Installed && TimelineLink.PeopleShown
                         && (_tlManual || (TimelineLink.Playing && !TimelineLink.Paused) || _cfg.DirectorPreview);
            _cam.ExternalLens = drive ? DirectorLens : null;
        }

        private void TimelineModTick()
        {
            if (BetaOn) DirectorTick();
            // Only the Timeline mod's own, unpaused playback (Show people / the slider leave it paused; our preview and render step it ourselves).
            bool play = BetaOn && _cfg.TimelineFilm && !_tlManual && TimelineLink.Installed && TimelineLink.Playing && !TimelineLink.Paused;
            if (play && !_tlFilming)
            {
                _tlFilming = true;
                if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
                _cam.FollowCamera = TimelineLink.PlaybackCamera();
                if (_cfg.TimelineAutoRecord && !_cam.Recording && !Streaming) { ToggleRecording(); _tlRec = _cam.Recording; }
                Log.Msg($"Timeline support: playback started, {(_cam.FollowCamera != null ? "following Timeline's camera" : "no Timeline camera found")}{(_tlRec ? ", recording" : "")}");
                Notify(_tlRec ? "Timeline playing: QuestShade is filming it" : "Timeline playing", 1.8f);
            }
            else if (play && _cam.FollowCamera == null) _cam.FollowCamera = TimelineLink.PlaybackCamera();
            else if (!play && _tlFilming)
            {
                _tlFilming = false;
                _cam.FollowCamera = null;
                if (_tlRec) { _tlRec = false; if (_cam.Recording && !Streaming) ToggleRecording(); }
                Log.Msg("Timeline support: playback ended");
            }
        }

        private float _betaMarkAt;
        private void TimelineTick()
        {
            TimelineModTick();
            // "Play + record": stop the recording when the move ends.
            if (_timelineRec && !_cam.AnyPlayback) { _timelineRec = false; if (_cam.Recording && !Streaming) ToggleRecording(); }
            // Keep the beta menu's highlights (active slow-mo / camera style, record button) in step, also after gestures.
            if (BetaOn && _boneLib && Time.unscaledTime >= _betaMarkAt)
            {
                _betaMarkAt = Time.unscaledTime + 0.25f;
                try { BoneMenuUi.MarkBeta(SlowMo.Level, _cam.Mode, _cam.MoveRecording, _cam.MoveRecordedSoFar); } catch { }
                try { BoneMenuUi.MarkTimeline(TimelineLink.Installed ? TimelineLine : "Timeline mod: not installed", _tlManual, _tlRender, TimelineLink.Installed ? TimelineLink.PlayHead : 0f); } catch { }
            }
        }

        internal void SetSlowMo(int level) { SlowMo.Set(level); Notify("Slow-mo: " + SlowMo.Names[SlowMo.Level], 1.2f); _betaMarkAt = 0f; }

        internal void ToggleRecording()
        {
            if (Streaming) { Notify("You're streaming: stop it in BoneMenu > Camera > Livestream", 2.5f); return; }
            if (_cam.Recording)
            {
                var file = _cam.StopRecording();
                _ctl.Buzz(_cfg, false, 0.6f, 0.1f);
                Notify(file != null ? "Saving video..." : "Recording stopped (save failed, see log)", 2f);
                return;
            }
            Log.Msg("Recording requested");
            if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            if (_cam.StartRecording(_cfg, out var err))
            {
                _recStart = Time.unscaledTime;
                _ctl.Buzz(_cfg, false, 0.8f, 0.1f);
                if (AudioCapture.Notice != null) { Notify(AudioCapture.Notice, 4f); AudioCapture.Notice = null; return; }
                Notify("Recording... (hold trigger again to stop)", 1.5f);
            }
            else Notify(err, 2f);
        }

        internal void FlipCamera()
        {
            if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            var m = _cam.Mode == CamMode.Selfie ? CamMode.Hand : CamMode.Selfie;
            _cam.SetMode(m);
            _cfg.DefaultMode = m;
            Notify(m == CamMode.Selfie ? "Camera flipped: facing you" : "Camera flipped: facing forward", 1f);
        }

        internal void SetHoverPreview(bool on)
        {
            _cfg.HoverPreview = on;
            _cam.SetHoverPreview(on);
        }

        private bool _trigWas, _trigFired; private float _trigStart;
        private float _recStart; private bool _hintShown; private int _lastHand, _lastBar;
        private void CameraTrigger(float now)
        {
            int hand = _cam.HoldingHand;
            string extra = null;
            if (_timerAt >= 0f) extra = $"<color=#ffa030>PHOTO IN {Mathf.CeilToInt(_timerAt - now)}</color>  (tap = cancel)";
            if (hand != 0 && _lastHand == 0 && !_hintShown)
            {
                _hintShown = true;
                Notify("Camera: TAP trigger = photo, HOLD trigger = start / stop recording", 4f);
            }
            _lastHand = hand;
            if (hand == 0)
            {
                _trigWas = false;
                if (!_ctl.Trigger(true) && !_ctl.Trigger(false)) _trigFired = false;
                _cam.LabelExtra = extra; return;
            }
            bool down = _ctl.Trigger(hand == 1);
            if (down && !_trigWas) { _trigStart = now; _lastBar = 0; }
            float held = now - _trigStart;
            if (down && !_trigFired && held >= 0.15f)
            {
                int n = Mathf.Clamp((int)((held - 0.15f) / (HoldToRecord - 0.15f) * 8f), 0, 8);
                string bar = new string('\u25A0', n) + new string('\u25A1', 8 - n);
                extra = $"<color=#ff5050>{(_cam.Recording ? "keep holding = STOP" : "keep holding = RECORD")} {bar}</color>";
                if (n != _lastBar && n > 0) _ctl.Buzz(_cfg, hand == 1, 0.15f, 0.02f);
                _lastBar = n;
            }
            if (down && !_trigFired && held >= HoldToRecord)
            {
                _trigFired = true;
                if (_cam.Recording && now - _recStart < MinClip) Notify("Recording just started - hold again to stop", 1.2f);
                else ToggleRecording();
            }
            if (!down && _trigWas && !_trigFired) TakePhoto();
            if (!down) _trigFired = false;
            _trigWas = down;
            _cam.LabelExtra = extra;
        }
        private const float HoldToRecord = 0.7f;
        private const float MinClip = 1.5f;

        internal void SetCameraLabel(bool on) { _cfg.CameraLabel = on; _cam.SetLabel(on); }

        internal void SetPreview(bool on)
        {
            _cfg.PreviewEnabled = on;
            Notify(on ? "Preview on" : "Preview off (camera still shoots)", 1f);
        }

        private float _hurt, _hurtNext; private float _hurtTarget; private IntPtr _overlayPtr; private bool _hurtLogged, _hurtErrLogged;
        // PCVR-style headset effects: a red pulse that grows as health drops, a quick flash on every hit, colour drains on death.
        private float _headHurt, _headHurtTarget, _death, _deathTarget, _hitFlash, _lastHp = 1f;
        private bool _dyingNow; // for the camera: lock to the body while dying
        private void TickHurt()
        {
            float now = Time.unscaledTime;
            if (now >= _hurtNext)
            {
                _hurtNext = now + 0.1f;
                _hurtTarget = 0f; _headHurtTarget = 0f; _deathTarget = 0f;
                try
                {
                    if (BoneMenuUi.PlayerHurt(out float hp, out bool dying, out GameObject overlay))
                    {
                        if (_cfg.HurtEffect) _hurtTarget = dying ? 0.8f : Mathf.Clamp01((0.55f - hp) / 0.5f) * 0.8f;
                        if (_cfg.HeadsetHurt && BetaOn)
                        {
                            _headHurtTarget = dying ? 0.9f : Mathf.Clamp01((0.6f - hp) / 0.5f) * 0.7f;
                            if (!dying && hp < _lastHp - 0.02f) _hitFlash = Mathf.Min(1f, _hitFlash + 0.35f + (_lastHp - hp) * 2f);
                        }
                        if (dying && BetaOn && (_cfg.HeadsetHurt || _cfg.HurtEffect)) _deathTarget = 1f;
                        _lastHp = hp; _dyingNow = dying;
                        if (!_hurtLogged && _hurtTarget > 0.2f) { _hurtLogged = true; Log.Msg($"Hurt effect on (health {hp * 100f:0}%, dying {dying})"); }
                        int layer = _cam.LensHiddenLayer;
                        if (overlay != null && layer >= 0 && (overlay.Pointer != _overlayPtr || overlay.layer != layer))
                        {
                            _overlayPtr = overlay.Pointer;
                            foreach (var t in overlay.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                            Log.Msg("Hurt overlay kept off the camera");
                        }
                    }
                }
                catch (Exception e) { if (!_hurtErrLogged) { _hurtErrLogged = true; Log.Warning("Hurt effect: can't read health: " + e.Message); } }
            }
            float dt = Time.unscaledDeltaTime;
            _hurt += (_hurtTarget - _hurt) * (1f - Mathf.Exp(-(_hurtTarget > _hurt ? 6f : 1.5f) * dt));
            _headHurt += (_headHurtTarget - _headHurt) * (1f - Mathf.Exp(-(_headHurtTarget > _headHurt ? 5f : 1.2f) * dt));
            _death += (_deathTarget - _death) * (1f - Mathf.Exp(-(_deathTarget > _death ? 1.4f : 3f) * dt)); // drains slowly, comes back quicker
            _hitFlash *= Mathf.Exp(-3.5f * dt);
            float capDeath = _cfg.HurtEffect && BetaOn ? _death : 0f;
            _fx.SetHurt(_hurt < 0.01f ? 0f : _hurt, capDeath < 0.01f ? 0f : capDeath);
            float head = Mathf.Clamp01(_headHurt + _hitFlash * 0.6f), headDeath = _cfg.HeadsetHurt && BetaOn ? _death : 0f;
            _fx.SetHeadsetHurt(head < 0.01f ? 0f : head, headDeath < 0.01f ? 0f : headDeath);
        }

        internal void SetMic(bool on)
        {
            _cfg.RecordMic = on; _cfg.SaveMenuSettings();
            Notify(on ? "Mic ON: your voice is in your videos" : "Mic OFF: videos have game sound only", 2f);
        }

        internal void ToggleHidden()
        {
            if (!_cam.Open) { Notify("Open the camera first", 1f); return; }
            _cam.SetHidden(!_cam.Hidden);
            Notify(_cam.Hidden ? (_cam.Recording ? "Camera hidden (still recording)" : "Camera hidden") : "Camera visible", 1f);
        }

        private string _chatYt, _chatTw;
        private void TickChat()
        {
            try
            {
                bool want = BetaOn && _cfg.ChatOnWrist;
                string yt = want ? _cfg.ChatYouTube ?? "" : "", tw = want ? _cfg.ChatTwitch ?? "" : "";
                if (yt != _chatYt || tw != _chatTw)
                {
                    _chatYt = yt; _chatTw = tw;
                    if (yt.Length == 0 && tw.Length == 0) ChatReader.Stop(); else ChatReader.Restart(tw, yt);
                }
                if (WristChat.Hand == null) { WristChat.Hand = BoneMenuUi.HandTransform; WristChat.HiddenLayer = () => _cam.LensHiddenLayer; }
                WristChat.Tick(_cfg, _fx.BoundCamera, want);
            }
            catch { }
        }

        internal void RefreshCamera() => _cam.Refresh(_cfg, _fx.BoundCamera);
        internal void ReapplyEffects() => _fx.MarkDirty();

        internal void SetPhysicalCamera(bool on)
        {
            _cfg.PhysicalCamera = on;
            if (_cam.Open && !_cam.Recording) { _cam.Toggle(_cfg, _fx.BoundCamera); _cam.Toggle(_cfg, _fx.BoundCamera); }
            Notify(on ? "Physical camera: grab it like any item" : "Floating camera", 1.2f);
        }

        private static string Pretty(CamMode m) => m switch
        {
            CamMode.ThirdPerson => "Third person", CamMode.Chest => "Chest (bodycam)", CamMode.FirstPerson => "First person (head cam)", CamMode.Helmet => "Helmet cam", CamMode.PoliceCam => "Police bodycam", CamMode.HelmetView => "Helmet cam (shows your helmet)", CamMode.Shoulder => "Shoulder bodycam", _ => m.ToString()
        };

        private void RefreshPresetMenu()
        {
            if (!_boneLib) return;
            try { BoneMenuUi.RefreshPresets(_cfg); } catch { }
        }

        private void FusionTick()
        {
            FusionLink.Tick();
            if (_cam.Blocked) _cam.Blocked = false;
        }

        private void Notify(string msg, float seconds = 1.6f)
        {
            if (_boneLib && _cfg.ShowToasts)
            {
                try { Log.Msg(msg); BoneMenuUi.Notify(msg, seconds); return; }
                catch {  }
            }
            _toast.Show(_cfg, _fx.BoundCamera, msg, seconds);
        }

        private float _nextSelfTestPoll, _selfTestAt;
        private int _selfTestStep = -1;
        private float _ftSum; private int _ftCount; private float _ftMax;

        private void SelfTest(float now)
        {
            if (_selfTestStep < 0)
            {
                if (now < _nextSelfTestPoll) return;
                _nextSelfTestPoll = now + 3f;
                var f = System.IO.Path.Combine(Config.Root, "selftest.txt");
                if (!System.IO.File.Exists(f) || _fx.BoundCamera == null) return;
                string mode = "";
                try { mode = System.IO.File.ReadAllText(f).Trim().ToLowerInvariant(); System.IO.File.Delete(f); } catch { }
                if (mode.StartsWith("camera")) { _camStep = 0; _selfTestAt = now + 1f; _selfTestStep = 2000; Log.Msg("[SelfTest] camera: photo + 4s recording"); return; }
                if (mode.StartsWith("ui")) { if (_boneLib) { BoneMenuUi.DumpUi(); _uiDumpAt = now + 1f; } return; }
                if (mode.StartsWith("avatar")) { if (_boneLib) BoneMenuUi.DumpAvatar(); return; }
                if (mode.StartsWith("compress:"))
                {
                    var target = _cfg.Presets.FirstOrDefault(p => p.StartsWith(mode.Substring(9).Trim(), StringComparison.OrdinalIgnoreCase) && p.EndsWith(ReShadeImport.Suffix));
                    if (target != null) { ReShadeImport.Answer(target, true); ReloadAll(); Log.Msg("[SelfTest] compressed " + target); }
                    return;
                }
                if (mode.StartsWith("only:"))
                {
                    var names = mode.Substring(5).Split('|').Select(x => x.Trim()).ToList();
                    _shotList = _cfg.Presets.Where(p => names.Any(n => p.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                    _shotQuest = true; _shotStep = 0; _selfTestAt = now + 1f; _selfTestStep = 1000; Log.Msg($"[SelfTest] {_shotList.Count} presets"); return;
                }
                if (mode.StartsWith("imports"))
                {
                    _shotList = _cfg.Presets.Where(p => p.EndsWith(ReShadeImport.Suffix)).ToList();
                    _shotQuest = true; _shotStep = 0; _selfTestAt = now + 1f; _selfTestStep = 1000; Log.Msg($"[SelfTest] {_shotList.Count} imported presets"); return;
                }
                if (mode.StartsWith("dof"))
                {
                    _shotList = new System.Collections.Generic.List<string>();
                    foreach (var n in new[] { "Bodycam", "Night Ops", "Night Vision", "CCTV", "Punchy" }) if (_cfg.Presets.Contains(n)) _shotList.Add(n);
                    _shotQuest = true; _shotStep = 0; _selfTestAt = now + 1f; _selfTestStep = 1000; Log.Msg("[SelfTest] dof photos"); return;
                }
                if (mode.StartsWith("presets")) { _shotQuest = !mode.Contains("pc"); _shotStep = 0; _selfTestAt = now + 1f; _selfTestStep = 1000; Log.Msg("[SelfTest] preset photos (" + (_shotQuest ? "Quest-equivalent" : "PC") + ")"); return; }
                Log.Msg("[SelfTest] start");
                _selfTestStep = 0; _selfTestAt = now + 1f;
                return;
            }
            float dt = Time.unscaledDeltaTime; _ftSum += dt; _ftCount++; if (dt > _ftMax) _ftMax = dt;
            if (now < _selfTestAt) return;
            void Report(string what) { Log.Msg($"[SelfTest] {what}: avg {_ftSum / Math.Max(1, _ftCount) * 1000f:0.0} ms, worst {_ftMax * 1000f:0.0} ms, QuestShade worst {_ownMax:0.00} ms"); _ftSum = 0; _ftCount = 0; _ftMax = 0; _ownMax = 0; }

            if (_selfTestStep >= 2000) { CameraTest(now); return; }
            if (_selfTestStep >= 1000) { PresetShots(now); return; }
            int nPresets = _cfg.Presets.Count;
            int s = _selfTestStep++;
            if (s == 0) { SetEffectsEnabled(false); _selfTestAt = now + 2f; }
            else if (s == 1) { Report("effects OFF"); SetEffectsEnabled(true); _selfTestAt = now + 2f; }
            else if (s < 2 + nPresets) { Report($"preset {_cfg.PresetName}"); StepPreset(1); _selfTestAt = now + 2f; }
            else if (s < 2 + nPresets + 5 * 2)
            {
                int k = s - 2 - nPresets, mode = k / 2;
                if (k % 2 == 0)
                {
                    if (k > 0) Report($"camera {(CamMode)(mode - 1)}");
                    SetCameraMode((CamMode)mode, true); _selfTestAt = now + 2f;
                }
                else { TakePhoto(); _selfTestAt = now + 1.5f; }
            }
            else
            {
                Report($"camera {(CamMode)4}");
                if (_cam.Open) ToggleCamera();
                SetCameraMode(CamMode.Hand, true); if (_cam.Open) ToggleCamera();
                Log.Msg("[SelfTest] done");
                _selfTestStep = -1;
            }
        }

        private int _camStep; private string _testPreset;
        private void CameraTest(float now)
        {
            int s = _camStep++;
            Log.Msg($"[SelfTest] camera step {s}");
            switch (s)
            {
                case 0: _testPreset = _cfg.PresetName; SetCameraMode(CamMode.ThirdPerson, true); SetEffectsEnabled(false); _selfTestAt = now + 1.5f; break;
                case 1:
                    _cam.PhotoTag = "_TestNoFx"; TakePhoto(); _selfTestAt = now + 3f; SetEffectsEnabled(true);
                    if (_cfg.Presets.Contains("Night Ops")) SelectPreset("Night Ops");
                    break;
                case 2: SetCameraMode(CamMode.ThirdPerson, true); _selfTestAt = now + 1.5f; break;
                case 3: _cam.PhotoTag = "_TestThird"; TakePhoto(); _selfTestAt = now + 3f; break;
                case 4: SetCameraMode(CamMode.Chest, true); _selfTestAt = now + 1.5f; break;
                case 5: _cam.PhotoTag = "_TestChest"; TakePhoto(); _selfTestAt = now + 3f; break;
                case 6: ToggleRecording(); _selfTestAt = now + 4f; break;
                case 7: if (_cam.Recording) ToggleRecording(); _selfTestAt = now + 1f; break;
                default:
                    if (_cam.Open) ToggleCamera();
                    _cam.SetMode(_cfg.DefaultMode);
                    if (_testPreset != null) SelectPreset(_testPreset);
                    Log.Msg("[SelfTest] camera done");
                    _selfTestStep = -1;
                    break;
            }
        }

        private int _shotStep; private bool _shotQuest; private string _shotOrigPreset;
        private System.Collections.Generic.List<string> _shotList;
        private void PresetShots(float now)
        {
            var list = _shotList ?? _cfg.Presets;
            int n = list.Count, s = _shotStep++;
            if (s == 0)
            {
                _shotOrigPreset = _cfg.PresetName;
                _cfg.EmulateQuest = _shotQuest; _fx.MarkDirty();
                SetCameraMode(CamMode.ThirdPerson, true); SetEffectsEnabled(false); _selfTestAt = now + 1.5f;
            }
            else if (s == 1) { _cam.PhotoTag = "_Original"; TakePhoto(); _selfTestAt = now + 1.5f; }
            else if (s < 2 + 2 * n)
            {
                int i = (s - 2) / 2;
                if ((s - 2) % 2 == 0) { SelectPreset(list[i]); _selfTestAt = now + (_shotList != null ? 2.5f : 1.5f); }
                else { _cam.PhotoTag = "_" + _cfg.PresetName.Replace(' ', '_'); TakePhoto(); _selfTestAt = now + 1.5f; }
            }
            else
            {
                _cfg.EmulateQuest = false; SelectPreset(_shotOrigPreset);
                if (_cam.Open) ToggleCamera();
                _cam.SetMode(CamMode.Hand); _cfg.DefaultMode = CamMode.Hand;
                Log.Msg("[SelfTest] preset photos done"); _shotList = null;
                _selfTestStep = -1;
            }
        }

        private void HandleInput()
        {
            _cam.MicOn = _cfg.RecordMic;
            _cam.ControlsHint = _cfg.ControllerShortcuts ? "Menu: QuestShade > Camera  (mode, flip, timer, vertical)" : "Trigger is off: take photos from QuestShade > Camera > Shoot";
            if (_handWanted != 0) TickCameraToHand(Time.unscaledTime);
            if (!_cam.Open) return;
            if (!_cfg.ControllerShortcuts) { _cam.LabelExtra = null; return; }
            CameraTrigger(Time.unscaledTime);
        }

        private int _handWanted;
        private float _handUntil;

        internal void CameraToHand(int hand)
        {
            if (!_cam.Open) ToggleCamera();
            if (!_cam.Open) return;
            _handWanted = hand; _handUntil = Time.unscaledTime + 6f;
            TickCameraToHand(Time.unscaledTime);
        }

        private void TickCameraToHand(float now)
        {
            if (now > _handUntil || !_cam.Open) { if (_handWanted != 0 && _cam.Open) Notify("Couldn't put the camera in your hand (is that hand holding something?)", 2.5f); _handWanted = 0; return; }
            if (!_cam.BringToHand(_handWanted)) return;
            Notify($"Camera in your {(_handWanted == 1 ? "left" : "right")} hand", 1.2f);
            _handWanted = 0;
        }

    }
}
