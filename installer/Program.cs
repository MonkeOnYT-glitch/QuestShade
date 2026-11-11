using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QuestShadeInstaller
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private const string Pkg = "com.StressLevelZero.BONELAB";
        private const string MlDir = "/sdcard/MelonLoader/" + Pkg;
        private const string Repo = "MonkeOnYT-glitch/QuestShade";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private readonly Label _status;
        private readonly TextBox _log;
        private readonly Button _go, _continue;
        private readonly string _home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestShadeInstaller");
        private string _adb;
        private volatile bool _skipWait;

        public MainForm()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("QuestShadeInstaller/1.0");
            Text = "QuestShade Installer";
            ClientSize = new Size(640, 460);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.FromArgb(18, 18, 22);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f);

            var title = new Label { Text = "QuestShade", Font = new Font("Segoe UI", 20f, FontStyle.Bold), ForeColor = Color.FromArgb(255, 138, 31), AutoSize = true, Location = new Point(20, 14) };
            var sub = new Label { Text = "Installs QuestShade on BONELAB for Quest. Your other mods are kept.", AutoSize = true, ForeColor = Color.Gainsboro, Location = new Point(22, 54) };
            _status = new Label { Text = "Plug your Quest into this PC, then press Install.", Font = new Font("Segoe UI", 11f, FontStyle.Bold), Location = new Point(20, 86), Size = new Size(600, 48) };
            _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(20, 138), Size = new Size(600, 250), BackColor = Color.FromArgb(30, 30, 36), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            _go = new Button { Text = "Install QuestShade", Location = new Point(20, 400), Size = new Size(200, 40), BackColor = Color.FromArgb(255, 138, 31), ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
            _continue = new Button { Text = "It's patched, continue", Location = new Point(230, 400), Size = new Size(200, 40), FlatStyle = FlatStyle.Flat, Visible = false };
            _go.Click += async (s, e) => await Go();
            _continue.Click += (s, e) => _skipWait = true;
            Controls.AddRange(new Control[] { title, sub, _status, _log, _go, _continue });
        }

        private async Task Go()
        {
            _go.Enabled = false;
            try
            {
                await Task.Run(RunAll);
            }
            catch (Exception e)
            {
                Log("Error: " + e.Message);
                Status("Something went wrong. See the details below, then press Install to try again.");
            }
            finally
            {
                UI(() => { _go.Enabled = true; _continue.Visible = false; });
            }
        }

        private void RunAll()
        {
            Directory.CreateDirectory(_home);
            Status("Getting ADB (the tool that talks to your Quest)...");
            EnsureAdb();
            WaitForDevice();
            if (!Adb($"shell pm path {Pkg}").Contains("package:"))
                throw new Exception("BONELAB isn't installed on this Quest. Install it from the Meta store first.");
            if (!Patched()) Patch();
            Status("Installing QuestShade...");
            InstallMods();
            Status("Done! Start BONELAB on your Quest. QuestShade is in BoneMenu.");
        }

        private void EnsureAdb()
        {
            _adb = Path.Combine(_home, "platform-tools", "adb.exe");
            if (File.Exists(_adb)) { Log("ADB ready."); return; }
            var zip = Path.Combine(_home, "platform-tools.zip");
            Download("https://dl.google.com/android/repository/platform-tools-latest-windows.zip", zip);
            if (Directory.Exists(Path.Combine(_home, "platform-tools"))) Directory.Delete(Path.Combine(_home, "platform-tools"), true);
            ZipFile.ExtractToDirectory(zip, _home);
            File.Delete(zip);
            if (!File.Exists(_adb)) throw new Exception("Couldn't set up ADB.");
            Log("ADB downloaded.");
        }

        private void WaitForDevice()
        {
            string last = null;
            while (true)
            {
                var lines = Adb("devices").Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                string msg;
                if (lines.Any(l => l.EndsWith("\tdevice"))) { Log("Quest found."); return; }
                if (lines.Any(l => l.EndsWith("unauthorized")))
                    msg = "Put your headset on and allow USB debugging (tick \"Always allow from this computer\").";
                else if (lines.Count > 0)
                    msg = "Your Quest is connecting... if this stays, unplug it and plug it back in.";
                else
                    msg = "Plug your Quest into this PC with a USB cable. Developer mode has to be on (Meta Horizon app > Devices > Developer mode).";
                if (msg != last) { Status(msg); last = msg; }
                Thread.Sleep(2000);
            }
        }

        private bool Patched() => !Adb($"shell ls {MlDir}").Contains("No such file");

        private void Patch()
        {
            var backup = Path.Combine(_home, "save-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Status("Backing up your BONELAB saves to this PC...");
            Adb($"pull /sdcard/Android/data/{Pkg}/files \"{backup}\"", 600000);
            bool haveBackup = Directory.Exists(backup) && Directory.EnumerateFileSystemEntries(backup).Any();
            Log(haveBackup ? "Saves backed up to " + backup : "No saves found to back up.");

            Status("Getting LemonLoader (it patches BONELAB so mods can load)...");
            var lemonDir = Path.Combine(_home, "lemonloader");
            var lemonExe = Path.Combine(lemonDir, "MelonLoader.Installer.App.exe");
            if (!File.Exists(lemonExe))
            {
                var zip = Path.Combine(_home, "lemonloader.zip");
                Download($"https://github.com/{Repo}/releases/download/tools/lemoninstaller-win-x64.zip", zip);
                if (Directory.Exists(lemonDir)) Directory.Delete(lemonDir, true);
                ZipFile.ExtractToDirectory(zip, lemonDir);
                File.Delete(zip);
            }
            Status("Patch BONELAB in the LemonLoader window, then start BONELAB once.");
            Log("");
            Log("1. In the LemonLoader window that opened, pick your Quest and BONELAB, then press Patch.");
            Log("   It reinstalls BONELAB. Your saves are backed up and get put back afterwards.");
            Log("2. When it's done, start BONELAB once on your Quest and wait for the main menu.");
            Log("This window carries on by itself once BONELAB is patched.");
            Log("(LemonLoader is made by the LemonLoader team, GPL-3.0: github.com/LemonLoader/MelonLoaderInstaller)");
            try { Process.Start(new ProcessStartInfo(lemonExe) { UseShellExecute = true, WorkingDirectory = lemonDir }); }
            catch (Exception e) { Log("Couldn't open LemonLoader: " + e.Message); }
            _skipWait = false;
            UI(() => _continue.Visible = true);
            while (!_skipWait && !Patched()) Thread.Sleep(3000);
            UI(() => _continue.Visible = false);
            Log("BONELAB is patched.");

            if (haveBackup)
            {
                Status("Putting your saves back...");
                Adb($"shell mkdir -p /sdcard/Android/data/{Pkg}/files");
                Adb($"push \"{backup}{Path.DirectorySeparatorChar}.\" /sdcard/Android/data/{Pkg}/files/", 600000);
                Log("Saves restored.");
            }
        }

        private void InstallMods()
        {
            Adb($"shell mkdir -p {MlDir}/Mods {MlDir}/Plugins");
            var json = Get($"https://api.github.com/repos/{Repo}/releases/latest");
            var tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
            string Asset(string name)
            {
                var m = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*/" + Regex.Escape(name) + ")\"");
                if (!m.Success) throw new Exception("The latest QuestShade release has no " + name);
                return m.Groups[1].Value;
            }
            var tmp = Path.Combine(_home, "download");
            Directory.CreateDirectory(tmp);

            var dll = Path.Combine(tmp, "QuestShade.dll");
            Download(Asset("QuestShade.dll"), dll);
            Push(dll, $"{MlDir}/Mods/QuestShade.dll");
            var plugin = Path.Combine(tmp, "QuestShadeUpdater.dll");
            Download(Asset("QuestShadeUpdater.dll"), plugin);
            Push(plugin, $"{MlDir}/Plugins/QuestShadeUpdater.dll");
            Log($"QuestShade {tag} installed.");

            var mods = Adb($"shell ls {MlDir}/Mods");
            if (mods.IndexOf("BoneLib.dll", StringComparison.OrdinalIgnoreCase) < 0)
            {
                Status("Installing BoneLib (QuestShade needs it)...");
                var info = Get("https://thunderstore.io/api/experimental/package/bonelib/BoneLib/");
                var url = Regex.Match(info, "\"download_url\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
                if (url.Length == 0) throw new Exception("Couldn't find BoneLib on Thunderstore.");
                var zip = Path.Combine(tmp, "BoneLib.zip");
                Download(url, zip);
                using (var z = ZipFile.OpenRead(zip))
                {
                    var entry = z.Entries.Where(e => e.Name.Equals("BoneLib.dll", StringComparison.OrdinalIgnoreCase))
                                         .OrderByDescending(e => e.FullName.IndexOf("Mods", StringComparison.OrdinalIgnoreCase) >= 0).FirstOrDefault();
                    if (entry == null) throw new Exception("BoneLib download has no BoneLib.dll");
                    var bl = Path.Combine(tmp, "BoneLib.dll");
                    entry.ExtractToFile(bl, true);
                    Push(bl, $"{MlDir}/Mods/BoneLib.dll");
                }
                Log("BoneLib installed.");
            }
            else Log("BoneLib is already installed.");

            var others = Adb($"shell ls {MlDir}/Mods").Split('\n').Select(s => s.Trim()).Where(s => s.EndsWith(".dll") && s != "QuestShade.dll" && s != "BoneLib.dll").ToList();
            if (others.Count > 0) Log($"Kept your other mods: {string.Join(", ", others)}");
            try { Directory.Delete(tmp, true); } catch { }
        }

        private void Push(string local, string remote)
        {
            var r = Adb($"push \"{local}\" \"{remote}\"", 300000);
            if (r.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 && r.IndexOf("pushed", StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception("Copying to the Quest failed: " + r.Trim());
        }

        private string Adb(string args, int timeoutMs = 60000)
        {
            var psi = new ProcessStartInfo(_adb, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new Exception("ADB stopped responding."); }
                return outTask.Result + errTask.Result;
            }
        }

        private static string Get(string url) => Http.GetStringAsync(url).GetAwaiter().GetResult();

        private void Download(string url, string path)
        {
            Log("Downloading " + Path.GetFileName(path) + "...");
            using (var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using (var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (var f = File.Create(path)) s.CopyTo(f);
            }
        }

        private void Status(string s) => UI(() => _status.Text = s);

        private void Log(string s) => UI(() => _log.AppendText(s + Environment.NewLine));

        private void UI(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }
    }
}
