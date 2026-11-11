using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(QuestShadeUpdater.Plugin), "QuestShade Updater", "1.0.1", "MonkeOnYT")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace QuestShadeUpdater
{
    public sealed class Plugin : MelonPlugin
    {
        private const string Repo = "MonkeOnYT-glitch/QuestShade";
        private const string Asset = "QuestShade.dll";
        private const int BudgetMs = 8000;

        private static MelonLogger.Instance Log;
        private static volatile bool _givenUp;

        public override void OnPreInitialization()
        {
            Log = LoggerInstance;
            if (!OperatingSystem.IsAndroid()) return;
            try
            {
                if (!AutoUpdateOn()) { Log.Msg("Auto-update is off ([General] AutoUpdate in QuestShade.ini)"); return; }
                string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "QuestShade");
                string lockFile = Path.Combine(dir, "updater-boot.lock"), disabled = Path.Combine(dir, "updater-disabled.txt");
                if (File.Exists(disabled)) { Log.Warning("Boot-time updates are off (updater-disabled.txt). The in-game updater still works."); return; }
                if (File.Exists(lockFile))
                {
                    try { File.Move(lockFile, disabled); } catch { }
                    Log.Warning("The last boot stopped during the update check, so boot-time updates are now off. The in-game updater still works.");
                    return;
                }
                Directory.CreateDirectory(dir);
                File.WriteAllText(lockFile, DateTime.UtcNow.ToString("o"));
                var work = Task.Run(Run);
                bool finished = work.Wait(BudgetMs);
                if (!finished) { _givenUp = true; Log.Warning("GitHub is slow to answer, skipping the update check this launch"); }
                try { File.Delete(lockFile); } catch { }
                if (finished && work.IsFaulted) throw work.Exception;
            }
            catch (Exception e) { Log.Warning("Update check failed: " + (e.InnerException ?? e).Message); }
        }

        private static void Run()
        {
            string modPath = Path.Combine(MelonEnvironment.ModsDirectory, "QuestShade.dll");
            Version current = new Version(0, 0, 0);
            if (File.Exists(modPath))
            {
                try { var v = AssemblyName.GetAssemblyName(modPath).Version; if (v != null) current = new Version(v.Major, v.Minor, Math.Max(0, v.Build)); } catch { }
            }

            string json = Http.GetString($"https://api.github.com/repos/{Repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) { Log.Warning($"Can't read the release version '{tag}'"); return; }
            if (latest <= current) { Log.Msg($"QuestShade is up to date ({current})"); return; }

            string url = null, digest = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != Asset) continue;
                url = a.GetProperty("browser_download_url").GetString();
                digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            }
            if (url == null || !url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            { Log.Warning($"Release {tag} has no {Asset}"); return; }

            Log.Msg($"Downloading QuestShade {latest} (you have {(current.Major == 0 && current.Minor == 0 ? "none / an older build" : current.ToString())})...");
            byte[] bytes = Http.GetBytes(url);
            if (bytes == null || bytes.Length < 64 * 1024) throw new Exception($"download too small ({bytes?.Length ?? 0} bytes)");
            if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                string got = Convert.ToHexString(SHA256.HashData(bytes));
                if (!got.Equals(digest.Substring(7), StringComparison.OrdinalIgnoreCase)) throw new Exception("download doesn't match GitHub's checksum");
            }

            string tmp = modPath + ".download";
            File.WriteAllBytes(tmp, bytes);
            try { if (AssemblyName.GetAssemblyName(tmp).Name != "QuestShade") throw new Exception("downloaded file isn't QuestShade"); }
            catch { TryDelete(tmp); throw; }
            if (_givenUp) { TryDelete(tmp); return; }
            File.Move(tmp, modPath, true);
            try { File.WriteAllText(Path.Combine(MelonEnvironment.UserDataDirectory, "QuestShade", "just-updated.txt"), latest.ToString()); } catch { }
            Log.Msg($"QuestShade updated to {latest}, it loads now");
        }

        private static bool AutoUpdateOn()
        {
            try
            {
                var ini = Path.Combine(MelonEnvironment.UserDataDirectory, "QuestShade", "QuestShade.ini");
                if (!File.Exists(ini)) return true;
                foreach (var raw in File.ReadAllLines(ini))
                {
                    var line = raw.Split(';')[0].Trim();
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line.Substring(0, eq).Trim().Equals("AutoUpdate", StringComparison.OrdinalIgnoreCase))
                        return !line.Substring(eq + 1).Trim().Equals("false", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return true;
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
    }
}
