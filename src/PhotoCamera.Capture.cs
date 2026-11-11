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
    internal sealed partial class PhotoCamera
    {
        public bool StartRecording(Config cfg, out string error)
        {
            error = null;
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
        }

        public string StopRecording()
        {
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
