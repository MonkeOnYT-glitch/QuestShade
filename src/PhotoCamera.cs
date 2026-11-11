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
    public enum CamMode { Hand, Selfie, FirstPerson, Helmet, Chest, PoliceCam, ThirdPerson }

    internal sealed partial class PhotoCamera
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
        public int LensHiddenLayer => _screenLayer;

        public bool Blocked;
        public void Toggle(Config cfg, Camera head) { if (Open) Close(); else if (!Blocked) OpenCam(cfg, head); }

        public void SetMode(CamMode m)
        {
            if (m == Mode) return;
            Mode = m;
            _hasLensPose = false;
            if (_held < 0 && !PropActive && _hasDevicePose) _pinned = true;
            _layoutMode = -1;
            Mod.Log.Msg($"Camera mode {m}: body {(PropActive ? (_propHand != 0 ? "held" : "placed") : "none")}, floating {(_held >= 0 ? "held" : _pinned ? "placed" : "on hand")}");
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
        public string ControlsHint;
        public bool MicOn;
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
            if (Time.unscaledTime >= _focusNext) { _focusNext = Time.unscaledTime + 0.1f; _focusHit = Focus.Raycast(t.position, t.forward, _screenLayer, ignore, _focus); }
            _focus = Focus.Smooth(_focus, _focusHit, dt);
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
                line1 = $"<color=#ff3030>\u25CF REC {s / 60}:{s % 60:00}</color>" + (MicOn ? "  <color=#80ff80>MIC</color>" : "");
                line2 = hand != 0 ? "HOLD TRIGGER = stop" : "grab it, then HOLD TRIGGER = stop";
            }
            else if (hand != 0)
            {
                line1 = "TRIGGER:  tap = <color=#ffa030>PHOTO</color>   hold = <color=#ff5050>RECORD</color>";
                line2 = (Mode == CamMode.Selfie ? "facing you (selfie)" : Mode == CamMode.Hand ? "facing forward" : ModeName(Mode))
                        + (MicOn ? "   <color=#80ff80>mic on</color>" : "   <color=#999999>mic off</color>");
            }
            else
            {
                line1 = "GRAB A HANDLE";
                line2 = "then TRIGGER: tap = photo, hold = record";
            }
            if (!string.IsNullOrEmpty(LabelExtra)) line1 = LabelExtra;
            string text = line1 + "\n<size=75%>" + line2 + "</size>";
            if (!string.IsNullOrEmpty(ControlsHint) && hand != 0 && !Recording) text += "\n<size=60%><color=#bbbbbb>" + ControlsHint + "</color></size>";
            if (text == _labelText) return;
            _labelText = text;
            try { _labelTmp.text = text; } catch { }
        }

        private static string ModeName(CamMode m) => m switch
        {
            CamMode.ThirdPerson => "mode: third person", CamMode.Chest => "mode: chest (bodycam)", CamMode.FirstPerson => "mode: first person (head cam)", CamMode.Helmet => "mode: helmet cam", CamMode.PoliceCam => "mode: police bodycam",
            _ => "mode: " + m.ToString().ToLowerInvariant()
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
            bool want = Open && _screenLayer >= 0 && BodyMode && HeadToLayer != null;
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
        private static readonly CamMode[] CycleOrder = { CamMode.Hand, CamMode.Selfie, CamMode.FirstPerson, CamMode.Helmet, CamMode.Chest, CamMode.PoliceCam, CamMode.ThirdPerson };
        public void CycleMode() => SetMode(CycleOrder[(Array.IndexOf(CycleOrder, Mode) + 1) % CycleOrder.Length]);

        private bool Detached => Mode == CamMode.ThirdPerson || BodyMode;
        private bool BodyMode => Mode == CamMode.Chest || Mode == CamMode.PoliceCam || Mode == CamMode.FirstPerson || Mode == CamMode.Helmet;

        public Func<Transform> ChestProvider;
        private bool _chestLogged;
        private float _appliedFov = -1f, _appliedNear = -1f;

        private float _wallNext, _wallFrac = 1f;
        private float _focusNext, _focusHit = 2f;
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
                if (Time.unscaledTime >= _wallNext)
                {
                    _wallNext = Time.unscaledTime + 0.066f;
                    var hits = Physics.SphereCastAll(pivot, 0.05f, d, len, mask, QueryTriggerInteraction.Ignore);
                    float best = float.MaxValue;
                    for (int i = 0; hits != null && i < hits.Length; i++)
                    {
                        var col = hits[i].collider;
                        if (col == null || col.attachedRigidbody != null) continue;
                        if (hits[i].distance < best) best = hits[i].distance;
                    }
                    _wallFrac = best < float.MaxValue ? Mathf.Clamp01(Mathf.Max(0f, best - 0.06f) / len) : 1f;
                }
                if (_wallFrac < 1f) return pivot + d * (len * _wallFrac);
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

    }
}
