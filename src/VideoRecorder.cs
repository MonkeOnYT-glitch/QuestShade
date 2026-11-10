#if QUEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    internal sealed class VideoRecorder
    {
        public bool Recording { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public string FilePath { get; private set; }
        public float Seconds => Recording ? Time.realtimeSinceStartup - _t0 : 0f;

        private IntPtr _codec, _audio, _muxer;
        private FileStream _file;
        private long _vTrack = -1, _aTrack = -1;
        private bool _muxStarted, _vEos, _aEos, _aEosQueued;
        private int _step;
        private void Step(int bit, string msg) { if ((_step & bit) != 0) return; _step |= bit; Mod.Log.Msg("Recording step: " + msg); Diag(msg); }
        private float _nextBeat;

        internal static void Diag(string msg, bool fresh = false)
        {
            try
            {
                var f = Path.Combine(Config.Root, "recording_debug.txt");
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
                if (fresh) File.WriteAllText(f, line); else File.AppendAllText(f, line);
            }
            catch { }
        }
        private float _t0;
        private int _frames, _dropped;

        private static string Marker => Path.Combine(Config.Root, "recording_in_progress.txt");
        private static void SetMarker(string file) { try { File.WriteAllText(Marker, file); } catch { } }
        private static void ClearMarker() { try { if (File.Exists(Marker)) File.Delete(Marker); } catch { } }

        public static string RecoverInterrupted()
        {
            try
            {
                if (!File.Exists(Marker)) return null;
                string file = File.ReadAllText(Marker).Trim();
                ClearMarker();
                if (file.Length == 0 || !File.Exists(file) || HasIndex(file)) return null;
                long size = new FileInfo(file).Length;
                if (size < 256 * 1024) { File.Delete(file); Mod.Log.Msg("Removed an unfinished recording (the game closed mid-take): " + file); return null; }
                string dir = Path.Combine(Config.Root, "Unfinished recordings");
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, Path.GetFileName(file));
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(file, dst);
                Mod.Log.Warning($"The last recording was cut off when the game closed ({size / 1048576f:0.0} MB). It can't play as-is; moved to {dst}");
                return "Last recording was cut off when the game closed - moved out of your gallery (see the QuestShade folder)";
            }
            catch (Exception e) { Mod.Log.Warning("Unfinished recording check failed: " + e.Message); return null; }
        }

        private static bool HasIndex(string file)
        {
            using var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var h = new byte[16];
            long pos = 0, len = f.Length;
            while (pos + 8 <= len)
            {
                f.Position = pos;
                if (f.Read(h, 0, 16) < 8) break;
                long size = (uint)(h[0] << 24 | h[1] << 16 | h[2] << 8 | h[3]);
                string type = System.Text.Encoding.ASCII.GetString(h, 4, 4);
                if (type == "moov") return true;
                if (size == 1) size = (long)((ulong)h[8] << 56 | (ulong)h[9] << 48 | (ulong)h[10] << 40 | (ulong)h[11] << 32 | (ulong)h[12] << 24 | (ulong)h[13] << 16 | (ulong)h[14] << 8 | h[15]);
                else if (size == 0) break;
                if (size < 8) break;
                pos += size;
            }
            return false;
        }

        private RenderTexture _yuv;
        private Material _yuvMat;
        private readonly Queue<(AsyncGPUReadbackRequest req, long pts)> _pending = new Queue<(AsyncGPUReadbackRequest, long)>();

        private AudioCapture _cap;
        private short[] _pcm = new short[48000];
        private int _pcmLen;
        private long _aFrames;
        private long _lastPts = -1; private int _fps = 30;
        private readonly List<(bool video, byte[] data, BufferInfo info)> _early = new List<(bool, byte[], BufferInfo)>();

        private const int COLOR_FormatYUV420SemiPlanar = 21;
        private const uint FLAG_CODEC_CONFIG = 2, FLAG_END_OF_STREAM = 4;
        private const long INFO_TRY_AGAIN_LATER = -1, INFO_OUTPUT_FORMAT_CHANGED = -2;

        public bool Start(int w, int h, int fps, int bitrate, string folder, Config cfg)
        {
            if (Recording) return true;
            if (ShaderPack.Yuv == null) { Mod.Log.Error("Recording: YUV shader unavailable on this GPU"); return false; }
            Width = w & ~15; Height = h & ~1;
            try
            {
                Directory.CreateDirectory(folder);
                FilePath = Path.Combine(folder, MediaScan.QuestName("mp4", DateTime.Now));

                Diag($"start {Width}x{Height}@{fps} {bitrate / 1000} kbps on {Device.Name}", true);
                Mod.Log.Msg("Recording: creating encoder");
                _fps = fps; _lastPts = -1;
                _codec = CreateVideoEncoder(fps, bitrate, false);
                if (_codec == IntPtr.Zero) throw new Exception("no video encoder (HEVC / H.264)");

                int stride = 0, slice = 0;
                var inFmt = Ndk.AMediaCodec_getInputFormat(_codec);
                if (inFmt != IntPtr.Zero)
                {
                    Ndk.AMediaFormat_getInt32(inFmt, "stride", out stride);
                    Ndk.AMediaFormat_getInt32(inFmt, "slice-height", out slice);
                    Ndk.AMediaFormat_delete(inFmt);
                }
                if (stride < Width) stride = (Width + 127) & ~127;
                if (slice < Height) slice = (Height + 31) & ~31;

                if (cfg.RecordGameAudio || cfg.RecordMic)
                {
                    _cap = new AudioCapture();
                    if (_cap.Start(cfg))
                    {
                        _audio = CreateAudioEncoder(_cap.Rate, cfg.AudioBitrateKbps * 1000);
                        if (_audio == IntPtr.Zero) { Mod.Log.Warning("Recording: no AAC encoder, video only"); _cap.Stop(); _cap = null; }
                        else Mod.Log.Msg($"Recording audio: {_cap.Status}, {_cap.Rate} Hz stereo AAC {cfg.AudioBitrateKbps} kbps");
                    }
                    else { _cap.Stop(); _cap = null; Mod.Log.Msg("Recording: no audio source available, video only"); }
                }

                Mod.Log.Msg("Recording: creating muxer");
                _file = new FileStream(FilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                int fd = (int)_file.SafeFileHandle.DangerousGetHandle();
                _muxer = Ndk.AMediaMuxer_new(fd, 0 );
                if (_muxer == IntPtr.Zero) throw new Exception("muxer create failed");

                _yuv = new RenderTexture(stride, slice * 3 / 2, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
                { name = "QuestShade_NV12", useMipMap = false, filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                _yuv.Create();
                _yuvMat = new Material(ShaderPack.Yuv) { hideFlags = HideFlags.HideAndDontSave };
                _yuvMat.SetVector("_QS_YUVSize", new Vector4(Width, Height, slice * 1.5f, 1));
                _yuvMat.SetVector("_QS_YUVLayout", new Vector4(slice, stride, 0, 0));

                _vTrack = _aTrack = -1; _muxStarted = _vEos = _aEos = _aEosQueued = false; _step = 0; _frames = 0; _dropped = 0;
                _pcmLen = 0; _aFrames = 0; _early.Clear();
                _t0 = Time.realtimeSinceStartup; _nextBeat = 1f;
                Recording = true;
                SetMarker(FilePath);
                Diag($"recording: audio={(_audio != IntPtr.Zero ? _cap.Status : "none")}");
                Mod.Log.Msg($"Recording {Width}x{Height}@{fps} {bitrate / 1000} kbps (NV12 {stride}x{slice}){(_audio != IntPtr.Zero ? " + audio" : "")} -> {FilePath}");
                return true;
            }
            catch (Exception e)
            {
                Mod.Log.Error("Recording failed to start: " + e.Message);
                Release();
                try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
                return false;
            }
        }

        public string Codec { get; private set; } = "";

        private IntPtr CreateVideoEncoder(int fps, int bitrate, bool high)
        {
            foreach (var (mime, name, br) in new[] { ("video/hevc", "HEVC", bitrate), ("video/avc", "H.264", (int)(bitrate * 1.6f)) })
            {
                IntPtr fmt = Ndk.AMediaFormat_new(), codec = IntPtr.Zero;
                try
                {
                    Ndk.AMediaFormat_setString(fmt, "mime", mime);
                    Ndk.AMediaFormat_setInt32(fmt, "width", Width);
                    Ndk.AMediaFormat_setInt32(fmt, "height", Height);
                    Ndk.AMediaFormat_setInt32(fmt, "color-format", COLOR_FormatYUV420SemiPlanar);
                    Ndk.AMediaFormat_setInt32(fmt, "bitrate", br);
                    Ndk.AMediaFormat_setInt32(fmt, "frame-rate", fps);
                    Ndk.AMediaFormat_setInt32(fmt, "i-frame-interval", 2);
                    Ndk.AMediaFormat_setInt32(fmt, "color-standard", 2);
                    Ndk.AMediaFormat_setInt32(fmt, "color-range", 2);
                    Ndk.AMediaFormat_setInt32(fmt, "color-transfer", 3);
                    if (mime == "video/hevc") Ndk.AMediaFormat_setInt32(fmt, "profile", 1);
                    codec = Ndk.AMediaCodec_createEncoderByType(mime);
                    if (codec == IntPtr.Zero) continue;
                    if (Ndk.AMediaCodec_configure(codec, fmt, IntPtr.Zero, IntPtr.Zero, 1) != 0 || Ndk.AMediaCodec_start(codec) != 0)
                    {
                        Ndk.AMediaCodec_delete(codec);
                        Mod.Log.Msg($"Recording: {name} encoder unavailable, trying the next");
                        continue;
                    }
                    Codec = name;
                    Mod.Log.Msg($"Recording: {name} {Width}x{Height} @ {br / 1000} kbps, key frame every 2 s");
                    return codec;
                }
                finally { Ndk.AMediaFormat_delete(fmt); }
            }
            return IntPtr.Zero;
        }
        private static IntPtr CreateAudioEncoder(int rate, int bitrate)
        {
            IntPtr fmt = Ndk.AMediaFormat_new();
            try
            {
                Ndk.AMediaFormat_setString(fmt, "mime", "audio/mp4a-latm");
                Ndk.AMediaFormat_setInt32(fmt, "sample-rate", rate);
                Ndk.AMediaFormat_setInt32(fmt, "channel-count", 2);
                Ndk.AMediaFormat_setInt32(fmt, "bitrate", bitrate);
                Ndk.AMediaFormat_setInt32(fmt, "aac-profile", 2);
                Ndk.AMediaFormat_setInt32(fmt, "max-input-size", 16384);
                var codec = Ndk.AMediaCodec_createEncoderByType("audio/mp4a-latm");
                if (codec == IntPtr.Zero) return IntPtr.Zero;
                if (Ndk.AMediaCodec_configure(codec, fmt, IntPtr.Zero, IntPtr.Zero, 1) != 0 || Ndk.AMediaCodec_start(codec) != 0)
                {
                    Ndk.AMediaCodec_delete(codec);
                    return IntPtr.Zero;
                }
                return codec;
            }
            finally { Ndk.AMediaFormat_delete(fmt); }
        }

        public void SubmitFrame(RenderTexture src)
        {
            if (!Recording || src == null) return;
            if (_pending.Count >= 4) { _dropped++; return; }
            Step(1, "first frame -> NV12");
            Graphics.Blit(src, _yuv, _yuvMat);
            var req = AsyncGPUReadback.Request(_yuv, 0, (Il2CppSystem.Action<AsyncGPUReadbackRequest>)null);
            long frameUs = 1_000_000L / Math.Max(1, _fps);
            long pts = (long)Math.Round((Time.realtimeSinceStartup - _t0) * 1_000_000.0 / frameUs) * frameUs;
            if (pts <= _lastPts) pts = _lastPts + frameUs;
            _lastPts = pts;
            _pending.Enqueue((req, pts));
        }

        public void Tick()
        {
            if (!Recording) return;
            float tNow = Time.realtimeSinceStartup - _t0;
            if (tNow >= _nextBeat && _nextBeat <= 10f) { _nextBeat += 1f; Diag($"t={tNow:0.0}s frames={_frames} dropped={_dropped} pending={_pending.Count} audio={_aFrames} muxer={(_muxStarted ? "on" : "waiting")}"); }
            try
            {
                while (_pending.Count > 0)
                {
                    var (req, pts) = _pending.Peek();
                    if (!req.done) break;
                    _pending.Dequeue();
                    if (req.hasError) { _dropped++; continue; }
                    Step(2, $"first readback ({req.layerDataSize} bytes) -> encoder");
                    Feed(req.GetDataRaw(0), req.layerDataSize, pts, 0);
                }
                Drain(_codec, true, false);
                if (_audio != IntPtr.Zero)
                {
                    Step(16, "audio: mixing game sound + mic");
                    _cap.Mix(ref _pcm, ref _pcmLen);
                    if (_pcmLen > 0) Step(4, $"first audio mixed ({_pcmLen / 2} frames)");
                    if (_pcmLen > 0) Step(32, "audio: feeding the AAC encoder");
                    FeedAudio(false);
                    Drain(_audio, false, false);
                    Step(128, "audio: first pump done");
                    if (_pcmLen >= 0 && (_step & 32) != 0) Step(64, "audio: encoder ok");
                }
            }
            catch (Exception e) { Mod.Log.Error("Recording error, stopping: " + e.Message); Diag("error: " + e); Stop(); }
        }

        private unsafe void Feed(IntPtr data, int size, long pts, uint flags)
        {
            long idx = (long)Ndk.AMediaCodec_dequeueInputBuffer(_codec, flags == FLAG_END_OF_STREAM ? 50_000L : 0L);
            if (idx < 0) { _dropped++; return; }
            if (size > 0 && data != IntPtr.Zero)
            {
                var dst = Ndk.AMediaCodec_getInputBuffer(_codec, (UIntPtr)idx, out UIntPtr capU);
                long cap = (long)capU;
                if (dst == IntPtr.Zero || cap <= 0) { Ndk.AMediaCodec_queueInputBuffer(_codec, (UIntPtr)idx, 0, UIntPtr.Zero, (ulong)Math.Max(0, pts), 0); _dropped++; return; }
                int n = (int)Math.Min(size, cap);
                Buffer.MemoryCopy((void*)data, (void*)dst, cap, n);
                size = n;
                _frames++;
            }
            else size = 0;
            Ok(Ndk.AMediaCodec_queueInputBuffer(_codec, (UIntPtr)idx, 0, (UIntPtr)size, (ulong)Math.Max(0, pts), flags), "queueInputBuffer");
        }

        private unsafe void FeedAudio(bool eos)
        {
            if (_aEosQueued) return;
            int rate = _cap.Rate;
            for (int guard = 0; guard < 16 && (_pcmLen > 0 || eos); guard++)
            {
                long idx = (long)Ndk.AMediaCodec_dequeueInputBuffer(_audio, eos ? 50_000L : 0L);
                if (idx < 0) return;
                var dst = Ndk.AMediaCodec_getInputBuffer(_audio, (UIntPtr)idx, out UIntPtr capU);
                int capShorts = (int)Math.Min((long)capU / 2, int.MaxValue) & ~1;
                int n = Math.Min(_pcmLen, capShorts);
                ulong pts = (ulong)(_aFrames * 1_000_000L / rate);
                bool last = eos && n == _pcmLen;
                if (n > 0 && dst != IntPtr.Zero)
                {
                    fixed (short* src = _pcm) Buffer.MemoryCopy(src, (void*)dst, (long)capU, n * 2L);
                    Array.Copy(_pcm, n, _pcm, 0, _pcmLen - n);
                    _pcmLen -= n;
                    _aFrames += n / 2;
                }
                Ok(Ndk.AMediaCodec_queueInputBuffer(_audio, (UIntPtr)idx, 0, (UIntPtr)(n * 2), pts, last ? FLAG_END_OF_STREAM : 0), "audio queueInputBuffer");
                if (last) { _aEosQueued = true; return; }
            }
        }

        private void Drain(IntPtr codec, bool video, bool untilEos)
        {
            for (int guard = 0; guard < (untilEos ? 300 : 32); guard++)
            {
                long o = (long)Ndk.AMediaCodec_dequeueOutputBuffer(codec, out var info, untilEos ? 10_000L : 0L);
                if (o == INFO_TRY_AGAIN_LATER) { if (untilEos) continue; return; }
                if (o == INFO_OUTPUT_FORMAT_CHANGED)
                {
                    var f = Ndk.AMediaCodec_getOutputFormat(codec);
                    long t = (long)Ndk.AMediaMuxer_addTrack(_muxer, f);
                    Ndk.AMediaFormat_delete(f);
                    if (t < 0) throw new Exception("muxer addTrack failed");
                    if (video) _vTrack = t; else _aTrack = t;
                    TryStartMuxer();
                    continue;
                }
                if (o < 0) continue;
                if ((info.flags & FLAG_CODEC_CONFIG) == 0 && info.size > 0)
                {
                    var buf = Ndk.AMediaCodec_getOutputBuffer(codec, (UIntPtr)o, out _);
                    if (buf != IntPtr.Zero) Write(video, buf, info);
                }
                Ndk.AMediaCodec_releaseOutputBuffer(codec, (UIntPtr)o, false);
                if ((info.flags & FLAG_END_OF_STREAM) != 0) { if (video) _vEos = true; else _aEos = true; return; }
            }
        }

        private void TryStartMuxer()
        {
            if (_muxStarted || _vTrack < 0 || (_audio != IntPtr.Zero && _aTrack < 0)) return;
            Ok(Ndk.AMediaMuxer_start(_muxer), "muxer start");
            _muxStarted = true;
            Step(8, "muxer started");
            foreach (var (video, data, info) in _early) WriteBytes(video, data, info);
            _early.Clear();
        }

        private unsafe void Write(bool video, IntPtr buf, BufferInfo info)
        {
            if (_muxStarted) { Ndk.AMediaMuxer_writeSampleData(_muxer, (UIntPtr)(video ? _vTrack : _aTrack), buf, ref info); return; }
            if (_early.Count > 600) return;
            var data = new byte[info.size];
            Marshal.Copy(IntPtr.Add(buf, info.offset), data, 0, info.size);
            info.offset = 0;
            _early.Add((video, data, info));
        }

        private unsafe void WriteBytes(bool video, byte[] data, BufferInfo info)
        {
            fixed (byte* p = data) Ndk.AMediaMuxer_writeSampleData(_muxer, (UIntPtr)(video ? _vTrack : _aTrack), (IntPtr)p, ref info);
        }

        public string Stop()
        {
            if (!Recording) return null;
            Recording = false;
            string file = FilePath;
            try
            {
                AsyncGPUReadback.WaitAllRequests();
                while (_pending.Count > 0)
                {
                    var (req, pts) = _pending.Dequeue();
                    if (req.done && !req.hasError) Feed(req.GetDataRaw(0), req.layerDataSize, pts, 0);
                }
                long end = (long)((Time.realtimeSinceStartup - _t0) * 1_000_000.0);
                Feed(IntPtr.Zero, 0, end, FLAG_END_OF_STREAM);
                if (_audio != IntPtr.Zero)
                {
                    _cap.Mix(ref _pcm, ref _pcmLen);
                    _cap.Stop();
                    for (int i = 0; i < 20 && !_aEos; i++) { FeedAudio(true); Drain(_audio, false, false); if (_pcmLen == 0) break; }
                    FeedAudio(true);
                }
                Drain(_codec, true, true);
                if (_audio != IntPtr.Zero) Drain(_audio, false, true);
                TryStartMuxer();
                if (_muxStarted) Ok(Ndk.AMediaMuxer_stop(_muxer), "muxer stop");
                else file = null;
                Mod.Log.Msg(file != null ? $"Saved video {file} ({_frames} frames, {_dropped} dropped{(_audio != IntPtr.Zero ? $", {_aFrames / (float)Math.Max(1, _cap?.Rate ?? 1):0.0}s audio" : "")})" : "Recording produced no frames");
            }
            catch (Exception e) { Mod.Log.Error("Recording stop error: " + e.Message); file = null; }
            Diag(file != null ? $"saved {_frames} frames" : "stopped without a file");
            Release();
            ClearMarker();
            if (file == null) try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
            return file;
        }

        private void Release()
        {
            Recording = false;
            _pending.Clear();
            _early.Clear();
            try { _cap?.Stop(); } catch { }
            _cap = null;
            try { if (_muxer != IntPtr.Zero) Ndk.AMediaMuxer_delete(_muxer); } catch { }
            try { if (_codec != IntPtr.Zero) { Ndk.AMediaCodec_stop(_codec); Ndk.AMediaCodec_delete(_codec); } } catch { }
            try { if (_audio != IntPtr.Zero) { Ndk.AMediaCodec_stop(_audio); Ndk.AMediaCodec_delete(_audio); } } catch { }
            _muxer = _codec = _audio = IntPtr.Zero;
            try { _file?.Dispose(); } catch { }
            _file = null;
            if (_yuv != null) { _yuv.Release(); UnityEngine.Object.Destroy(_yuv); _yuv = null; }
            if (_yuvMat != null) { UnityEngine.Object.Destroy(_yuvMat); _yuvMat = null; }
        }

        private static void Ok(int status, string where)
        {
            if (status != 0) throw new Exception($"{where} failed ({status})");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BufferInfo { public int offset; public int size; public long presentationTimeUs; public uint flags; }

        private static class Ndk
        {
            private const string Lib = "libmediandk.so";
            [DllImport(Lib)] public static extern IntPtr AMediaFormat_new();
            [DllImport(Lib)] public static extern int AMediaFormat_delete(IntPtr fmt);
            [DllImport(Lib)] public static extern void AMediaFormat_setInt32(IntPtr fmt, [MarshalAs(UnmanagedType.LPStr)] string name, int value);
            [DllImport(Lib)] public static extern void AMediaFormat_setString(IntPtr fmt, [MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
            [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AMediaFormat_getInt32(IntPtr fmt, [MarshalAs(UnmanagedType.LPStr)] string name, out int value);

            [DllImport(Lib)] public static extern IntPtr AMediaCodec_createEncoderByType([MarshalAs(UnmanagedType.LPStr)] string mime);
            [DllImport(Lib)] public static extern int AMediaCodec_configure(IntPtr codec, IntPtr format, IntPtr surface, IntPtr crypto, uint flags);
            [DllImport(Lib)] public static extern int AMediaCodec_start(IntPtr codec);
            [DllImport(Lib)] public static extern int AMediaCodec_stop(IntPtr codec);
            [DllImport(Lib)] public static extern int AMediaCodec_delete(IntPtr codec);
            [DllImport(Lib)] public static extern IntPtr AMediaCodec_dequeueInputBuffer(IntPtr codec, long timeoutUs);
            [DllImport(Lib)] public static extern IntPtr AMediaCodec_getInputBuffer(IntPtr codec, UIntPtr idx, out UIntPtr outSize);
            [DllImport(Lib)] public static extern int AMediaCodec_queueInputBuffer(IntPtr codec, UIntPtr idx, long offset, UIntPtr size, ulong timeUs, uint flags);
            [DllImport(Lib)] public static extern IntPtr AMediaCodec_dequeueOutputBuffer(IntPtr codec, out BufferInfo info, long timeoutUs);
            [DllImport(Lib)] public static extern IntPtr AMediaCodec_getOutputBuffer(IntPtr codec, UIntPtr idx, out UIntPtr outSize);
            [DllImport(Lib)] public static extern IntPtr AMediaCodec_getOutputFormat(IntPtr codec);
            [DllImport(Lib)] public static extern IntPtr AMediaCodec_getInputFormat(IntPtr codec);
            [DllImport(Lib)] public static extern int AMediaCodec_releaseOutputBuffer(IntPtr codec, UIntPtr idx, [MarshalAs(UnmanagedType.U1)] bool render);

            [DllImport(Lib)] public static extern IntPtr AMediaMuxer_new(int fd, int format);
            [DllImport(Lib)] public static extern int AMediaMuxer_delete(IntPtr muxer);
            [DllImport(Lib)] public static extern IntPtr AMediaMuxer_addTrack(IntPtr muxer, IntPtr format);
            [DllImport(Lib)] public static extern int AMediaMuxer_start(IntPtr muxer);
            [DllImport(Lib)] public static extern int AMediaMuxer_stop(IntPtr muxer);
            [DllImport(Lib)] public static extern int AMediaMuxer_writeSampleData(IntPtr muxer, UIntPtr trackIdx, IntPtr data, ref BufferInfo info);
        }
    }
}
#endif
