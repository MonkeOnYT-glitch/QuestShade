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

        private static readonly Color Bg = Color.FromArgb(14, 14, 18), Card = Color.FromArgb(24, 24, 31), Line = Color.FromArgb(44, 44, 56);
        private static readonly Color Accent = Color.FromArgb(255, 138, 31), Good = Color.FromArgb(80, 220, 130), Dim = Color.FromArgb(120, 120, 135);

        private readonly Label _status;
        private readonly TextBox _log;
        private readonly FancyButton _go, _continue, _remove;
        private readonly LinkLabel _details;
        private readonly ProgressBar _bar;
        private readonly Label[] _steps = new Label[4];
        private readonly string _home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestShadeInstaller");
        private string _adb;
        private volatile bool _skipWait;

        public MainForm()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("QuestShadeInstaller/1.0");
            Text = "QuestShade Installer";
            ClientSize = new Size(680, 470);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Location = new Point(0, 0), Size = new Size(680, 96), BackColor = Card };
            header.Paint += (s, e) =>
            {
                using (var br = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 92, 680, 4), Accent, Color.FromArgb(255, 60, 120), 0f))
                    e.Graphics.FillRectangle(br, 0, 92, 680, 4);
            };
            var logo = new PictureBox { Location = new Point(22, 16), Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            try { using (var st = typeof(MainForm).Assembly.GetManifestResourceStream("QuestShadeInstaller.logo.png")) if (st != null) logo.Image = Image.FromStream(st); } catch { }
            var title = new Label { Text = "QuestShade", Font = new Font("Segoe UI Semibold", 22f), ForeColor = Color.White, AutoSize = true, Location = new Point(98, 14), BackColor = Color.Transparent };
            var sub = new Label { Text = "ReShade-style effects and a real camera for BONELAB on Quest", AutoSize = true, ForeColor = Color.FromArgb(185, 185, 200), Location = new Point(101, 56), BackColor = Color.Transparent };
            header.Controls.AddRange(new Control[] { logo, title, sub });

            string[] names = { "Find your Quest", "Patch BONELAB", "Install QuestShade", "Done" };
            for (int k = 0; k < 4; k++)
            {
                _steps[k] = new Label { Text = "\u25CB  " + names[k], AutoSize = true, ForeColor = Dim, Font = new Font("Segoe UI Semibold", 10f), Location = new Point(24 + k * 160, 114) };
            }

            _status = new Label { Text = "Plug your Quest into this PC, then press Install.", Font = new Font("Segoe UI Semibold", 12.5f), Location = new Point(22, 150), Size = new Size(636, 56) };
            _bar = new ProgressBar { Location = new Point(24, 210), Size = new Size(632, 6), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, Visible = false };
            _details = new LinkLabel { Text = "Show details", AutoSize = true, Location = new Point(22, 228), LinkColor = Dim, ActiveLinkColor = Accent, LinkBehavior = LinkBehavior.HoverUnderline };
            _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Location = new Point(24, 252), Size = new Size(632, 120), BackColor = Card, ForeColor = Color.FromArgb(200, 200, 212), BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f), Visible = false };
            _details.Click += (s, e) => { _log.Visible = !_log.Visible; _details.Text = _log.Visible ? "Hide details" : "Show details"; };

            _go = new FancyButton("Install QuestShade", Accent, Color.Black, true) { Location = new Point(24, 390), Size = new Size(210, 46) };
            _continue = new FancyButton("It's patched, continue", Card, Color.White, false) { Location = new Point(244, 390), Size = new Size(200, 46), Visible = false };
            _remove = new FancyButton("Uninstall", Card, Color.White, false) { Location = new Point(536, 390), Size = new Size(120, 46) };
            var privacy = new Label { Text = "Downloads only from GitHub, Thunderstore and Google. Sends nothing about you.  \u2022  GPL-3.0", AutoSize = true, ForeColor = Dim, Font = new Font("Segoe UI", 8f), Location = new Point(22, 446) };
            _go.Click += async (s, e) => await Go();
            _continue.Click += (s, e) => _skipWait = true;
            _remove.Click += async (s, e) => await Remove();
            Controls.Add(header);
            Controls.AddRange(_steps);
            Controls.AddRange(new Control[] { _status, _bar, _details, _log, _go, _continue, _remove, privacy });
        }

        private void Step(int n)
        {
            UI(() =>
            {
                for (int k = 0; k < 4; k++)
                {
                    var text = _steps[k].Text.Substring(3);
                    if (k < n) { _steps[k].Text = "\u2713  " + text; _steps[k].ForeColor = Good; }
                    else if (k == n) { _steps[k].Text = (n == 3 ? "\u2713  " : "\u25CF  ") + text; _steps[k].ForeColor = n == 3 ? Good : Accent; }
                    else { _steps[k].Text = "\u25CB  " + text; _steps[k].ForeColor = Dim; }
                }
            });
        }

        private void Busy(bool on) => UI(() => { _bar.Visible = on; _go.Enabled = _remove.Enabled = !on; });

        private async Task Go()
        {
            Busy(true);
            try
            {
                await Task.Run(RunAll);
            }
            catch (Exception e)
            {
                Log("Error: " + e.Message);
                Status("Something went wrong: " + e.Message);
                UI(() => { _log.Visible = true; _details.Text = "Hide details"; });
            }
            finally
            {
                Busy(false);
                UI(() => _continue.Visible = false);
            }
        }

        private async Task Remove()
        {
            if (MessageBox.Show(this, "Remove QuestShade and its updater from your Quest? Your other mods, BoneLib and your QuestShade settings stay.", "Uninstall QuestShade", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Busy(true);
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(_home);
                    EnsureAdb();
                    WaitForDevice();
                    Adb($"shell rm -f {MlDir}/Mods/QuestShade.dll {MlDir}/Plugins/QuestShadeUpdater.dll");
                    Log("QuestShade removed. Settings are still in UserData/QuestShade if you reinstall.");
                    Status("QuestShade is uninstalled.");
                });
            }
            catch (Exception e) { Log("Error: " + e.Message); Status("Couldn't uninstall: " + e.Message); }
            finally { Busy(false); }
        }

        private void RunAll()
        {
            Directory.CreateDirectory(_home);
            Step(0);
            Status("Getting ADB (the tool that talks to your Quest)...");
            EnsureAdb();
            WaitForDevice();
            Step(1);
            if (!Adb($"shell pm path {Pkg}").Contains("package:"))
                throw new Exception("BONELAB isn't installed on this Quest. Install it from the Meta store first.");
            if (!Patched()) Patch();
            Step(2);
            Status("Installing QuestShade...");
            InstallMods();
            Step(3);
            Status("All done! Start BONELAB on your Quest. QuestShade is in BoneMenu.");
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

    internal sealed class FancyButton : Control
    {
        private readonly Color _fill, _text;
        private readonly bool _primary;
        private bool _hover, _down;

        public FancyButton(string text, Color fill, Color textColor, bool primary)
        {
            Text = text; _fill = fill; _text = textColor; _primary = primary;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Font = new Font("Segoe UI Semibold", 10.5f);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Round(r, 10))
            {
                var fill = !Enabled ? Color.FromArgb(40, 40, 48) : _down ? Shade(_fill, -30) : _hover ? Shade(_fill, 22) : _fill;
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (!_primary) using (var pen = new Pen(Enabled ? Color.FromArgb(70, 70, 86) : Color.FromArgb(50, 50, 60))) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, r, Enabled ? _text : Color.FromArgb(110, 110, 120), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static Color Shade(Color c, int d) => Color.FromArgb(Clamp(c.R + d), Clamp(c.G + d), Clamp(c.B + d));
        private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

        private static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
