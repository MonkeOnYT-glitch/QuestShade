using System;
using System.Linq;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(QuestShade.Mod), "QuestShade", "1.5.2", "QuestShade")]
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
            try
            {
                Config.InitPaths();
                _cfg.LoadAll();
                if (_cfg._audioMigrated) { _cfg._audioMigrated = false; _cfg.SaveMenuSettings(); Log.Msg("Game audio + mic in recordings: on (new default)"); }
                Sharing.Init(_cfg);
                PhotoSaver.ResolveFolder(_cfg.SaveFolder);
                _cam.SetMode(_cfg.DefaultMode);
                _cam.Fx = _fx;
                _cam.SetHoverPreview(_cfg.HoverPreview);
                _cam.Feedback += (msg, left) => { _ctl.Buzz(_cfg, left, 0.5f, 0.05f); if (msg != null) Notify(msg, 1f); };
                _ready = true;
#if QUEST
                _startNotice = VideoRecorder.RecoverInterrupted();
#endif
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
                try { BoneMenuUi.Build(this, _cfg); _cam.ChestProvider = BoneMenuUi.Chest; Focus.PlayerRoot = BoneMenuUi.PlayerRoot; _cam.HideHead = BoneMenuUi.HideHead; _cam.RestoreHead = BoneMenuUi.RestoreHead; _cam.UiLayerMask = BoneMenuUi.UiLayerMask; _cam.ActorPose = BoneMenuUi.ActorPose; _cam.HideHeadOf = BoneMenuUi.HideHeadOf; _cam.HeadToLayer = BoneMenuUi.HeadToLayer; _cam.HeadRestore = BoneMenuUi.RestoreHeadLayer; _cam.OwnBodyToLayer = BoneMenuUi.OwnBodyToLayer;
                    _cam.SpawnCrate = BoneMenuUi.SpawnCrate; _cam.IsHeldBy = BoneMenuUi.IsHeld; _cam.AttachToHand = BoneMenuUi.Attach; _cam.PlayerColliders = BoneMenuUi.PlayerColliders; _cam.HandTransform = BoneMenuUi.HandTransform; _cam.HandBusy = BoneMenuUi.HandBusy; Flashlights.Held = BoneMenuUi.HeldObject; Flashlights.ItemRoot = BoneMenuUi.ItemRoot; Log.Msg("BoneMenu page added"); }
                catch (Exception e) { _boneLib = false; Log.Warning("BoneLib found but menu failed (gestures still work): " + e.Message); }
            }
            else Log.Msg("BoneLib not found - using controller gestures only");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!_ready) return;
            if (_cam.Recording) { var f = _cam.StopRecording(); if (f != null) Log.Msg("Recording stopped by level change: " + f); }
            TimelineLink.CancelPreview();
            if (_boneLib) { try { BoneMenuUi.OutlineSelected(false); } catch { } }
            _cam.OnSceneLoaded();
            Flashlights.Reset();
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
                if (now >= _nextCamCheck) { _nextCamCheck = now + 1f; _fx.EnsureCamera(_cfg); }
                _fx.Tick(_cfg);
                Flashlights.Tick(_cfg.FlashlightBoost);
                if (_boneLib) TickHurt();
                _fx.TickHeadsetFocus(Time.unscaledDeltaTime);

                _ctl.Update(_cfg, now);
                HandleInput();
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
                TimelineTick();
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
        }

        private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        private float _nextCalibAt; private int _calibTries; private float _uiDumpAt = -1f;
        private double _ownMs, _ownMax;

        public override void OnLateUpdate()
        {
            if (!_ready) return;
            _sw.Restart();
            try
            {
                var head = _fx.BoundCamera;
                _fx.PhotoCamera = _cam.Open ? _cam.LensCamera : null;
                _cam.LateTick(_cfg, head, Time.unscaledDeltaTime, _ctl.GripL, _ctl.GripR);
                if (!_cam.Open)
                {
                    _cam.PrewarmTick(_cfg, head);
                    float now = Time.unscaledTime;
                    if (_fx.CalibrationWanted && !_cam.Prewarming && now > _nextCalibAt && _calibTries < 8)
                    { _calibTries++; _nextCalibAt = now + 5f; _cam.SchedulePrewarm(0f); }
                }
                _toast.LateTick(head);
                if (_boneLib)
                {
                    BoneMenuUi.Hmd = head != null ? head.transform : null;
                    Vector3 top = default; int which = 0, count = 0;
                    bool show = _cfg.TimelineFollow && TimelineLink.Present && BoneMenuUi.ActorMarker(_cam.ActorIndex, out top, out which, out count);
                    _marker.Tick(show, top, head, which, count);
                    BoneMenuUi.OutlineSelected(show && !TimelineLink.PlayingTake);
                }
                _cam.ToastObject = _toast.Object;
            }
            catch (Exception e) { _cam.Close(); Guard(e); }
            _ownMs += _sw.Elapsed.TotalMilliseconds;
            if (_ownMs > _ownMax) _ownMax = _ownMs;
            SpikeWatch();
            Perf.Tick(Time.unscaledDeltaTime);
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
            Notify(copyCurrent ? $"Copied to \"{name}\": edit it in Effects > Edit preset" : $"New preset \"{name}\": edit it in Effects > Edit preset", 3f);
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
                Notify($"\"{name}\" is public! Find it in Effects > Preset browser", 3f);
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
            Notify($"\"{name}\" is yours: edit, rename and share it in Effects > Edit preset", 3f);
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
            if (_cam.Blocked && !_cam.Open) { Notify("Camera's off in Fusion lobbies (Camera > Settings to change)", 2f); return; }
            _cam.Toggle(_cfg, _fx.BoundCamera);
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

        internal void ToggleRecording()
        {
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
#if QUEST
                if (AudioCapture.Notice != null) { Notify(AudioCapture.Notice, 4f); AudioCapture.Notice = null; return; }
#endif
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

        private string _hint; private int _hintKey = -1;
        private string ShortcutsHint()
        {
            int key = (_cfg.LeftHanded ? 1 << 20 : 0) | (int)_cfg.CameraMode.Gesture | (int)_cfg.TakePhoto.Gesture << 4 | (int)_cfg.HideCamera.Gesture << 8 | (int)_cfg.Record.Gesture << 12;
            if (key == _hintKey) return _hint;
            _hintKey = key;
            var parts = new System.Collections.Generic.List<string>();
            void Add(Binding b, string what) { if (b.Gesture != Gesture.None) parts.Add(GestureName(_ctl.Shown(b.Gesture)) + ": " + what); }
            Add(_cfg.CameraMode, "mode"); Add(_cfg.TakePhoto, "photo"); Add(_cfg.Record, "record"); Add(_cfg.HideCamera, "hide");
            return _hint = string.Join("   ", parts);
        }
        private static string GestureName(Gesture g) => g switch
        {
            Gesture.LeftStickHold => "hold L stick", Gesture.RightStickHold => "hold R stick",
            Gesture.LeftStickDoubleTap => "2x L stick", Gesture.RightStickDoubleTap => "2x R stick",
            Gesture.BothSticks => "both sticks", Gesture.LeftGripStickHold => "L grip + L stick", Gesture.RightGripStickHold => "R grip + R stick",
            _ => g.ToString()
        };

        internal void SetCameraLabel(bool on) { _cfg.CameraLabel = on; _cam.SetLabel(on); }

        internal void SetPreview(bool on)
        {
            _cfg.PreviewEnabled = on;
            Notify(on ? "Preview on" : "Preview off (camera still shoots)", 1f);
        }

        private float _hurt, _hurtNext; private float _hurtTarget; private IntPtr _overlayPtr;
        private void TickHurt()
        {
            float now = Time.unscaledTime;
            if (now >= _hurtNext)
            {
                _hurtNext = now + 0.1f;
                _hurtTarget = 0f;
                try
                {
                    if (BoneMenuUi.PlayerHurt(out float hp, out bool dying, out GameObject overlay))
                    {
                        if (_cfg.HurtEffect) _hurtTarget = dying ? 1f : Mathf.Clamp01((0.75f - hp) / 0.6f);
                        int layer = _cam.LensHiddenLayer;
                        if (overlay != null && layer >= 0 && (overlay.Pointer != _overlayPtr || overlay.layer != layer))
                        {
                            _overlayPtr = overlay.Pointer;
                            overlay.layer = layer;
                            Log.Msg("Hurt overlay kept off the camera");
                        }
                    }
                }
                catch { }
            }
            float dt = Time.unscaledDeltaTime;
            _hurt += (_hurtTarget - _hurt) * (1f - Mathf.Exp(-(_hurtTarget > _hurt ? 6f : 1.5f) * dt));
            _fx.SetHurt(_hurt < 0.01f ? 0f : _hurt);
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
            CamMode.ThirdPerson => "Third person", CamMode.Chest => "Chest (bodycam)", CamMode.FirstPerson => "First person (head cam)", _ => m.ToString()
        };

        private void RefreshPresetMenu()
        {
            if (!_boneLib) return;
            try { BoneMenuUi.RefreshPresets(_cfg); } catch { }
        }

        private bool _tlWasPlaying, _tlWasRecording, _tlStartedRec, _tlWasBusy, _tlActOn;
        private float _tlChangedAt;
        private void TimelineTick()
        {
            _cam.FollowActor = _cfg.TimelineFollow;
            TimelineLink.Tick();
            if (TimelineLink.JustFound && _boneLib) { try { BoneMenuUi.BuildTimeline(this, _cfg); } catch (Exception e) { Log.Warning("Timeline menu failed: " + e.Message); } }
            if (!TimelineLink.Present) return;
            bool rec = TimelineLink.Recording;
            bool playing = TimelineLink.PlayingTake;
            bool busy = rec || TimelineLink.Playing;
            if (busy != _tlWasBusy)
            {
                _tlWasBusy = busy;
                _cam.SuppressBody = busy;
                Log.Msg(busy ? $"Timeline busy (playing {TimelineLink.Playing}, paused {TimelineLink.Paused}, recording {rec}): camera body put away" : "Timeline idle: camera body back");
                if (busy && _cam.HasBody) _cam.ParkBody();
            }
            if (rec != _tlWasRecording) { _tlWasRecording = rec; Log.Msg("Timeline recording: " + rec); }
            float tnow = Time.unscaledTime;
            if (playing != _tlWasPlaying)
            {
                _tlWasPlaying = playing; _tlChangedAt = tnow; _tlActOn = true;
                Log.Msg("Timeline playback: " + (playing ? "started" : "stopped"));
            }
            if (!_tlActOn || tnow - _tlChangedAt < (playing ? 0.15f : 0.75f)) return;
            _tlActOn = false;
            if (!_cfg.TimelineAutoRecord) return;
            if (playing && !_cam.Recording)
            {
                if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
                ToggleRecording();
                _tlStartedRec = _cam.Recording;
            }
            else if (!playing && _tlStartedRec)
            {
                _tlStartedRec = false;
                if (_cam.Recording) ToggleRecording();
            }
        }

        private void FusionTick()
        {
            FusionLink.Tick();
            bool block = _cfg.FusionEffectsOnly && FusionLink.InLobby;
            if (block == _cam.Blocked) return;
            _cam.Blocked = block;
            if (block && _cam.Open) _cam.Close();
            Log.Msg(block ? "Fusion lobby: camera off, effects stay on" : "Left the Fusion lobby: camera available");
            Notify(block ? "Fusion lobby: camera off, effects still on" : "Camera available again", 2f);
        }

        private ReplayView _replayView = ReplayView.Chest;
        private readonly ReplayMarker _marker = new ReplayMarker();

        private static CamMode ModeFor(ReplayView v) => v == ReplayView.OverTheShoulder ? CamMode.ThirdPerson : CamMode.Chest;

        internal void SetReplayView(ReplayView v)
        {
            _replayView = v;
            if (_cfg.TimelineFollow) SetCameraMode(ModeFor(v));
        }

        internal void SetFollowActor(bool on)
        {
            _cfg.TimelineFollow = on;
            if (!on) { Notify("Camera follows you again", 1.2f); return; }
            if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera);
            SetCameraMode(ModeFor(_replayView));
            NextActor(advance: false);
        }

        internal void NextActor() => NextActor(true);

        private void NextActor(bool advance)
        {
            if (!_cfg.TimelineFollow) { _cfg.TimelineFollow = true; if (!_cam.Open) _cam.Toggle(_cfg, _fx.BoundCamera); SetCameraMode(ModeFor(_replayView)); advance = false; }
            int n = _boneLib ? BoneMenuUi.ActorCount() : 0;
            if (n == 0)
            {
                if (TimelineLink.Present && !TimelineLink.Playing && TimelineLink.Preview())
                {
                    BoneMenuUi.RefreshActorsSoon();
                    Notify("Loading the take's people (paused). The marker shows who gets filmed", 2.5f);
                }
                else if (TimelineLink.Previewing) Notify("Still loading the take's people...", 1.5f);
                else Notify("No replay people - record a Timeline take first", 2f);
                return;
            }
            if (advance) _cam.ActorIndex = (_cam.ActorIndex + 1) % n;
            Notify($"Filming person {_cam.ActorIndex % n + 1} of {n}", 1.2f);
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
            _ctl.HandHolding = h => _cam.HoldingHand == h || (_cam.HandBusy != null && _cam.HandBusy(h));
            _cam.MicOn = _cfg.RecordMic;
            _cam.ControlsHint = _cfg.ControllerShortcuts ? ShortcutsHint() : null;
            if (!_cfg.ControllerShortcuts)
            {
                if (_cam.Open) CameraTrigger(Time.unscaledTime);
                return;
            }
            if (_ctl.Fired(_cfg.ToggleEffects) || _ctl.Key(_cfg, KeyCode.F7)) { SetEffectsEnabled(!_cfg.Enabled); _ctl.Buzz(_cfg, false); }
            if (_ctl.Fired(_cfg.NextPreset) || _ctl.Key(_cfg, KeyCode.F8)) { StepPreset(1); _ctl.Buzz(_cfg, false, 0.25f, 0.04f); }
            if (_ctl.Fired(_cfg.PrevPreset) || _ctl.Key(_cfg, KeyCode.F6)) { StepPreset(-1); _ctl.Buzz(_cfg, false, 0.25f, 0.04f); }
            if (_ctl.Fired(_cfg.ToggleCamera) || _ctl.Key(_cfg, KeyCode.F9)) { ToggleCamera(); _ctl.Buzz(_cfg, true); }
            if (!_cam.Open) return;
            CameraTrigger(Time.unscaledTime);
            if (_ctl.Fired(_cfg.CameraMode) || _ctl.Key(_cfg, KeyCode.F11))
            {
                _cam.CycleMode();
                _cfg.DefaultMode = _cam.Mode;
                _ctl.Buzz(_cfg, true, 0.25f, 0.04f);
                Notify($"Camera: {Pretty(_cam.Mode)}", 1f);
            }
            if (_ctl.Fired(_cfg.TakePhoto) || _ctl.Key(_cfg, KeyCode.F10)) TakePhoto();
            if (_ctl.Fired(_cfg.HideCamera) || _ctl.Key(_cfg, KeyCode.F12)) { ToggleHidden(); _ctl.Buzz(_cfg, true, 0.3f, 0.04f); }
            if (_ctl.Fired(_cfg.Record) || _ctl.Key(_cfg, KeyCode.R)) ToggleRecording();
        }
    }
}
