using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
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

    internal static class Look
    {
        public static readonly Color Bg = Color.FromArgb(11, 11, 13), Text = Color.FromArgb(241, 241, 244), Dim = Color.FromArgb(157, 157, 170);
        public static readonly Color Orange = Color.FromArgb(255, 138, 30), Amber = Color.FromArgb(255, 196, 107), Deep = Color.FromArgb(255, 106, 0);
        public static readonly Color Red = Color.FromArgb(255, 59, 48), Green = Color.FromArgb(80, 220, 130);

        private static readonly PrivateFontCollection Fonts = new PrivateFontCollection();
        private static readonly List<IntPtr> Pinned = new List<IntPtr>();
        public static FontFamily Display, Body, BodySemi, Mono;

        public static void Load()
        {
            foreach (var n in new[] { "SpaceGrotesk-Bold", "Inter-Regular", "Inter-SemiBold", "JetBrainsMono-Medium" })
            {
                try
                {
                    using (var s = typeof(Look).Assembly.GetManifestResourceStream("QuestShadeInstaller.fonts." + n + ".ttf"))
                    {
                        if (s == null) continue;
                        var bytes = new byte[s.Length];
                        s.Read(bytes, 0, bytes.Length);
                        var mem = Marshal.AllocCoTaskMem(bytes.Length);
                        Marshal.Copy(bytes, 0, mem, bytes.Length);
                        Fonts.AddMemoryFont(mem, bytes.Length);
                        Pinned.Add(mem);
                    }
                }
                catch { }
            }
            FontFamily Find(string part, string fallback) => Fonts.Families.FirstOrDefault(f => f.Name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) ?? new FontFamily(fallback);
            Display = Find("Space Grotesk", "Segoe UI");
            Mono = Find("JetBrains", "Consolas");
            BodySemi = Fonts.Families.FirstOrDefault(f => f.Name.IndexOf("SemiBold", StringComparison.OrdinalIgnoreCase) >= 0) ?? new FontFamily("Segoe UI Semibold");
            Body = Fonts.Families.FirstOrDefault(f => f.Name.Equals("Inter", StringComparison.OrdinalIgnoreCase)) ?? new FontFamily("Segoe UI");
        }

        public static FontStyle StyleFor(FontFamily f)
        {
            foreach (var st in new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic, FontStyle.Bold | FontStyle.Italic })
                if (f.IsStyleAvailable(st)) return st;
            return FontStyle.Regular;
        }

        public static Font F(FontFamily f, float px) => new Font(f, px, StyleFor(f), GraphicsUnit.Pixel);

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    internal sealed class Btn
    {
        public RectangleF R;
        public string Text, Sub;
        public bool Primary, Card, Hover, Visible = true;
        public Action Click;
    }

    internal enum Screen { Welcome, Choose, Work, Done }

    internal sealed class MainForm : Form
    {
        private const string Pkg = "com.StressLevelZero.BONELAB";
        private const string MlDir = "/sdcard/MelonLoader/" + Pkg;
        private const string Repo = "MonkeOnYT-glitch/QuestShade";
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private readonly string _home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestShadeInstaller");
        private readonly List<Image> _frames = new List<Image>();
        private readonly Image _logo;
        private readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer { Interval = 33 };
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Btn> _buttons = new List<Btn>();
        private readonly TextBox _log;
        private Screen _screen = Screen.Welcome;
        private string _status = "", _detail = "", _doneTitle = "", _doneText = "";
        private bool _busy, _failed, _waitingPatch, _uninstalling;
        private int _step;
        private string _adb;
        private volatile bool _skipWait;
        private Font _fTitle, _fTag, _fBody, _fSemi, _fMono, _fSmall, _fBtn, _fH2;

        public MainForm()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("QuestShadeInstaller/1.0");
            Look.Load();
            _fTitle = Look.F(Look.Display, 78);
            _fH2 = Look.F(Look.Display, 34);
            _fTag = Look.F(Look.Body, 17);
            _fBody = Look.F(Look.Body, 14);
            _fSemi = Look.F(Look.BodySemi, 16);
            _fBtn = Look.F(Look.BodySemi, 16);
            _fMono = Look.F(Look.Mono, 12);
            _fSmall = Look.F(Look.Body, 12);

            Text = "QuestShade Installer";
            ClientSize = new Size(880, 580);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Look.Bg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            for (int i = 0; i < 8; i++)
            {
                using (var s = typeof(MainForm).Assembly.GetManifestResourceStream($"QuestShadeInstaller.bg.f{i}.jpg"))
                    if (s != null) _frames.Add(Image.FromStream(s));
            }
            using (var s = typeof(MainForm).Assembly.GetManifestResourceStream("QuestShadeInstaller.logo.png"))
                if (s != null) _logo = Image.FromStream(s);

            _log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(18, 18, 21), ForeColor = Color.FromArgb(200, 200, 212), Font = Look.F(Look.Mono, 12),
                Location = new Point(110, 362), Size = new Size(660, 96), Visible = false
            };
            Controls.Add(_log);

            _tick.Tick += (s, e) => Invalidate();
            _tick.Start();
            MouseMove += (s, e) => { bool c = false; foreach (var b in _buttons) { bool h = b.Visible && b.R.Contains(e.Location); if (h != b.Hover) { b.Hover = h; c = true; } } Cursor = _buttons.Any(b => b.Hover) ? Cursors.Hand : Cursors.Default; if (c) Invalidate(); };
            MouseClick += (s, e) => { var b = _buttons.FirstOrDefault(x => x.Visible && x.R.Contains(e.Location)); b?.Click?.Invoke(); };
            Show(Screen.Welcome);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref on, 4); } catch { }
        }

        private void Show(Screen s)
        {
            _screen = s;
            _buttons.Clear();
            int w = ClientSize.Width;
            switch (s)
            {
                case Screen.Welcome:
                    _buttons.Add(new Btn { Text = "Get started  →", Primary = true, R = new RectangleF(w / 2f - 120, 430, 240, 58), Click = () => Show(Screen.Choose) });
                    break;
                case Screen.Choose:
                    _buttons.Add(new Btn { Text = "Install or update", Sub = "Gets BONELAB ready if needed, then adds the latest QuestShade and BoneLib. Your other mods stay.", Card = true, Primary = true, R = new RectangleF(110, 250, 320, 170), Click = () => Start(false) });
                    _buttons.Add(new Btn { Text = "Uninstall", Sub = "Removes QuestShade and its updater from your Quest. BoneLib, other mods and your settings stay.", Card = true, R = new RectangleF(450, 250, 320, 170), Click = () => Start(true) });
                    _buttons.Add(new Btn { Text = "←  Back", R = new RectangleF(110, 448, 120, 44), Click = () => Show(Screen.Welcome) });
                    break;
                case Screen.Work:
                    _buttons.Add(new Btn { Text = "Show details", R = new RectangleF(110, 470, 150, 44), Click = ToggleLog });
                    _buttons.Add(new Btn { Text = "It's patched, continue", Primary = true, R = new RectangleF(560, 470, 210, 44), Visible = false, Click = () => _skipWait = true });
                    break;
                case Screen.Done:
                    if (_failed) _buttons.Add(new Btn { Text = "Try again", Primary = true, R = new RectangleF(w / 2f - 200, 430, 190, 54), Click = () => Start(_uninstalling) });
                    else _buttons.Add(new Btn { Text = "Close", Primary = true, R = new RectangleF(w / 2f - 200, 430, 190, 54), Click = Close });
                    _buttons.Add(new Btn { Text = "Back to start", R = new RectangleF(w / 2f + 10, 430, 190, 54), Click = () => Show(Screen.Choose) });
                    break;
            }
            _log.Visible = false;
            Invalidate();
        }

        private void ToggleLog()
        {
            _log.Visible = !_log.Visible;
            var b = _buttons.FirstOrDefault(x => x.Text.EndsWith("details"));
            if (b != null) b.Text = _log.Visible ? "Hide details" : "Show details";
        }

        private async void Start(bool uninstall)
        {
            if (_busy) return;
            if (uninstall && MessageBox.Show(this, "Remove QuestShade and its updater from your Quest? Your other mods, BoneLib and your QuestShade settings stay.", "Uninstall QuestShade", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _uninstalling = uninstall; _busy = true; _failed = false; _step = 0; _status = "Starting..."; _detail = "";
            Show(Screen.Work);
            try
            {
                await Task.Run(() => { if (uninstall) RunUninstall(); else RunAll(); });
                _doneTitle = uninstall ? "QuestShade removed" : "You're all set";
                _doneText = uninstall ? "QuestShade is off your Quest. Your other mods and settings are still there." : "Start BONELAB on your Quest. QuestShade is in BoneMenu.";
            }
            catch (Exception e)
            {
                _failed = true;
                Log("Error: " + e.Message);
                _doneTitle = "Something went wrong";
                _doneText = e.Message;
            }
            _busy = false;
            Show(Screen.Done);
        }

        private void RunAll()
        {
            Directory.CreateDirectory(_home);
            Step(0); Status("Getting ready...");
            EnsureAdb();
            WaitForDevice();
            Step(1);
            if (!Adb($"shell pm path {Pkg}").Contains("package:"))
                throw new Exception("BONELAB isn't installed on this Quest. Install it from the Meta store first.");
            Status("Checking BONELAB...");
            if (!Patched()) Patch();
            Step(2);
            Status("Installing QuestShade...");
            InstallMods();
            Step(4);
        }

        private void RunUninstall()
        {
            Directory.CreateDirectory(_home);
            Step(0); Status("Getting ready...");
            EnsureAdb();
            WaitForDevice();
            Step(2);
            Status("Removing QuestShade...");
            Adb($"shell rm -f {MlDir}/Mods/QuestShade.dll {MlDir}/Plugins/QuestShadeUpdater.dll");
            Log("QuestShade removed. Settings are still in UserData/QuestShade if you reinstall.");
            Step(4);
        }

        private void Step(int n) => UI(() => _step = n);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.Bilinear;
            float t = (float)_clock.Elapsed.TotalSeconds;
            int W = ClientSize.Width, H = ClientSize.Height;
            DrawFootage(g, t, W, H);
            DrawOverlay(g, t, W, H);
            switch (_screen)
            {
                case Screen.Welcome: DrawWelcome(g, t, W); break;
                case Screen.Choose: DrawChoose(g, W); break;
                case Screen.Work: DrawWork(g, t, W); break;
                case Screen.Done: DrawDone(g, W); break;
            }
            foreach (var b in _buttons) if (b.Visible) DrawButton(g, b);
        }

        private void DrawFootage(Graphics g, float t, int W, int H)
        {
            if (_frames.Count == 0) { g.Clear(Look.Bg); return; }
            const float hold = 6f, fade = 1.6f;
            int n = _frames.Count;
            int i = (int)(t / hold) % n;
            float local = t % hold;
            DrawFrame(g, _frames[i], local / hold, W, H, 1f);
            if (local > hold - fade)
            {
                float a = (local - (hold - fade)) / fade;
                DrawFrame(g, _frames[(i + 1) % n], 0f, W, H, a * a * (3 - 2 * a));
            }
        }

        private static void DrawFrame(Graphics g, Image img, float p, int W, int H, float alpha)
        {
            float z = 1.04f + 0.08f * p;
            float iw = W * z, ih = iw * img.Height / img.Width;
            if (ih < H * z) { ih = H * z; iw = ih * img.Width / img.Height; }
            var dst = new RectangleF((W - iw) / 2f - 14f * p, (H - ih) / 2f, iw, ih);
            if (alpha >= 0.999f) { g.DrawImage(img, dst); return; }
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                g.DrawImage(img, new[] { dst.Location, new PointF(dst.Right, dst.Top), new PointF(dst.Left, dst.Bottom) }, new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
            }
        }

        private void DrawOverlay(Graphics g, float t, int W, int H)
        {
            using (var b = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(120, 11, 11, 13), Color.FromArgb(225, 11, 11, 13), 90f))
                g.FillRectangle(b, 0, 0, W, H);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(-W * 0.25f, -H * 0.3f, W * 1.5f, H * 1.6f);
                using (var pb = new PathGradientBrush(path) { CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(200, 0, 0, 0) } })
                    g.FillRectangle(pb, 0, 0, W, H);
            }
            using (var pen = new Pen(Color.FromArgb(14, 255, 255, 255)))
                for (int y = 0; y < H; y += 3) g.DrawLine(pen, 0, y, W, y);

            using (var pen = new Pen(Color.FromArgb(215, 241, 241, 244), 2f))
            {
                int m = 22, L = 24;
                g.DrawLines(pen, new[] { new Point(m, m + L), new Point(m, m), new Point(m + L, m) });
                g.DrawLines(pen, new[] { new Point(W - m - L, m), new Point(W - m, m), new Point(W - m, m + L) });
                g.DrawLines(pen, new[] { new Point(m, H - m - L), new Point(m, H - m), new Point(m + L, H - m) });
                g.DrawLines(pen, new[] { new Point(W - m - L, H - m), new Point(W - m, H - m), new Point(W - m, H - m - L) });
            }
            bool blink = ((int)(t * 1.6f)) % 2 == 0;
            if (blink) using (var rb = new SolidBrush(Look.Red)) g.FillEllipse(rb, 40, 36, 9, 9);
            int f = (int)(t * 30) % 30, s = (int)t % 60, mi = (int)(t / 60) % 60;
            using (var wb = new SolidBrush(Look.Text))
            {
                g.DrawString("REC", _fMono, wb, 54, 32);
                g.DrawString($"00:{mi:00}:{s:00}:{f:00}", _fMono, wb, 96, 32);
                var right = _busy ? "USB · ADB · WORKING" : "QUEST · INSTALLER";
                var sz = g.MeasureString(right, _fMono);
                g.DrawString(right, _fMono, wb, W - 40 - sz.Width, 32);
            }
            using (var db = new SolidBrush(Color.FromArgb(150, 157, 157, 170)))
            {
                var foot = "GPL-3.0  ·  DOWNLOADS ONLY FROM GITHUB, THUNDERSTORE & GOOGLE  ·  SENDS NOTHING ABOUT YOU";
                var sz = g.MeasureString(foot, _fMono);
                g.DrawString(foot, _fMono, db, (W - sz.Width) / 2f, H - 40);
            }
        }

        private void DrawLogo(Graphics g, float cx, float y, float size)
        {
            if (_logo == null) return;
            for (int k = 6; k >= 1; k--)
                using (var gb = new SolidBrush(Color.FromArgb(10, Look.Orange)))
                using (var p = Look.Round(new RectangleF(cx - size / 2 - k * 4, y - k * 4, size + k * 8, size + k * 8), 18 + k * 4))
                    g.FillPath(gb, p);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_logo, cx - size / 2, y, size, size);
            g.InterpolationMode = InterpolationMode.Bilinear;
        }

        private void GradientText(Graphics g, string text, Font font, float cx, float y)
        {
            using (var path = new GraphicsPath())
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center };
                path.AddString(text, font.FontFamily, (int)font.Style, font.Size, new PointF(cx, y), fmt);
                var b = path.GetBounds();
                for (int k = 5; k >= 1; k--)
                    using (var pen = new Pen(Color.FromArgb(9, Look.Orange), k * 6) { LineJoin = LineJoin.Round })
                        g.DrawPath(pen, path);
                using (var br = new LinearGradientBrush(new RectangleF(b.X, b.Y - 1, b.Width, b.Height + 2), Color.White, Color.FromArgb(255, 214, 178), 90f))
                    g.FillPath(br, path);
            }
        }

        private void Center(Graphics g, string s, Font f, Color c, float cx, float y, float width = 640)
        {
            using (var b = new SolidBrush(c))
                g.DrawString(s, f, b, new RectangleF(cx - width / 2, y, width, 200), new StringFormat { Alignment = StringAlignment.Center });
        }

        private void DrawWelcome(Graphics g, float t, int W)
        {
            DrawLogo(g, W / 2f, 92, 84);
            GradientText(g, "QuestShade", _fTitle, W / 2f, 196);
            using (var amber = new SolidBrush(Look.Amber))
            using (var white = new SolidBrush(Look.Text))
            {
                string a = "ReShade for BONELAB", b = " on standalone Quest, plus a camera";
                float wa = g.MeasureString(a, _fSemi).Width, wb = g.MeasureString(b, _fTag).Width;
                float x = W / 2f - (wa + wb) / 2f;
                g.DrawString(a, _fSemi, amber, x, 300);
                g.DrawString(b, _fTag, white, x + wa - 9, 301);
                Center(g, "you can actually pick up and film with. This installs it for you.", _fTag, Look.Text, W / 2f, 326);
            }
            string[] need = { "This Windows PC", "Your Quest", "A USB-C cable" };
            var nf = Look.F(Look.BodySemi, 13);
            float pw = 172, gap = 10, x0 = W / 2f - (need.Length * pw + (need.Length - 1) * gap) / 2f;
            for (int k = 0; k < need.Length; k++)
            {
                var r = new RectangleF(x0 + k * (pw + gap), 366, pw, 36);
                using (var p = Look.Round(r, 18))
                {
                    using (var br = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(46, 255, 255, 255))) g.DrawPath(pen, p);
                }
                var tw = g.MeasureString(need[k], nf).Width;
                float dx = r.X + (r.Width - tw - 14) / 2f;
                using (var ob = new SolidBrush(Look.Orange)) g.FillEllipse(ob, dx, r.Y + r.Height / 2f - 4, 8, 8);
                using (var tb = new SolidBrush(Look.Text))
                    g.DrawString(need[k], nf, tb, dx + 14, r.Y + (r.Height - nf.GetHeight(g)) / 2f);
            }
            Center(g, "Run this on your PC, not the headset. Plug your Quest into the PC with the cable, then press Get started.", _fSmall, Look.Dim, W / 2f, 500, 700);
        }

        private void DrawChoose(Graphics g, int W)
        {
            GradientText(g, "What do you want to do?", _fH2, W / 2f, 120);
            Center(g, "Keep your Quest plugged into this PC. Developer mode needs to be on in the Meta Horizon app.", _fBody, Look.Dim, W / 2f, 182, 700);
        }

        private void DrawWork(Graphics g, float t, int W)
        {
            GradientText(g, _uninstalling ? "Uninstalling" : "Installing", _fH2, W / 2f, 96);
            string[] names = _uninstalling ? new[] { "Find your Quest", "", "Remove QuestShade", "Done" } : new[] { "Find your Quest", "Patch BONELAB", "Install QuestShade", "Done" };
            var steps = names.Select((n, i) => (n, i)).Where(x => x.n.Length > 0).ToList();
            float pw = 160, gap = 12, total = steps.Count * pw + (steps.Count - 1) * gap, x0 = W / 2f - total / 2f;
            for (int k = 0; k < steps.Count; k++)
            {
                var (name, idx) = steps[k];
                bool done = _step > idx || _step >= 4, active = !done && _step == idx;
                var r = new RectangleF(x0 + k * (pw + gap), 168, pw, 40);
                using (var p = Look.Round(r, 20))
                {
                    if (active)
                        using (var br = new LinearGradientBrush(r, Color.FromArgb(70, Look.Amber), Color.FromArgb(70, Look.Deep), 0f)) g.FillPath(br, p);
                    else using (var br = new SolidBrush(Color.FromArgb(done ? 34 : 18, done ? Look.Green : Color.White))) g.FillPath(br, p);
                    using (var pen = new Pen(active ? Look.Orange : done ? Color.FromArgb(150, Look.Green) : Color.FromArgb(46, 255, 255, 255), 1.4f)) g.DrawPath(pen, p);
                }
                string mark = done ? "✓ " : active ? "● " : "";
                using (var sb = new SolidBrush(done ? Look.Green : active ? Look.Text : Look.Dim))
                    g.DrawString(mark + name, Look.F(Look.BodySemi, 13), sb, r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }

            Center(g, _status, _fSemi, Look.Text, W / 2f, 240, 680);
            Center(g, _detail, _fSmall, Look.Dim, W / 2f, 300, 680);

            var track = new RectangleF(110, 340, W - 220, 6);
            using (var p = Look.Round(track, 3)) using (var br = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) g.FillPath(br, p);
            if (_busy)
            {
                float seg = 180, pos = ((t * 260f) % (track.Width + seg)) - seg;
                var r = new RectangleF(track.X + Math.Max(0, pos), track.Y, Math.Min(seg, track.Width - Math.Max(0, pos)) - Math.Max(0, -pos), track.Height);
                if (r.Width > 1)
                {
                    g.SetClip(track);
                    using (var p = Look.Round(r, 3)) using (var br = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Look.Amber, Look.Deep, 0f)) g.FillPath(br, p);
                    g.ResetClip();
                }
            }
            var cont = _buttons.FirstOrDefault(b => b.Text.StartsWith("It's patched"));
            if (cont != null) cont.Visible = _waitingPatch;
        }

        private void DrawDone(Graphics g, int W)
        {
            float cx = W / 2f, cy = 170;
            var col = _failed ? Look.Red : Look.Green;
            for (int k = 6; k >= 1; k--) using (var gb = new SolidBrush(Color.FromArgb(10, col))) g.FillEllipse(gb, cx - 46 - k * 5, cy - 46 - k * 5, 92 + k * 10, 92 + k * 10);
            using (var cb = new SolidBrush(Color.FromArgb(40, col))) g.FillEllipse(cb, cx - 46, cy - 46, 92, 92);
            using (var pen = new Pen(col, 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (_failed) { g.DrawLine(pen, cx - 16, cy - 16, cx + 16, cy + 16); g.DrawLine(pen, cx + 16, cy - 16, cx - 16, cy + 16); }
                else g.DrawLines(pen, new[] { new PointF(cx - 20, cy + 2), new PointF(cx - 5, cy + 17), new PointF(cx + 22, cy - 14) });
            }
            GradientText(g, _doneTitle, _fH2, cx, 248);
            Center(g, _doneText, _fTag, _failed ? Look.Amber : Look.Text, cx, 312, 660);
        }

        private void DrawButton(Graphics g, Btn b)
        {
            var r = b.R;
            if (b.Card)
            {
                using (var p = Look.Round(r, 18))
                {
                    using (var br = new SolidBrush(Color.FromArgb(b.Hover ? 34 : 20, 255, 255, 255))) g.FillPath(br, p);
                    using (var pen = new Pen(b.Primary ? Color.FromArgb(b.Hover ? 255 : 170, Look.Orange) : Color.FromArgb(b.Hover ? 90 : 46, 255, 255, 255), b.Primary ? 1.6f : 1.2f)) g.DrawPath(pen, p);
                }
                if (b.Primary)
                    using (var bar = new LinearGradientBrush(new RectangleF(r.X + 24, r.Y + 26, 44, 4), Look.Amber, Look.Deep, 0f)) g.FillRectangle(bar, r.X + 24, r.Y + 26, 44, 4);
                using (var tb = new SolidBrush(Look.Text)) g.DrawString(b.Text, Look.F(Look.Display, 24), tb, r.X + 22, r.Y + 42);
                using (var sb = new SolidBrush(Look.Dim)) g.DrawString(b.Sub ?? "", _fBody, sb, new RectangleF(r.X + 24, r.Y + 84, r.Width - 48, r.Height - 90));
                return;
            }
            using (var p = Look.Round(r, 14))
            {
                if (b.Primary)
                {
                    for (int k = 5; k >= 1; k--)
                        using (var gp = Look.Round(new RectangleF(r.X - k * 3, r.Y + 6 - k * 2, r.Width + k * 6, r.Height + k * 4), 14 + k * 3))
                        using (var gb = new SolidBrush(Color.FromArgb(b.Hover ? 22 : 15, Look.Orange))) g.FillPath(gb, gp);
                    using (var br = new LinearGradientBrush(r, Look.Amber, Look.Deep, 35f))
                    {
                        br.InterpolationColors = new ColorBlend { Colors = new[] { Look.Amber, Look.Orange, Look.Deep }, Positions = new[] { 0f, 0.55f, 1f } };
                        g.FillPath(br, p);
                    }
                    using (var hl = new Pen(Color.FromArgb(120, 255, 255, 255), 1f)) g.DrawLine(hl, r.X + 12, r.Y + 1.5f, r.Right - 12, r.Y + 1.5f);
                    using (var tb = new SolidBrush(Color.FromArgb(26, 12, 0)))
                        g.DrawString(b.Text, _fBtn, tb, r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
                else
                {
                    using (var br = new SolidBrush(Color.FromArgb(b.Hover ? 31 : 18, 255, 255, 255))) g.FillPath(br, p);
                    using (var pen = new Pen(Color.FromArgb(46, 255, 255, 255), 1.2f)) g.DrawPath(pen, p);
                    using (var tb = new SolidBrush(Look.Text))
                        g.DrawString(b.Text, Look.F(Look.BodySemi, 14), tb, r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
            }
        }

        private void EnsureAdb()
        {
            _adb = Path.Combine(_home, "platform-tools", "adb.exe");
            if (File.Exists(_adb)) { Log("ADB ready."); return; }
            Status("Getting ADB (the tool that talks to your Quest)...");
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
                    msg = "Plug your Quest into this PC with a USB cable. Developer mode has to be on.";
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
            Status("In the LemonLoader window: pick your Quest and BONELAB, press Patch.");
            Detail("When it's done, start BONELAB once on your Quest and wait for the main menu. This carries on by itself.");
            Log("LemonLoader is made by the LemonLoader team, GPL-3.0: github.com/LemonLoader/MelonLoaderInstaller");
            try { Process.Start(new ProcessStartInfo(lemonExe) { UseShellExecute = true, WorkingDirectory = lemonDir }); }
            catch (Exception e) { Log("Couldn't open LemonLoader: " + e.Message); }
            _skipWait = false;
            _waitingPatch = true;
            while (!_skipWait && !Patched()) Thread.Sleep(3000);
            _waitingPatch = false;
            Detail("");
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
            Detail($"QuestShade {tag}");

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

        private void Status(string s) => UI(() => _status = s);

        private void Detail(string s) => UI(() => _detail = s);

        private void Log(string s) => UI(() => _log.AppendText(s + Environment.NewLine));

        private void UI(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a); else a();
        }
    }
}
