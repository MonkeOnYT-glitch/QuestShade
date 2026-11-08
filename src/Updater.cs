using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using UnityEngine.Networking;

#if THUNDERSTORE
namespace QuestShade
{
    internal static class Updater
    {
        public static string Notice;
        public static void Begin(string currentVersion) { }
        public static void Tick() { }
    }
}
#else
namespace QuestShade
{
    internal static class Updater
    {
        private const string Repo = "MonkeOnYT-glitch/QuestShade";
        private const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";
#if QUEST
        private const string AssetName = "QuestShade.dll";
#else
        private const string AssetName = "QuestShade-PC.dll";
#endif
        private const string DllName = "QuestShade.dll";

        public static string Notice;

        private static UnityWebRequest _req;
        private static int _stage;
        private static string _current, _tag, _digest, _target;
        private static float _startAt = -1f;

        public static void Begin(string currentVersion)
        {
            _current = currentVersion;
            _startAt = UnityEngine.Time.realtimeSinceStartup + 15f;
        }

        public static void Tick()
        {
            if (_stage == 3 || _startAt < 0f) return;
            try
            {
                if (_stage == 0)
                {
                    if (UnityEngine.Time.realtimeSinceStartup < _startAt) return;
                    _target = Path.Combine(MelonLoader.Utils.MelonEnvironment.ModsDirectory, DllName);
                    if (!File.Exists(_target)) { Mod.Log.Msg("Updates: QuestShade.dll isn't in the Mods folder, not updating"); _stage = 3; return; }
                    _req = Get(Api);
                    _req.SetRequestHeader("Accept", "application/vnd.github+json");
                    _req.SendWebRequest();
                    _stage = 1;
                    return;
                }
                if (_req == null || !_req.isDone) return;
                if (_req.result != UnityWebRequest.Result.Success) throw new Exception($"{_req.error} ({_req.responseCode})");

                if (_stage == 1)
                {
                    string url = ReadRelease(_req.downloadHandler.text);
                    Done();
                    if (url == null) { _stage = 3; return; }
                    Mod.Log.Msg($"Updates: downloading QuestShade {_tag} (you have {_current})");
                    _req = Get(url);
                    _req.SendWebRequest();
                    _stage = 2;
                    return;
                }
                if (_stage == 2)
                {
                    byte[] bytes = _req.downloadHandler.data;
                    Done();
                    _stage = 3;
                    Install(bytes);
                }
            }
            catch (Exception e) { Mod.Log.Warning("Update check failed: " + e.Message); Done(); _stage = 3; }
        }

        private static UnityWebRequest Get(string url)
        {
            var r = UnityWebRequest.Get(url);
            r.timeout = 60;
            r.SetRequestHeader("User-Agent", "QuestShade-Updater");
            return r;
        }

        private static void Done() { try { _req?.Dispose(); } catch { } _req = null; }

        private static string ReadRelease(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryVersion(_tag, out var latest) || !TryVersion(_current, out var current)) { Mod.Log.Msg($"Updates: can't compare '{_tag}' with {_current}"); return null; }
            if (latest <= current) { Mod.Log.Msg($"Updates: up to date ({_current})"); return null; }
            string url = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != AssetName) continue;
                url = a.GetProperty("browser_download_url").GetString();
                _digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            }
            if (url == null || !url.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            { Mod.Log.Msg($"Updates: {_tag} has no {AssetName} to install"); return null; }
            return url;
        }

        private static void Install(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 64 * 1024) throw new Exception($"download too small ({bytes?.Length ?? 0} bytes)");
            if (_digest != null && _digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                string got = Convert.ToHexString(SHA256.HashData(bytes));
                if (!got.Equals(_digest.Substring(7), StringComparison.OrdinalIgnoreCase)) throw new Exception("download doesn't match GitHub's checksum");
            }
            string tmp = _target + ".download";
            File.WriteAllBytes(tmp, bytes);
            try { if (AssemblyName.GetAssemblyName(tmp).Name != "QuestShade") throw new Exception("downloaded file isn't QuestShade"); }
            catch { TryDelete(tmp); throw; }

            string old = _target + ".old";
            bool copied = false;
            try { File.Copy(tmp, _target, true); TryDelete(tmp); copied = true; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!copied)
            {
                TryDelete(old);
                File.Move(_target, old);
                try { File.Move(tmp, _target); }
                catch { File.Move(old, _target); throw; }
            }
            Mod.Log.Msg($"Updates: QuestShade {_tag} installed - it loads next time you start the game");
            Notice = $"QuestShade {_tag} downloaded - restart the game to use it";
        }

        private static bool TryVersion(string s, out Version v)
        {
            s = (s ?? "").Trim().TrimStart('v', 'V');
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) s = s.Substring(0, cut);
            return Version.TryParse(s, out v);
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }
}
#endif
