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
        private static Camera _head;
        private static int _layer = -2, _res;
        private static bool _on, _logged, _renderWarned;
        private static float _dark;
        private static Config _cfg;

        private static readonly int SMTexId = Shader.PropertyToID("_QS_SMTex"), SMId = Shader.PropertyToID("_QS_SM"),
            OriginId = Shader.PropertyToID("_QS_SMOrigin"), DirId = Shader.PropertyToID("_QS_SMDir"), OptsId = Shader.PropertyToID("_QS_SMOpts"),
            InvVP0Id = Shader.PropertyToID("_QS_InvVP0"), InvVP1Id = Shader.PropertyToID("_QS_InvVP1");

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
        }

        public static void Update(Config cfg, Camera head, Camera lens, Vector3 dir, float darkness)
        {
            _cfg = cfg; _head = head; _dark = Mathf.Clamp01(darkness);
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

            var scaleBias = Matrix4x4.TRS(new Vector3(0.5f, 0.5f, 0f), Quaternion.identity, new Vector3(0.5f, 0.5f, 1f));
            Shader.SetGlobalMatrix(SMId, scaleBias * _cam.projectionMatrix * _cam.worldToCameraMatrix);
            Shader.SetGlobalTexture(SMTexId, _rt);
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
            if (!_on || cam == null) return;
            bool head = _head != null && cam.Pointer == _head.Pointer;
            Shader.SetGlobalVector(OriginId, new Vector4(_originXYZ.x, _originXYZ.y, _originXYZ.z, head ? _dark : 0f));
            if (!head || _cfg == null || !_cfg.ShadowMapCpuMatrices) return;
            try
            {
                if (cam.stereoEnabled)
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
