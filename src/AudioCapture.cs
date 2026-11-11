#if QUEST
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace QuestShade
{
    public sealed class QSAudioTap : MonoBehaviour
    {
        public QSAudioTap(IntPtr p) : base(p) { }
        internal static AudioCapture Sink;

        public void OnAudioFilterRead(Il2CppStructArray<float> data, int channels)
        {
            try { Sink?.OnGameAudio(data, channels); } catch { }
        }
    }

    internal sealed class AudioCapture
    {
        public int Rate { get; private set; }
        public bool GameOn { get; private set; }
        public bool MicOn { get; private set; }
        public string Status = "";
        public static string Notice;

        private float _gameVol = 1f, _micVol = 1f;

        private float[] _ring;
        private long _wr, _rd;
        private long _gameFrames;
        private float[] _scratch = new float[8192];
        private QSAudioTap _tap;
        private static bool _registered;

        private AudioClip _micClip;
        private string _micDev;
        private int _micRate, _micLastPos, _micLen;
        private float[] _micRing; private int _micW, _micR, _micCount;
        private double _micFrac;
        private int _micCh = 1;
        private Il2CppStructArray<float> _micAll;
        private float[] _micAllManaged;
        private bool _micTraced, _tapTraced;

        private long _framesOut;
        private float _t0;
        private bool _guardCleared;

        private static string GuardFile => Path.Combine(Config.Root, "audio_guard.txt");

        public bool Start(Config cfg)
        {
            bool game = cfg.RecordGameAudio, mic = cfg.RecordMic;
            _gameVol = cfg.GameVolume; _micVol = cfg.MicVolume;

            try
            {
                if (File.Exists(GuardFile))
                {
                    string g = File.ReadAllText(GuardFile);
                    File.Delete(GuardFile);
                    string off = "";
                    if (game && g.Contains("game")) { game = false; cfg.RecordGameAudio = false; off = "Game audio"; }
                    if (mic && g.Contains("mic")) { mic = false; cfg.RecordMic = false; off = off.Length > 0 ? "Game audio + mic" : "Microphone"; }
                    if (off.Length > 0)
                    {
                        Notice = $"{off} crashed the game last time - turned off. Turn it back on in the menu to try again.";
                        VideoRecorder.Diag("audio guard: " + off + " switched off after last crash");
                    }
                }
            }
            catch { }

            Rate = AudioSettings.outputSampleRate;
            if (Rate <= 0) Rate = 48000;
            _ring = new float[Rate * 2 * 4]; _wr = _rd = 0; _gameFrames = 0;
            _framesOut = 0; _t0 = Time.realtimeSinceStartup; _guardCleared = false;

            WriteGuard((game ? "game " : "") + (mic ? "mic" : ""));
            if (game) GameOn = AttachTap();
            if (mic) MicOn = StartMic();
            if (!GameOn && !MicOn) ClearGuard();
            Status = $"game audio {(GameOn ? "on" : "off")}, mic {(MicOn ? $"on ({_micDev ?? "default"}, {_micRate} Hz)" : "off")}";
            return GameOn || MicOn;
        }

        private static void WriteGuard(string what)
        {
            if (what.Trim().Length == 0) return;
            try { File.WriteAllText(GuardFile, what); } catch { }
        }

        private static void ClearGuard() { try { if (File.Exists(GuardFile)) File.Delete(GuardFile); } catch { } }

        private bool AttachTap()
        {
            try
            {
                var listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
                if (listener == null) { Mod.Log.Warning("Recording: no AudioListener in this scene - no game audio"); return false; }
                if (!_registered) { ClassInjector.RegisterTypeInIl2Cpp<QSAudioTap>(); _registered = true; }
                VideoRecorder.Diag("audio: attaching tap to " + listener.gameObject.name);
                QSAudioTap.Sink = this;
                _tap = listener.gameObject.GetComponent<QSAudioTap>() ?? listener.gameObject.AddComponent<QSAudioTap>();
                VideoRecorder.Diag("audio: tap attached");
                return _tap != null;
            }
            catch (Exception e) { Mod.Log.Warning("Recording: game audio unavailable: " + e.Message); QSAudioTap.Sink = null; return false; }
        }

        internal void OnGameAudio(Il2CppStructArray<float> data, int channels)
        {
            if (!GameOn || data == null || channels <= 0) return;
            int n = (int)data.Length;
            if (n <= 0) return;
            var buf = _scratch;
            if (buf.Length < n) { buf = new float[n]; _scratch = buf; }
            Marshal.Copy(IntPtr.Add(data.Pointer, IntPtr.Size * 4), buf, 0, n);
            int frames = n / channels;
            var ring = _ring;
            int len = ring.Length;
            long wr = Volatile.Read(ref _wr);
            for (int f = 0; f < frames; f++)
            {
                float l = buf[f * channels];
                float r = channels > 1 ? buf[f * channels + 1] : l;
                ring[(int)(wr % len)] = l; ring[(int)((wr + 1) % len)] = r;
                wr += 2;
            }
            Volatile.Write(ref _wr, wr);
            Interlocked.Add(ref _gameFrames, frames);
        }

        private bool StartMic()
        {
            try
            {
                VideoRecorder.Diag("audio: starting mic");
                var devs = Microphone.devices;
                if (devs == null || devs.Length == 0) { AskMicPermission(); Mod.Log.Msg("Recording: no microphone (asked Android for mic permission)"); return false; }
                _micDev = null;
                if (Microphone.IsRecording(_micDev))
                {
                    var live = FindLiveMicClip();
                    if (live == null) { Mod.Log.Msg("Recording: the microphone is busy and couldn't be shared - recording game audio only"); return false; }
                    _micClip = live;
                    Mod.Log.Msg(live == _keptClip ? "Recording: microphone reused" : "Recording: sharing the microphone with voice chat");
                }
                else
                {
                    Microphone.GetDeviceCaps(_micDev, out int minF, out int maxF);
                    _micRate = (minF == 0 && maxF == 0) ? 48000 : Mathf.Clamp(48000, minF, maxF);
                    _micClip = Microphone.Start(_micDev, true, 1, _micRate);
                    if (_micClip == null) return false;
                    _keptClip = _micClip;
                }
                _micRate = _micClip.frequency;
                _micLen = _micClip.samples;
                _micCh = Math.Max(1, _micClip.channels);
                _micAll = new Il2CppStructArray<float>((long)_micLen * _micCh);
                _micAllManaged = new float[_micLen * _micCh];
                _micLastPos = Math.Max(0, Microphone.GetPosition(_micDev));
                _micRing = new float[_micRate * 2]; _micW = _micR = _micCount = 0; _micFrac = 0;
                VideoRecorder.Diag($"audio: mic started ({_micRate} Hz, {_micCh} ch, {_micLen} samples)");
                return true;
            }
            catch (Exception e) { Mod.Log.Warning("Recording: microphone failed: " + e.Message); return false; }
        }

        private static AudioClip _keptClip;

        private static AudioClip FindLiveMicClip()
        {
            AudioClip other = null;
            bool keptAlive = false;
            foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (clip == null || clip.samples <= 0 || clip.name != "Microphone") continue;
                if (_keptClip != null && clip.Pointer == _keptClip.Pointer) keptAlive = true;
                else other = clip;
            }
            if (other != null) return other;
            return keptAlive ? _keptClip : null;
        }

        public static void AskMicPermission()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            try
            {
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
                    UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
            }
            catch (Exception e) { Mod.Log.Warning("Mic permission: " + e.Message); }
        }

        private bool MicStillOurs()
        {
            if (_micClip == null) return false;
            if (!Microphone.IsRecording(_micDev)) return false;
            return true;
        }

        private void LetGoOfMic(string why)
        {
            if (!MicOn) return;
            MicOn = false; _micClip = null; _micEnded = true;
            Mod.Log.Msg("Recording: microphone handed over (" + why + ") - recording game audio only from here");
            VideoRecorder.Diag("audio: mic released without ending it: " + why);
        }
        private bool _micEnded;

        private void PumpMic()
        {
            if (!MicOn) return;
            if (!MicStillOurs()) { LetGoOfMic("another mod took the mic"); return; }
            int pos = Microphone.GetPosition(_micDev);
            if (pos < 0 || pos >= _micLen) return;
            int n = pos - _micLastPos;
            if (n < 0) n += _micLen;
            if (n <= 0) return;
            if (!_micTraced) VideoRecorder.Diag("audio: first mic read...");
            if (!_micClip.GetData(_micAll, 0)) return;
            Marshal.Copy(IntPtr.Add(_micAll.Pointer, IntPtr.Size * 4), _micAllManaged, 0, _micAllManaged.Length);
            for (int k = 0; k < n; k++)
            {
                int s = (_micLastPos + k) % _micLen;
                if (_micCount == _micRing.Length) { _micR = (_micR + 1) % _micRing.Length; _micCount--; }
                _micRing[_micW] = _micAllManaged[s * _micCh]; _micW = (_micW + 1) % _micRing.Length; _micCount++;
            }
            _micLastPos = pos;
            if (!_micTraced) { _micTraced = true; VideoRecorder.Diag("audio: mic ok"); }
        }

        public void Mix(ref short[] pcm, ref int len)
        {
            PumpMic();
            long gf = Interlocked.Read(ref _gameFrames);
            if (!_tapTraced && gf > 0) { _tapTraced = true; VideoRecorder.Diag($"audio: game sound flowing ({gf} frames)"); }
            if (!_guardCleared && Time.realtimeSinceStartup - _t0 > 4f) { _guardCleared = true; ClearGuard(); VideoRecorder.Diag("audio: stable, guard cleared"); }

            long wr = Volatile.Read(ref _wr);
            long avail = (wr - _rd) / 2;
            if (avail > _ring.Length / 2) { _rd = wr - _ring.Length / 2 + Rate / 10 * 2; avail = (wr - _rd) / 2; }
            long frames;
            bool gameClock = GameOn && gf > 0;
            if (gameClock) frames = avail;
            else
            {
                long due = (long)((Time.realtimeSinceStartup - _t0) * Rate);
                frames = Math.Max(0, due - _framesOut);
                avail = 0;
            }
            if (frames <= 0) return;
            if (len + frames * 2 > pcm.Length) Array.Resize(ref pcm, (int)Math.Max(pcm.Length * 2, len + frames * 2));

            double step = MicOn ? (double)_micRate / Rate : 0;
            if (MicOn)
            {
                int target = _micRate * 12 / 100;
                if (!_micPrimed && _micCount >= target) _micPrimed = true;
                int extra = _micCount - _micRate * 30 / 100;
                if (_micPrimed && extra > 0) { int drop = _micCount - target; _micR = (_micR + drop) % _micRing.Length; _micCount -= drop; }
            }

            int rlen = _ring.Length;
            for (long f = 0; f < frames; f++)
            {
                float l = 0, r = 0;
                if (f < avail)
                {
                    long i = _rd + f * 2;
                    l = _ring[(int)(i % rlen)] * _gameVol; r = _ring[(int)((i + 1) % rlen)] * _gameVol;
                }
                if (MicOn && _micPrimed)
                {
                    if (_micCount > 1)
                    {
                        float a = _micRing[_micR], b = _micRing[(_micR + 1) % _micRing.Length];
                        float x = a + (b - a) * (float)_micFrac;
                        float hp = 0.988f * (_hpY + x - _hpX); _hpX = x; _hpY = hp;
                        float m = hp * _micVol;
                        l += m; r += m;
                        _micFrac += step;
                        while (_micFrac >= 1.0 && _micCount > 1) { _micFrac -= 1.0; _micR = (_micR + 1) % _micRing.Length; _micCount--; }
                    }
                    else _micPrimed = false;
                }
                pcm[len++] = (short)(Soft(l) * 32767f);
                pcm[len++] = (short)(Soft(r) * 32767f);
            }
            _rd += Math.Min(frames, avail) * 2;
            _framesOut += frames;
        }

        private bool _micPrimed; private float _hpX, _hpY;

        private static float Soft(float x)
        {
            float a = Math.Abs(x);
            if (a <= 0.75f) return x;
            float y = 0.75f + 0.25f * (float)Math.Tanh((a - 0.75f) / 0.25f);
            return x < 0 ? -y : y;
        }

        public void Stop()
        {
            bool wasOn = GameOn || MicOn;
            GameOn = false;
            QSAudioTap.Sink = null;
            _tap = null;
            try { if (MicOn && !_micEnded && MicStillOurs()) Microphone.End(_micDev); } catch { }
            _micClip = null;
            MicOn = false;
            if (wasOn) ClearGuard();
        }
    }
}
#endif
