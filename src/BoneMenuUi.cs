using System;
using System.Runtime.CompilerServices;
using BoneLib.BoneMenu;
using BoneLib.Notifications;
using UnityEngine;

namespace QuestShade
{
    internal static class BoneMenuUi
    {
        private static Page _presetPage;
        private static Mod _mod;
        private static readonly Color Accent = new Color(0.55f, 0.85f, 1f);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Build(Mod mod, Config cfg)
        {
            _mod = mod;
            var root = Page.Root.CreatePage("QuestShade", Accent);


            var cam = root.CreatePage("Camera", Color.white);
            cam.CreateFunction("Open / close camera", Color.green, () => mod.ToggleCamera());
            cam.CreateBool("Mic in videos", new Color(1f, 0.6f, 0.6f), cfg.RecordMic, v => mod.SetMic(v));
            var shoot = cam.CreatePage("Shoot", Color.cyan);
            shoot.CreateFunction("Take photo", Color.cyan, () => mod.TakePhoto());
            shoot.CreateFunction("Record / stop", Color.red, () => mod.ToggleRecording());
            shoot.CreateInt("Photo timer (s)", Color.white, cfg.PhotoTimer, 1, 0, 10, v => cfg.PhotoTimer = v);
            var view = cam.CreatePage("Mode + view", Color.white);
            view.CreateEnum("Mode", Color.white, cfg.DefaultMode, v => mod.SetCameraMode((CamMode)v));
            view.CreateFunction("Flip camera", Color.white, () => mod.FlipCamera());
            view.CreateFloat("Field of view", Color.white, cfg.Fov, 5f, 10f, 150f, v => cfg.Fov = v);
            view.CreateFloat("Fisheye", Color.white, cfg.Fisheye, 0.1f, 0f, 1f, v => { cfg.Fisheye = v; cfg.SaveMenuSettings(); });
            view.CreateEnum("Timestamp", Color.white, cfg.Timestamp, v => { cfg.Timestamp = (StampMode)v; cfg.SaveMenuSettings(); });
            var audio = cam.CreatePage("Audio", Color.white);
            audio.CreateBool("Game audio", Color.white, cfg.RecordGameAudio, v => { cfg.RecordGameAudio = v; cfg.SaveMenuSettings(); });
            audio.CreateBool("Microphone", Color.white, cfg.RecordMic, v => mod.SetMic(v));
            var settingsHurt = cam.CreatePage("Hurt effect", Color.white);
            settingsHurt.CreateBool("Blurry + red when hurt", new Color(1f, 0.5f, 0.5f), cfg.HurtEffect, v => { cfg.HurtEffect = v; cfg.SaveMenuSettings(); });

            var help = cam.CreatePage("How to use", Color.yellow);
            void Tip(string line, string more) => help.CreateFunction(line, Color.white, () => Notify(more, 4f));
            Tip("Grab a handle (grip)", "Squeeze grip on either handle, like any item.");
            Tip("Tap trigger = photo", "While holding the camera, tap that hand's trigger to take a photo.");
            Tip("Hold trigger = record / stop", "Hold the trigger until the bar on the camera fills: recording starts. Do it again to stop.");
            Tip("Let go = it floats", "Let go and it stays in the air where you left it.");
            Tip("Saved to the Quest gallery", "Straight into the gallery, no file manager or permissions needed.");

            var more = cam.CreatePage("Settings", Color.gray);
            more.CreateFloat("Grip angle (tilt in hand)", Color.white, cfg.GripAngle, 5f, -90f, 90f, v => cfg.GripAngle = v);
            more.CreateFloat("Smoothing", Color.white, cfg.Smoothing, 0.05f, 0f, 0.9f, v => cfg.Smoothing = v);
            more.CreateBool("Hover preview", Color.white, cfg.HoverPreview, v => mod.SetHoverPreview(v));
            more.CreateBool("Controls text on camera", Color.white, cfg.CameraLabel, v => mod.SetCameraLabel(v));
            more.CreateFunction("Hide / show camera", Color.white, () => mod.ToggleHidden());
            var pv = more.CreatePage("Preview screen", Color.white);
            pv.CreateBool("Preview on", Color.white, cfg.PreviewEnabled, v => mod.SetPreview(v));
            pv.CreateInt("Preview FPS", Color.white, cfg.PreviewFps, 6, 6, 72, v => cfg.PreviewFps = v);
            pv.CreateBool("Preview shadows", Color.white, cfg.PreviewShadows, v => { cfg.PreviewShadows = v; mod.RefreshCamera(); });
            var tp = more.CreatePage("Third person", Color.white);
            tp.CreateFloat("Distance", Color.white, cfg.ThirdPersonDistance, 0.1f, 0.3f, 6f, v => cfg.ThirdPersonDistance = v);
            tp.CreateFloat("Height", Color.white, cfg.ThirdPersonHeight, 0.05f, -1f, 3f, v => cfg.ThirdPersonHeight = v);
            tp.CreateFloat("Shoulder offset", Color.white, cfg.ThirdPersonSide, 0.05f, -2f, 2f, v => cfg.ThirdPersonSide = v);
            var ch = more.CreatePage("Chest (bodycam)", Color.white);
            ch.CreateFloat("Field of view", Color.white, cfg.ChestFov, 5f, 10f, 150f, v => cfg.ChestFov = v);
            ch.CreateFloat("Forward", Color.white, cfg.ChestForward, 0.02f, -0.5f, 1f, v => cfg.ChestForward = v);
            ch.CreateFloat("Height", Color.white, cfg.ChestHeight, 0.02f, -1f, 1f, v => cfg.ChestHeight = v);
            ch.CreateFloat("Side", Color.white, cfg.ChestSide, 0.02f, -1f, 1f, v => cfg.ChestSide = v);
            ch.CreateFloat("Pitch", Color.white, cfg.ChestPitch, 5f, -60f, 60f, v => cfg.ChestPitch = v);

            var fx = root.CreatePage("Effects", Color.white);
            fx.CreateBool("Effects on", Color.green, cfg.Enabled, v => mod.SetEffectsEnabled(v));
            _presetPage = fx.CreatePage("Choose preset", Color.white);
            PresetEditorUi.Build(fx, mod, cfg);
            fx.CreateFunction("Next preset", Color.white, () => mod.StepPreset(1));
            fx.CreateBool("Videos match the preview", Color.white, cfg.MatchPreview, v => cfg.MatchPreview = v);
            var fxMore = fx.CreatePage("Bloom + depth of field", Color.white);
            fxMore.CreateBool("On every preset", Color.green, cfg.BloomDofAll, v => { cfg.BloomDofAll = v; mod.ReapplyEffects(); });
            fxMore.CreateFloat("Bloom strength", Color.white, cfg.BloomStrength, 0.25f, 0f, 4f, v => { cfg.BloomStrength = v; mod.ReapplyEffects(); });
            fxMore.CreateFloat("Blur strength", Color.white, cfg.BlurStrength, 0.25f, 0f, 3f, v => { cfg.BlurStrength = v; mod.ReapplyEffects(); });
            var lit = fx.CreatePage("Lighting + shadows", new Color(1f, 0.85f, 0.5f));
            lit.CreateFunction("What is this?", new Color(1f, 0.75f, 0.35f), () => Notify("Real-time sun shadows from you, NPCs and props, flashlights that light up what they hit, less plastic-looking shine. Costs some performance.", 5f));
            lit.CreateBool("Real-time shadows", Color.white, cfg.Shadows, v => { cfg.Shadows = v; cfg.SaveMenuSettings(); Lighting.Reapply(); });
            lit.CreateFloat("Shadow darkness", Color.white, cfg.ShadowStrength, 0.1f, 0.1f, 1f, v => { cfg.ShadowStrength = v; cfg.SaveMenuSettings(); });
            lit.CreateBool("Real lights (flashlights light things up)", Color.white, cfg.RealLights, v => { cfg.RealLights = v; cfg.SaveMenuSettings(); Lighting.Reapply(); });
            lit.CreateFloat("Less shine", Color.white, cfg.LessShine, 0.1f, 0f, 1f, v => { cfg.LessShine = v; cfg.SaveMenuSettings(); Lighting.Reapply(); });
            lit.CreateBool("Bloom in your headset", Color.white, cfg.HeadsetBloom, v => { cfg.HeadsetBloom = v; cfg.SaveMenuSettings(); mod.ReapplyEffects(); });
            var eye = fx.CreatePage("Eye adaptation, dark maps, flashlights", Color.white);
            eye.CreateFunction("What's this?", new Color(1f, 0.75f, 0.35f), () => Notify("Walk into a dark room: it starts dark, then your eyes adjust and it brightens. Walk back out: it's blinding for a moment, then settles.", 5f));
            eye.CreateBool("Eye adaptation", Color.green, cfg.EyeAdapt, v => { cfg.EyeAdapt = v; mod.ReapplyEffects(); });
            eye.CreateFloat("How much", Color.white, cfg.EyeAdaptStrength, 0.1f, 0f, 1.5f, v => { cfg.EyeAdaptStrength = v; mod.ReapplyEffects(); });
            eye.CreateFloat("How fast", Color.white, cfg.EyeAdaptSpeed, 0.25f, 0.25f, 3f, v => cfg.EyeAdaptSpeed = v);
            eye.CreateFloat("Dark maps: lift shadows", Color.white, cfg.DarkMaps, 0.1f, 0f, 1f, v => { cfg.DarkMaps = v; cfg.SaveMenuSettings(); mod.ReapplyEffects(); });
            eye.CreateFloat("Flashlight boost", Color.white, cfg.FlashlightBoost, 0.25f, 0f, 2f, v => { cfg.FlashlightBoost = v; cfg.SaveMenuSettings(); });
            fxMore.CreateBool("Photos + video", Color.white, cfg.BloomDofCapture, v => { cfg.BloomDofCapture = v; mod.ReapplyEffects(); });
            fxMore.CreateBool("Camera preview", Color.white, cfg.BloomDofPreview, v => { cfg.BloomDofPreview = v; mod.ReapplyEffects(); });
            _importPage = fx.CreatePage("ReShade presets", Color.cyan);
            RefreshPresets(cfg);
            root.CreateFunction("Join the QuestShade Discord", new Color(0.35f, 0.4f, 0.95f), () => mod.OpenDiscord());
            root.CreateFunction("Save settings to ini", Color.yellow, () => { cfg.SaveMenuSettings(); Notify("Settings saved to QuestShade.ini"); });
            _root = root;
        }

        private static Page _root;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SpawnCrate(string barcode, Vector3 pos, Quaternion rot, Action<GameObject> done)
            => BoneLib.HelperMethods.SpawnCrate(barcode, pos, rot, Vector3.one, false, done);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Collider> PlayerColliders()
        {
            var rm = BoneLib.Player.RigManager;
            return rm != null ? rm.GetComponentsInChildren<Collider>(true) : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Transform PlayerRoot() { var rm = BoneLib.Player.RigManager; return rm != null ? rm.transform : null; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int IsHeld(GameObject root)
        {
            if (root == null) return 0;
            var hands = new[] { BoneLib.Player.LeftHand, BoneLib.Player.RightHand };
            for (int i = 0; i < 2; i++)
            {
                try
                {
                    var go = hands[i] != null ? hands[i].m_CurrentAttachedGO : null;
                    if (go != null && (go == root || go.transform.IsChildOf(root.transform))) return i + 1;
                }
                catch { }
            }
            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Attach(int hand, GameObject root)
        {
            var h = hand == 1 ? BoneLib.Player.LeftHand : BoneLib.Player.RightHand;
            if (h == null || root == null || h.m_CurrentAttachedGO != null) return false;
            var grip = root.GetComponentInChildren<Il2CppSLZ.Marrow.Grip>();
            h.AttachObject(grip != null ? grip.gameObject : root);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Transform HandTransform(int hand)
        {
            var h = hand == 1 ? BoneLib.Player.LeftHand : BoneLib.Player.RightHand;
            return h != null ? h.transform : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool HandBusy(int hand)
        {
            var h = hand == 1 ? BoneLib.Player.LeftHand : BoneLib.Player.RightHand;
            return h != null && h.m_CurrentAttachedGO != null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool PlayerHurt(out float health01, out bool dying, out GameObject overlay)
        {
            health01 = 1f; dying = false; overlay = null;
            var rm = BoneLib.Player.RigManager;
            var h = rm != null ? rm.health : null;
            if (h == null) return false;
            health01 = h.max_Health > 0f ? Mathf.Clamp01(h.curr_Health / h.max_Health) : 1f;
            dying = h.deathIsImminent || !h.alive;
            var ph = h.TryCast<Il2CppSLZ.Marrow.Player_Health>();
            if (ph != null) { if (ph.Vignetter != null) overlay = ph.Vignetter; else if (ph.vigRend != null) overlay = ph.vigRend.gameObject; }
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static GameObject HeldObject(int hand)
        {
            var h = hand == 1 ? BoneLib.Player.LeftHand : BoneLib.Player.RightHand;
            return h != null ? h.m_CurrentAttachedGO : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Transform ItemRoot(GameObject go)
        {
            var e = go != null ? go.GetComponentInParent<Il2CppSLZ.Marrow.Interaction.MarrowEntity>() : null;
            return e != null ? e.transform : null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Transform Chest()
        {
            var rig = BoneLib.Player.PhysicsRig;
            return rig != null ? rig.m_chest : null;
        }

        private static readonly System.Collections.Generic.List<(Renderer r, bool was)> _hidden = new System.Collections.Generic.List<(Renderer, bool)>();

        private static int _avId;
        private static readonly System.Collections.Generic.List<Renderer> _headAttached = new System.Collections.Generic.List<Renderer>();
        private static readonly System.Collections.Generic.List<Renderer> _smallSkinned = new System.Collections.Generic.List<Renderer>();
        private static float _avScanAt;
        private static int _headLogFor;
        private static readonly System.Collections.Generic.List<Renderer> _allRenderers = new System.Collections.Generic.List<Renderer>();

        private static void ScanAvatar(Il2CppSLZ.VRMK.Avatar av)
        {
            _avId = av.GetInstanceID(); _avScanAt = Time.unscaledTime + 30f;
            _headAttached.Clear(); _smallSkinned.Clear(); _allRenderers.Clear();
            foreach (var r in av.GetComponentsInChildren<Renderer>(true)) if (r != null) _allRenderers.Add(r);
            var neck = av.neck2;
            if (neck != null) foreach (var r in neck.GetComponentsInChildren<Renderer>(true)) if (r != null) _headAttached.Add(r);
            foreach (var arr in new[] { av.headMeshes, av.hairMeshes }) if (arr != null) foreach (var m in arr) if (m != null && !_headAttached.Contains(m)) _headAttached.Add(m);
            foreach (var r in av.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (r != null && !_headAttached.Contains(r)) _smallSkinned.Add(r);
        }

        private static readonly System.Collections.Generic.List<Il2CppSLZ.VRMK.Avatar> _actors = new System.Collections.Generic.List<Il2CppSLZ.VRMK.Avatar>();
        private static float _actorsAt;
        private static Il2CppSLZ.VRMK.Avatar _actor;

        public static Transform Hmd;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Renderer[] AvatarRenderers()
        {
            var av = BoneLib.Player.Avatar;
            if (av == null) return null;
            var a = av.GetComponentsInChildren<Renderer>(false);
            var list = new Renderer[a.Length];
            for (int i = 0; i < a.Length; i++) list[i] = a[i];
            return list;
        }
        private static readonly System.Collections.Generic.Dictionary<int, Transform> _headBones = new System.Collections.Generic.Dictionary<int, Transform>();

        private static Transform HeadBone(Il2CppSLZ.VRMK.Avatar av)
        {
            int id = av.GetInstanceID();
            if (_headBones.TryGetValue(id, out var t) && t != null) return t;
            t = null;
            if (av.neck2 != null)
                foreach (var c in av.neck2.GetComponentsInChildren<Transform>(true))
                    if (c != av.neck2 && c.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 && c.name.IndexOf("top", StringComparison.OrdinalIgnoreCase) < 0) { t = c; break; }
            t ??= av.neck2;
            _headBones[id] = t;
            return t;
        }

        private static bool SameAvatar(Il2CppSLZ.VRMK.Avatar a, Il2CppSLZ.VRMK.Avatar b)
        {
            string n(string s) => s.Replace("(Clone)", "").Trim();
            return n(a.name) == n(b.name);
        }

        public static void RefreshActorsSoon() => _actorsAt = 0f;

        public static bool ActorHeadTop(out Vector3 pos)
        {
            pos = default;
            if (_actor == null || _actor.neck2 == null) return false;
            pos = _actor.neck2.position + Vector3.up * 0.38f;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ActorCount() { RefreshActors(); return _actors.Count; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ActorMarker(int index, out Vector3 top, out int which, out int count)
        {
            top = default; which = 0;
            RefreshActors();
            count = _actors.Count;
            if (count == 0) return false;
            which = ((index % count) + count) % count;
            var av = _actors[which];
            if (av == null || av.neck2 == null) return false;
            _actor = av;
            top = av.neck2.position + Vector3.up * 0.42f;
            return true;
        }

        private static void RefreshActors()
        {
            if (Time.unscaledTime < _actorsAt) return;
            _actorsAt = Time.unscaledTime + (_actors.Count == 0 ? 0.5f : 2f);
            _actors.Clear();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool ActorPose(int index, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity; _actor = null;
            RefreshActors();
            if (_actors.Count == 0) return false;
            var av = _actors[((index % _actors.Count) + _actors.Count) % _actors.Count];
            if (av == null || av.neck2 == null) return false;
            _actor = av;
            pos = av.neck2.position + Vector3.up * 0.1f;
            Vector3 fwd = av.transform.forward;
            if (av.scapulaLf != null && av.scapulaRt != null)
            {
                var right = av.scapulaRt.position - av.scapulaLf.position;
                var f = Vector3.Cross(right, Vector3.up);
                if (f.sqrMagnitude > 1e-6f) fwd = f;
            }
            fwd = Vector3.ProjectOnPlane(fwd, Vector3.up);
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);
            try
            {
                var mine = BoneLib.Player.Avatar;
                if (Hmd != null && mine != null && SameAvatar(mine, av))
                {
                    var myHead = HeadBone(mine); var theirHead = HeadBone(av);
                    if (myHead != null && theirHead != null)
                    {
                        var inv = Quaternion.Inverse(myHead.rotation);
                        var offPos = inv * (Hmd.position - myHead.position);
                        var offRot = inv * Hmd.rotation;
                        pos = theirHead.position + theirHead.rotation * offPos;
                        rot = theirHead.rotation * offRot;
                    }
                }
            }
            catch { }
            return true;
        }
        private static readonly System.Collections.Generic.List<(GameObject go, int layer)> _layered = new System.Collections.Generic.List<(GameObject, int)>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HeadToLayer(bool actor, int layer)
        {
            RestoreHeadLayer();
            var av = actor ? _actor : BoneLib.Player.Avatar;
            if (av == null) return;
            if (av.GetInstanceID() != _avId || Time.unscaledTime > _avScanAt) ScanAvatar(av);
            void Move(Renderer r) { if (r == null) return; var go = r.gameObject; if (go.layer == layer) return; _layered.Add((go, go.layer)); go.layer = layer; }
            foreach (var r in _headAttached) Move(r);
            var neck = av.neck2;
            if (neck == null) return;
            Vector3 hc = neck.position + neck.up * 0.1f;
            Vector3 wl = av.wristLf != null ? av.wristLf.position : new Vector3(1e6f, 0, 0), wr = av.wristRt != null ? av.wristRt.position : new Vector3(1e6f, 0, 0);
            foreach (var r in _smallSkinned)
            {
                if (r == null) continue;
                var b = r.bounds;
                if (Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) > 0.5f) continue;
                if ((b.center - hc).sqrMagnitude > 0.22f * 0.22f) continue;
                if ((b.center - wl).sqrMagnitude < 0.18f * 0.18f || (b.center - wr).sqrMagnitude < 0.18f * 0.18f) continue;
                Move(r);
            }
            float neckY = neck.position.y - 0.06f;
            bool log = !actor && _headLogFor != _avId;
            if (log) _headLogFor = _avId;
            var sb = log ? new System.Text.StringBuilder() : null;
            foreach (var r in _allRenderers)
            {
                if (r == null) continue;
                var b = r.bounds;
                bool near = (b.center - hc).sqrMagnitude < 0.6f * 0.6f;
                bool hands = (b.center - wl).sqrMagnitude < 0.15f * 0.15f || (b.center - wr).sqrMagnitude < 0.15f * 0.15f;
                bool above = b.center.y > neckY && b.size.y < 0.9f;
                bool toNeck = b.max.y > neckY + 0.02f && b.size.y < 0.7f && b.size.x < 0.7f;
                above |= toNeck;
                if (above && near && !hands) { Move(r); if (sb != null) sb.Append($" +{r.name}"); }
                else if (sb != null && b.max.y > neckY && near)
                    sb.Append($" -{r.name}(centre {(b.center.y - neck.position.y):+0.00;-0.00} m, height {b.size.y:0.00} m)");
            }
            if (sb != null) Mod.Log.Msg($"Chest cam: hiding from the lens on '{av.name}':{sb} (+ hidden, - left visible)");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RestoreHeadLayer()
        {
            foreach (var (go, layer) in _layered) if (go != null) go.layer = layer;
            _layered.Clear();
        }

        private static readonly System.Collections.Generic.List<(GameObject go, int layer)> _bodyLayered = new System.Collections.Generic.List<(GameObject, int)>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void OwnBodyToLayer(bool on, int layer)
        {
            foreach (var (go, l) in _bodyLayered) if (go != null) go.layer = l;
            _bodyLayered.Clear();
            if (!on) return;
            var rm = BoneLib.Player.RigManager;
            if (rm == null) return;
            foreach (var r in rm.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                var go = r.gameObject;
                if (go.layer == layer) continue;
                _bodyLayered.Add((go, go.layer));
                go.layer = layer;
            }
        }

        private static Material _outlineMat;
        private static Il2CppSLZ.VRMK.Avatar _outlined;
        private static readonly System.Collections.Generic.List<GameObject> _shells = new System.Collections.Generic.List<GameObject>();
        private static bool _outlineBroken, _outlineStereo;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void OutlineSelected(bool on)
        {
            if (_outlineBroken) return;
            var want = on ? _actor : null;
            if (want != null && want == _outlined && _shells.Count > 0 && _shells[0] != null)
            {
                bool stereo = ShaderPack.HeadsetActive;
                if (stereo != _outlineStereo) { _outlineStereo = stereo; ShaderPack.SetStereo(_outlineMat, stereo); }
                return;
            }
            foreach (var s in _shells) if (s != null) UnityEngine.Object.Destroy(s);
            _shells.Clear();
            _outlined = null;
            if (want == null || ShaderPack.Outline == null) return;
            try
            {
                if (_outlineMat == null)
                {
                    _outlineMat = new Material(ShaderPack.Outline) { name = "QuestShade_Outline", hideFlags = HideFlags.HideAndDontSave };
                    _outlineMat.SetColor("_Color", new Color(1f, 0.06f, 0.04f, 1f));
                    _outlineMat.SetFloat("_Width", 0.007f);
                }
                _outlineStereo = ShaderPack.HeadsetActive;
                ShaderPack.SetStereo(_outlineMat, _outlineStereo);
                foreach (var src in want.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                {
                    if (src == null || src.sharedMesh == null || !src.enabled) continue;
                    var go = new GameObject("QuestShade_Outline") { layer = 5 };
                    go.transform.SetParent(src.transform, false);
                    var r = go.AddComponent<SkinnedMeshRenderer>();
                    r.sharedMesh = src.sharedMesh;
                    r.rootBone = src.rootBone;
                    r.bones = src.bones;
                    int n = Mathf.Max(1, src.sharedMesh.subMeshCount);
                    var mats = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Material>(n);
                    for (int i = 0; i < n; i++) mats[i] = _outlineMat;
                    r.sharedMaterials = mats;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                    r.updateWhenOffscreen = true;
                    _shells.Add(go);
                }
                _outlined = want;
            }
            catch (Exception e) { _outlineBroken = true; Mod.Log.Warning("Replay outline unavailable: " + e.Message); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HideHead() => HideHeadOf(false);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HideHeadOf(bool actor)
        {
            _hidden.Clear();
            var av = actor ? _actor : BoneLib.Player.Avatar;
            if (av == null) return;
            if (av.GetInstanceID() != _avId || Time.unscaledTime > _avScanAt) ScanAvatar(av);
            void Hide(Renderer r) { if (r == null || !r.enabled) return; _hidden.Add((r, true)); r.enabled = false; }
            foreach (var r in _headAttached) Hide(r);
            var neck = av.neck2;
            if (neck == null) return;
            Vector3 hc = neck.position + neck.up * 0.1f;
            Vector3 wl = av.wristLf != null ? av.wristLf.position : new Vector3(1e6f, 0, 0), wr = av.wristRt != null ? av.wristRt.position : new Vector3(1e6f, 0, 0);
            foreach (var r in _smallSkinned)
            {
                if (r == null || !r.enabled) continue;
                var b = r.bounds;
                if (Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) > 0.5f) continue;
                if ((b.center - hc).sqrMagnitude > 0.22f * 0.22f) continue;
                if ((b.center - wl).sqrMagnitude < 0.18f * 0.18f || (b.center - wr).sqrMagnitude < 0.18f * 0.18f) continue;
                Hide(r);
            }
        }
        private static readonly System.Collections.Generic.List<Canvas> _uiCanvases = new System.Collections.Generic.List<Canvas>();
        private static readonly System.Collections.Generic.List<Renderer> _uiRenderers = new System.Collections.Generic.List<Renderer>();
        private static readonly System.Collections.Generic.List<Behaviour> _uiOff = new System.Collections.Generic.List<Behaviour>();
        private static readonly System.Collections.Generic.List<Renderer> _uiROff = new System.Collections.Generic.List<Renderer>();
        private static float _uiScanAt; private static int _uiRigId;

        private static int _uiMask; private static float _uiMaskAt;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int UiLayerMask()
        {
            if (Time.unscaledTime < _uiMaskAt) return _uiMask;
            _uiMaskAt = Time.unscaledTime + 3f;
            int mask = 0;
            Component rig = BoneLib.Player.UIRig;
            if (rig != null)
                foreach (var t in rig.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null) continue;
                    int l = t.gameObject.layer; if (l == 0) continue;
                    string n = LayerMask.LayerToName(l) ?? "";
                    if (l == 5 || n.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0)
                        mask |= 1 << l;
                }
            mask |= 1 << UiLayer;
            _uiMask = mask;
            return mask;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void HideUi()
        {
            _uiOff.Clear(); _uiROff.Clear();
            Component rig = BoneLib.Player.UIRig;
            if (rig == null) return;
            if (rig.GetInstanceID() != _uiRigId || Time.unscaledTime > _uiScanAt)
            {
                _uiRigId = rig.GetInstanceID(); _uiScanAt = Time.unscaledTime + 1f;
                _uiCanvases.Clear(); _uiRenderers.Clear();
                foreach (var cv in rig.GetComponentsInChildren<Canvas>(false)) if (cv != null) _uiCanvases.Add(cv);
                foreach (var r in rig.GetComponentsInChildren<Renderer>(false)) if (r != null) _uiRenderers.Add(r);
            }
            foreach (var cv in _uiCanvases) if (cv != null && cv.enabled) { cv.enabled = false; _uiOff.Add(cv); }
            foreach (var r in _uiRenderers) if (r != null && r.enabled) { r.enabled = false; _uiROff.Add(r); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RestoreUi()
        {
            foreach (var b in _uiOff) if (b != null) b.enabled = true;
            foreach (var r in _uiROff) if (r != null) r.enabled = true;
            _uiOff.Clear(); _uiROff.Clear();
        }

        private static readonly System.Collections.Generic.List<(Canvas c, GameObject go, Transform t, string name)> _menuCanvases = new System.Collections.Generic.List<(Canvas, GameObject, Transform, string)>();
        private static float _menuCheckAt; private static bool _menuOpen;
        private static float _menuScanAt; private static int _menuRigId;
        public static string LastMenu;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool MenuInView(Transform head)
        {
            if (head == null) return false;
            if (Time.unscaledTime < _menuCheckAt) return _menuOpen;
            _menuCheckAt = Time.unscaledTime + 0.1f;
            _menuOpen = MenuInViewNow(head);
            return _menuOpen;
        }

        private static bool MenuInViewNow(Transform head)
        {
            Component rig = BoneLib.Player.UIRig;
            if (rig == null) return false;
            int id = rig.GetInstanceID();
            bool newRig = id != _menuRigId;
            if (newRig || (_menuScanAt > 0f && Time.unscaledTime > _menuScanAt))
            {
                _menuScanAt = newRig ? Time.unscaledTime + 5f : -1f;
                _menuRigId = id;
                _menuCanvases.Clear();
                foreach (var cv in rig.GetComponentsInChildren<Canvas>(true))
                    if (cv != null && cv.isRootCanvas && cv.renderMode == RenderMode.WorldSpace) _menuCanvases.Add((cv, cv.gameObject, cv.transform, cv.name));
            }
            var hp = head.position; var hf = head.forward;
            foreach (var (cv, go, tr, name) in _menuCanvases)
            {
                if (cv == null || go == null || !go.activeInHierarchy || !cv.enabled) continue;
                var to = tr.position - hp;
                float d = to.magnitude;
                if (d > 1.5f || d < 0.05f) continue;
                if (Vector3.Dot(hf, to / d) > 0.55f) { LastMenu = name; return true; }
            }
            return false;
        }

        private const int UiLayer = 5;
        private static float _menuLayerAt;
        private static int _menuLayerRig, _menuLayerPasses;
        private static float _menuLayerPoll;
        private static bool _menuLayerLogged;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void KeepMenusOffCamera()
        {
            if (Time.unscaledTime < _menuLayerPoll) return;
            _menuLayerPoll = Time.unscaledTime + 1f;
            var rig = BoneLib.Player.UIRig;
            int id = rig != null ? rig.GetInstanceID() : 0;
            if (id != _menuLayerRig) { _menuLayerRig = id; _menuLayerPasses = 0; _menuLayerAt = 0f; }
            if (_menuLayerPasses >= 3 || Time.unscaledTime < _menuLayerAt) return;
            _menuLayerAt = Time.unscaledTime + (_menuLayerPasses == 0 ? 5f : 15f);
            _menuLayerPasses++;
            int moved = 0;
            if (rig != null && rig.popUpMenu != null && rig.popUpMenu.preferencesPanelView != null)
                moved += ToUiLayer(rig.popUpMenu.preferencesPanelView.transform);
            var tut = Il2CppSLZ.Bonelab.TutorialRig.Instance;
            if (tut != null && tut.headTitles != null) moved += ToUiLayer(tut.headTitles.transform);
            if (moved > 0 && !_menuLayerLogged) { _menuLayerLogged = true; Mod.Log.Msg($"Menus: moved {moved} BoneMenu / notification parts onto the UI layer (the camera never films them now)"); }
        }

        private static int ToUiLayer(Transform root)
        {
            int n = 0;
            foreach (var c in root.GetComponentsInChildren<Canvas>(true))
                if (c != null && c.gameObject.layer == 0) { c.gameObject.layer = UiLayer; n++; }
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                if (r != null && r.gameObject.layer == 0) { r.gameObject.layer = UiLayer; n++; }
            return n;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void DumpUi()
        {
            Notify("QuestShade UI test", 5f);
            foreach (var cv in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            {
                if (cv == null || !cv.isRootCanvas) continue;
                var t = cv.transform; string path = t.name; var p = t.parent; int d = 0;
                while (p != null && d++ < 6) { path = p.name + "/" + path; p = p.parent; }
                Mod.Log.Msg($"[UI] canvas \"{path}\" layer={cv.gameObject.layer}({LayerMask.LayerToName(cv.gameObject.layer)}) mode={cv.renderMode} active={cv.gameObject.activeInHierarchy} enabled={cv.enabled}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void DumpAvatar()
        {
            var av = BoneLib.Player.Avatar;
            if (av == null) { Mod.Log.Msg("[Avatar] none"); return; }
            Mod.Log.Msg($"[Avatar] {av.name} type {av.GetIl2CppType().FullName}");
            foreach (var p in av.GetType().GetProperties())
            {
                try
                {
                    var t = p.PropertyType.ToString();
                    if (!t.Contains("Renderer") && !t.Contains("Transform") && !t.Contains("Mesh")) continue;
                    Mod.Log.Msg($"[Avatar]   prop {p.Name} : {t}");
                }
                catch { }
            }
            Transform head = null;
            foreach (var tr in av.GetComponentsInChildren<Transform>(true)) if (tr.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 && tr.name.IndexOf("top", StringComparison.OrdinalIgnoreCase) < 0) { head = tr; break; }
            Mod.Log.Msg($"[Avatar] head bone {(head != null ? head.name + " " + head.position : "none")}");
            var inHead = new System.Collections.Generic.HashSet<int>();
            foreach (var arr in new[] { av.headMeshes, av.hairMeshes }) if (arr != null) foreach (var m in arr) if (m != null) inHead.Add(m.GetInstanceID());
            foreach (var r in av.GetComponentsInChildren<Renderer>(true))
            {
                var smr = r.TryCast<SkinnedMeshRenderer>();
                string bs = "";
                if (smr != null && smr.sharedMesh != null) { var mm = smr.sharedMesh; for (int i = 0; i < Math.Min(mm.blendShapeCount, 6); i++) bs += mm.GetBlendShapeName(i) + " "; }
                float dh = head != null ? Vector3.Distance(r.bounds.center, head.position) : -1;
                Mod.Log.Msg($"[Avatar]   {r.GetIl2CppType().Name} \"{r.name}\" on={r.enabled} head/hair={inHead.Contains(r.GetInstanceID())} root={(smr != null && smr.rootBone != null ? smr.rootBone.name : "-")} size={r.bounds.size} distToHead={dh:0.00} bones={(smr != null ? smr.bones.Length : 0)} shapes=[{bs}]");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RestoreHead()
        {
            foreach (var (r, was) in _hidden) if (r != null) r.enabled = was;
            _hidden.Clear();
        }

        private static Page _importPage;
        private static readonly System.Collections.Generic.HashSet<string> _asked = new System.Collections.Generic.HashSet<string>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RefreshImports(Config cfg)
        {
            if (_importPage == null) return;
            _importPage.RemoveAll();
            var pending = ReShadeImport.PendingHeavy(cfg);
            foreach (var p in pending)
            {
                var name = p;
                string shown = name.EndsWith(ReShadeImport.Suffix) ? name.Substring(0, name.Length - ReShadeImport.Suffix.Length) : name;
                var q = _importPage.CreatePage($"[{shown}] Was Too Heavy To Run. Would You Like To Compress It?", Color.yellow);
                q.CreateFunction("Yes - compress it (colours only, fastest)", Color.green, () => { ReShadeImport.Answer(name, true); _mod.SelectPreset(name); _mod.ReloadAll(); Notify($"{shown}: compressed to colours only"); });
                q.CreateFunction("No - keep everything that runs", Color.white, () => { ReShadeImport.Answer(name, false); _mod.SelectPreset(name); _mod.ReloadAll(); Notify($"{shown}: kept (heavy parts skipped)"); });
            }
            int n = 0; foreach (var p in cfg.Presets) if (p.EndsWith(ReShadeImport.Suffix)) n++;
            _importPage.CreateFunction($"{n} imported - pick them in Choose preset", Color.white, () => Notify("Imported ReShade presets end in (ReShade) in Choose preset.", 3f));
            _importPage.CreateFunction("How to add: UserData/QuestShade/ReShade", Color.gray, () => Notify("Copy a ReShade .ini into UserData/QuestShade/ReShade, then Reload. No .fx files needed.", 6f));
            _importPage.CreateFunction("Reload", Color.yellow, () => _mod.ReloadAll());
            if (pending.Count > 0 && _asked.Add(pending[0])) Notify($"[{pending[0].Replace(ReShadeImport.Suffix, "")}] Was Too Heavy To Run. Would You Like To Compress It? (Effects > ReShade presets)", 6f);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RefreshPresets(Config cfg)
        {
            try { RefreshImports(cfg); } catch { }
            try { PresetEditorUi.Refresh(); } catch (Exception e) { Mod.Log.Warning("Preset editor: " + e.Message); }
            if (_presetPage == null) return;
            _presetPage.RemoveAll();
            foreach (var p in cfg.Presets)
            {
                var name = p;
                _presetPage.CreateFunction(name, name.Equals(cfg.PresetName, StringComparison.OrdinalIgnoreCase) ? Color.green : Color.white,
                    () => _mod.SelectPreset(name));
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Notify(string msg, float seconds = 2f)
        {
            Notifier.Send(new Notification
            {
                Title = "QuestShade",
                Message = msg,
                Type = NotificationType.Information,
                PopupLength = seconds,
                ShowTitleOnPopup = true,
            });
        }
    }
}
