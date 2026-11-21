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
        private bool _micOwned;
        // Direct headset mic through raw JNI (the same path the video encoder uses). Unity's AndroidJavaObject
        // generics (Call<int>, CallStatic<int>, ConvertFromJNIArray<T>) aren't all compiled into BONELAB and crash the game.
        private IntPtr _arRecord, _arArray, _arClass;
        private IntPtr _arReadMid, _arStopMid, _arReleaseMid;
        private short[] _arBuf;
        private bool _micNative;
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
        private static string NoNativeFile => Path.Combine(Config.Root, "no_headset_mic.txt");

        public bool Start(Config cfg)
        {
            bool game = cfg.RecordGameAudio, mic = cfg.RecordMic;
            _gameVol = cfg.GameVolume * 0.5f; _micVol = cfg.MicVolume;

            try
            {
                if (File.Exists(GuardFile))
                {
                    string g = File.ReadAllText(GuardFile);
                    File.Delete(GuardFile);
                    string off = "";
                    if (game && g.Contains("game") && !g.Contains("nativemic")) { game = false; cfg.RecordGameAudio = false; off = "Game audio"; }
                    if (mic && g.Contains("nativemic"))
                    {
                        // The direct headset mic took the game down: keep the mic on, but use the game's own mic from now on.
                        try { File.WriteAllText(NoNativeFile, "headset mic crashed the game " + DateTime.Now.ToString("u")); } catch { }
                        Notice = "The direct headset mic crashed the game last time, so recordings use the game's mic from now on.";
                        VideoRecorder.Diag("audio guard: direct headset mic disabled after last crash, using Unity mic");
                    }
                    else if (mic && g.Contains("mic")) { mic = false; cfg.RecordMic = false; off = off.Length > 0 ? "Game audio + mic" : "Microphone"; }
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
            if (MicOn && _micNative) WriteGuard((GameOn ? "game " : "") + "mic nativemic");
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

        private bool StartAudioRecord()
        {
            if (Application.platform != RuntimePlatform.Android) return false;
            if (!Jni.Init()) return false;
            bool attached = false;
            try
            {
                var e = Jni.Attach(out attached);
                e.PushFrame(16);
                try
                {
                    VideoRecorder.Diag("audio: AudioRecord: class");
                    var cls = e.Class("android/media/AudioRecord");
                    // MIC source (1), 48 kHz, mono (CHANNEL_IN_MONO = 16), 16-bit PCM (ENCODING_PCM_16BIT = 2)
                    int minBuf = e.CallStaticInt(cls, e.StaticMethod(cls, "getMinBufferSize", "(III)I"), "getMinBufferSize", 48000, 16, 2);
                    if (minBuf <= 0) { VideoRecorder.Diag("audio: AudioRecord: no buffer size " + minBuf); return false; }
                    int bufBytes = Math.Max(minBuf, 8192) * 2;
                    VideoRecorder.Diag("audio: AudioRecord: create");
                    var rec = e.New(cls, e.Method(cls, "<init>", "(IIIII)V"), 1, 48000, 16, 2, bufBytes);
                    var release = e.Method(cls, "release", "()V");
                    if (e.CallInt(rec, e.Method(cls, "getState", "()I"), "getState") != 1) { e.CallVoid(rec, release, "release"); VideoRecorder.Diag("audio: AudioRecord: not initialized"); return false; }
                    VideoRecorder.Diag("audio: AudioRecord: start");
                    e.CallVoid(rec, e.Method(cls, "startRecording", "()V"), "startRecording");
                    var stop = e.Method(cls, "stop", "()V");
                    if (e.CallInt(rec, e.Method(cls, "getRecordingState", "()I"), "getRecordingState") != 3)
                    { e.CallVoid(rec, stop, "stop"); e.CallVoid(rec, release, "release"); VideoRecorder.Diag("audio: AudioRecord: didn't start (mic busy?)"); return false; }
                    // read(short[], int, int, int) with READ_NON_BLOCKING: never stall the game thread waiting for mic data
                    _arReadMid = e.Method(cls, "read", "([SIII)I");
                    _arStopMid = stop; _arReleaseMid = release;
                    _arClass = e.Global(cls);
                    _arRecord = e.Global(rec);
                    _arArray = e.Global(e.NewShorts(4096));
                }
                finally { e.PopFrame(); }
                _arBuf = new short[4096];
                _micRate = 48000;
                _micCh = 1;
                _micLen = 48000;
                _micRing = new float[_micRate * 2]; _micW = _micR = _micCount = 0; _micFrac = 0;
                VideoRecorder.Diag("audio: mic via AudioRecord (48 kHz, raw MIC)");
                return true;
            }
            catch (Exception ex) { Mod.Log.Warning("Recording: direct headset mic failed, using the game's mic: " + ex.Message); StopAudioRecord(); return false; }
            finally { if (attached) Jni.Detach(); }
        }

        private void StopAudioRecord()
        {
            if (_arRecord == IntPtr.Zero && _arArray == IntPtr.Zero && _arClass == IntPtr.Zero) return;
            bool attached = false;
            try
            {
                var e = Jni.Attach(out attached);
                if (_arRecord != IntPtr.Zero)
                {
                    try { if (_arStopMid != IntPtr.Zero) e.CallVoid(_arRecord, _arStopMid, "stop"); } catch { }
                    try { if (_arReleaseMid != IntPtr.Zero) e.CallVoid(_arRecord, _arReleaseMid, "release"); } catch { }
                }
                e.DeleteGlobal(_arRecord); e.DeleteGlobal(_arArray); e.DeleteGlobal(_arClass);
            }
            catch { }
            finally { if (attached) Jni.Detach(); }
            _arRecord = _arArray = _arClass = IntPtr.Zero;
            _arReadMid = _arStopMid = _arReleaseMid = IntPtr.Zero;
        }

        private void PumpNativeMic()
        {
            if (_arRecord == IntPtr.Zero || _arArray == IntPtr.Zero || _arBuf == null || _arReadMid == IntPtr.Zero) return;
            int n;
            bool attached = false;
            try
            {
                var e = Jni.Attach(out attached);
                n = e.CallInt(_arRecord, _arReadMid, "read", Jni.O(_arArray), 0, _arBuf.Length, 1); // 1 = READ_NON_BLOCKING
                if (n > 0) e.GetShorts(_arArray, 0, Math.Min(n, _arBuf.Length), _arBuf);
            }
            catch { LetGoOfMic("AudioRecord read failed"); return; }
            finally { if (attached) Jni.Detach(); }
            if (n < 0) { LetGoOfMic("AudioRecord error " + n); return; }
            if (n <= 0) return;
            for (int k = 0; k < n; k++)
            {
                if (_micCount == _micRing.Length) { _micR = (_micR + 1) % _micRing.Length; _micCount--; }
                _micRing[_micW] = _arBuf[k] / 32768f; _micW = (_micW + 1) % _micRing.Length; _micCount++;
            }
            if (!_micTraced) { _micTraced = true; VideoRecorder.Diag("audio: mic ok"); }
        }

        private bool StartMic()
        {
            try
            {
                VideoRecorder.Diag("audio: starting mic");
                if (!HasMicPermission())
                {
                    AskMicPermission();
                    Notice = "Allow the microphone on your Quest, then record again to get your voice. This video has game audio only.";
                    Mod.Log.Msg("Recording: no microphone permission yet (asked Android)");
                    return false;
                }
                bool tryNative = !File.Exists(NoNativeFile);
                // Mark the attempt first: if starting the headset mic takes the game down, next time uses the game's mic.
                if (tryNative) WriteGuard((GameOn ? "game " : "") + "mic nativemic");
                bool native = tryNative && StartAudioRecord();
                if (tryNative && !native) WriteGuard((GameOn ? "game " : "") + "mic");
                if (native)
                {
                    _micNative = true;
                    Mod.Log.Msg("Recording: mic via direct AudioRecord (48 kHz)");
                    return true;
                }
                _micNative = false;
                var devs = Microphone.devices;
                if (devs == null || devs.Length == 0) { AskMicPermission(); Mod.Log.Msg("Recording: no microphone (asked Android for mic permission)"); return false; }
                _micDev = null;
                _micOwned = false;
                if (Microphone.IsRecording(_micDev))
                {
                    var live = FindLiveMicClip();
                    if (live != null)
                    {
                        _micClip = live;
                        Mod.Log.Msg("Recording: sharing the microphone with voice chat");
                    }
                    else if (_keptClip != null)
                    {
                        try { Microphone.End(_micDev); } catch { }
                        _keptClip = null;
                    }
                    else
                    {
                        Mod.Log.Msg("Recording: the microphone is busy and couldn't be shared - recording game audio only");
                        return false;
                    }
                }
                if (_micClip == null)
                {
                    Microphone.GetDeviceCaps(_micDev, out int minF, out int maxF);
                    _micRate = (minF == 0 && maxF == 0) ? 48000 : Mathf.Clamp(48000, minF, maxF);
                    _micClip = Microphone.Start(_micDev, true, 1, _micRate);
                    if (_micClip == null) return false;
                    _keptClip = _micClip;
                    _micOwned = true;
                }
                _micRate = _micClip.frequency;
                _micLen = _micClip.samples;
                _micCh = Math.Max(1, _micClip.channels);
                _micAll = new Il2CppStructArray<float>((long)_micLen * _micCh);
                _micAllManaged = new float[_micLen * _micCh];
                _micLastPos = _micOwned ? 0 : Math.Max(0, Microphone.GetPosition(_micDev));
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
            foreach (var clip in Resources.FindObjectsOfTypeAll<AudioClip>())
            {
                if (clip == null || clip.samples <= 0 || clip.name != "Microphone") continue;
                if (_keptClip != null && clip.Pointer == _keptClip.Pointer) continue;
                other = clip;
            }
            return other;
        }

        private static bool HasMicPermission()
        {
            if (Application.platform != RuntimePlatform.Android) return true;
            try { return UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone); }
            catch { return true; }
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
            MicOn = false; _micClip = null;
            Mod.Log.Msg("Recording: microphone handed over (" + why + ") - recording game audio only from here");
            VideoRecorder.Diag("audio: mic released without ending it: " + why);
        }

        private void PumpMic()
        {
            if (!MicOn) return;
            if (_micNative) { PumpNativeMic(); return; }
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
                        _lp += 0.28f * (hp - _lp);
                        hp += 0.9f * (hp - _lp);
                        // Fixed boost, no auto-gain: the old "stabilizer" pumped the volume up and down between words.
                        float m = hp * MicBoost * _micVol;
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

        private bool _micPrimed; private float _hpX, _hpY, _lp;
        private const float MicBoost = 10f; // the Quest mic is very quiet raw; Soft() keeps loud peaks from clipping

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
            try { if (_micOwned) Microphone.End(_micDev); } catch { }
            StopAudioRecord();
            _micNative = false;
            _micOwned = false;
            _keptClip = null;
            _micClip = null;
            MicOn = false;
            if (wasOn) ClearGuard();
        }
    }
}
