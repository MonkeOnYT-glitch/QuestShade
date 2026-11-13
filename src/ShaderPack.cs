using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;

namespace QuestShade
{
    internal static class ShaderPack
    {
        public static Shader FinalBlit, Screen, Yuv, Post, Body, Outline, Beam, Shadow;
        public static GameObject Handheld;
        public static string Error;
        private static bool _tried;

        public static bool Load()
        {
            if (FinalBlit != null) return true;
            if (_tried) return false;
            _tried = true;
            try
            {
                var name = "QuestShade.Bundles.android";
                using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
                if (s == null) { Error = "shader pack missing from DLL"; Mod.Log.Error(Error); return false; }
                var bytes = new byte[s.Length];
                int read = 0; while (read < bytes.Length) { int n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                var bundle = Il2CppAssetBundleManager.LoadFromMemory(bytes);
                if (bundle == null) { Error = "shader pack failed to load"; Mod.Log.Error(Error); return false; }
                FinalBlit = Get(bundle, "Assets/QuestShade/QSFinalBlit.shader");
                Screen = Get(bundle, "Assets/QuestShade/QSScreen.shader");
                Yuv = Get(bundle, "Assets/QuestShade/QSYUV.shader");
                Post = Get(bundle, "Assets/QuestShade/QSPost.shader");
                Body = Get(bundle, "Assets/QuestShade/QSBody.shader");
                Outline = Get(bundle, "Assets/QuestShade/QSOutline.shader");
                Beam = Get(bundle, "Assets/QuestShade/QSBeam.shader");
                Shadow = Get(bundle, "Assets/QuestShade/QSShadow.shader");
                try
                {
                    Handheld = Body != null ? bundle.LoadAsset<GameObject>("Assets/QuestShade/HandheldCam/QSHandheld.prefab") : null;
                    if (Handheld != null) Handheld.hideFlags = HideFlags.DontUnloadUnusedAsset;
                }
                catch (Exception e) { Mod.Log.Warning("Camera model failed to load: " + e.Message); Handheld = null; }
                if (FinalBlit == null) { Error = "post shader not supported on this GPU"; Mod.Log.Error(Error); return false; }
                Mod.Log.Msg($"Shader pack loaded ({bytes.Length / 1024} KB): blit={Ok(FinalBlit)} screen={Ok(Screen)} yuv={Ok(Yuv)} bloom/dof={Ok(Post)} camera model={(Handheld != null ? "ok" : "missing")}");
                return true;
            }
            catch (Exception e) { Error = "shader pack error: " + e.Message; Mod.Log.Error("Shader pack: " + e); return false; }

            static string Ok(Shader s) => s != null ? "ok" : "unsupported";
        }

        private static Shader Get(Il2CppAssetBundle b, string path)
        {
            var s = b.LoadAsset<Shader>(path);
            if (s == null || !s.isSupported) return null;
            s.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return s;
        }

        public static bool HeadsetActive
        {
            get { try { return XRSettings.enabled && XRSettings.isDeviceActive && XRSettings.eyeTextureWidth > 0; } catch { return false; } }
        }

        public static void SetStereo(Material m, bool stereo, bool procedural = false)
        {
            if (m == null) return;
            m.DisableKeyword("QS_STEREO_MULTIVIEW");
            m.DisableKeyword("QS_STEREO_INSTANCING");
            if (procedural) m.EnableKeyword("QS_PROCEDURAL"); else m.DisableKeyword("QS_PROCEDURAL");
            if (!stereo || !XRSettings.enabled || XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.MultiPass) return;
            m.EnableKeyword(SystemInfo.supportsMultiview ? "QS_STEREO_MULTIVIEW" : "QS_STEREO_INSTANCING");
        }
    }
}
