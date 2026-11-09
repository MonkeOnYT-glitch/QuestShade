using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader.Utils;
using UnityEngine;

namespace QuestShade
{
    internal sealed class Config
    {
        public static string Root, PresetDir, LutDir, MainIniPath;

        public bool Enabled = true;
        public string PresetName = "Vivid";
        public bool HotReload = true;
        public float ReloadInterval = 2f;
        public bool AllowUnsupportedOnQuest = false;
        public bool ShowToasts = true;
        public bool AutoUpdate = true;
        public string ShareServer = Sharing.DefaultServer, ShareAuthor = "", ShareClientId = "";
        public bool EmulateQuest;
        public bool BloomDofHeadset = false, BloomDofPreview = true, BloomDofCapture = true, BloomDofAll = true;
        public bool MatchPreview = true;
        public float BloomStrength = 1f, BlurStrength = 1f, GripAngle = 0f;
        public bool HeadsetBloom = true;
        public bool EyeAdapt = true;
        public float EyeAdaptStrength = 0.7f, EyeAdaptSpeed = 1f;
        public bool TimelineFollow = false, TimelineAutoRecord = true;

        public Binding ToggleEffects, NextPreset, PrevPreset, ToggleCamera, TakePhoto, CameraMode, HideCamera, Record;
        public bool Haptics = true, Keyboard = true;
        public float HoldTime = 0.6f, DoubleTapTime = 0.35f;

        public int PreviewWidth = 512, PreviewHeight = 288, PreviewFps = 24;
        public int PhotoWidth = 1920, PhotoHeight = 1080, PhotoMsaa = 2;
        public float Fov = 60f, FarClip = 0f, Smoothing = 0.12f, ScreenSize = 0.16f;
        public bool PreviewShadows = false, PhotoShadows = true, CameraPostProcessing = true;
        public Vector3 HandOffset = new Vector3(0f, 0.06f, 0.10f);
        public float HandTilt = 25f;
        public float ThirdPersonDistance = 1.6f, ThirdPersonHeight = 0.35f, ThirdPersonSide = 0.35f;
        public bool FusionEffectsOnly = true;
        public bool PhysicalCamera = false, FreezeOnRelease = true, HandheldModel = true, HoverPreview = false, CameraLabel = true;
        public int PhotoTimer = 0;
        public string BodyBarcode = "c1534c5a-87ce-436d-b00c-ef9547726176";
        public float BodyMountHeight = 0.06f;
        public float ChestFov = 100f, ChestForward = 0.16f, ChestHeight = 0f, ChestSide = 0f, ChestPitch = -5f;
        public string SaveFolder = "auto";
        public CamMode DefaultMode = CamMode.Hand;
        public bool PreviewEnabled = true;
        public int RecordWidth = 1920, RecordHeight = 1080, RecordFps = 30, RecordBitrateKbps = 35000;
        public float RecordSupersample = 1.5f;
        public bool RecordGameAudio = false, RecordMic = false;
        public float GameVolume = 1f, MicVolume = 1f;
        public int AudioBitrateKbps = 128;

        public Ini Preset { get; private set; }
        public string PresetFile { get; private set; }
        public List<string> Presets { get; } = new List<string>();
        public int PresetIndex { get; private set; }

        private DateTime _mainTime, _presetTime, _lutTime;
        private string _lutWatched;
        private float _nextCheck;

        public static void InitPaths()
        {
            Root = Path.Combine(MelonEnvironment.UserDataDirectory, "QuestShade");
            PresetDir = Path.Combine(Root, "Presets");
            LutDir = Path.Combine(Root, "LUTs");
            MainIniPath = Path.Combine(Root, "QuestShade.ini");
            Directory.CreateDirectory(PresetDir);
            Directory.CreateDirectory(LutDir);
            try { var nm = Path.Combine(Root, ".nomedia"); if (!File.Exists(nm)) File.WriteAllText(nm, ""); } catch { }

            ExtractDefaults();
        }

        private static void ExtractDefaults()
        {
            var asm = Assembly.GetExecutingAssembly();
            const string prefix = "QuestShade.Defaults.";
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(prefix)) continue;
                var rel = name.Substring(prefix.Length).Replace('\\', '/');
                var dest = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
                byte[] shipped;
                using (var s = asm.GetManifestResourceStream(name)) { using var ms = new MemoryStream(); s.CopyTo(ms); shipped = ms.ToArray(); }
                if (File.Exists(dest))
                {
                    try
                    {
                        string have = Fingerprint(File.ReadAllBytes(dest));
                        if (OldShipped.Contains(have) && have != Fingerprint(shipped))
                        {
                            File.WriteAllBytes(dest, shipped);
                            Mod.Log.Msg("Updated built-in preset " + Path.GetFileNameWithoutExtension(dest));
                        }
                    }
                    catch { }
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                File.WriteAllBytes(dest, shipped);
            }
        }

        private static readonly HashSet<string> OldShipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "067051ba79d03c32c691b2faf0dd4ccfed1893de7b5c55d9f578c26f6f8b7ed9",
            "0cb46f80045875a8330dbdcef42125ff1d6b4120f85f47b5f13b29b695b82e20",
            "1a44a6ccd5affe81bd556b46ecd95cb45498a8bdefb2e1413461208513cc4c87",
            "cdaf1f72e5926ba692043ba6477d3429548757661efd7d75f652d0f689227220",
            "f6f6ea28d07a0085f6e6de9947c6acb198b600f554dfd0417420a9eb27026d53"
        };

        private static string Fingerprint(byte[] b)
        {
            var t = System.Text.Encoding.UTF8.GetString(b).TrimStart((char)0xFEFF).Replace("\r\n", "\n").TrimEnd();
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(t))).ToLowerInvariant();
        }

        public void LoadAll()
        {
            LoadMain();
            if (_pendingClientId) { _pendingClientId = false; SetKey("Sharing", "ClientId", ShareClientId); }
            ScanPresets();
            LoadPreset(PresetName);
        }

        private bool _pendingClientId;

        private void LoadMain()
        {
            var ini = File.Exists(MainIniPath) ? Ini.Parse(File.ReadAllText(MainIniPath)) : Ini.Parse("");
            _mainTime = Mtime(MainIniPath);

            Enabled = ini.Bool("General", "Enabled", true);
            PresetName = ini.Str("General", "Preset", PresetName);
            HotReload = ini.Bool("General", "HotReload", true);
            ReloadInterval = Mathf.Max(0.5f, ini.Float("General", "ReloadInterval", 2f));
            AllowUnsupportedOnQuest = ini.Bool("General", "AllowUnsupportedOnQuest", false);
            ShowToasts = ini.Bool("General", "ShowToasts", true);
            AutoUpdate = ini.Bool("General", "AutoUpdate", true);
            ShareServer = (ini.Str("Sharing", "Server", Sharing.DefaultServer) ?? Sharing.DefaultServer).Trim().TrimEnd('/');
            ShareAuthor = ini.Str("Sharing", "Author", "") ?? "";
            ShareClientId = ini.Str("Sharing", "ClientId", "") ?? "";
            if (ShareClientId.Length < 16) { ShareClientId = Guid.NewGuid().ToString("N"); _pendingClientId = true; }
            BloomDofHeadset = ini.Bool("General", "BloomDofHeadset", false);
            BloomDofPreview = ini.Bool("General", "BloomDofPreview", true);
            BloomDofCapture = ini.Bool("General", "BloomDofPhotosVideo", true);
            MatchPreview = ini.Bool("General", "MatchPreview", true);
            BloomDofAll = ini.Bool("General", "BloomDofEveryPreset", true);
            BloomStrength = Mathf.Clamp(ini.Float("General", "BloomStrength", 1f), 0f, 4f);
            HeadsetBloom = ini.Bool("General", "HeadsetBloom", true);
            EyeAdapt = ini.Bool("General", "EyeAdaptation", true);
            EyeAdaptStrength = Mathf.Clamp(ini.Float("General", "EyeAdaptationStrength", 0.7f), 0f, 1.5f);
            EyeAdaptSpeed = Mathf.Clamp(ini.Float("General", "EyeAdaptationSpeed", 1f), 0.1f, 4f);
            BlurStrength = Mathf.Clamp(ini.Float("General", "BlurStrength", 1f), 0f, 3f);
            GripAngle = Mathf.Clamp(ini.Float("Camera", "GripAngle", 0f), -90f, 90f);
            TimelineFollow = ini.Bool("Camera", "TimelineFollow", false);
            TimelineAutoRecord = ini.Bool("Camera", "TimelineAutoRecord", true);

            ToggleEffects = Binding.Parse(ini.Str("Controls", "ToggleEffects", "RightStickHold"));
            NextPreset = Binding.Parse(ini.Str("Controls", "NextPreset", "RightStickDoubleTap"));
            PrevPreset = Binding.Parse(ini.Str("Controls", "PrevPreset", "None"));
            ToggleCamera = Binding.Parse(ini.Str("Controls", "ToggleCamera", "BothSticks"));
            TakePhoto = Binding.Parse(ini.Str("Controls", "TakePhoto", "LeftStickDoubleTap"));
            CameraMode = Binding.Parse(ini.Str("Controls", "CameraMode", "LeftStickHold"));
            HideCamera = Binding.Parse(ini.Str("Controls", "HideCamera", "LeftGripStickHold"));
            Record = Binding.Parse(ini.Str("Controls", "Record", "RightGripStickHold"));
            Haptics = ini.Bool("Controls", "Haptics", true);
            Keyboard = ini.Bool("Controls", "Keyboard", true);
            HoldTime = Mathf.Clamp(ini.Float("Controls", "HoldTime", 0.6f), 0.2f, 3f);
            DoubleTapTime = Mathf.Clamp(ini.Float("Controls", "DoubleTapTime", 0.35f), 0.15f, 1f);

            PreviewWidth = Mathf.Clamp(ini.Int("Camera", "PreviewWidth", 512), 64, 2048);
            PreviewHeight = Mathf.Clamp(ini.Int("Camera", "PreviewHeight", 288), 64, 2048);
            PreviewFps = Mathf.Clamp(ini.Int("Camera", "PreviewFps", 24), 1, 120);
            PhotoWidth = Mathf.Clamp(ini.Int("Camera", "PhotoWidth", 1920), 128, 4096);
            PhotoHeight = Mathf.Clamp(ini.Int("Camera", "PhotoHeight", 1080), 128, 4096);
            PhotoMsaa = ini.Int("Camera", "PhotoMSAA", 2) switch { >= 8 => 8, >= 4 => 4, >= 2 => 2, _ => 1 };
            if (Application.platform == RuntimePlatform.Android) PhotoMsaa = 1;
            Fov = Mathf.Clamp(ini.Float("Camera", "FieldOfView", 60f), 10f, 140f);
            var fc = ini.Float("Camera", "FarClip", 0f);
            FarClip = fc <= 0f ? 0f : Mathf.Clamp(fc, 10f, 100000f);
            Smoothing = Mathf.Clamp01(ini.Float("Camera", "Smoothing", 0.12f));
            ScreenSize = Mathf.Clamp(ini.Float("Camera", "ScreenSize", 0.16f), 0.05f, 1f);
            PreviewShadows = ini.Bool("Camera", "PreviewShadows", false);
            PhotoShadows = ini.Bool("Camera", "PhotoShadows", true);
            CameraPostProcessing = ini.Bool("Camera", "PostProcessing", true);
            HandOffset = ini.Vec3("Camera", "HandOffset", HandOffset);
            HandTilt = ini.Float("Camera", "HandTilt", HandTilt);
            ThirdPersonDistance = Mathf.Clamp(ini.Float("Camera", "ThirdPersonDistance", 1.6f), 0.3f, 10f);
            ThirdPersonHeight = Mathf.Clamp(ini.Float("Camera", "ThirdPersonHeight", 0.35f), -2f, 5f);
            ThirdPersonSide = Mathf.Clamp(ini.Float("Camera", "ThirdPersonSide", 0.35f), -3f, 3f);
            PhysicalCamera = ini.Bool("Camera", "CupBody", false);
            FusionEffectsOnly = ini.Bool("Camera", "NoCameraInFusion", true);
            HoverPreview = ini.Bool("Camera", "HoverPreview", false);
            CameraLabel = ini.Bool("Camera", "ControlsLabel", true);
            PhotoTimer = Mathf.Clamp(ini.Int("Camera", "PhotoTimer", 0), 0, 30);
            HandheldModel = !string.Equals((ini.Str("Camera", "CameraModel", "handheld") ?? "").Trim(), "simple", StringComparison.OrdinalIgnoreCase);
            FreezeOnRelease = ini.Bool("Camera", "FreezeOnRelease", true);
            BodyBarcode = (ini.Str("Camera", "BodyBarcode", BodyBarcode) ?? BodyBarcode).Trim();
            BodyMountHeight = Mathf.Clamp(ini.Float("Camera", "BodyMountHeight", 0.06f), -0.5f, 0.5f);
            ChestFov = Mathf.Clamp(ini.Float("Camera", "ChestFieldOfView", 100f), 10f, 150f);
            ChestForward = Mathf.Clamp(ini.Float("Camera", "ChestForward", 0.16f), -0.5f, 1f);
            ChestHeight = Mathf.Clamp(ini.Float("Camera", "ChestHeight", 0f), -1f, 1f);
            ChestSide = Mathf.Clamp(ini.Float("Camera", "ChestSide", 0f), -1f, 1f);
            ChestPitch = Mathf.Clamp(ini.Float("Camera", "ChestPitch", -5f), -60f, 60f);
            SaveFolder = ini.Str("Camera", "SaveFolder", "auto");
            var modeStr = ini.Str("Camera", "Mode", "Hand");
            if (string.Equals(modeStr?.Trim(), "FirstPerson", StringComparison.OrdinalIgnoreCase)) modeStr = "Chest";
            DefaultMode = Enum.TryParse<CamMode>(modeStr, true, out var dm) ? dm : CamMode.Hand;
            PreviewEnabled = ini.Bool("Camera", "Preview", true);
            var q = (ini.Str("Camera", "RecordQuality", "ultra") ?? "ultra").Trim().ToLowerInvariant();
            int autoKbps;
            long budget = Device.RecordPixelBudget;
            if (q == "ultra" || q == "auto")
                q = budget >= 1920L * 1080 ? "1080p" : budget >= 1280L * 720 ? "720p" : "540p";
            (RecordWidth, RecordHeight, autoKbps) = q switch
            {
                "480p" => (848, 480, 1300),
                "540p" => (960, 540, 1600),
                "1080p" => (1920, 1080, 4500),
                _ => (1280, 720, 2500),
            };
            RecordSupersample = Mathf.Clamp(ini.Float("Camera", "RecordSupersample", 1f), 1f, 2f);
            double room = Math.Sqrt(budget * 1.3 / ((double)RecordWidth * RecordHeight));
            RecordSupersample = Mathf.Max(1f, Mathf.Min(RecordSupersample, (float)room));
            if ((long)RecordWidth * RecordHeight > budget)
                Mod.Log.Warning($"RecordQuality {q} is above what a {Device.Name} usually handles while playing - expect stutter or crashes");
            RecordFps = Mathf.Clamp(ini.Int("Camera", "RecordFps", 30), 10, 60);
            var br = (ini.Str("Camera", "RecordBitrate", "auto") ?? "auto").Trim();
            RecordBitrateKbps = int.TryParse(br, out int kb) ? Mathf.Clamp(kb, 1000, 60000) : autoKbps;
            RecordGameAudio = ini.Bool("Camera", "RecordGameAudio", false);
            RecordMic = ini.Bool("Camera", "RecordMic", false);
            GameVolume = Mathf.Clamp(ini.Float("Camera", "GameVolume", 1f), 0f, 4f);
            MicVolume = Mathf.Clamp(ini.Float("Camera", "MicVolume", 1f), 0f, 4f);
            AudioBitrateKbps = Mathf.Clamp(ini.Int("Camera", "AudioBitrate", 128), 64, 320);
        }

        public void ScanPresets()
        {
            ReShadeImport.Run();
            Presets.Clear();
            Presets.AddRange(Directory.GetFiles(PresetDir, "*.ini")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }

        public void LoadPreset(string name)
        {
            if (Presets.Count == 0) { Preset = Ini.Parse(""); PresetFile = null; PresetName = "(none)"; return; }
            int idx = Presets.FindIndex(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = 0;
            PresetIndex = idx;
            PresetName = Presets[idx];
            PresetFile = Path.Combine(PresetDir, PresetName + ".ini");
            try { Preset = Ini.Parse(File.ReadAllText(PresetFile)); }
            catch (Exception e) { Mod.Log.Warning($"Couldn't read preset {PresetFile}: {e.Message}"); Preset = Ini.Parse(""); }
            _presetTime = Mtime(PresetFile);
            WatchLut(LutPath());
        }

        public void StepPreset(int dir)
        {
            ScanPresets();
            if (Presets.Count == 0) return;
            int i = Presets.FindIndex(p => p.Equals(PresetName, StringComparison.OrdinalIgnoreCase));
            i = ((i < 0 ? 0 : i + dir) % Presets.Count + Presets.Count) % Presets.Count;
            LoadPreset(Presets[i]);
            SaveSelectedPreset();
        }

        public void SelectPreset(string name)
        {
            ScanPresets();
            LoadPreset(name);
            SaveSelectedPreset();
        }

        private void SaveSelectedPreset() => SetKey("General", "Preset", PresetName);


        public void SetPresetValue(string section, string key, string value)
        {
            if (PresetFile == null || Preset == null) return;
            Preset.Set(section, key, value);
            WriteKey(PresetFile, section, key, value);
            if (Preset.HasKey(section, "Enabled") && !Preset.Bool(section, "Enabled", true))
            {
                Preset.Set(section, "Enabled", "true");
                WriteKey(PresetFile, section, "Enabled", "true");
            }
            _presetTime = Mtime(PresetFile);
        }

        public string NewPreset(bool copyCurrent) => NewPresetFrom(copyCurrent ? PresetName : null);

        public string NewPresetFrom(string source)
        {
            string srcFile = source != null ? Path.Combine(PresetDir, source + ".ini") : null;
            bool copy = srcFile != null && File.Exists(srcFile);
            string stem = copy ? (CleanName(source + " edit") ?? "My preset") : "My preset";
            string name = null;
            for (int i = 1; i < 1000; i++)
            {
                var n = copy ? (i == 1 ? stem : $"{stem} {i}") : $"{stem} {i}";
                if (!File.Exists(Path.Combine(PresetDir, n + ".ini"))) { name = n; break; }
            }
            if (name == null) return null;
            string text = copy
                ? $"; {name} - made in game from {source}\n" + StripShared(File.ReadAllText(srcFile).TrimStart('\uFEFF'))
                : $"; {name} - made in game with QuestShade's preset editor\n[Color]\nExposure = 0\nContrast = 0\nSaturation = 0\n";
            File.WriteAllText(Path.Combine(PresetDir, name + ".ini"), text);
            SelectPreset(name);
            return name;
        }

        private static string StripShared(string text)
        {
            var sb = new System.Text.StringBuilder();
            bool skip = false;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var tr = line.Trim();
                if (tr.StartsWith("[") && tr.EndsWith("]")) skip = tr.Equals("[Shared]", StringComparison.OrdinalIgnoreCase);
                if (!skip) sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        public bool ResetBuiltIn()
        {
            if (PresetFile == null || !IsBuiltIn(PresetName)) return false;
            var asm = Assembly.GetExecutingAssembly();
            foreach (var r in asm.GetManifestResourceNames())
            {
                var n = r.Replace('\\', '/');
                if (!n.Equals("QuestShade.Defaults.Presets/" + PresetName + ".ini", StringComparison.OrdinalIgnoreCase) &&
                    !n.Equals("QuestShade.Defaults.Presets." + PresetName + ".ini", StringComparison.OrdinalIgnoreCase)) continue;
                using (var s = asm.GetManifestResourceStream(r))
                using (var f = File.Create(PresetFile)) s.CopyTo(f);
                LoadPreset(PresetName);
                return true;
            }
            return false;
        }

        public static bool IsBuiltIn(string name)
        {
            foreach (var r in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                var n = r.Replace('\\', '/');
                if (n.Equals("QuestShade.Defaults.Presets/" + name + ".ini", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("QuestShade.Defaults.Presets." + name + ".ini", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static string CleanName(string name)
        {
            if (name == null) return null;
            var bad = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.Trim())
                if (Array.IndexOf(bad, c) < 0 && c != '[' && c != ']' && c != ';' && !char.IsControl(c)) sb.Append(c);
            var n = sb.ToString().Trim().Trim('.');
            if (n.Length > 40) n = n.Substring(0, 40).Trim();
            return n.Length == 0 ? null : n;
        }

        public string RenamePreset(string newName, out string error)
        {
            error = null;
            var n = CleanName(newName);
            if (n == null) { error = "That name has no usable letters"; return null; }
            if (PresetFile == null) { error = "No preset to rename"; return null; }
            if (n.Equals(PresetName, StringComparison.Ordinal)) return n;
            var dest = Path.Combine(PresetDir, n + ".ini");
            bool sameFile = n.Equals(PresetName, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(dest) && !sameFile) { error = $"There's already a preset called \"{n}\""; return null; }
            try
            {
                if (IsBuiltIn(PresetName)) File.Copy(PresetFile, dest);
                else if (sameFile) { var tmp = dest + ".tmp"; File.Move(PresetFile, tmp); File.Move(tmp, dest); }
                else File.Move(PresetFile, dest);
            }
            catch (Exception e) { error = e.Message; return null; }
            SelectPreset(n);
            return n;
        }

        public string AddSharedPreset(string name, string author, string id, string ini)
        {
            var baseName = CleanName(name) ?? "Shared preset";
            var n = baseName;
            for (int i = 2; File.Exists(Path.Combine(PresetDir, n + ".ini")); i++) n = $"{baseName} ({i})";
            author = CleanName(author) ?? "Anonymous";
            id = CleanName(id) ?? "";
            var text = $"; {n} by {author} - from the QuestShade preset browser\n[Shared]\nId = {id}\nAuthor = {author}\n" + Sharing.CleanIni(ini);
            File.WriteAllText(Path.Combine(PresetDir, n + ".ini"), text);
            SelectPreset(n);
            return n;
        }

        public string CurrentPresetText()
        {
            try { return PresetFile != null && File.Exists(PresetFile) ? File.ReadAllText(PresetFile) : ""; } catch { return ""; }
        }

        public bool DeleteCurrentPreset()
        {
            if (PresetFile == null || IsBuiltIn(PresetName)) return false;
            try { File.Delete(PresetFile); } catch (Exception e) { Mod.Log.Warning($"Couldn't delete {PresetFile}: {e.Message}"); return false; }
            ScanPresets();
            LoadPreset(Presets.Count > 0 ? Presets[0] : "");
            SaveSelectedPreset();
            return true;
        }

        public void SetKey(string section, string key, string value)
        {
            if (WriteKey(MainIniPath, section, key, value)) _mainTime = Mtime(MainIniPath);
        }

        private static bool WriteKey(string path, string section, string key, string value)
        {
            try
            {
                var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
                bool inSec = false; int secEnd = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith("["))
                    {
                        if (inSec) { secEnd = i; break; }
                        inSec = t.Equals("[" + section + "]", StringComparison.OrdinalIgnoreCase);
                        if (inSec) secEnd = lines.Count;
                        continue;
                    }
                    if (!inSec) continue;
                    int eq = t.IndexOf('=');
                    if (eq > 0 && t.Substring(0, eq).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        int sc = lines[i].IndexOf(';');
                        string comment = sc >= 0 ? "   " + lines[i].Substring(sc) : "";
                        lines[i] = $"{key} = {value}{comment}";
                        Write(lines);
                        return true;
                    }
                }
                if (secEnd < 0) { lines.Add(""); lines.Add("[" + section + "]"); lines.Add($"{key} = {value}"); }
                else
                {
                    int at = secEnd;
                    while (at > 0 && lines[at - 1].Trim().Length == 0) at--;
                    lines.Insert(at, $"{key} = {value}");
                }
                Write(lines);
                return true;
            }
            catch (Exception e) { Mod.Log.Warning($"Couldn't save {section}.{key} in {Path.GetFileName(path)}: {e.Message}"); return false; }

            void Write(List<string> l) => File.WriteAllLines(path, l);
        }

        private static string F(float f) => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        public void SaveMenuSettings()
        {
            SetKey("General", "Enabled", Enabled ? "true" : "false");
            SetKey("General", "Preset", PresetName);
            SetKey("General", "BloomDofHeadset", BloomDofHeadset ? "true" : "false");
            SetKey("General", "BloomDofPreview", BloomDofPreview ? "true" : "false");
            SetKey("General", "BloomDofPhotosVideo", BloomDofCapture ? "true" : "false");
            SetKey("General", "MatchPreview", MatchPreview ? "true" : "false");
            SetKey("General", "BloomDofEveryPreset", BloomDofAll ? "true" : "false");
            SetKey("General", "BloomStrength", BloomStrength.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            SetKey("General", "BlurStrength", BlurStrength.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            SetKey("General", "HeadsetBloom", HeadsetBloom ? "true" : "false");
            SetKey("General", "EyeAdaptation", EyeAdapt ? "true" : "false");
            SetKey("General", "EyeAdaptationStrength", EyeAdaptStrength.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            SetKey("General", "EyeAdaptationSpeed", EyeAdaptSpeed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            SetKey("Camera", "GripAngle", GripAngle.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
            SetKey("Camera", "TimelineFollow", TimelineFollow ? "true" : "false");
            SetKey("Camera", "TimelineAutoRecord", TimelineAutoRecord ? "true" : "false");
            SetKey("Camera", "Mode", DefaultMode.ToString());
            SetKey("Camera", "FieldOfView", F(Fov));
            SetKey("Camera", "Smoothing", F(Smoothing));
            SetKey("Camera", "PreviewFps", PreviewFps.ToString());
            SetKey("Camera", "Preview", PreviewEnabled ? "true" : "false");
            SetKey("Camera", "PreviewShadows", PreviewShadows ? "true" : "false");
            SetKey("Camera", "PostProcessing", CameraPostProcessing ? "true" : "false");
            SetKey("Camera", "ThirdPersonDistance", F(ThirdPersonDistance));
            SetKey("Camera", "ThirdPersonHeight", F(ThirdPersonHeight));
            SetKey("Camera", "ThirdPersonSide", F(ThirdPersonSide));
            SetKey("Camera", "HoverPreview", HoverPreview ? "true" : "false");
            SetKey("Camera", "ControlsLabel", CameraLabel ? "true" : "false");
            SetKey("Camera", "RecordGameAudio", RecordGameAudio ? "true" : "false");
            SetKey("Camera", "RecordMic", RecordMic ? "true" : "false");
            SetKey("Camera", "PhotoTimer", PhotoTimer.ToString());
            SetKey("Camera", "FreezeOnRelease", FreezeOnRelease ? "true" : "false");
            SetKey("Camera", "NoCameraInFusion", FusionEffectsOnly ? "true" : "false");
            SetKey("Camera", "ChestFieldOfView", F(ChestFov));
            SetKey("Camera", "ChestForward", F(ChestForward));
            SetKey("Camera", "ChestHeight", F(ChestHeight));
            SetKey("Camera", "ChestSide", F(ChestSide));
            SetKey("Camera", "ChestPitch", F(ChestPitch));
        }
        public string LutPath()
        {
            var f = Preset?.Str("LUT", "File");
            if (string.IsNullOrEmpty(f) || !Preset.Enabled("LUT")) return null;
            return Path.IsPathRooted(f) ? f : Path.Combine(LutDir, f);
        }

        private void WatchLut(string path) { _lutWatched = path; _lutTime = path != null ? Mtime(path) : default; }

        private volatile bool _statBusy, _statReady;
        private DateTime _sMain, _sPreset, _sLut;
        private string _sPresetPath, _sLutPath;

        public bool PollChanges(float now)
        {
            if (!HotReload) return false;
            if (!_statReady)
            {
                if (!_statBusy && now >= _nextCheck)
                {
                    _nextCheck = now + ReloadInterval;
                    _statBusy = true;
                    string mainP = MainIniPath, presetP = PresetFile, lutP = _lutWatched;
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try { _sMain = Mtime(mainP); _sPreset = Mtime(presetP); _sLut = Mtime(lutP); _sPresetPath = presetP; _sLutPath = lutP; _statReady = true; }
                        finally { _statBusy = false; }
                    });
                }
                return false;
            }
            _statReady = false;
            bool changed = false;
            if (_sMain > _mainTime)
            {
                Mod.Log.Msg("QuestShade.ini changed, reloading");
                var before = PresetName;
                LoadMain();
                ScanPresets();
                LoadPreset(PresetName);
                changed = true;
            }
            else if (PresetFile != null && _sPresetPath == PresetFile && _sPreset > _presetTime)
            {
                Mod.Log.Msg($"Preset '{PresetName}' changed, reloading");
                LoadPreset(PresetName);
                changed = true;
            }
            else if (_lutWatched != null && _sLutPath == _lutWatched && _sLut > _lutTime)
            {
                WatchLut(_lutWatched);
                changed = true;
            }
            return changed;
        }

        private static DateTime Mtime(string p)
        {
            try { return p != null && File.Exists(p) ? File.GetLastWriteTimeUtc(p) : default; }
            catch { return default; }
        }
    }
}
