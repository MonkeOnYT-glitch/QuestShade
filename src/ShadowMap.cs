using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace QuestShade
{
    // QuestShade's own shadow map, rendered next to BONELAB's renderer:
    //  1. a small orthographic "sun camera" draws only the shadow-caster copies (on a layer no other camera draws)
    //     into a distance map, in world space, so shadows never swim with head movement;
    //  2. the headset's final pass reconstructs each pixel's world position from scene depth and darkens it if a
    //     caster sits between it and the sun. That lands on floors, walls, tables, props and over edges alike.
    internal static class ShadowMap
    {
        private static Camera _cam;
        private static RenderTexture _rt;
        private static UniversalAdditionalCameraData _headData;
        private static Camera _head, _lens;
        private static UniversalAdditionalCameraData _lensData;
        private static int _layer = -2, _res;
        private static bool _on, _logged, _renderWarned;
        private static float _dark;
        private static Config _cfg;

        private static readonly int SMTexId = Shader.PropertyToID("_QS_SMTex"), SMId = Shader.PropertyToID("_QS_SM"),
            OriginId = Shader.PropertyToID("_QS_SMOrigin"), DirId = Shader.PropertyToID("_QS_SMDir"), OptsId = Shader.PropertyToID("_QS_SMOpts"),
            InvVP0Id = Shader.PropertyToID("_QS_InvVP0"), InvVP1Id = Shader.PropertyToID("_QS_InvVP1"), ParamsId = Shader.PropertyToID("_QS_SMParams"),
            ModeId = Shader.PropertyToID("_QS_SMMode"), FLTexId = Shader.PropertyToID("_QS_FLTex"), FLMId = Shader.PropertyToID("_QS_FLM"),
            FLPosId = Shader.PropertyToID("_QS_FLPos"), FLDirId = Shader.PropertyToID("_QS_FLDir"), FLParamsId = Shader.PropertyToID("_QS_FLParams"), FLParams2Id = Shader.PropertyToID("_QS_FLParams2");
        private static Camera _flCam;
        private static RenderTexture _flRt;
        private static float _flStrength;
        private static Vector3 _flPos;

        // The lowest free layer (the camera's lens-only layer takes the highest one).
        public static int Layer
        {
            get
            {
                if (_layer != -2) return _layer;
                _layer = -1;
                for (int l = 8; l <= 31; l++) if (string.IsNullOrEmpty(LayerMask.LayerToName(l))) { _layer = l; break; }
                Mod.Log.Msg(_layer >= 0 ? $"Shadow map: caster layer {_layer}" : "Shadow map: no free layer, shadow map off");
                return _layer;
            }
        }

        public static bool Available => ShaderPack.ShadowCaster != null && Layer >= 0;

        public static void Off()
        {
            if (!_on) return;
            _on = false;
            if (_cam != null) _cam.enabled = false;
            Shader.SetGlobalVector(OriginId, Vector4.zero);
            _flStrength = 0f; Shader.SetGlobalVector(FLPosId, Vector4.zero);
        }

        public static void Update(Config cfg, Camera head, Camera lens, Vector3 dir, float darkness)
        {
            _cfg = cfg; _head = head; _lens = lens; _dark = Mathf.Clamp01(darkness);
            int L = Layer;
            if (L < 0 || head == null) { Off(); return; }
            int res = Mathf.ClosestPowerOfTwo(Mathf.Clamp(cfg.ShadowMapRes, 256, 2048));
            float range = Mathf.Clamp(cfg.ShadowMapRange, 3f, 20f), depth = range * 4f;
            Ensure(res);
            if (_cam == null || _rt == null) { Off(); return; }

            // Only the sun camera may see the casters.
            int bit = 1 << L;
            if ((head.cullingMask & bit) != 0) head.cullingMask &= ~bit;
            if (lens != null && (lens.cullingMask & bit) != 0) lens.cullingMask &= ~bit;
            if (_cam.cullingMask != bit) _cam.cullingMask = bit;

            // The headset needs scene depth to place each pixel in the world.
            try
            {
                if (_headData == null || _headData.gameObject != head.gameObject) _headData = head.GetComponent<UniversalAdditionalCameraData>();
                if (_headData != null && !_headData.requiresDepthTexture) _headData.requiresDepthTexture = true;
            }
            catch { }
            // So does the in-game camera, for shadows in its photos and videos.
            if (lens != null && cfg.ShadowMapCamera)
            {
                try
                {
                    if (_lensData == null || _lensData.gameObject != lens.gameObject) _lensData = lens.GetComponent<UniversalAdditionalCameraData>();
                    if (_lensData != null && !_lensData.requiresDepthTexture) _lensData.requiresDepthTexture = true;
                }
                catch { }
            }

            // Place the sun camera around the player and snap it to whole shadow-map texels, so edges don't shimmer as you walk.
            dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector3.down;
            var rot = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.99f ? Vector3.forward : Vector3.up);
            Vector3 right = rot * Vector3.right, up = rot * Vector3.up;
            Vector3 c = head.transform.position;
            float texel = 2f * range / res;
            float cx = Mathf.Round(Vector3.Dot(c, right) / texel) * texel, cy = Mathf.Round(Vector3.Dot(c, up) / texel) * texel, cz = Vector3.Dot(c, dir);
            Vector3 center = right * cx + up * cy + dir * cz;
            Vector3 origin = center - dir * (depth * 0.5f);
            var t = _cam.transform;
            t.SetPositionAndRotation(origin, rot);
            _cam.orthographicSize = range;
            _cam.nearClipPlane = 0.02f;
            _cam.farClipPlane = depth;

            RenderFlash(cfg, bit);
            Shader.SetGlobalFloat(ModeId, 0f);
            var scaleBias = Matrix4x4.TRS(new Vector3(0.5f, 0.5f, 0f), Quaternion.identity, new Vector3(0.5f, 0.5f, 1f));
            Shader.SetGlobalMatrix(SMId, scaleBias * _cam.projectionMatrix * _cam.worldToCameraMatrix);
            Shader.SetGlobalTexture(SMTexId, _rt);
            Shader.SetGlobalVector(ParamsId, new Vector4(texel, 0f, 0f, 0f));
            Shader.SetGlobalVector(DirId, new Vector4(dir.x, dir.y, dir.z, 1f / res));
            Shader.SetGlobalVector(OptsId, new Vector4(cfg.ShadowMapCpuMatrices ? 1f : 0f, cfg.ShadowMapFlipY ? 1f : 0f, Mathf.Clamp(cfg.ShadowMapBias, 0f, 0.5f), Mathf.Clamp(cfg.ShadowMapSoftness, 0.5f, 4f)));
            // The casters measure distance from this point; w (darkness) is set per camera in BeginCamera.
            Shader.SetGlobalVector(OriginId, new Vector4(origin.x, origin.y, origin.z, _dark));
            _originXYZ = origin;
            _on = true;
            // Kept disabled and rendered by hand: an enabled extra camera (lowest depth in the scene) can be picked up by the game or
            // other mods as "the" camera, which made the player freeze / lose the joystick until regrabbing.
            if (cfg.ShadowMapManualRender) { if (_cam.enabled) _cam.enabled = false; try { _cam.Render(); } catch (Exception e) { if (!_renderWarned) { _renderWarned = true; Mod.Log.Warning("Shadow map: manual render failed, switching to auto: " + e.Message); } cfg.ShadowMapManualRender = false; } }
            else if (!_cam.enabled) _cam.enabled = true;
            if (!_logged)
            {
                _logged = true;
                Mod.Log.Msg($"Shadow map on: {res}x{res}, {range * 2f:0} m across, layer {L}, {(cfg.ShadowMapCpuMatrices ? "CPU" : "built-in")} eye matrices{(cfg.ShadowMapFlipY ? ", flipped" : "")}");
            }
        }

        private static Vector3 _originXYZ;

        // Shadows only on the headset (the camera's own footage is handled separately); everything else gets darkness 0.
        public static void BeginCamera(Camera cam)
        {
            if (!_on || cam == null || _cfg == null) return;
            // Our own shadow cameras: leave their globals alone (this used to overwrite the flashlight's position with the sun's
            // right before the flashlight map was drawn, so flashlight shadows never lined up).
            if ((_cam != null && cam.Pointer == _cam.Pointer) || (_flCam != null && cam.Pointer == _flCam.Pointer)) return;
            bool head = _head != null && cam.Pointer == _head.Pointer;
            bool lens = !head && _lens != null && _cfg.ShadowMapCamera && cam.Pointer == _lens.Pointer;
            Shader.SetGlobalVector(OriginId, new Vector4(_originXYZ.x, _originXYZ.y, _originXYZ.z, head || lens ? _dark : 0f));
            Shader.SetGlobalVector(FLPosId, new Vector4(_flPos.x, _flPos.y, _flPos.z, head || lens ? _flStrength : 0f));
            if (!head && !lens) return;
            // The in-game camera always uses its own (CPU) matrices; the headset uses the built-in eye matrices unless switched.
            bool cpu = lens || _cfg.ShadowMapCpuMatrices;
            bool flip = lens ? _cfg.ShadowMapCameraFlipY : _cfg.ShadowMapFlipY;
            Shader.SetGlobalVector(OptsId, new Vector4(cpu ? 1f : 0f, flip ? 1f : 0f, Mathf.Clamp(_cfg.ShadowMapBias, 0f, 0.5f), Mathf.Clamp(_cfg.ShadowMapSoftness, 0.5f, 4f)));
            if (!cpu) return;
            try
            {
                if (!lens && cam.stereoEnabled)
                {
                    Shader.SetGlobalMatrix(InvVP0Id, Inv(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left)));
                    Shader.SetGlobalMatrix(InvVP1Id, Inv(cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right), cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right)));
                }
                else
                {
                    var m = Inv(cam.projectionMatrix, cam.worldToCameraMatrix);
                    Shader.SetGlobalMatrix(InvVP0Id, m); Shader.SetGlobalMatrix(InvVP1Id, m);
                }
            }
            catch { }
        }

        private static Matrix4x4 Inv(Matrix4x4 proj, Matrix4x4 view) => (GL.GetGPUProjectionMatrix(proj, true) * view).inverse;

        // ---- The engine's own shadow map for the flashlight: every shadow-casting object in the level (walls, floors...),
        // so its light and shadows stop at walls. Only while a flashlight is on; everything is put back afterwards.
        private static readonly int FLURPId = Shader.PropertyToID("_QS_FLURP");
        private static UniversalRenderPipelineAsset _urp;
        private static bool _urpOn, _urpSaved, _oAddShadows;
        private static int _oTierL, _oTierM, _oTierH;
        private static UnityEngine.Rendering.Universal.ShadowResolution _oAtlas;
        private static Light _urpLight;
        private static LightShadows _oLightShadows;
        private static float _oNear;

        private static void UrpFlash(Light l, bool want)
        {
            if (!want || l == null) { UrpOff(); return; }
            try
            {
                if (_urp == null)
                {
                    var rp = QualitySettings.renderPipeline ?? GraphicsSettings.currentRenderPipeline;
                    _urp = rp != null ? rp.TryCast<UniversalRenderPipelineAsset>() : null;
                    if (_urp == null) return;
                }
                if (!_urpSaved)
                {
                    _urpSaved = true;
                    _oAddShadows = _urp.m_AdditionalLightShadowsSupported; _oAtlas = _urp.m_AdditionalLightsShadowmapResolution;
                    _oTierL = _urp.m_AdditionalLightsShadowResolutionTierLow; _oTierM = _urp.m_AdditionalLightsShadowResolutionTierMedium; _oTierH = _urp.m_AdditionalLightsShadowResolutionTierHigh;
                }
                if (!_urpOn)
                {
                    // One slice that fills the whole atlas, so the shader can read slice 0 with the full texture.
                    _urp.m_AdditionalLightShadowsSupported = true;
                    _urp.m_AdditionalLightsShadowmapResolution = UnityEngine.Rendering.Universal.ShadowResolution._1024;
                    _urp.m_AdditionalLightsShadowResolutionTierLow = 1024; _urp.m_AdditionalLightsShadowResolutionTierMedium = 1024; _urp.m_AdditionalLightsShadowResolutionTierHigh = 1024;
                    _urpOn = true;
                    Mod.Log.Msg("Flashlight shadows: engine shadow map on (walls and level geometry block the beam)");
                }
                if (_urpLight != l)
                {
                    RestoreLight();
                    _urpLight = l; _oLightShadows = l.shadows; _oNear = l.shadowNearPlane;
                }
                if (l.shadows != LightShadows.Hard) l.shadows = LightShadows.Hard;
                l.shadowNearPlane = 0.25f; // past the flashlight body and the hand holding it
                Shader.SetGlobalFloat(FLURPId, 1f);
            }
            catch (Exception e) { Mod.Log.Warning("Flashlight engine shadows unavailable: " + e.Message); Shader.SetGlobalFloat(FLURPId, 0f); }
        }

        private static void RestoreLight()
        {
            try { if (_urpLight != null) { _urpLight.shadows = _oLightShadows; _urpLight.shadowNearPlane = _oNear; } } catch { }
            _urpLight = null;
        }

        private static void UrpOff()
        {
            Shader.SetGlobalFloat(FLURPId, 0f);
            if (!_urpOn) return;
            _urpOn = false;
            RestoreLight();
            try
            {
                if (_urp != null)
                {
                    _urp.m_AdditionalLightShadowsSupported = _oAddShadows; _urp.m_AdditionalLightsShadowmapResolution = _oAtlas;
                    _urp.m_AdditionalLightsShadowResolutionTierLow = _oTierL; _urp.m_AdditionalLightsShadowResolutionTierMedium = _oTierM; _urp.m_AdditionalLightsShadowResolutionTierHigh = _oTierH;
                }
            }
            catch { }
        }

        // Flashlight shadows: a perspective map from the held flashlight (only while one is on). Drawn with the same caster
        // copies; the near clip starts past the flashlight and the hand holding it so they don't block the whole beam.
        private static void RenderFlash(Config cfg, int bit)
        {
            Light l = null;
            if (cfg.FlashlightShadows) { try { l = Flashlights.ActiveSpot(); } catch { } }
            if (l == null) { _flStrength = 0f; Shader.SetGlobalVector(FLPosId, Vector4.zero); if (_flCam != null) _flCam.enabled = false; UrpOff(); return; }
            UrpFlash(l, cfg.FlashlightWorldShadows);
            int res = 1024;
            if (_flRt == null)
            {
                var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RHalf) ? RenderTextureFormat.RHalf : RenderTextureFormat.RFloat;
                _flRt = new RenderTexture(res, res, 16, fmt, RenderTextureReadWrite.Linear)
                { name = "QuestShade_FlashShadowMap", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false, hideFlags = HideFlags.HideAndDontSave };
                _flRt.Create();
            }
            if (_flCam == null)
            {
                var go = new GameObject("QuestShade_FlashCam");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _flCam = go.AddComponent<Camera>();
                _flCam.enabled = false;
                _flCam.aspect = 1f;
                _flCam.clearFlags = CameraClearFlags.SolidColor;
                _flCam.backgroundColor = new Color(1000f, 0f, 0f, 1f);
                _flCam.depth = -101f;
                _flCam.allowHDR = false; _flCam.allowMSAA = false; _flCam.useOcclusionCulling = false;
                _flCam.stereoTargetEye = StereoTargetEyeMask.None;
                _flCam.targetTexture = _flRt;
                try
                {
                    var d = go.AddComponent<UniversalAdditionalCameraData>();
                    d.renderShadows = false; d.requiresDepthTexture = false; d.requiresColorTexture = false; d.renderPostProcessing = false; d.antialiasing = AntialiasingMode.None;
                }
                catch { }
            }
            var t = l.transform;
            float spot = Mathf.Clamp(l.spotAngle, 10f, 150f), range = Mathf.Clamp(l.range, 2f, 25f);
            _flCam.cullingMask = bit;
            _flCam.transform.SetPositionAndRotation(t.position, t.rotation);
            _flCam.fieldOfView = spot + 4f;
            _flCam.nearClipPlane = 0.25f;
            _flCam.farClipPlane = range;
            _flPos = t.position;
            var fwd = t.forward;
            Shader.SetGlobalVector(OriginId, new Vector4(_flPos.x, _flPos.y, _flPos.z, 0f));
            Shader.SetGlobalVector(DirId, new Vector4(fwd.x, fwd.y, fwd.z, 0f));
            Shader.SetGlobalFloat(ModeId, 1f);
            try { _flCam.Render(); } catch { _flStrength = 0f; return; }
            float inner = l.innerSpotAngle > 1f ? l.innerSpotAngle : spot * 0.6f;
            Shader.SetGlobalTexture(FLTexId, _flRt);
            Shader.SetGlobalMatrix(FLMId, _flCam.projectionMatrix * _flCam.worldToCameraMatrix);
            Shader.SetGlobalVector(FLDirId, new Vector4(fwd.x, fwd.y, fwd.z, Mathf.Cos(spot * 0.5f * Mathf.Deg2Rad)));
            Shader.SetGlobalVector(FLParamsId, new Vector4(range, 1f / res, Mathf.Cos(inner * 0.5f * Mathf.Deg2Rad), 0.06f));
            _flStrength = Mathf.Clamp01(cfg.FlashShadowDarkness);
            Shader.SetGlobalVector(FLParams2Id, new Vector4(Mathf.Clamp(cfg.FlashlightLight, 0f, 3f), 0f, 0f, 0f));
            if (!_flLogged) { _flLogged = true; Mod.Log.Msg($"Flashlight shadows on ({spot:0} deg, {range:0} m)"); }
        }
        private static bool _flLogged;

        private static void Ensure(int res)
        {
            if (_rt != null && _res != res) { _rt.Release(); UnityEngine.Object.Destroy(_rt); _rt = null; }
            if (_rt == null)
            {
                var fmt = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RHalf) ? RenderTextureFormat.RHalf : RenderTextureFormat.RFloat;
                _rt = new RenderTexture(res, res, 16, fmt, RenderTextureReadWrite.Linear)
                { name = "QuestShade_ShadowMap", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, useMipMap = false, hideFlags = HideFlags.HideAndDontSave };
                _rt.Create();
                _res = res;
                if (_cam != null) _cam.targetTexture = _rt;
            }
            if (_cam != null) return;
            var go = new GameObject("QuestShade_SunCam");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.orthographic = true;
            _cam.aspect = 1f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(1000f, 0f, 0f, 1f); // "no caster": very far
            _cam.depth = -100f;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.useOcclusionCulling = false;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.targetTexture = _rt;
            try
            {
                var d = go.AddComponent<UniversalAdditionalCameraData>();
                d.renderShadows = false;
                d.requiresDepthTexture = false;
                d.requiresColorTexture = false;
                d.renderPostProcessing = false;
                d.antialiasing = AntialiasingMode.None;
            }
            catch (Exception e) { Mod.Log.Warning("Shadow map: camera setup: " + e.Message); }
        }
    }
}
