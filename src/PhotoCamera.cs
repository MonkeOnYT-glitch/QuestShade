using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using InputDevice = UnityEngine.XR.InputDevice;

namespace QuestShade
{
    public enum CamMode { Hand, Selfie, FirstPerson, ThirdPerson, Tripod, Chest }

    internal sealed class PhotoCamera
    {
        public bool Open { get; private set; }
        public CamMode Mode { get; private set; } = CamMode.Hand;
        public string PhotoTag;
        private static readonly Color BezelIdle = new Color(0.06f, 0.06f, 0.07f, 1f), BezelRec = new Color(0.75f, 0.05f, 0.05f, 1f);
        private bool _previewOn = true;
        private RenderTexture _recRT, _gradeRT, _photoGraded;
        private bool _recRendered;
        private float _recAccum;
#if QUEST
        private readonly VideoRecorder _rec = new VideoRecorder();
        public bool Recording => _rec.Recording;
        public float RecordSeconds => _rec.Seconds;
#else
        public bool Recording => false;
        public float RecordSeconds => 0f;
#endif
        public Camera LensCamera => _cam;
        public Effects Fx;
        private RenderTexture _rawPhotoRT, _rawRecRT, _rawPreviewRT;
        private bool _photoHQ, _recHQ, _recExtras, _recSS, _previewHQ, _previewRendered;

        private void LeavePreviewHQ()
        {
            if (!_previewHQ) return;
            _previewHQ = false; _previewRendered = false;
            EndHQ();
            Free(ref _rawPreviewRT);
        }

        private RenderTexture MakeRaw(int w, int h, string name)
        {
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            { name = name, antiAliasing = 1, useMipMap = false, hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
            return rt;
        }

        private static void Free(ref RenderTexture rt)
        {
            if (rt == null) return;
            rt.Release(); UnityEngine.Object.Destroy(rt); rt = null;
        }

        private void BeginHQ(bool depth)
        {
            if (Fx != null) Fx.PhotoHQ = true;
            _camData.requiresDepthTexture = depth;
        }

        private void EndHQ()
        {
            if (Fx != null) Fx.PhotoHQ = false;
            if (_camData != null) _camData.requiresDepthTexture = false;
        }

        private GameObject _device, _lensGo, _screen, _bezel;
        private Camera _cam;
        private UniversalAdditionalCameraData _camData;
        private RenderTexture _previewRT, _photoRT;
        private Material _screenMat, _bezelMat;
        private float _accum;
        private int _captureWait = -1;
        private bool _hasDevicePose, _hasLensPose;
        private Vector3 _devPos, _lensPos; private Quaternion _devRot = Quaternion.identity, _lensRot = Quaternion.identity;
        private InputDevice _headDev, _leftDev, _rightDev;
        public bool Hidden { get; private set; }
        public bool Held => _held >= 0;
        private int _held = -1;
        private bool _pinned;
        private Vector3 _grabPos; private Quaternion _grabRot = Quaternion.identity;
        private bool _prevGripL, _prevGripR;
        private bool _grabOnHand;
        private float _grabAt, _grabCheckUntil;
        private Vector3 _blendPos; private Quaternion _blendRot = Quaternion.identity;
        public Func<int, Transform> HandTransform;
        public Func<int, bool> HandBusy;
        public event Action<string, bool> Feedback;
        private float _nextDev;
        private int _builtPW, _builtPH, _builtFW, _builtFH, _builtMsaa;
        private int _layoutMode = -1;
        private int _screenLayer = -1, _fullMask;

        public bool Blocked;
        public void Toggle(Config cfg, Camera head) { if (Open) Close(); else if (!Blocked) OpenCam(cfg, head); }

        public void SetMode(CamMode m)
        {
            if (m == Mode) return;
            if (m == CamMode.Tripod)
            {
                if (Detached) { _devPos = _lensPos; _devRot = _lensRot; }
            }
            Mode = m;
            _hasLensPose = false;
            _pinned = m == CamMode.Tripod && _hasDevicePose;
            if (m != CamMode.Tripod) _hasDevicePose = false;
            _layoutMode = -1;
        }

        public void SetHidden(bool h) { Hidden = h; if (h) _held = -1; ApplyHidden(); }

        private MeshRenderer _screenR, _bezelR;
        private bool _hiddenForLens, _rpHooked, _headHidden, _screenStereo;
        public Action HideHead, RestoreHead;
        public Action HideUi, RestoreUi;
        public Func<int> UiLayerMask;
        private int UiLayers { get { try { return UiLayerMask != null ? UiLayerMask() : 0; } catch { return 0; } } }
        public GameObject ToastObject;
        private bool _uiHidden, _toastWasOn;

        private GameObject _hover;
        private MeshRenderer _hoverR;
        private Material _hoverMat;
        private bool _hoverWanted;

        private GameObject _label;
        private Il2CppTMPro.TextMeshPro _labelTmp;
        private MeshRenderer _labelR;
        private bool _labelWanted = true;
        private string _labelText;
        public string LabelExtra;

        public void SetLabel(bool on) { _labelWanted = on; ApplyHidden(); }

        private void BuildLabel(Config cfg)
        {
            _labelWanted = cfg.CameraLabel;
            try
            {
                _label = new GameObject("QuestShade_Label");
                _label.transform.SetParent(_device.transform, false);
                _labelTmp = _label.AddComponent<Il2CppTMPro.TextMeshPro>();
                _labelTmp.alignment = Il2CppTMPro.TextAlignmentOptions.Center;
                _labelTmp.fontSize = 0.16f;
                _labelTmp.enableWordWrapping = false;
                _labelTmp.richText = true;
                _labelTmp.color = Color.white;
                _labelTmp.outlineWidth = 0.25f;
                _labelTmp.outlineColor = new Color32(0, 0, 0, 255);
                _labelTmp.rectTransform.sizeDelta = new Vector2(0.4f, 0.06f);
                var pos = _model != null
                    ? new Vector3(_modelBounds.center.x, _modelBounds.max.y + 0.02f, _modelBounds.min.z + 0.01f)
                    : new Vector3(0f, -cfg.ScreenSize * 0.5f - 0.03f, -0.004f);
                _label.transform.localPosition = pos;
                _label.transform.localRotation = Quaternion.identity;
                _labelR = _label.GetComponent<MeshRenderer>();
                if (_labelR != null) _labelR.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            catch (Exception e)
            {
                Mod.Log.Warning("Camera label unavailable: " + e.Message);
                if (_label != null) UnityEngine.Object.Destroy(_label);
                _label = null; _labelTmp = null; _labelR = null;
            }
        }

        private float _focus = -1f, _focusDt;
        private Vector3 _focusPrevPos; private Quaternion _focusPrevRot; private bool _focusPrevSet;
        private void UpdateFocus(float dt)
        {
            if (Fx == null || !Fx.AutoFocus || _lensGo == null) return;
            _focusDt += dt;
            if (_focusDt < 1f / 15f) return;
            dt = _focusDt; _focusDt = 0f;
            var t = _lensGo.transform;
            float speed = _focusPrevSet ? Vector3.Distance(t.position, _focusPrevPos) / dt : 0f;
            float turn = _focusPrevSet ? Quaternion.Angle(t.rotation, _focusPrevRot) / dt : 0f;
            _focusPrevPos = t.position; _focusPrevRot = t.rotation; _focusPrevSet = true;
            float motion = Mathf.Max(Mathf.InverseLerp(0.8f, 3f, speed), Mathf.InverseLerp(60f, 200f, turn));
            Fx.MotionScale = Mathf.Lerp(Fx.MotionScale, Mathf.Lerp(1f, 0.5f, motion), 1f - Mathf.Exp(-dt * 4f));
            if (turn > 120f) return;
            Transform ignore = null;
            try { if (PropActive) ignore = _prop.Root.transform; } catch { }
            _focus = Focus.Smooth(_focus, Focus.Raycast(t.position, t.forward, _screenLayer, ignore, _focus), dt);
            Fx.SetCaptureFocus(_focus);
        }

        private void UpdateLabel()
        {
            if (_labelTmp == null || !_labelWanted || Hidden) return;
            string line1, line2;
            int hand = HoldingHand;
            if (Recording)
            {
                int s = (int)RecordSeconds;
                line1 = $"<color=#ff3030>Ã¢â€”Â REC {s / 60}:{s % 60:00}</color>";
                line2 = hand != 0 ? "HOLD TRIGGER = stop" : "grab it, then HOLD TRIGGER = stop";
            }
            else if (hand != 0)
            {
                line1 = "TRIGGER:  tap = <color=#ffa030>PHOTO</color>   hold = <color=#ff5050>RECORD</color>";
                line2 = Mode == CamMode.Selfie ? "facing you (selfie)" : Mode == CamMode.Hand ? "facing forward" : ModeName(Mode);
            }
            else
            {
                line1 = "GRAB A HANDLE";
                line2 = "then TRIGGER: tap = photo, hold = record";
            }
            if (!string.IsNullOrEmpty(LabelExtra)) line1 = LabelExtra;
            string text = line1 + "\n<size=75%>" + line2 + "</size>";
            if (text == _labelText) return;
            _labelText = text;
            try { _labelTmp.text = text; } catch { }
        }

        private static string ModeName(CamMode m) => m switch
        {
            CamMode.FirstPerson => "mode: first person", CamMode.ThirdPerson => "mode: third person", CamMode.Chest => "mode: chest (bodycam)",
            CamMode.Tripod => "mode: tripod", _ => "mode: " + m.ToString().ToLowerInvariant()
        };

        public void SetHoverPreview(bool on) { _hoverWanted = on; ApplyHidden(); }

        private void ApplyHidden()
        {
            if (_screenR == null) return;
            if (_hoverR != null) _hoverR.enabled = _hoverWanted && !Hidden;
            if (_labelR != null) _labelR.enabled = _labelWanted && !Hidden;
            if (_model != null)
            {
                foreach (var r in _modelRenderers) if (r != null) r.enabled = !Hidden;
                _screenR.enabled = false;
                _bezelR.enabled = !Hidden && Recording;
                return;
            }
            _screenR.enabled = !Hidden;
            _bezelR.enabled = !Hidden;
        }

        private GameObject _model;
        private Transform _modelLens, _modelScreen;
        private Vector3 _modelHandleL;
        private readonly System.Collections.Generic.List<Renderer> _modelRenderers = new System.Collections.Generic.List<Renderer>();
        private readonly System.Collections.Generic.List<Material> _modelMats = new System.Collections.Generic.List<Material>();
        private Bounds _modelBounds = new Bounds(Vector3.zero, new Vector3(0.3f, 0.2f, 0.1f));

        private void TryBuildModel(Config cfg)
        {
            if (!cfg.HandheldModel || ShaderPack.Handheld == null || _model != null) return;
            try
            {
                _model = UnityEngine.Object.Instantiate(ShaderPack.Handheld, _device.transform);
                _model.name = "QuestShade_Handheld";
                _model.transform.localPosition = Vector3.zero;
                _model.transform.localRotation = Quaternion.identity;
                _model.transform.localScale = Vector3.one;
                foreach (var t in _model.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "QS_HandleL") _modelHandleL = _device.transform.InverseTransformPoint(t.position);
                    if (t.name == "QS_Lens") _modelLens = t;
                    else if (t.name == "QS_Screen") _modelScreen = t;
                }
                bool first = true;
                foreach (var r in _model.GetComponentsInChildren<MeshRenderer>(true))
                {
                    _modelRenderers.Add(r);
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    var lb = new Bounds(_device.transform.InverseTransformPoint(r.bounds.center), r.bounds.size);
                    if (first) { _modelBounds = lb; first = false; } else _modelBounds.Encapsulate(lb);
                    if (_modelScreen != null && r.transform == _modelScreen) { r.sharedMaterial = _screenMat; continue; }
                    foreach (var m in r.sharedMaterials) if (m != null && !_modelMats.Contains(m)) _modelMats.Add(m);
                }
                _screenMat.mainTextureScale = new Vector2(-1f, -1f);
                _screenMat.mainTextureOffset = new Vector2(1f, 1f);
                if (_modelScreen != null)
                {
                    var sp = _device.transform.InverseTransformPoint(_modelScreen.position);
                    _bezel.transform.localPosition = sp + new Vector3(-0.156f, 0.09f, -0.004f);
                }
                Mod.Log.Msg($"Camera model: handheld rig ({_modelRenderers.Count} parts, lens {(_modelLens != null ? "ok" : "missing")}, screen {(_modelScreen != null ? "ok" : "missing")})");
            }
            catch (Exception e)
            {
                Mod.Log.Warning("Camera model failed, using the flat screen: " + e.Message);
                if (_model != null) UnityEngine.Object.Destroy(_model);
                _model = null; _modelRenderers.Clear(); _modelMats.Clear();
            }
        }

        private void HookRender()
        {
            if (_rpHooked) return;
            _rpHooked = true;
            RenderPipelineManager.add_beginCameraRendering(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                new Action<ScriptableRenderContext, Camera>(OnBeginRender)));
            RenderPipelineManager.add_endCameraRendering(DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                new Action<ScriptableRenderContext, Camera>(OnEndRender)));
        }

        private void OnBeginRender(ScriptableRenderContext ctx, Camera cam) { }
        private void OnEndRender(ScriptableRenderContext ctx, Camera cam) { }

        public Action<bool, int> HeadToLayer;
        public Action<bool, int> OwnBodyToLayer;
        private bool _bodyHidden; private float _bodyAt;
        private void UpdateOwnBody(bool on)
        {
            if (!on) { if (_bodyHidden) { try { OwnBodyToLayer(false, _screenLayer); } catch { } _bodyHidden = false; } return; }
            if (_bodyHidden && Time.unscaledTime < _bodyAt) return;
            _bodyAt = Time.unscaledTime + 1f;
            try { OwnBodyToLayer(true, _screenLayer); _bodyHidden = true; } catch { }
        }
        public Action HeadRestore;
        private bool _headOnLayer, _headWasActor; private float _headLayerAt;
        private void UpdateHeadLayer()
        {
            bool want = Open && _screenLayer >= 0 && (Mode == CamMode.FirstPerson || Mode == CamMode.Chest) && HeadToLayer != null;
            if (!want)
            {
                if (_headOnLayer) { try { HeadRestore?.Invoke(); } catch { } _headOnLayer = false; }
                return;
            }
            if (_headOnLayer && _headWasActor == _following && Time.unscaledTime < _headLayerAt) return;
            _headLayerAt = Time.unscaledTime + 1f;
            _headWasActor = _following;
            try { HeadToLayer(_following, _screenLayer); _headOnLayer = true; } catch { }
        }
        private static readonly CamMode[] CycleOrder = { CamMode.Hand, CamMode.Selfie, CamMode.FirstPerson, CamMode.Chest, CamMode.ThirdPerson, CamMode.Tripod };
        public void CycleMode() => SetMode(CycleOrder[(Array.IndexOf(CycleOrder, Mode) + 1) % CycleOrder.Length]);

        private bool Detached => Mode == CamMode.FirstPerson || Mode == CamMode.ThirdPerson || Mode == CamMode.Chest;

        public Func<Transform> ChestProvider;
        private bool _chestLogged;
        private float _appliedFov = -1f, _appliedNear = -1f;

        private Vector3 AvoidWalls(Vector3 pivot, Vector3 desired)
        {
            try
            {
                var d = desired - pivot;
                float len = d.magnitude;
                if (len < 0.01f) return desired;
                d /= len;
                int mask = unchecked((int)0x80000441);
                if (_screenLayer >= 0) mask &= ~(1 << _screenLayer);
                var hits = Physics.SphereCastAll(pivot, 0.05f, d, len, mask, QueryTriggerInteraction.Ignore);
                float best = float.MaxValue;
                for (int i = 0; hits != null && i < hits.Length; i++)
                {
                    var col = hits[i].collider;
                    if (col == null || col.attachedRigidbody != null) continue;
                    if (hits[i].distance < best) best = hits[i].distance;
                }
                if (best < float.MaxValue) return pivot + d * Mathf.Max(0f, best - 0.06f);
            }
            catch { }
            return desired;
        }

        private void ChestPose(Config cfg, Camera head, out Vector3 pos, out Quaternion rot) => ChestPose(cfg, head, out pos, out rot, out _);

        private void ChestPose(Config cfg, Camera head, out Vector3 pos, out Quaternion rot, out Vector3 pivot, Transform actorHead = null)
        {
            var ht = actorHead ?? head.transform;
            var headFwd = Vector3.ProjectOnPlane(ht.forward, Vector3.up);
            if (headFwd.sqrMagnitude < 1e-4f) headFwd = Vector3.ProjectOnPlane(ht.up, Vector3.up);
            headFwd.Normalize();

            Transform chest = null;
            if (actorHead == null) { try { chest = ChestProvider?.Invoke(); } catch { } }
            Vector3 c, fwd;
            if (chest != null)
            {
                c = chest.position + Vector3.up * 0.07f;
                fwd = Vector3.ProjectOnPlane(chest.forward, Vector3.up);
                if (fwd.sqrMagnitude < 1e-3f || Vector3.Dot(fwd.normalized, headFwd) < 0.2f) fwd = headFwd;
                if (!_chestLogged) { _chestLogged = true; Mod.Log.Msg($"Chest cam: using rig chest '{chest.name}' (agrees with head: {Vector3.Dot(Vector3.ProjectOnPlane(chest.forward, Vector3.up).normalized, headFwd):0.00})"); }
            }
            else
            {
                c = ht.position - Vector3.up * (actorHead != null ? 0.24f : 0.31f);
                fwd = headFwd;
                if (!_chestLogged) { _chestLogged = true; Mod.Log.Msg("Chest cam: no rig chest, estimating from head"); }
            }
            fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            pivot = c;
            pos = c + fwd * cfg.ChestForward + Vector3.up * cfg.ChestHeight + right * cfg.ChestSide;
            rot = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(-cfg.ChestPitch, 0f, 0f);
        }

        private void OpenCam(Config cfg, Camera head)
        {
            if (head == null) return;
            CamDiag.Begin();
            Build(cfg, head);
            CamDiag.Step("built");
            if (_device == null) return;
            _device.SetActive(true);
            _lensGo.SetActive(true);
            Open = true;
            _hasDevicePose = _hasLensPose = false;
            _pinned = false; _held = -1;
            if (Mode == CamMode.Tripod) Mode = CamMode.Hand;
            ApplyHidden();
            _accum = 1f;
            _propRetryAt = 0f;
            SpawnPose(head, out _devPos, out _devRot);
            _hasDevicePose = true;
            _pinned = true;
            CamDiag.Step("spawning body");
            RequestProp(cfg, head);
            CamDiag.Step("open done");
        }

        private static void SpawnPose(Camera head, out Vector3 pos, out Quaternion rot)
        {
            var ht = head.transform;
            var fwd = Vector3.ProjectOnPlane(ht.forward, Vector3.up);
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);
            pos = ht.position + fwd * 0.45f - Vector3.up * 0.15f + right * 0.19f;
            rot = Quaternion.LookRotation(fwd, Vector3.up);
        }

        public Action<string, Vector3, Quaternion, Action<GameObject>> SpawnCrate;
        public Func<GameObject, int> IsHeldBy;
        public Func<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Collider>> PlayerColliders;
        private PropBody _prop;
        private bool _propSpawning, _propWasHeld, _propWasActive;
        private float _propRetryAt, _propSpawnTime;
        private Vector3 _propSpawnPos;
        private int _propHand;
        private bool PropActive => _prop != null && _prop.Alive;
        public bool HasBody => PropActive;
        public int HoldingHand => !Open ? 0 : PropActive ? _propHand : _held >= 0 ? _held + 1 : 0;

        private void RequestProp(Config cfg, Camera head)
        {
            if (!cfg.PhysicalCamera || SuppressBody || SpawnCrate == null || _propSpawning || PropActive || head == null) return;
            if (Time.unscaledTime < _propRetryAt) return;
            _propRetryAt = Time.unscaledTime + 5f;
            Vector3 pos; Quaternion rot;
            if (_hasDevicePose) { pos = _devPos; rot = _devRot; } else SpawnPose(head, out pos, out rot);
            try
            {
                _propSpawning = true;
                _propSpawnTime = Time.unscaledTime;
                _propSpawnPos = pos;
                SpawnCrate(cfg.BodyBarcode, pos, rot, go => OnPropSpawned(go, cfg));
            }
            catch (Exception e) { _propSpawning = false; Mod.Log.Warning("Camera body spawn failed (using the floating camera): " + e.Message); }
        }

        private void PollForSpawnedProp(Config cfg)
        {
            if (!_propSpawning) return;
            if (SuppressBody) { _propSpawning = false; return; }
            if (Time.unscaledTime < _propSpawnTime + 0.15f) return;
            if (Time.unscaledTime > _propSpawnTime + 1.5f && Time.frameCount % 10 != 0) return;
            var go = PropBody.FindSpawnedNear(_propSpawnPos, 1.5f, cfg.BodyBarcode);
            if (go != null) { Mod.Log.Msg("Camera body: found it without the spawn callback"); OnPropSpawned(go, cfg); return; }
            if (Time.unscaledTime > _propSpawnTime + 6f) { _propSpawning = false; Mod.Log.Warning("Camera body didn't appear - using the floating camera"); }
        }

        private void OnPropSpawned(GameObject go, Config cfg)
        {
            if (SuppressBody) { _propSpawning = false; return; }
            CamDiag.Step("body spawned");
            if (_prop != null && _prop.Root == go) return;
            _propSpawning = false;
            if (go == null) { Mod.Log.Warning($"Camera body '{cfg.BodyBarcode}' didn't spawn - using the floating camera"); return; }
            float h = cfg.ScreenSize, w = h * cfg.PreviewWidth / (float)cfg.PreviewHeight;
            var p = _model != null
                ? PropBody.Adapt(go, 0f, _modelBounds.size + new Vector3(0.02f, 0.02f, 0.02f), _modelBounds.center)
                : PropBody.Adapt(go, cfg.BodyMountHeight, new Vector3(w + 0.03f, h + 0.03f, 0.06f), Vector3.zero);
            if (!Open) { p.Despawn(); return; }
            _prop = p;
            _propWasHeld = false;
            _mountSet = false;
            try { if (PlayerColliders != null) _prop.IgnoreCollisions(PlayerColliders()); } catch { }
            if (_hasDevicePose) _prop.PlaceMountAt(_devPos, _devRot);
            _prop.SetFrozen(true);
            _anchorSet = false; _settling = false; _cupAt = 0; _braked = false; _notHeldFor = 0f; _propHand = 0;
            _layoutMode = -1;
        }

        private Vector3 _mountPos; private Quaternion _mountRot = Quaternion.identity; private bool _mountSet;

        private void FollowProp(Config cfg, Camera head)
        {
            var t = _prop.Root.transform;
            if (!_mountSet) { _mountPos = _prop.MountLocal; _mountRot = Quaternion.Inverse(t.rotation) * _devRot; _mountSet = true; SetAnchor(); }
            int rawHand = 0;
            try { rawHand = IsHeldBy != null ? IsHeldBy(_prop.Root) : 0; } catch { }
            if (rawHand != 0)
            {
                if (_braked) { _braked = false; _prop.Brake(false); }
                _propHand = rawHand; _notHeldFor = 0f;
            }
            else
            {
                if (_propWasHeld && !_braked && cfg.FreezeOnRelease) { _braked = true; _prop.Brake(true); }
                if (_propWasHeld && _notHeldFor == 0f) ScheduleIgnore();
                _notHeldFor += Time.unscaledDeltaTime;
                if (!_propWasHeld || _notHeldFor > 0.12f) _propHand = 0;
            }
            bool held = _propHand != 0;
            float now = Time.unscaledTime;

            if (!held) GrabAssist(head, t); else _assistHand = 0;

            if (!held && _prop.Frozen && _anchorSet) _prop.HoldAt(_anchorPos, _anchorRot);

            if (held && !_propWasHeld) _settleUntil = now + 0.35f;
            var node = _propHand == 1 ? XRNode.LeftHand : XRNode.RightHand;
            if (held && now < _settleUntil && TryHandPose(head, node, out var hp, out var hr))
            {
                var rigRot = hr * Quaternion.Euler(cfg.GripAngle, 0f, 0f);
                _devRot = rigRot;
                _devPos = hp - rigRot * (_propHand == 1 ? _modelHandleL : Vector3.zero);
                _settling = true;
            }
            else
            {
                if (_settling)
                {
                    _settling = false;
                    _mountPos = t.InverseTransformPoint(_devPos);
                    _mountRot = Quaternion.Inverse(t.rotation) * _devRot;
                }
                _devPos = t.TransformPoint(_mountPos);
                _devRot = t.rotation * _mountRot;
            }
            _hasDevicePose = true;
            if (held == _propWasHeld) return;
            _propWasHeld = held;
            ScheduleIgnore();
            if (held) { _prop.SetFrozen(false); _prop.Brake(false); _braked = false; _anchorSet = false; }
            else if (cfg.FreezeOnRelease) { _prop.SetFrozen(true); SetAnchor(); _cupAt = -1; _braked = false; }
            else { _prop.Brake(false); _braked = false; }
            Mod.Log.Msg(held ? "Camera grabbed" : (cfg.FreezeOnRelease ? "Camera placed" : "Camera dropped"));
            Feedback?.Invoke(null, false);
        }

        private float _settleUntil; private bool _settling;
        private float _notHeldFor; private bool _braked;

        public delegate bool PoseFn(int index, out Vector3 pos, out Quaternion rot);
        public PoseFn ActorPose;
        public Action<bool> HideHeadOf;
        public bool FollowActor;
        public int ActorIndex;
        private bool _following;
        private GameObject _actorAnchor;
        public bool SuppressBody;
        public bool Following => _following;

        public void ParkBody()
        {
            if (_prop == null) return;
            try { _prop.Despawn(); } catch { }
            _prop = null; _propSpawning = false;
            _pinned = true;
        }

        private readonly float[] _ignoreAt = new float[4]; private int _ignoreLeft;
        private void ScheduleIgnore()
        {
            float t = Time.unscaledTime;
            _ignoreAt[0] = t; _ignoreAt[1] = t + 0.3f; _ignoreAt[2] = t + 0.8f; _ignoreAt[3] = t + 1.5f; _ignoreLeft = 4;
        }
        private void RunIgnore(float now)
        {
            if (_ignoreLeft == 0 || PlayerColliders == null) return;
            int idx = 4 - _ignoreLeft;
            if (now < _ignoreAt[idx]) return;
            _ignoreLeft--;
            try { _prop.IgnoreCollisions(PlayerColliders(), quiet: true); } catch { }
        }
        private int _cupAt;

        public Func<int, GameObject, bool> AttachToHand;
        private bool _gL, _gR, _gLWas, _gRWas;
        private int _assistHand, _assistFrames; private float _assistHoldUntil;

        private void GrabAssist(Camera head, Transform cup)
        {
            if (_model == null) { _gLWas = _gL; _gRWas = _gR; return; }
            var rig = Matrix4x4.TRS(_devPos, _devRot, Vector3.one);
            Vector3 hR = _devPos, hL = rig.MultiplyPoint3x4(_modelHandleL);
            for (int side = 1; side <= 2; side++)
            {
                bool down = side == 1 ? _gL : _gR, was = side == 1 ? _gLWas : _gRWas;
                if (!down || was || _assistHand != 0) continue;
                if (!TryHandPose(head, side == 1 ? XRNode.LeftHand : XRNode.RightHand, out var hp, out _)) continue;
                float dR = Vector3.Distance(hp, hR), dL = Vector3.Distance(hp, hL);
                if (Mathf.Min(dR, dL) > 0.3f) continue;
                _assistHand = side; _assistFrames = 3;
            }
            _gLWas = _gL; _gRWas = _gR;
            if (_assistHand == 0) return;
            if (!(_assistHand == 1 ? _gL : _gR)) { _assistHand = 0; return; }
            if (--_assistFrames > 0) return;
            int hand = _assistHand; _assistHand = 0;
            try
            {
                if (TryHandPose(head, hand == 1 ? XRNode.LeftHand : XRNode.RightHand, out var hp2, out _))
                {
                    var delta = hp2 - cup.TransformPoint(_prop.MountLocal);
                    _anchorPos += delta;
                    _prop.HoldAt(_anchorPos, _anchorRot);
                    _mountPos = cup.InverseTransformPoint(_devPos);
                    _mountRot = Quaternion.Inverse(cup.rotation) * _devRot;
                    _cupAt = -1; _assistHoldUntil = Time.unscaledTime + 1f;
                    Mod.Log.Msg($"Camera: grab assist ({(hand == 1 ? "left" : "right")} hand)");
                }
            }
            catch (Exception e) { Mod.Log.Warning("Grab assist failed: " + e.Message); }
        }

        private void ShiftCupToReachedHandle(Camera head, Transform cup)
        {
            var rig = Matrix4x4.TRS(_devPos, _devRot, Vector3.one);
            Vector3 hR = _devPos, hL = rig.MultiplyPoint3x4(_modelHandleL);
            float best = 0.30f; int want = _cupAt;
            foreach (var node in new[] { XRNode.LeftHand, XRNode.RightHand })
            {
                if (!TryHandPose(head, node, out var hp, out _)) continue;
                float dR = Vector3.Distance(hp, hR), dL = Vector3.Distance(hp, hL);
                if (dR < best) { best = dR; want = 0; }
                if (dL < best) { best = dL; want = 1; }
            }
            if (want == _cupAt) return;
            MoveCupTo(want, cup, hR, hL);
        }

        private void MoveCupTo(int want, Transform cup, Vector3 hR, Vector3 hL)
        {
            _cupAt = want;
            var target = want == 0 ? hR : hL;
            var delta = target - cup.TransformPoint(_prop.MountLocal);
            _anchorPos += delta;
            _prop.HoldAt(_anchorPos, _anchorRot);
            _mountPos = cup.InverseTransformPoint(_devPos);
            _mountRot = Quaternion.Inverse(cup.rotation) * _devRot;
        }
        private Vector3 _anchorPos; private Quaternion _anchorRot; private bool _anchorSet;
        private void SetAnchor()
        {
            try { var t = _prop.Root.transform; _anchorPos = t.position; _anchorRot = t.rotation; _anchorSet = true; } catch { _anchorSet = false; }
        }
        public void Close()
        {
            if (_headOnLayer) { try { HeadRestore?.Invoke(); } catch { } _headOnLayer = false; }
            UpdateOwnBody(false);
            StopRecording();
            LeavePreviewHQ();
            if (_photoHQ) { EndHQ(); _photoHQ = false; }
            Open = false;
            _captureWait = -1;
            _held = -1;
            if (_prop != null) { try { _prop.Despawn(); } catch { } _prop = null; }
            if (_cam != null) { _cam.enabled = false; _cam.targetTexture = _previewRT; }
            if (_device != null) _device.SetActive(false);
            if (_lensGo != null) _lensGo.SetActive(false);
            if (!_photoReading && _photoRT != null && _photoRT.IsCreated()) _photoRT.Release();
        }

        private int _prewarmStep = -1; private float _prewarmAt; private RenderTexture _prewarmRaw; private bool _prewarmLogged;
        public void SchedulePrewarm(float delay = 4f) { _prewarmStep = 0; _prewarmAt = Time.unscaledTime + delay; }
        public bool Prewarming => _prewarmStep >= 0;

        public void PrewarmTick(Config cfg, Camera head)
        {
            if (_prewarmStep < 0) return;
            if (Open || head == null) { AbortPrewarm(); return; }
            if (Time.unscaledTime < _prewarmAt) return;
            try
            {
                if (_prewarmStep == 0)
                {
                    Build(cfg, head);
                    if (_device == null || _cam == null) { _prewarmStep = -1; return; }
                    _device.SetActive(false);
                    _lensGo.SetActive(true);
                    _lensGo.transform.SetPositionAndRotation(head.transform.position, head.transform.rotation);
                    _cam.fieldOfView = cfg.Fov;
                    _prewarmRaw = MakeRaw(256, 144, "QuestShade_Prewarm");
                    if (Fx != null && Fx.CanPost) BeginHQ(Fx.WantsDepth);
                    _cam.targetTexture = _prewarmRaw;
                    _cam.enabled = true;
                    _prewarmStep = 1;
                    return;
                }
                _cam.enabled = false;
                if (Fx != null && Fx.CanPost)
                {
                    var dst = RenderTexture.GetTemporary(256, 144, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Fx.ProcessHQ(_prewarmRaw, dst);
                    RenderTexture.ReleaseTemporary(dst);
                    Fx.Calibrate(_prewarmRaw);
                }
                if (!_prewarmLogged) { _prewarmLogged = true; Mod.Log.Msg("Camera prewarmed"); }
            }
            catch (Exception e) { Mod.Log.Warning("Camera prewarm skipped: " + e.Message); }
            AbortPrewarm();
        }

        private void AbortPrewarm()
        {
            if (_prewarmStep > 0)
            {
                EndHQ();
                if (_cam != null) { if (!Open) _cam.enabled = false; _cam.targetTexture = _previewRT; }
                if (_lensGo != null && !Open) _lensGo.SetActive(false);
            }
            Free(ref _prewarmRaw);
            _prewarmStep = -1;
        }

        public void OnSceneLoaded()
        {
            SchedulePrewarm();
            if (Mode == CamMode.Tripod) Mode = CamMode.Hand;
            _hasDevicePose = _hasLensPose = false;
            _prop = null; _propSpawning = false; _propRetryAt = Time.unscaledTime + 2f;
        }

        public void Refresh(Config cfg, Camera head) { if (_device != null && head != null) Build(cfg, head); }

        private void Build(Config cfg, Camera head)
        {
            bool sizeChanged = _builtPW != cfg.PreviewWidth || _builtPH != cfg.PreviewHeight ||
                               _builtFW != cfg.PhotoWidth || _builtFH != cfg.PhotoHeight || _builtMsaa != cfg.PhotoMsaa;
            if (_device == null || sizeChanged)
            {
                DestroyRTs();
                _previewRT = new RenderTexture(cfg.PreviewWidth, cfg.PreviewHeight, 16, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "QuestShade_Preview", antiAliasing = 1, useMipMap = false, hideFlags = HideFlags.HideAndDontSave };
                _previewRT.Create();
                _photoRT = new RenderTexture(cfg.PhotoWidth, cfg.PhotoHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "QuestShade_Photo", antiAliasing = cfg.PhotoMsaa, useMipMap = false, hideFlags = HideFlags.HideAndDontSave };
                _builtPW = cfg.PreviewWidth; _builtPH = cfg.PreviewHeight; _builtFW = cfg.PhotoWidth; _builtFH = cfg.PhotoHeight; _builtMsaa = cfg.PhotoMsaa;
            }

            if (_device == null)
            {
                _device = new GameObject("QuestShade_CameraDevice");
                UnityEngine.Object.DontDestroyOnLoad(_device);
                _device.hideFlags = HideFlags.DontSave;

                _lensGo = new GameObject("QuestShade_CameraLens");
                UnityEngine.Object.DontDestroyOnLoad(_lensGo);
                _lensGo.hideFlags = HideFlags.DontSave;
                _cam = _lensGo.AddComponent<Camera>();
                _cam.stereoTargetEye = StereoTargetEyeMask.None;
                _cam.enabled = false;
                _camData = _lensGo.GetComponent<UniversalAdditionalCameraData>() ?? _lensGo.AddComponent<UniversalAdditionalCameraData>();
                DisableVrsMask(_lensGo);

                bool own = ShaderPack.Load() && ShaderPack.Screen != null;
                var shader = own ? ShaderPack.Screen : FindUnlitShader();
                _screenMat = new Material(shader) { name = "QuestShade_Screen", hideFlags = HideFlags.HideAndDontSave };
                _bezelMat = new Material(shader) { name = "QuestShade_Bezel", hideFlags = HideFlags.HideAndDontSave, color = BezelIdle };
                _bezelMat.mainTexture = Texture2D.whiteTexture;
                foreach (var m in new[] { _screenMat, _bezelMat })
                {
                    if (own) { ShaderPack.SetStereo(m, ShaderPack.HeadsetActive); m.renderQueue = 2450; }
                    else { m.SetFloat("unity_GUIZTestMode", 4f); m.renderQueue = 3000; }
                }
                _bezel = MakeQuad("Bezel", _bezelMat);
                _screen = MakeQuad("Screen", _screenMat);
                _screenR = _screen.GetComponent<MeshRenderer>();
                _bezelR = _bezel.GetComponent<MeshRenderer>();
                try { HookRender(); } catch (Exception e) { Mod.Log.Warning("Camera render hook failed: " + e.Message); }
                TryBuildModel(cfg);
                _hoverMat = new Material(_screenMat.shader) { name = "QuestShade_Hover", hideFlags = HideFlags.HideAndDontSave };
                _hoverMat.renderQueue = _screenMat.renderQueue;
                _hover = MakeQuad("Hover", _hoverMat);
                _hoverR = _hover.GetComponent<MeshRenderer>();
                _hoverR.enabled = false;
                var top = _model != null ? new Vector3(_modelBounds.center.x, _modelBounds.max.y + 0.12f, _modelBounds.center.z - 0.03f)
                                         : new Vector3(0f, cfg.ScreenSize * 0.5f + 0.13f, 0f);
                _hover.transform.localPosition = top;
                _hover.transform.localScale = new Vector3(0.32f, 0.18f, 1f);
                BuildLabel(cfg);
            }
            _screenMat.mainTexture = cfg.PreviewEnabled ? _previewRT : Texture2D.blackTexture;
            _screenStereo = !ShaderPack.HeadsetActive;

            _cam.depth = head.depth - 1;
            _appliedFov = -1f; _appliedNear = -1f;
            _cam.farClipPlane = cfg.FarClip > 0f ? cfg.FarClip : head.farClipPlane;
            _fullFar = _cam.farClipPlane;
            _cam.clearFlags = head.clearFlags;
            _cam.backgroundColor = head.backgroundColor;
            _cam.allowMSAA = false;
            _cam.allowHDR = false;
            _cam.useOcclusionCulling = true;
            _cam.targetTexture = _previewRT;

            _camData.renderShadows = cfg.PreviewShadows;
            _camData.renderPostProcessing = false;
            _camData.antialiasing = AntialiasingMode.None;
            _camData.requiresDepthTexture = false;
            _camData.requiresColorTexture = false;
            _camData.stopNaN = false;
            _camData.dithering = false;
            var headData = head.GetComponent<UniversalAdditionalCameraData>();
            if (headData != null) _camData.volumeLayerMask = headData.volumeLayerMask;

            _fullMask = head.cullingMask;
            _screenLayer = (_fullMask & (1 << 5)) != 0 ? 5 : PickLayer(_fullMask);
            if (_screenLayer >= 0)
            {
                _screen.layer = _screenLayer; _bezel.layer = _screenLayer;
                if (_hover != null) _hover.layer = _screenLayer;
                if (_label != null) _label.layer = _screenLayer;
                if (_model != null) foreach (var t in _model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = _screenLayer;
            }

            float h = cfg.ScreenSize, w = h * cfg.PreviewWidth / (float)cfg.PreviewHeight;
            _screen.transform.localScale = new Vector3(w, h, 1f);
            _bezel.transform.localScale = _model != null ? new Vector3(0.012f, 0.012f, 1f) : new Vector3(w + 0.012f, h + 0.012f, 1f);
            _layoutMode = -1;
            ApplyHidden();
        }

        private static void DisableVrsMask(GameObject lens)
        {
#if QUEST
            try
            {
                var ov = lens.GetComponent<Il2CppSLZ.Graphics2.VRS.CameraVRSOverride>() ?? lens.AddComponent<Il2CppSLZ.Graphics2.VRS.CameraVRSOverride>();
                var r = new Il2CppSLZ.Graphics2.VRS.VRSRadii
                {
                    RDMInnerRadius = 100f, RDMOuterRadius = 100f,
                    VRSInnerElipseMinorRadius = 100f, VRSInnerElipseMajorRadius = 100f,
                    VRSOuterElipseMinorRadius = 100f, VRSOuterElipseMajorRadius = 100f,
                };
                ov.radii = r;
                Mod.Log.Msg("Camera: BONELAB's VRS pixel mask switched off for the in-game camera");
            }
            catch (Exception e) { Mod.Log.Warning("Camera: couldn't override VRS (photos may get a black band): " + e.Message); }
#endif
        }

        private static int PickLayer(int mask)
        {
            for (int i = 31; i >= 1; i--) if ((mask & (1 << i)) != 0) return i;
            return -1;
        }

        private GameObject MakeQuad(string name, Material mat)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            var col = q.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.DestroyImmediate(col);
            var mr = q.GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            q.transform.SetParent(_device.transform, false);
            return q;
        }

        private static Shader FindUnlitShader()
        {
            foreach (var n in new[] { "Universal Render Pipeline/Unlit", "UI/Default", "Unlit/Texture", "Sprites/Default" })
            {
                var s = Shader.Find(n);
                if (s != null) { Mod.Log.Msg("Camera screen shader: " + n); return s; }
            }
            Mod.Log.Warning("No unlit shader found; camera screen may render incorrectly.");
            return Shader.Find("Hidden/InternalErrorShader");
        }

        public void LateTick(Config cfg, Camera head, float dt, bool gripL, bool gripR)
        {
            PollPhoto();
            if (!Open || _device == null) return;
            if (head == null) { Close(); return; }

            CamDiag.Step("tick");
            _gL = gripL; _gR = gripR;
            if (!PropActive) { PollForSpawnedProp(cfg); RequestProp(cfg, head); }
            CamDiag.Step("body ok");
            if (PropActive != _propWasActive) { _propWasActive = PropActive; _layoutMode = -1; }
            if (!PropActive) UpdateGrab(cfg, head, gripL, gripR);
            UpdatePoses(cfg, head, dt);
            bool hideBody = Open && _screenLayer >= 0 && _following && OwnBodyToLayer != null;
            if (!hideBody) UpdateOwnBody(false);
            UpdateHeadLayer();
            if (hideBody) UpdateOwnBody(true);
            CamDiag.Step("poses");
            UpdateLabel();
            CamDiag.Step("label");
            UpdateFocus(dt);
            CamDiag.Step("focus");

            if (_captureWait >= 0)
            {
                if (_captureWait-- == 0) FinishCapture(cfg);
                return;
            }

            if (Recording)
            {
#if QUEST
                if (_recRendered)
                {
                    if (_recHQ) Fx.ProcessHQ(_rawRecRT, _recRT, _recExtras && Perf.RecordingExtras);
                    else if (_recSS) Graphics.Blit(_rawRecRT, _recRT);
                    var frame = _recRT;
                    if (cfg.MatchPreview && Fx != null)
                    {
                        _gradeRT ??= new RenderTexture(_recRT.width, _recRT.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "QuestShade_RecordGraded", hideFlags = HideFlags.HideAndDontSave };
                        if (Fx.GradeAgain(_recRT, _gradeRT)) frame = _gradeRT;
                    }
                    _rec.SubmitFrame(frame);
                    _recRendered = false;
                }
                _rec.Tick();
                _recAccum += dt;
                float rstep = 1f / cfg.RecordFps;
                bool rrender = _recAccum >= rstep;
                if (rrender) SetFar(_fullFar);
                if (rrender) { _recAccum = Mathf.Min(_recAccum - rstep, rstep); _recRendered = true; }
                if (_cam.enabled != rrender) _cam.enabled = rrender;
#endif
                return;
            }

            if (_previewOn != cfg.PreviewEnabled) { _previewOn = cfg.PreviewEnabled; _screenMat.mainTexture = _previewOn ? _previewRT : Texture2D.blackTexture; }

            bool pHQ = Fx != null && Fx.WantsPreviewHQ && cfg.PreviewEnabled && !Hidden && Perf.PreviewExtras;
            if (pHQ != _previewHQ)
            {
                _previewHQ = pHQ;
                if (pHQ)
                {
                    _rawPreviewRT ??= MakeRaw(_previewRT.width, _previewRT.height, "QuestShade_PreviewRaw");
                    BeginHQ(Fx.WantsDepth);
                    _cam.targetTexture = _rawPreviewRT;
                }
                else { EndHQ(); _cam.targetTexture = _previewRT; Free(ref _rawPreviewRT); }
                _previewRendered = false;
            }
            if (_previewHQ && _previewRendered) { CamDiag.Step("preview bloom/dof"); Fx.ProcessHQ(_rawPreviewRT, _previewRT, true, light: true); _previewRendered = false; CamDiag.Step("preview bloom/dof done"); }

            _accum += dt;
            int pfps = Perf.PreviewFps(cfg.PreviewFps, HoldingHand != 0);
            float step = pfps > 0 ? 1f / pfps : float.MaxValue;
            bool render = _accum >= step && !Hidden && cfg.PreviewEnabled && (_hoverWanted || ScreenInView(head));
            if (render) SetFar(Mathf.Min(_fullFar, PreviewFar));
            if (render) { _accum = Mathf.Min(_accum - step, step); _previewRendered = _previewHQ; }
            if (_cam.enabled != render) _cam.enabled = render;
        }

        public bool StartRecording(Config cfg, out string error)
        {
            error = null;
#if QUEST
            if (!Open) { error = "Open the camera first"; return false; }
            if (Recording) return true;
            if (PhotoBusy) { error = "Wait for the photo to finish"; return false; }
            int w = cfg.RecordWidth & ~15, h = cfg.RecordHeight & ~1;
            _recRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            { name = "QuestShade_Record", antiAliasing = 1, useMipMap = false, hideFlags = HideFlags.HideAndDontSave };
            _recRT.Create();
            if (!_rec.Start(w, h, cfg.RecordFps, cfg.RecordBitrateKbps * 1000, PhotoSaver.RecordFolder, cfg))
            {
                _recRT.Release(); UnityEngine.Object.Destroy(_recRT); _recRT = null;
                error = "Recording failed to start (see log)";
                return false;
            }
            LeavePreviewHQ();
            float ss = cfg.RecordSupersample;
            int rw = Mathf.RoundToInt(w * ss), rh = Mathf.RoundToInt(h * ss);
            _recExtras = Fx != null && Fx.WantsHQ;
            _recHQ = Fx != null && Fx.CanPost && (_recExtras || ss > 1.01f);
            _recSS = !_recHQ && ss > 1.01f;
            if (_recHQ || _recSS)
            {
                _rawRecRT = _recHQ ? MakeRaw(rw, rh, "QuestShade_RecordRaw")
                                   : new RenderTexture(rw, rh, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "QuestShade_RecordBig", hideFlags = HideFlags.HideAndDontSave };
                _rawRecRT.Create();
                if (_recHQ) BeginHQ(_recExtras && Fx.WantsDepth);
                _cam.targetTexture = _rawRecRT;
            }
            else _cam.targetTexture = _recRT;
            Mod.Log.Msg($"Recording: rendering {(_recHQ || _recSS ? $"{rw}x{rh}" : $"{w}x{h}")} -> {w}x{h}{(_recExtras ? ", bloom / depth of field on" : "")}");
            VideoRecorder.Diag($"render {(_recHQ || _recSS ? $"{rw}x{rh}" : $"{w}x{h}")} -> {w}x{h}, effects chain {_recHQ}, extras {_recExtras}");
            _camData.renderShadows = Application.platform == RuntimePlatform.Android ? false : cfg.PhotoShadows;
            if (cfg.PreviewEnabled) _screenMat.mainTexture = _recRT;
            _bezelMat.color = BezelRec;
            ApplyHidden();
            _recAccum = 1f; _recRendered = false;
            return true;
#else
            error = "Recording is Quest-only (uses the headset's video encoder)";
            return false;
#endif
        }

        public string StopRecording()
        {
#if QUEST
            if (!Recording) return null;
            var file = _rec.Stop();
            PhotoSaver.PublishVideo(file);
            if (_cam != null) { _cam.targetTexture = _previewRT; _cam.enabled = false; }
            if (_camData != null) _camData.renderShadows = false;
            if (_screenMat != null) _screenMat.mainTexture = _previewOn ? _previewRT : Texture2D.blackTexture;
            if (_bezelMat != null) _bezelMat.color = BezelIdle;
            ApplyHidden();
            if (_recRT != null) { _recRT.Release(); UnityEngine.Object.Destroy(_recRT); _recRT = null; }
            if (_recHQ) { EndHQ(); _recHQ = false; }
            _recSS = _recExtras = false;
            Free(ref _rawRecRT);
            Free(ref _gradeRT);
            return file;
#else
            return null;
#endif
        }

        private float _fpPush;
        private float _fullFar = 1000f;
        private const float PreviewFar = 200f;
        private void SetFar(float f) { if (_cam != null && Mathf.Abs(_cam.farClipPlane - f) > 0.5f) _cam.farClipPlane = f; }

        private bool ScreenInView(Camera head)
        {
            if (head == null) return true;
            var ht = head.transform;
            var sp = _modelScreen != null ? _modelScreen.position : _devPos;
            var to = sp - ht.position;
            float d = to.magnitude;
            if (d < 0.7f) return true;
            if (d > 12f) return false;
            return Vector3.Dot(ht.forward, to / d) > 0.26f;
        }
        private float SmoothK(Config cfg, float dt) => _hasLensPose && cfg.Smoothing > 0f ? 1f - Mathf.Pow(cfg.Smoothing, dt * 10f) : 1f;

        private void UpdatePoses(Config cfg, Camera head, float dt)
        {
            var ht = head.transform;
            _following = false;
            if (FollowActor && ActorPose != null && (Mode == CamMode.FirstPerson || Mode == CamMode.Chest || Mode == CamMode.ThirdPerson)
                && ActorPose(ActorIndex, out var ap, out var ar))
            {
                if (_actorAnchor == null) { _actorAnchor = new GameObject("QuestShade_ActorAnchor"); UnityEngine.Object.DontDestroyOnLoad(_actorAnchor); _actorAnchor.hideFlags = HideFlags.HideAndDontSave; }
                _actorAnchor.transform.SetPositionAndRotation(ap, ar);
                ht = _actorAnchor.transform;
                _following = true;
            }

            if (PropActive) FollowProp(cfg, head);
            else if (_held >= 0 && HeldPose(head, out var gp, out var gr))
            {
                _devRot = gr;
                _devPos = gp;
                _hasDevicePose = true;
            }
            else if (!((_pinned || Mode == CamMode.Tripod) && _hasDevicePose) && TryHandPose(head, XRNode.LeftHand, out var hp, out var hr))
            {
                var rot = hr * Quaternion.Euler(cfg.HandTilt, 0f, 0f);
                var pos = hp + rot * cfg.HandOffset;
                if (Mode == CamMode.Selfie && _model == null)
                {
                    var face = ht.position - Vector3.up * 0.06f;
                    var toFace = face - pos;
                    rot = toFace.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(toFace, Vector3.up) : rot * Quaternion.Euler(0f, 180f, 0f);
                }
                bool lensOnDevice = Mode == CamMode.Hand || Mode == CamMode.Selfie;
                float k = lensOnDevice && _hasDevicePose && cfg.Smoothing > 0f ? 1f - Mathf.Pow(cfg.Smoothing, dt * 10f) : 1f;
                _devPos = _hasDevicePose ? Vector3.Lerp(_devPos, pos, k) : pos;
                _devRot = _hasDevicePose ? Quaternion.Slerp(_devRot, rot, k) : rot;
                _hasDevicePose = true;
            }
            _device.transform.SetPositionAndRotation(_devPos, _devRot);
            LayoutScreen();

            Vector3 lp; Quaternion lr;
            switch (Mode)
            {
                case CamMode.FirstPerson:
                {
                    float push = Mathf.Clamp01((cfg.Fov - 75f) / 75f) * 0.11f;
                    lp = ht.position + ht.forward * push; lr = ht.rotation;
                    _fpPush = push;
                }
                    break;
                case CamMode.ThirdPerson:
                {
                    var fwd = Vector3.ProjectOnPlane(ht.forward, Vector3.up);
                    if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(ht.up, Vector3.up);
                    fwd.Normalize();
                    var right = Vector3.Cross(Vector3.up, fwd);
                    lp = ht.position - fwd * cfg.ThirdPersonDistance + Vector3.up * cfg.ThirdPersonHeight + right * cfg.ThirdPersonSide;
                    lp = AvoidWalls(ht.position, lp);
                    var target = ht.position + fwd * 1.5f;
                    lr = Quaternion.LookRotation(target - lp, Vector3.up);
                    break;
                }
                case CamMode.Chest:
                    ChestPose(cfg, head, out lp, out lr, out var chestPivot, _following ? ht : null);
                    lp = AvoidWalls(chestPivot, lp);
                    break;
                default:
                    if (Mode == CamMode.Selfie && (PropActive || _model != null))
                    {
                        lp = _model != null && _modelScreen != null
                            ? _modelScreen.position - (_devRot * Vector3.forward) * 0.03f
                            : _devPos + _devRot * new Vector3(0f, 0f, -0.02f);
                        lr = _devRot * Quaternion.Euler(0f, 180f, 0f);
                    }
                    else
                    {
                        lp = _model != null && _modelLens != null
                            ? _modelLens.position + (_devRot * Vector3.forward) * 0.01f
                            : _devPos + _devRot * new Vector3(0f, 0f, 0.02f);
                        lr = _devRot;
                    }
                    break;
            }
            bool onDevice = Mode == CamMode.Hand || Mode == CamMode.Selfie || Mode == CamMode.Tripod;
            float lk = onDevice && _held < 0 ? 1f : SmoothK(cfg, dt);
            bool bodyLocked = Mode == CamMode.Chest || Mode == CamMode.FirstPerson;
            _lensPos = _hasLensPose && !bodyLocked ? Vector3.Lerp(_lensPos, lp, lk) : lp;
            _lensRot = _hasLensPose ? Quaternion.Slerp(_lensRot, lr, lk) : lr;
            _hasLensPose = true;
            _lensGo.transform.SetPositionAndRotation(_lensPos, _lensRot);
            if (_hoverMat != null && _hoverWanted)
            {
                _hoverMat.mainTexture = _screenMat.mainTexture;
                bool sm = Mode == CamMode.Selfie;
                _hoverMat.mainTextureScale = new Vector2(sm ? -1f : 1f, 1f);
                _hoverMat.mainTextureOffset = new Vector2(sm ? 1f : 0f, 0f);
            }
            float fov = Mode == CamMode.Chest ? cfg.ChestFov : cfg.Fov;
            if (fov != _appliedFov) { _appliedFov = fov; _cam.fieldOfView = fov; }
            float near = Mode == CamMode.FirstPerson ? 0.03f + _fpPush * 0.6f : Mode == CamMode.Chest ? 0.05f : 0.015f;
            if (near != _appliedNear) { _appliedNear = near; _cam.nearClipPlane = near; }

            bool stereo = ShaderPack.HeadsetActive;
            if (stereo != _screenStereo && ShaderPack.Screen != null && _screenMat != null && _screenMat.shader == ShaderPack.Screen)
            {
                _screenStereo = stereo;
                ShaderPack.SetStereo(_screenMat, stereo);
                ShaderPack.SetStereo(_bezelMat, stereo);
                foreach (var m in _modelMats) if (m != null) ShaderPack.SetStereo(m, stereo);
                if (_hoverMat != null) ShaderPack.SetStereo(_hoverMat, stereo);
            }
        }

        private const float HandleReach = 0.16f, BodyReach = 0.09f, SnapTime = 0.07f;
        private Vector3 _grabPosC; private Quaternion _grabRotC = Quaternion.identity;

        private void UpdateGrab(Config cfg, Camera head, bool gripL, bool gripR)
        {
            bool downL = gripL && !_prevGripL, downR = gripR && !_prevGripR;
            _prevGripL = gripL; _prevGripR = gripR;
            float now = Time.unscaledTime;

            if (_held >= 0)
            {
                if (now < _grabCheckUntil && HandBusySafe(_held + 1)) { DropHold(true); return; }
                if (Hidden || !(_held == 0 ? gripL : gripR)) { DropHold(false); return; }
                int other = 1 - _held;
                if ((other == 0 ? downL : downR) && Reach(head, other, out int handle) < float.MaxValue) StartHold(cfg, head, other, handle);
                return;
            }
            if (Hidden || !_hasDevicePose || (!downL && !downR)) return;

            int bestHand = -1, bestHandle = 0; float best = float.MaxValue;
            for (int h = 0; h < 2; h++)
            {
                if (h == 0 ? !downL : !downR) continue;
                if (HandBusySafe(h + 1)) continue;
                float d = Reach(head, h, out int handle);
                if (d < best) { best = d; bestHand = h; bestHandle = handle; }
            }
            if (bestHand >= 0) StartHold(cfg, head, bestHand, bestHandle);
        }

        private float Reach(Camera head, int h, out int handle)
        {
            handle = h == 0 ? 1 : 0;
            if (!TryHandPose(head, h == 0 ? XRNode.LeftHand : XRNode.RightHand, out var hp, out _)) return float.MaxValue;
            var local = Quaternion.Inverse(_devRot) * (hp - _devPos);
            if (_model != null)
            {
                float dR = local.magnitude, dL = (local - _modelHandleL).magnitude;
                handle = dL < dR ? 1 : 0;
                float d = Mathf.Min(dR, dL);
                if (d <= HandleReach) return d;
                float db = (_modelBounds.ClosestPoint(local) - local).magnitude;
                return db <= BodyReach ? HandleReach + db : float.MaxValue;
            }
            var body = new Bounds(Vector3.zero, new Vector3(0.3f, 0.2f, 0.05f));
            float dd = (body.ClosestPoint(local) - local).magnitude;
            return dd <= BodyReach ? dd : float.MaxValue;
        }

        private void StartHold(Config cfg, Camera head, int h, int handle)
        {
            if (!TryHandPose(head, h == 0 ? XRNode.LeftHand : XRNode.RightHand, out var hp, out var hr)) return;
            Quaternion rigRot; Vector3 rigPos;
            if (_model != null)
            {
                rigRot = hr * Quaternion.Euler(cfg.GripAngle, 0f, 0f);
                rigPos = hp - rigRot * (handle == 1 ? _modelHandleL : Vector3.zero);
            }
            else { rigRot = _devRot; rigPos = _devPos; }

            var inv = Quaternion.Inverse(hr);
            _grabRotC = inv * rigRot; _grabPosC = inv * (rigPos - hp);
            _grabOnHand = false;
            Transform ht = null;
            try { ht = HandTransform?.Invoke(h + 1); } catch { }
            if (ht != null)
            {
                var hinv = Quaternion.Inverse(ht.rotation);
                _grabRot = hinv * rigRot; _grabPos = hinv * (rigPos - ht.position);
                _grabOnHand = true;
            }
            bool handOver = _held >= 0;
            if (!handOver) { _blendPos = _devPos; _blendRot = _devRot; }
            _held = h;
            _pinned = false;
            _grabAt = Time.unscaledTime;
            _grabCheckUntil = _grabAt + 0.3f;
            Mod.Log.Msg($"Camera grabbed ({(h == 0 ? "left" : "right")} hand, {(handle == 1 ? "left" : "right")} handle{(_grabOnHand ? "" : ", controller pose")})");
            Feedback?.Invoke(null, h == 0);
        }

        private void DropHold(bool cancel)
        {
            bool left = _held == 0;
            _held = -1;
            _pinned = true;
            if (cancel) { _devPos = _blendPos; _devRot = _blendRot; Mod.Log.Msg("Camera grab skipped (your hand took an item)"); return; }
            Mod.Log.Msg("Camera placed");
            Feedback?.Invoke(null, left);
        }

        private bool HandBusySafe(int hand) { try { return HandBusy != null && HandBusy(hand); } catch { return false; } }

        private bool HeldPose(Camera head, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = default;
            Transform ht = null;
            if (_grabOnHand) { try { ht = HandTransform?.Invoke(_held + 1); } catch { } }
            if (ht != null) { rot = ht.rotation * _grabRot; pos = ht.position + ht.rotation * _grabPos; }
            else if (TryHandPose(head, _held == 0 ? XRNode.LeftHand : XRNode.RightHand, out var hp, out var hr)) { rot = hr * _grabRotC; pos = hp + hr * _grabPosC; }
            else return false;
            float t = (Time.unscaledTime - _grabAt) / SnapTime;
            if (t < 1f)
            {
                t = t * t * (3f - 2f * t);
                pos = Vector3.Lerp(_devPos, pos, t);
                rot = Quaternion.Slerp(_devRot, rot, t);
            }
            return true;
        }

        private void LayoutScreen()
        {
            if (_layoutMode == (int)Mode) return;
            _layoutMode = (int)Mode;

            bool hide = Detached && _screenLayer >= 0;
            _cam.cullingMask = (_screenLayer >= 0 ? _fullMask & ~(1 << _screenLayer) : _fullMask) & ~UiLayers;

            bool selfie = Mode == CamMode.Selfie;
            if (_model != null)
            {
                _screenMat.mainTextureScale = new Vector2(selfie ? 1f : -1f, -1f);
                _screenMat.mainTextureOffset = new Vector2(selfie ? 0f : 1f, 1f);
                return;
            }
            bool flip = selfie && !PropActive;
            var face = flip ? Quaternion.Euler(0f, 180f, 0f) : Quaternion.identity;
            _screen.transform.localRotation = face;
            _bezel.transform.localRotation = face;
            _screen.transform.localPosition = Vector3.zero;
            _bezel.transform.localPosition = new Vector3(0f, 0f, flip ? -0.002f : 0.002f);
            var s = _screen.transform.localScale;
            _screen.transform.localScale = new Vector3(selfie ? -Mathf.Abs(s.x) : Mathf.Abs(s.x), s.y, 1f);
        }

        private bool TryHandPose(Camera head, XRNode node, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = default;
            float now = Time.unscaledTime;
            if (now >= _nextDev || !_headDev.isValid || !_leftDev.isValid || !_rightDev.isValid)
            {
                _nextDev = now + 2f;
                _headDev = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
                if (!_headDev.isValid) _headDev = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                _leftDev = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                _rightDev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            }
            var dev = node == XRNode.RightHand ? _rightDev : _leftDev;
            var ht = head.transform;
            Vector3 hp = default; Quaternion hr = default;
            bool xr = _headDev.isValid && dev.isValid &&
                      (_headDev.TryGetFeatureValue(CommonUsages.centerEyePosition, out hp) || _headDev.TryGetFeatureValue(CommonUsages.devicePosition, out hp)) &&
                      (_headDev.TryGetFeatureValue(CommonUsages.centerEyeRotation, out hr) || _headDev.TryGetFeatureValue(CommonUsages.deviceRotation, out hr));
            if (!xr || !dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 lp) ||
                !dev.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion lr))
            {
                if (node == XRNode.RightHand) return false;
                pos = ht.position + ht.forward * 0.45f - ht.up * 0.15f;
                rot = ht.rotation * Quaternion.Euler(-25f, 0f, 0f);
                return true;
            }
            var R = ht.rotation * Quaternion.Inverse(hr);
            pos = ht.position + R * (lp - hp);
            rot = R * lr;
            return true;
        }

        public bool RequestPhoto(Config cfg)
        {
            if (!Open || PhotoBusy || _cam == null || PhotoSaver.Busy || Recording) return false;
            if (!_photoRT.IsCreated()) _photoRT.Create();
            LeavePreviewHQ();
            _photoHQ = Fx != null && Fx.WantsHQ;
            if (_photoHQ)
            {
                _rawPhotoRT ??= MakeRaw(_photoRT.width, _photoRT.height, "QuestShade_PhotoRaw");
                BeginHQ(Fx.WantsDepth);
                _cam.targetTexture = _rawPhotoRT;
            }
            else _cam.targetTexture = _photoRT;
            _camData.renderShadows = cfg.PhotoShadows;
            _cam.allowMSAA = cfg.PhotoMsaa > 1;
            SetFar(_fullFar);
            _cam.enabled = true;
            _captureWait = 2;
            return true;
        }

        private AsyncGPUReadbackRequest _photoReq;
        private bool _photoReading;
        private int _photoW, _photoH;
        private string _photoReqTag;
        public bool PhotoBusy => _captureWait >= 0 || _photoReading;

        private void FinishCapture(Config cfg)
        {
            try
            {
                int w = _photoRT.width, h = _photoRT.height;
                if (_photoHQ) { Fx.ProcessHQ(_rawPhotoRT, _photoRT); Mod.Log.Msg("Photo: bloom / depth of field applied"); }
                Mod.Log.Msg($"Photo: reading back {w}x{h}");
                var shot = _photoRT;
                if (cfg.MatchPreview && Fx != null)
                {
                    _photoGraded = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    if (Fx.GradeAgain(_photoRT, _photoGraded)) shot = _photoGraded; else { RenderTexture.ReleaseTemporary(_photoGraded); _photoGraded = null; }
                }
                _photoReq = AsyncGPUReadback.Request(shot, 0, (Il2CppSystem.Action<AsyncGPUReadbackRequest>)null);
                _photoW = w; _photoH = h; _photoReqTag = PhotoTag; _photoReading = true;
                PhotoTag = null;
            }
            catch (Exception e) { Mod.Log.Error("Photo capture failed: " + e); _photoReading = false; }
            finally
            {
                _cam.targetTexture = _previewRT;
                _camData.renderShadows = cfg.PreviewShadows;
                _cam.allowMSAA = false;
                _cam.enabled = false;
                _captureWait = -1;
                if (_photoHQ) { EndHQ(); _photoHQ = false; }
                Free(ref _rawPhotoRT);
                if (!_photoReading && _photoRT != null && _photoRT.IsCreated()) _photoRT.Release();
            }
        }

        public void PollPhoto()
        {
            if (!_photoReading) return;
            bool done;
            try { done = _photoReq.done; } catch { done = true; }
            if (!done) return;
            _photoReading = false;
            try
            {
                if (_photoReq.hasError) { Mod.Log.Error("Photo: GPU readback failed"); return; }
                int n = _photoW * _photoH * 4;
                if (_photoReq.layerDataSize < n) { Mod.Log.Error($"Photo: readback too small ({_photoReq.layerDataSize} < {n})"); return; }
                var rgba = new byte[n];
                Marshal.Copy(_photoReq.GetDataRaw(0), rgba, 0, n);
                Mod.Log.Msg("Photo: encoding PNG");
                PhotoSaver.SaveAsync(rgba, _photoW, _photoH, _photoReqTag);
            }
            catch (Exception e) { Mod.Log.Error("Photo readback failed: " + e.Message); }
            finally
            {
                _photoReqTag = null;
                if (_photoGraded != null) { RenderTexture.ReleaseTemporary(_photoGraded); _photoGraded = null; }
                if (_photoRT != null && _photoRT.IsCreated()) _photoRT.Release();
            }
        }

        private void DestroyRTs()
        {
            if (_cam != null) _cam.targetTexture = null;
            LeavePreviewHQ();
            if (_previewRT != null) { _previewRT.Release(); UnityEngine.Object.Destroy(_previewRT); _previewRT = null; }
            if (_photoRT != null) { _photoRT.Release(); UnityEngine.Object.Destroy(_photoRT); _photoRT = null; }
        }
    }
}
