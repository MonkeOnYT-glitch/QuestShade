using System.Runtime.InteropServices;
using System;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace QuestShade
{
    internal sealed class Effects
    {
        public bool Active { get; private set; }
        public Camera BoundCamera => _cam;
        public Transform BoundTransform { get { if (_camT == null || _camTFor != _cam) { _camTFor = _cam; _camT = _cam != null ? _cam.transform : null; } return _camT; } }
        private Transform _camT; private Camera _camTFor;
        public string Status { get; private set; } = "starting";

        private Material _mat;
        private Material _matMono;
        private bool _cbRegistered, _stereoReady, _headLogged;
        public Camera PhotoCamera;
        private bool _bundleTried;
        private Texture2D _lut;
        private float[] _userCube; private int _userN; private string _userKey;

        private Camera _cam;
        private UniversalAdditionalCameraData _data;
        private UniversalRenderer _renderer;
        private Material _origBlit;
        private IntermediateTextureMode _origMode;
        private bool _origPost, _hooked, _postChanged;

        private bool _dirty = true, _hasAnything, _hasHQ;
        private Vector4 _flareVec;
        private float _dirtAmt;
        private Texture2D _dirtTex;
        private bool LensFx => _flareVec.x > 0f || _dirtAmt > 0f || _godVec.x > 0f;
        private Vector4 _godVec;
        private Transform _sunT; private int _sunScene = -1; private float _sunLookAt;
        private float _baseExposure, _tonemap, _appliedEv = float.NaN, _appliedLift, _adaptStrength = -1f;
        private int _frame;

        private static bool IsQuest => Application.platform == RuntimePlatform.Android;

        public void MarkDirty() => _dirty = true;

        private bool EnsureShader()
        {
            if (_mat != null) return true;
            if (_bundleTried) return false;
            _bundleTried = true;
            if (!ShaderPack.Load()) { Status = ShaderPack.Error; return false; }
            _mat = new Material(ShaderPack.FinalBlit) { name = "QuestShade_FinalBlit_XR", hideFlags = HideFlags.HideAndDontSave };
            _matMono = new Material(ShaderPack.FinalBlit) { name = "QuestShade_FinalBlit", hideFlags = HideFlags.HideAndDontSave };
            ShaderPack.SetStereo(_matMono, false);
            if (ShaderPack.Post != null)
            {
                _matPre = new Material(ShaderPack.FinalBlit) { name = "QuestShade_Prepass", hideFlags = HideFlags.HideAndDontSave };
                _post = new Material(ShaderPack.Post) { name = "QuestShade_Post", hideFlags = HideFlags.HideAndDontSave };
            }
            return true;
        }

        private Material _matPre, _post;
        private float _bloomIntensity, _dofStrength, _dofMaxBlur, _dofBokeh;
        private Vector4 _bloomVec;
        public bool CanPost => Active && _post != null && _cfg != null;
        public bool PhotoHQ;
        private Config _cfg;
        private bool HQReady => Active && _post != null && _cfg != null && (_bloomIntensity > 0f || _dofStrength > 0f || LensFx);
        public bool WantsHQ => HQReady && _cfg.BloomDofCapture;
        public bool WantsPreviewHQ => HQReady && _cfg.BloomDofPreview;
        public bool WantsDepth => _dofStrength > 0f;

        private bool _liveDof, _depthForced, _origDepth;
        private void ApplyHeadsetDepth()
        {
            bool want = Active && _liveDof && _data != null;
            if (want == _depthForced) return;
            try
            {
                if (want) { _origDepth = _data.requiresDepthTexture; _data.requiresDepthTexture = true; }
                else if (_data != null) _data.requiresDepthTexture = _origDepth;
            }
            catch { }
            _depthForced = want;
        }

        private void SetLive(Config cfg, Ini p)
        {
            bool live = false;
            _liveDof = false;
            _matMono.SetVector(ID.Live, Vector4.zero);
            if (!live) { _mat.SetVector(ID.Live, Vector4.zero); return; }
            int eyeH = XRSettings.enabled && XRSettings.eyeTextureHeight > 0 ? XRSettings.eyeTextureHeight : (_cam != null ? _cam.pixelHeight : 1080);
            float blur = Mathf.Min(_dofMaxBlur, 14f) * eyeH / 1080f;
            _mat.SetVector(ID.Live, new Vector4(_bloomIntensity * 0.6f, 0, blur, _dofBokeh));
            float thr = Mathf.Clamp(p.Float("Bloom", "Threshold", 0.85f), 0f, 4f);
            _mat.SetVector(ID.LiveBloom, new Vector4(thr, thr * Mathf.Clamp01(p.Float("Bloom", "SoftKnee", 0.5f)), 0.02f + 0.05f * Mathf.Clamp01(p.Float("Bloom", "Scatter", 0.7f)), 0));
            var tint = p.Color("Bloom", "Tint", Color.white);
            _mat.SetVector(ID.LiveTint, new Vector4(tint.r, tint.g, tint.b, 1));
            Mod.Log.Msg($"Headset bloom {(_bloomIntensity > 0 ? "on" : "off")}, depth of field {(_liveDof ? "on" : "off")} (light version)");
        }
        private readonly RenderTexture[] _down = new RenderTexture[6], _up = new RenderTexture[6];

        private static readonly int SourceTexId = Shader.PropertyToID("_SourceTex");
        private Material _matGrade;

        public bool GradeAgain(RenderTexture src, RenderTexture dst)
        {
            if (!Active || _matMono == null || src == null || dst == null) return false;
            try
            {
                _matGrade ??= new Material(_matMono) { name = "QuestShade_GradeAgain", hideFlags = HideFlags.HideAndDontSave };
                _matGrade.CopyPropertiesFromMaterial(_matMono);
                foreach (var id in new[] { ID.Vignette, ID.Sharpen, ID.CA, ID.Distort, ID.Grain, ID.Scan, ID.Glitch, ID.Noise, ID.Posterize, ID.Curve, ID.Hurt })
                    _matGrade.SetVector(id, Vector4.zero);
                _matGrade.SetVector(ID.Texel, new Vector4(1f / dst.width, 1f / dst.height, dst.width, dst.height));
                _matGrade.SetTexture(SourceTexId, src);
                Graphics.Blit(src, dst, _matGrade);
                _matGrade.SetTexture(SourceTexId, null);
                return true;
            }
            catch (Exception e) { Mod.Log.Warning("Second grade pass failed: " + e.Message); return false; }
        }

        public void ProcessHQ(RenderTexture raw, RenderTexture dst, bool extras = true, bool light = false)
        {
            if (raw == null || dst == null || _post == null) return;
            int w = raw.width, h = raw.height;
            RenderTexture src = raw, dof = null, dof2 = null, flare = null, flare2 = null, god = null;
            int levels = 0;
            try
            {
                var tex = new Vector4(1f / dst.width, 1f / dst.height, dst.width, dst.height);
                _post.SetVector(ID.Texel, tex);
                bool ss = raw.width > dst.width + 1;
                _post.SetVector(ID.SS, ss ? new Vector4(1, 0.25f / dst.width, 0.25f / dst.height, 0) : Vector4.zero);
                if (extras && _bloomIntensity > 0f && !_calDone) Calibrate(raw);
                var bloomV = BloomEff();
                _post.SetVector(ID.Bloom, extras ? bloomV : Vector4.zero);
                bool lens = extras && LensFx;
                _post.SetVector(ID.God, Vector4.zero);
                _post.SetVector(ID.Flare, lens ? _flareVec : Vector4.zero);
                _post.SetVector(ID.Dirt, lens ? new Vector4(_dirtAmt, 0, 0, 0) : Vector4.zero);
                if (lens && _dirtAmt > 0f) _post.SetTexture(ID.DirtTex, DirtTexture());
                if (extras && _dofStrength > 0f)
                {
                    _post.SetVector(ID.DofPx, new Vector4(_dofMaxBlur * h / 1080f * MotionScale, _dofBokeh, light ? 20 : 0, 0));
                    dof = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                    Graphics.Blit(raw, dof, _post, 0);
                    if (light) src = dof;
                    else
                    {
                        dof2 = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                        Graphics.Blit(dof, dof2, _post, 5);
                        src = dof2;
                    }
                }
                if (extras && (_bloomIntensity > 0f || lens))
                {
                    int bw = Math.Max(1, w / 2), bh = Math.Max(1, h / 2);
                    for (; levels < _down.Length && Math.Min(bw, bh) >= 8; levels++, bw /= 2, bh /= 2)
                        _down[levels] = RenderTexture.GetTemporary(bw, bh, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                    Graphics.Blit(src, _down[0], _post, 1);
                    for (int i = 1; i < levels; i++) Graphics.Blit(_down[i - 1], _down[i], _post, 2);
                    var low = _down[levels - 1];
                    for (int i = levels - 2; i >= 0; i--)
                    {
                        _up[i] = RenderTexture.GetTemporary(_down[i].width, _down[i].height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                        _post.SetTexture(ID.BloomLow, low);
                        Graphics.Blit(_down[i], _up[i], _post, 3);
                        low = _up[i];
                    }
                    _post.SetTexture(ID.BloomTex, low);
                    if (lens && _flareVec.x > 0f && levels >= 2)
                    {
                        var fsrc = levels >= 3 ? _down[2] : _down[1];
                        flare = RenderTexture.GetTemporary(fsrc.width, fsrc.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                        Graphics.Blit(fsrc, flare, _post, 6);
                        flare2 = RenderTexture.GetTemporary(Math.Max(1, flare.width / 2), Math.Max(1, flare.height / 2), 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                        Graphics.Blit(flare, flare2, _post, 2);
                        _post.SetTexture(ID.FlareTex, flare2);
                    }
                    else _post.SetTexture(ID.FlareTex, Texture2D.blackTexture);
                    if (lens && _godVec.x > 0f && levels >= 2 && SunOnScreen(PhotoCamera, out var sunUv))
                    {
                        _post.SetVector(ID.God, _godVec);
                        _post.SetVector(ID.Sun, sunUv);
                        god = RenderTexture.GetTemporary(_down[1].width, _down[1].height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                        Graphics.Blit(_down[1], god, _post, 7);
                        _post.SetTexture(ID.GodTex, god);
                    }
                    else _post.SetVector(ID.God, Vector4.zero);
                    float norm = 0, wgt = 1; for (int i = 0; i < levels; i++) { norm += wgt; wgt *= bloomV.w; }
                    _post.SetVector(ID.Bloom, new Vector4(bloomV.x * 1.8f / Mathf.Max(1f, norm), bloomV.y, bloomV.z, bloomV.w));
                }
                Graphics.Blit(src, dst, _post, 4);
            }
            catch (Exception e) { Mod.Log.Error("Bloom/DOF pass failed: " + e.Message); Graphics.Blit(raw, dst); }
            finally
            {
                if (dof != null) RenderTexture.ReleaseTemporary(dof);
                if (dof2 != null) RenderTexture.ReleaseTemporary(dof2);
                if (flare != null) RenderTexture.ReleaseTemporary(flare);
                if (flare2 != null) RenderTexture.ReleaseTemporary(flare2);
                if (god != null) RenderTexture.ReleaseTemporary(god);
                for (int i = 0; i < _down.Length; i++)
                {
                    if (_down[i] != null) { RenderTexture.ReleaseTemporary(_down[i]); _down[i] = null; }
                    if (_up[i] != null) { RenderTexture.ReleaseTemporary(_up[i]); _up[i] = null; }
                }
            }
        }

        private Vector4 _dofV; private bool _autoFocus; private float _headFocus = -1f;
        public bool AutoFocus => _autoFocus && _dofStrength > 0f;
        private RenderTexture _calRT;
        private AsyncGPUReadbackRequest _calReq;
        private bool _calPending, _calDone;
        private float _calThr, _calInt = 1f;
        private readonly byte[] _calBytes = new byte[64 * 36 * 4];

        public bool CalibrationWanted => !_calDone && !_calPending && _bloomIntensity > 0f;

        public void ResetCalibration() { _calDone = _calPending = false; _calThr = 0f; _calInt = 1f; }

        public void Calibrate(RenderTexture raw)
        {
            if (_calDone || _calPending || raw == null) return;
            try
            {
                if (_calRT == null)
                {
                    _calRT = new RenderTexture(64, 36, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "QuestShade_Calib", hideFlags = HideFlags.HideAndDontSave };
                    _calRT.Create();
                }
                Graphics.Blit(raw, _calRT);
                _calReq = AsyncGPUReadback.Request(_calRT, 0, (Il2CppSystem.Action<AsyncGPUReadbackRequest>)null);
                _calPending = true;
            }
            catch (Exception e) { _calDone = true; Mod.Log.Warning("Bloom calibration unavailable: " + e.Message); }
        }

        private void CalibrationTick()
        {
            if (!_calPending || !_calReq.done) return;
            _calPending = false;
            if (_calReq.hasError) return;
            try
            {
                Marshal.Copy(_calReq.GetDataRaw(0), _calBytes, 0, _calBytes.Length);
                int n = _calBytes.Length / 4; var lum = new float[n]; double sum = 0;
                for (int i = 0; i < n; i++)
                {
                    float l = (0.2126f * _calBytes[i * 4] + 0.7152f * _calBytes[i * 4 + 1] + 0.0722f * _calBytes[i * 4 + 2]) / 255f;
                    lum[i] = l; sum += l;
                }
                Array.Sort(lum);
                float mean = (float)(sum / n), p90 = lum[(int)(n * 0.9f)], p95 = lum[(int)(n * 0.95f)];
                if (mean < 0.02f && p90 < 0.05f) return;
                float bright = Mathf.InverseLerp(0.3f, 0.7f, mean);
                _calThr = Mathf.Clamp(Mathf.Lerp(p90 * 0.85f, p95 * 0.97f, bright), 0f, Mathf.Lerp(0.85f, 0.95f, bright));
                _calInt = Mathf.Clamp(0.3f / Mathf.Max(mean, 0.01f), 0.4f, 1f);
                _calDone = true;
                Mod.Log.Msg($"Bloom calibrated for this level: brightness {mean:0.00} (top 10% {p90:0.00}) -> threshold {_calThr:0.00}, strength x{_calInt:0.00}");
            }
            catch (Exception e) { _calDone = true; Mod.Log.Warning("Bloom calibration failed: " + e.Message); }
        }

        private Vector4 BloomEff()
        {
            float thr = Mathf.Max(_bloomVec.y, _calThr);
            return new Vector4(_bloomVec.x * _calInt, thr, thr * 0.8f, _bloomVec.w);
        }

        public float MotionScale = 1f;

        public void SetCaptureFocus(float metres)
        {
            if (!AutoFocus || _matPre == null) return;
            var v = _dofV; v.y = metres; _matPre.SetVector(ID.Dof, v);
        }

        public void TickHeadsetFocus(float dt)
        {
            if (!_liveDof || !AutoFocus || _cam == null || _mat == null) return;
            _headFocus = Focus.Smooth(_headFocus, Focus.Raycast(_cam.transform.position, _cam.transform.forward, -1, null, _headFocus), dt);
            var v = _dofV; v.y = _headFocus; _mat.SetVector(ID.Dof, v);
        }

        private void RebuildHQ(Ini p)
        {
            if (_post == null) { _bloomIntensity = _dofStrength = 0f; return; }
            _bloomIntensity = p.Enabled("Bloom") ? Mathf.Max(0f, p.Float("Bloom", "Intensity", 0f)) * (_cfg != null ? _cfg.BloomStrength : 1f) : 0f;
            float thr = Mathf.Clamp(p.Float("Bloom", "Threshold", 0.85f), 0f, 4f) * 0.5f;
            float knee = thr * 0.8f;
            _bloomIntensity *= 1.0f;
            _bloomVec = new Vector4(_bloomIntensity, thr, knee, Mathf.Max(0.8f, Mathf.Clamp01(p.Float("Bloom", "Scatter", 0.7f))));
            _post.SetVector(ID.Bloom, _bloomVec);
            var tint = p.Color("Bloom", "Tint", Color.white);
            _post.SetVector(ID.BloomTint, new Vector4(tint.r, tint.g, tint.b, 1));
            _post.SetTexture(ID.BloomTex, Texture2D.blackTexture);

            float fl = p.Enabled("LensFlare") ? Mathf.Clamp(p.Float("LensFlare", "Intensity", 0f), 0f, 3f) : 0f;
            _flareVec = new Vector4(fl * 0.18f, Mathf.Clamp(p.Float("LensFlare", "Spacing", 0.35f), 0.05f, 1f),
                                    Mathf.Clamp(p.Float("LensFlare", "Halo", 0.5f), 0f, 2f), Mathf.Clamp(p.Float("LensFlare", "Fringe", 0.5f), 0f, 2f));
            _dirtAmt = p.Enabled("LensDirt") ? Mathf.Clamp(p.Float("LensDirt", "Intensity", 0f), 0f, 3f) : 0f;
            _post.SetTexture(ID.FlareTex, Texture2D.blackTexture);
            float gr = p.Enabled("GodRays") ? Mathf.Clamp(p.Float("GodRays", "Intensity", 0f), 0f, 3f) : 0f;
            _godVec = new Vector4(gr, Mathf.Clamp(p.Float("GodRays", "Length", 0.6f), 0.1f, 1f), Mathf.Clamp(p.Float("GodRays", "Threshold", 0.7f), 0f, 1.5f), 0);
            _post.SetTexture(ID.GodTex, Texture2D.blackTexture);

            _dofStrength = p.Enabled("DepthOfField") ? Mathf.Max(0f, p.Float("DepthOfField", "Strength", 1f)) : 0f;
            _dofMaxBlur = Mathf.Clamp(p.Float("DepthOfField", "MaxBlur", 20f) * (_cfg != null ? _cfg.BlurStrength : 1f), 0f, 48f);
            if (_dofMaxBlur < 0.5f) _dofStrength = 0f;
            _dofBokeh = Mathf.Clamp(p.Float("DepthOfField", "BokehIntensity", 0.3f), 0f, 2f);
            float focus = p.Float("DepthOfField", "FocusDistance", 0f);
            var afc = p.Vec2("DepthOfField", "AutofocusCenter", new Vector2(0.5f, 0.5f));

            _matPre.CopyPropertiesFromMaterial(_matMono);
            ShaderPack.SetStereo(_matPre, false);
            _matPre.EnableKeyword("QS_PREPASS");
            var dofV = new Vector4(_dofStrength, focus, p.Float("DepthOfField", "FarCurve", 1.5f), p.Float("DepthOfField", "NearCurve", 6f));
            var afV = new Vector4(afc.x, afc.y, Mathf.Clamp01(p.Float("DepthOfField", "AutofocusRadius", 0.6f)), Mathf.Clamp01(p.Float("DepthOfField", "NearStrength", 0.25f)));
            _matPre.SetVector(ID.Dof, dofV); _matPre.SetVector(ID.DofAF, afV);
            _dofV = dofV; _autoFocus = focus <= 0f; _headFocus = -1f;
            _mat.SetVector(ID.Dof, dofV); _mat.SetVector(ID.DofAF, afV);

            foreach (var id in ID.Stack) _post.SetVector(id, _mat.GetVector(id));
            if (_lut != null) _post.SetTexture(ID.Lut, _lut);
            if (_bloomIntensity > 0f || _dofStrength > 0f || LensFx)
                Mod.Log.Msg($"Photo/video extras: bloom {_bloomIntensity:0.##}, depth of field {_dofStrength:0.##} (focus {(focus > 0 ? focus.ToString("0.0") + " m" : "auto")}), lens flare {_flareVec.x:0.##}, lens dirt {_dirtAmt:0.##}");
        }
        public void EnsureCamera(Config cfg)
        {
            var main = Camera.main;
            if (main == null) return;
            if (main != _cam)
            {
                Unhook();
                _cam = main;
                _data = main.GetComponent<UniversalAdditionalCameraData>();
                Mod.Log.Msg($"Bound to camera '{main.name}'");
            }
            if (_data == null) return;

            bool want = cfg.Enabled && _hasAnything && EnsureShader();
            if (want) Hook(cfg); else Unhook();
            Active = want && _hooked;
            ApplyHeadsetDepth();
        }

        private void Hook(Config cfg)
        {
            UniversalRenderer r = null;
            try { r = _data.scriptableRenderer?.TryCast<UniversalRenderer>(); } catch { }
            if (r == null) { Status = "no URP renderer on camera"; return; }

            if (_renderer != null && _renderer.Pointer != r.Pointer) Unhook();
            var fbp = r.m_FinalBlitPass;
            if (fbp == null) { Status = "renderer has no final blit"; return; }

            if (!_hooked)
            {
                _renderer = r;
                _origBlit = fbp.m_BlitMaterial;
                _origMode = r.m_IntermediateTextureMode;
                _hooked = true;
                Mod.Log.Msg($"Hooked final blit (intermediate texture was {_origMode}, shader was {(_origBlit != null ? _origBlit.shader.name : "null")})");
            }
            if (!_cbRegistered)
            {
                RenderPipelineManager.add_beginCameraRendering(
                    DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                        new Action<ScriptableRenderContext, Camera>(OnBeginCamera)));
                _cbRegistered = true;
            }

            if (!IsQuest)
            {
                if (!_postChanged) { _origPost = _data.renderPostProcessing; _postChanged = true; }
                if (_data.renderPostProcessing) _data.renderPostProcessing = false;
            }
            Status = "active";
        }

        private void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
        {
            try
            {
                if (!_hooked || _renderer == null || cam == null) return;
                var fbp = _renderer.m_FinalBlitPass;
                if (fbp == null) return;
                bool head = _cam != null && cam.Pointer == _cam.Pointer;
                bool photo = !head && PhotoCamera != null && cam.Pointer == PhotoCamera.Pointer;
                bool stereo = head && ShaderPack.HeadsetActive;
                if (stereo && !_stereoReady)
                {
                    ShaderPack.SetStereo(_mat, true, procedural: true);
                    _stereoReady = true;
                    Mod.Log.Msg($"XR stereo: {XRSettings.stereoRenderingMode}, multiview={SystemInfo.supportsMultiview}");
                }
                if (head && !_headLogged)
                {
                    _headLogged = true;
                    Mod.Log.Msg($"Headset camera path: {(stereo ? "VR stereo" : "flat")} (xr={XRSettings.enabled}, device={XRSettings.isDeviceActive}, eye={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, cam.stereo={cam.stereoEnabled})");
                }
                if (photo) CamDiag.Step(PhotoHQ ? "lens render (bloom/dof prepass)" : "lens render");
                fbp.m_BlitMaterial = stereo ? _mat : head ? _matMono : photo ? (PhotoHQ && _matPre != null ? _matPre : _matMono) : _origBlit;
                if (photo && cam.targetTexture != null)
                {
                    var rt = cam.targetTexture;
                    _matMono.SetVector(ID.Texel, new Vector4(1f / rt.width, 1f / rt.height, rt.width, rt.height));
                }
                _renderer.m_IntermediateTextureMode = (head || photo) ? IntermediateTextureMode.Always : _origMode;
                SetVrs(!photo);
            }
            catch { }
        }

        private System.Collections.Generic.List<(ScriptableRendererFeature f, bool on)> _vrs;
        private bool _vrsOff;
        private void SetVrs(bool on)
        {
            if (_renderer == null) return;
            if (_vrs == null)
            {
                _vrs = new System.Collections.Generic.List<(ScriptableRendererFeature, bool)>();
                try
                {
                    var list = _renderer.rendererFeatures;
                    for (int i = 0; list != null && i < list.Count; i++)
                    {
                        var ft = list[i];
                        if (ft == null) continue;
                        string n = ""; try { n = ft.GetIl2CppType().Name; } catch { }
                        if (n.IndexOf("VRS", StringComparison.OrdinalIgnoreCase) >= 0) _vrs.Add((ft, ft.isActive));
                    }
                    Mod.Log.Msg($"Camera: found {_vrs.Count} VRS render feature(s), off for the in-game camera");
                }
                catch (Exception e) { Mod.Log.Warning("Camera: VRS feature lookup failed: " + e.Message); }
            }
            bool off = !on;
            if (off == _vrsOff) return;
            _vrsOff = off;
            foreach (var (ft, orig) in _vrs) { try { if (ft != null) ft.SetActive(off ? false : orig); } catch { } }
        }

        private void Unhook()
        {
            if (_vrsOff) SetVrs(true);
            _vrs = null;
            if (_hooked)
            {
                try
                {
                    if (_renderer != null)
                    {
                        var fbp = _renderer.m_FinalBlitPass;
                        if (fbp != null && _origBlit != null) fbp.m_BlitMaterial = _origBlit;
                        _renderer.m_IntermediateTextureMode = _origMode;
                    }
                }
                catch {  }
                Mod.Log.Msg("Final blit restored to the game's original");
            }
            if (_postChanged && _data != null) { try { _data.renderPostProcessing = _origPost; } catch { } }
            if (_depthForced && _data != null) { try { _data.requiresDepthTexture = _origDepth; } catch { } }
            _depthForced = false;
            _postChanged = false;
            _hooked = false; _renderer = null; _origBlit = null;
            Active = false;
        }

        public void Tick(Config cfg)
        {
            if (_dirty)
            {
                _dirty = false;
                try { Rebuild(cfg); }
                catch (Exception e) { Mod.Log.Error("Failed to apply preset: " + e); }
                EnsureCamera(cfg);
            }
            CalibrationTick();
            if (!Active || _mat == null) return;
            TickEyeAdaptation(cfg);
            TickGodRays(cfg);

            _frame++;
            var tv = new Vector4(Time.unscaledTime % 1000f, _frame % 4096, 0, 0);
            _mat.SetVector(ID.Time, tv); _matMono.SetVector(ID.Time, tv);
            if (_post != null && _hasHQ) _post.SetVector(ID.Time, tv);
            if ((_frame & 31) == 0) SetTexel();
        }

        private Transform Sun()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene == _sunScene && (_sunT != null || Time.unscaledTime < _sunLookAt)) return _sunT;
            _sunScene = scene; _sunLookAt = Time.unscaledTime + 10f; _sunT = null;
            try
            {
                var s = RenderSettings.sun;
                if (s != null) _sunT = s.transform;
                else foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>()) if (l != null && l.type == LightType.Directional) { _sunT = l.transform; break; }
            }
            catch { }
            return _sunT;
        }

        private bool SunOnScreen(Camera cam, out Vector4 uv)
        {
            uv = Vector4.zero;
            var sun = Sun();
            if (cam == null || sun == null) return false;
            var p = cam.WorldToViewportPoint(cam.transform.position - sun.forward * 1000f);
            if (p.z <= 0f) return false;
            uv = new Vector4(p.x, p.y, SunFade(p.x, p.y), 0);
            return uv.z > 0f;
        }
        private static float SunFade(float x, float y) => Mathf.Clamp01(1f - (Mathf.Max(Mathf.Abs(x - 0.5f), Mathf.Abs(y - 0.5f)) - 0.6f) / 0.5f);

        private void TickGodRays(Config cfg)
        {
            bool want = false;
            if (!want) { if (_godOn) { _mat.SetVector(ID.God, Vector4.zero); _godOn = false; } return; }
            var sun = Sun();
            if (sun == null) { if (_godOn) { _mat.SetVector(ID.God, Vector4.zero); _godOn = false; } return; }
            Vector3 toSun = -sun.forward;
            Vector4 l = Vector4.zero, r = Vector4.zero;
            if (XRSettings.enabled && _cam.stereoEnabled)
            {
                l = EyeSun(Camera.StereoscopicEye.Left, toSun);
                r = EyeSun(Camera.StereoscopicEye.Right, toSun);
            }
            else
            {
                var p = _cam.WorldToViewportPoint(_cam.transform.position + toSun * 1000f);
                if (p.z > 0f) l = new Vector4(p.x, p.y, SunFade(p.x, p.y), 0);
                r = l;
            }
            _mat.SetVector(ID.SunL, l); _mat.SetVector(ID.SunR, r);
            _mat.SetVector(ID.God, new Vector4(_godVec.x * 0.7f, _godVec.y, _godVec.z, 0));
            _godOn = true;
        }
        private bool _godOn;
        private Vector4 EyeSun(Camera.StereoscopicEye eye, Vector3 toSun)
        {
            var v = _cam.GetStereoViewMatrix(eye);
            var pr = _cam.GetStereoProjectionMatrix(eye);
            var dir = v.MultiplyVector(toSun);
            var clip = pr * new Vector4(dir.x, dir.y, dir.z, 0f);
            if (clip.w <= 1e-4f) return Vector4.zero;
            float x = clip.x / clip.w * 0.5f + 0.5f, y = clip.y / clip.w * 0.5f + 0.5f;
            return new Vector4(x, y, SunFade(x, y), 0);
        }

        private float _hurtApplied = -1f;
        public void SetHurt(float k)
        {
            if (_matMono == null || Mathf.Abs(k - _hurtApplied) < 0.005f) return;
            _hurtApplied = k;
            var v = new Vector4(k, 0, 0, 0);
            _matMono.SetVector(ID.Hurt, v);
            if (_matPre != null) _matPre.SetVector(ID.Hurt, v);
        }

        private void TickEyeAdaptation(Config cfg)
        {
            float strength = _adaptStrength >= 0f ? _adaptStrength : cfg.EyeAdaptStrength;
            EyeAdaptation.Tick(BoundTransform, cfg.EyeAdapt, strength, cfg.EyeAdaptSpeed, Time.unscaledDeltaTime);
            float ev = EyeAdaptation.Ev;
            float lift = _adaptStrength == 0f ? 0f : 0.6f * cfg.DarkMaps * EyeAdaptation.DarkMap;
            if (!float.IsNaN(_appliedEv) && Mathf.Abs(ev - _appliedEv) < 0.004f && Mathf.Abs(lift - _appliedLift) < 0.004f) return;
            _appliedEv = ev; _appliedLift = lift;
            var tone = new Vector4(MathF.Pow(2f, _baseExposure + ev), _tonemap, lift, 0);
            _mat.SetVector(ID.Tone, tone);
            _matMono.SetVector(ID.Tone, tone);
            if (_matPre != null) _matPre.SetVector(ID.Tone, tone);
            if (_post != null) _post.SetVector(ID.Tone, tone);
        }

        private Texture2D DirtTexture()
        {
            if (_dirtTex != null) return _dirtTex;
            const int W = 320, H = 180;
            var v = new float[W * H];
            var rng = new System.Random(1337);
            void Blob(float cx, float cy, float r, float a)
            {
                int x0 = Math.Max(0, (int)(cx - r)), x1 = Math.Min(W - 1, (int)(cx + r)), y0 = Math.Max(0, (int)(cy - r)), y1 = Math.Min(H - 1, (int)(cy + r));
                for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                {
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / r;
                    if (d < 1f) v[y * W + x] += a * (1f - d * d) * (1f - d * d);
                }
            }
            for (int i = 0; i < 40; i++) Blob((float)rng.NextDouble() * W, (float)rng.NextDouble() * H, 6f + (float)rng.NextDouble() * 28f, 0.15f + (float)rng.NextDouble() * 0.45f);
            for (int i = 0; i < 160; i++) Blob((float)rng.NextDouble() * W, (float)rng.NextDouble() * H, 1f + (float)rng.NextDouble() * 2.5f, 0.4f + (float)rng.NextDouble() * 0.6f);
            for (int s = 0; s < 3; s++)
            {
                float sx = (float)rng.NextDouble() * W, sy = (float)rng.NextDouble() * H, ang = (float)(rng.NextDouble() * Math.PI);
                for (int k = 0; k < 60; k++) Blob(sx + MathF.Cos(ang) * k * 2.5f, sy + MathF.Sin(ang) * k * 2.5f, 5f, 0.05f);
            }
            var px = new Color32[W * H];
            for (int i = 0; i < px.Length; i++) { byte b = (byte)(Mathf.Clamp01(v[i]) * 255f); px[i] = new Color32(b, b, b, 255); }
            _dirtTex = new Texture2D(W, H, TextureFormat.RGBA32, false, true) { name = "QuestShade_LensDirt", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            _dirtTex.SetPixels32(px);
            _dirtTex.Apply(false, true);
            return _dirtTex;
        }

        private void SetTexel()
        {
            int w, h;
            if (XRSettings.enabled && XRSettings.eyeTextureWidth > 0) { w = XRSettings.eyeTextureWidth; h = XRSettings.eyeTextureHeight; }
            else if (_cam != null) { w = _cam.pixelWidth; h = _cam.pixelHeight; }
            else { w = Screen.width; h = Screen.height; }
            _mat.SetVector(ID.Texel, new Vector4(1f / Math.Max(1, w), 1f / Math.Max(1, h), w, h));
            if (PhotoCamera != null && PhotoCamera.targetTexture != null)
            {
                var rt = PhotoCamera.targetTexture;
                _matMono.SetVector(ID.Texel, new Vector4(1f / rt.width, 1f / rt.height, rt.width, rt.height));
            }
            else _matMono.SetVector(ID.Texel, new Vector4(1f / Math.Max(1, w), 1f / Math.Max(1, h), w, h));
        }

        private static readonly Ini DefaultExtras = Ini.Parse(@"
[Bloom]
Intensity = 0.6
Threshold = 0.9
SoftKnee = 0.5
Scatter = 0.7
[DepthOfField]
Strength = 1.0
FocusDistance = 0
MaxBlur = 16
FarCurve = 1.8
NearCurve = 6
BokehIntensity = 0.3
");

        private void Rebuild(Config cfg)
        {
            if (!EnsureShader()) { _hasAnything = false; return; }
            var p = cfg.Preset;
            int n = 0;
            var m = _mat;

            bool grade = Grade.HasGrading(p);
            if (grade)
            {
                LoadUserLut(cfg);
                float contrib = p.Enabled("LUT") ? Mathf.Clamp01(p.Float("LUT", "Contribution", 1f)) : 0f;
                _lut = Grade.Bake(p, _userCube, _userN, contrib, _lut);
                m.SetTexture(ID.Lut, _lut);
                m.SetVector(ID.LutParams, new Vector4(1f / (Grade.Size * Grade.Size), 1f / Grade.Size, Grade.Size - 1, 1));
                n++;
            }
            else m.SetVector(ID.LutParams, Vector4.zero);

            float exposure = p.Enabled("Color") ? p.Float("Color", "Exposure", 0f) : 0f;
            float tm = 0;
            if (p.Enabled("Tonemapping"))
            {
                var mode = (p.Str("Tonemapping", "Mode", "Neutral") ?? "").ToLowerInvariant();
                tm = mode == "aces" ? 2 : mode == "none" ? 0 : 1;
            }
            _baseExposure = exposure; _tonemap = tm; _appliedEv = float.NaN;
            _adaptStrength = p.Has("EyeAdaptation") ? (p.Enabled("EyeAdaptation") ? Mathf.Clamp(p.Float("EyeAdaptation", "Strength", cfg.EyeAdaptStrength), 0f, 1.5f) : 0f) : -1f;
            m.SetVector(ID.Tone, new Vector4(MathF.Pow(2f, exposure + EyeAdaptation.Ev), tm, 0, 0));
            if (exposure != 0 || tm > 0 || ((cfg.EyeAdapt || cfg.DarkMaps > 0f) && _adaptStrength != 0f)) n++;

            if (p.Enabled("Vignette") && string.Equals(p.Str("Vignette", "Mode", ""), "reshade", StringComparison.OrdinalIgnoreCase))
            {
                float aspect = _cam != null && _cam.pixelHeight > 0 ? _cam.pixelWidth / (float)_cam.pixelHeight : 16f / 9f;
                if (XRSettings.enabled && XRSettings.eyeTextureHeight > 0) aspect = XRSettings.eyeTextureWidth / (float)XRSettings.eyeTextureHeight;
                float radius = p.Float("Vignette", "Radius", 2f); if (Mathf.Abs(radius) < 0.01f) radius = 0.01f;
                m.SetVector(ID.Vignette, new Vector4(1f / radius, p.Float("Vignette", "Ratio", 1f), aspect, 1));
                var vc = p.Vec2("Vignette", "Center", new Vector2(.5f, .5f));
                m.SetVector(ID.VignetteCenter, new Vector4(vc.x, vc.y, p.Float("Vignette", "Amount", -1f), p.Float("Vignette", "Slope", 2f)));
                n++;
            }
            else if (p.Enabled("Vignette") && p.Float("Vignette", "Intensity", 0f) > 0f)
            {
                float aspect = _cam != null && _cam.pixelHeight > 0 ? _cam.pixelWidth / (float)_cam.pixelHeight : 1f;
                if (XRSettings.enabled && XRSettings.eyeTextureHeight > 0) aspect = XRSettings.eyeTextureWidth / (float)XRSettings.eyeTextureHeight;
                bool rounded = p.Bool("Vignette", "Rounded", false);
                m.SetVector(ID.Vignette, new Vector4(p.Float("Vignette", "Intensity", 0f) * 3f, p.Float("Vignette", "Smoothness", 0.2f) * 5f, rounded ? aspect : 1f, 0));
                var vc = p.Color("Vignette", "Color", Color.black);
                m.SetVector(ID.VignetteColor, new Vector4(vc.r, vc.g, vc.b, 1));
                var c = p.Vec2("Vignette", "Center", new Vector2(.5f, .5f));
                m.SetVector(ID.VignetteCenter, new Vector4(c.x, c.y, 0, 0));
                n++;
            }
            else m.SetVector(ID.Vignette, Vector4.zero);

            n += Set(m, ID.Sharpen, p, "Sharpen", s => new Vector4(Mathf.Clamp01(p.Float(s, "Amount", 0.5f)), 0, 0, 0), v => v.x > 0);
            n += Set(m, ID.CA, p, "ChromaticAberration", s => new Vector4(p.Float(s, "Intensity", 0f), 0, 0, 0), v => v.x > 0);
            n += Set(m, ID.Distort, p, "LensDistortion", s => new Vector4(Mathf.Clamp(p.Float(s, "Intensity", 0f), -1f, 1f), p.Float(s, "Scale", 1f), 0, 0), v => v.x != 0);
            n += Set(m, ID.Grain, p, "FilmGrain", s => new Vector4(p.Float(s, "Intensity", 0f), p.Float(s, "Response", 0.8f), p.Float(s, "Size", 1.5f), p.Bool(s, "Colored", false) ? 1 : 0), v => v.x > 0);
            n += Set(m, ID.Scan, p, "Scanlines", s => new Vector4(Mathf.Clamp01(p.Float(s, "Intensity", 0.3f)), p.Float(s, "Count", 240f), p.Float(s, "Speed", 0f), p.Float(s, "RollingBar", 0f)), v => v.x > 0);
            n += Set(m, ID.Glitch, p, "Glitch", s => new Vector4(Mathf.Clamp01(p.Float(s, "Amount", 0.1f)), p.Float(s, "Rows", 40f), p.Float(s, "Speed", 12f), 0), v => v.x > 0);
            n += Set(m, ID.Noise, p, "Noise", s => new Vector4(Mathf.Clamp01(p.Float(s, "Intensity", 0.1f)), 0, 0, 0), v => v.x > 0);
            n += Set(m, ID.Posterize, p, "Posterize", s => new Vector4(p.Float(s, "Levels", 0f), 0, 0, 0), v => v.x > 1);
            n += Set(m, ID.Curve, p, "CRT", s => new Vector4(Mathf.Clamp01(p.Float(s, "Curvature", 0.3f)), 0, 0, 0), v => v.x > 0);

            _matMono.CopyPropertiesFromMaterial(_mat);
            _matMono.SetVector(ID.God, Vector4.zero);
            _hurtApplied = -1f;
            ShaderPack.SetStereo(_matMono, false);
            SetTexel();

            _cfg = cfg;
            if (cfg.BloomDofAll && p.Str("Import", "Answer", "") != "compress") p = p.WithFallback(DefaultExtras, "Bloom", "DepthOfField");
            try { RebuildHQ(p); } catch (Exception e) { _bloomIntensity = _dofStrength = 0f; Mod.Log.Warning("Bloom/DOF setup failed: " + e.Message); }
            try { SetLive(cfg, p); } catch (Exception e) { _liveDof = false; Mod.Log.Warning("Headset bloom/DOF setup failed: " + e.Message); }
            _hasHQ = _bloomIntensity > 0f || _dofStrength > 0f || LensFx;
            if (_hasHQ) n++;
            _hasAnything = n > 0;
            Mod.Log.Msg($"Preset '{cfg.PresetName}' applied ({n} effect{(n == 1 ? "" : "s")})");
        }

        private static int Set(Material m, int id, Ini p, string section, Func<string, Vector4> read, Func<Vector4, bool> on)
        {
            var v = p.Enabled(section) ? read(section) : Vector4.zero;
            if (!on(v)) v = Vector4.zero;
            m.SetVector(id, v);
            return on(v) ? 1 : 0;
        }

        private void LoadUserLut(Config cfg)
        {
            var path = cfg.LutPath();
            if (path == null) { _userCube = null; _userKey = null; return; }
            string key = path + "|" + SafeMtime(path);
            if (key == _userKey) return;
            try
            {
                (_userCube, _userN) = Lut.LoadCube(path, cfg.Preset.Str("LUT", "FlipY", "auto"));
                _userKey = key;
                Mod.Log.Msg($"Loaded LUT {Path.GetFileName(path)} ({_userN}^3)");
            }
            catch (Exception e) { _userCube = null; _userKey = null; Mod.Log.Warning($"LUT '{path}' failed: {e.Message}"); }
        }

        private static long SafeMtime(string p) { try { return File.GetLastWriteTimeUtc(p).Ticks; } catch { return 0; } }

        private static class ID
        {
            public static readonly int Lut = Shader.PropertyToID("_QS_Lut"), LutParams = Shader.PropertyToID("_QS_LutParams"),
                Tone = Shader.PropertyToID("_QS_Tone"), Vignette = Shader.PropertyToID("_QS_Vignette"),
                VignetteColor = Shader.PropertyToID("_QS_VignetteColor"), VignetteCenter = Shader.PropertyToID("_QS_VignetteCenter"),
                Sharpen = Shader.PropertyToID("_QS_Sharpen"), CA = Shader.PropertyToID("_QS_CA"), Distort = Shader.PropertyToID("_QS_Distort"),
                Grain = Shader.PropertyToID("_QS_Grain"), Scan = Shader.PropertyToID("_QS_Scan"), Glitch = Shader.PropertyToID("_QS_Glitch"),
                Noise = Shader.PropertyToID("_QS_Noise"), Posterize = Shader.PropertyToID("_QS_Posterize"), Time = Shader.PropertyToID("_QS_Time"),
                Curve = Shader.PropertyToID("_QS_Curve"), Texel = Shader.PropertyToID("_QS_SrcTexel"),
                Dof = Shader.PropertyToID("_QS_Dof"), DofAF = Shader.PropertyToID("_QS_DofAF"), DofPx = Shader.PropertyToID("_QS_DofPx"),
                Bloom = Shader.PropertyToID("_QS_Bloom"), BloomTint = Shader.PropertyToID("_QS_BloomTint"),
                BloomTex = Shader.PropertyToID("_QS_BloomTex"), BloomLow = Shader.PropertyToID("_QS_BloomLow"),
                Flare = Shader.PropertyToID("_QS_Flare"), FlareTex = Shader.PropertyToID("_QS_FlareTex"),
                God = Shader.PropertyToID("_QS_God"), GodTex = Shader.PropertyToID("_QS_GodTex"), Sun = Shader.PropertyToID("_QS_Sun"),
                SunL = Shader.PropertyToID("_QS_SunL"), SunR = Shader.PropertyToID("_QS_SunR"),
                Dirt = Shader.PropertyToID("_QS_Dirt"), DirtTex = Shader.PropertyToID("_QS_DirtTex"),
                SS = Shader.PropertyToID("_QS_SS"), Hurt = Shader.PropertyToID("_QS_Hurt"),
                Live = Shader.PropertyToID("_QS_Live"), LiveBloom = Shader.PropertyToID("_QS_LiveBloom"), LiveTint = Shader.PropertyToID("_QS_LiveTint");
            public static readonly int[] Stack = { LutParams, Tone, Vignette, VignetteColor, VignetteCenter, Sharpen, CA, Distort,
                Grain, Scan, Glitch, Noise, Posterize, Time, Curve, Hurt };
        }
    }
}
