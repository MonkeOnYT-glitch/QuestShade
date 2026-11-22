using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace QuestShade
{
    // Livestream: a small RTMP / RTMPS publisher, the same protocol OBS uses to stream to Twitch, YouTube and Kick.
    // The video recorder hands it H.264 + AAC straight from the Quest's hardware encoders; this wraps them in FLV tags and
    // sends them on a background thread so the game never waits on the network.
    internal sealed class RtmpClient : IDisposable
    {
        public volatile string Status = "connecting";
        public volatile bool Live, Failed;
        public volatile string Error;
        public long BytesSent;
        public int Dropped;
        public float SecondsBehind => _vkbps <= 0 ? 0f : _qBytes * 8f / (_vkbps * 1000f);

        private readonly string _server, _key;
        private int _w, _h, _fps, _vkbps, _akbps, _arate;
        private Thread _thread, _reader;
        private volatile bool _stop;
        private Stream _net, _rd; private TcpClient _tcp; // _net: writes (whole messages at once), _rd: buffered reads (reader thread)
        private readonly object _wlock = new object(), _qlock = new object();
        private readonly Queue<Packet> _q = new Queue<Packet>();
        private long _qBytes;
        private bool _waitKey = true; // never send a P-frame before the first key frame
        private byte[] _avcSeq, _aacSeq;
        private bool _sentAvcSeq, _sentAacSeq;
        private int _outChunk = 128, _inChunk = 128;
        private long _inBytes, _inAcked; private int _ackWindow = 2_500_000;
        private uint _streamId;
        // Level changes: the connection stays open while the encoders restart. Timestamps carry on from where they were,
        // and the last key frame is re-sent twice a second meanwhile so viewers see a held picture instead of the stream ending.
        private long _tsBase; private uint _lastTs; private byte[] _lastKey;
        private bool _parked; private long _nextHold;
        public bool Parked { get { lock (_qlock) return _parked; } }

        public void Park() { lock (_qlock) { _parked = true; _nextHold = 0; Monitor.Pulse(_qlock); } Mod.Log.Msg("Stream: level change, holding the picture"); }

        // Call before the new encoders start: their timestamps begin at 0 again, so shift them past everything already sent.
        public void Resume()
        {
            lock (_qlock) { _tsBase = (long)_lastTs + 100; _parked = false; _waitKey = true; }
            Mod.Log.Msg("Stream: resuming after the level change");
        }

        private struct Packet { public byte Type; public uint Ts; public byte[] Body; }

        private const int CS_CONTROL = 2, CS_COMMAND = 3, CS_AUDIO = 4, CS_DATA = 5, CS_VIDEO = 6;

        public RtmpClient(string server, string key) { _server = (server ?? "").Trim(); _key = (key ?? "").Trim(); }

        public void Configure(int w, int h, int fps, int vkbps, int akbps, int arate) { _w = w; _h = h; _fps = fps; _vkbps = vkbps; _akbps = akbps; _arate = arate; }

        public void Start()
        {
            if (_thread != null) return; // already connected (resumed after a level change)
            _thread = new Thread(Run) { IsBackground = true, Name = "QuestShade RTMP" };
            _thread.Start();
        }

        // ---------- media in (main thread) ----------

        // MediaCodec's codec-config output for H.264: SPS + PPS in Annex-B form.
        public void VideoConfig(byte[] annexB)
        {
            byte[] sps = null, pps = null;
            foreach (var nal in SplitAnnexB(annexB, 0, annexB.Length))
            {
                int t = annexB[nal.off] & 0x1f;
                if (t == 7 && sps == null) sps = Slice(annexB, nal.off, nal.len);
                else if (t == 8 && pps == null) pps = Slice(annexB, nal.off, nal.len);
            }
            if (sps == null || pps == null || sps.Length < 4) { Mod.Log.Warning("Stream: encoder config had no SPS/PPS"); return; }
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x17, 0x00, 0, 0, 0 });               // key frame, AVC, sequence header, cts 0
            ms.Write(new byte[] { 1, sps[1], sps[2], sps[3], 0xFF, 0xE1 }); // AVCDecoderConfigurationRecord, 4-byte lengths, 1 SPS
            U16(ms, sps.Length); ms.Write(sps);
            ms.WriteByte(1); U16(ms, pps.Length); ms.Write(pps);
            lock (_qlock) { _avcSeq = ms.ToArray(); _sentAvcSeq = false; }
        }

        // MediaCodec's codec-config output for AAC: the 2-byte AudioSpecificConfig.
        public void AudioConfig(byte[] asc)
        {
            var b = new byte[2 + asc.Length]; b[0] = 0xAF; b[1] = 0x00; Buffer.BlockCopy(asc, 0, b, 2, asc.Length);
            lock (_qlock) { _aacSeq = b; _sentAacSeq = false; }
        }

        public void Video(byte[] annexB, long ptsUs, bool key)
        {
            if (_stop || Failed) return;
            // Annex-B -> AVCC (4-byte lengths); drop SPS/PPS/AUD that some encoders repeat inline.
            var ms = new MemoryStream(annexB.Length + 16);
            bool idr = false;
            ms.Write(new byte[] { 0, 1, 0, 0, 0 }); // frame type / packet type 1 / cts 0 (filled below)
            foreach (var nal in SplitAnnexB(annexB, 0, annexB.Length))
            {
                int t = annexB[nal.off] & 0x1f;
                if (t == 7 || t == 8 || t == 9) continue;
                if (t == 5) idr = true;
                U32(ms, nal.len); ms.Write(annexB, nal.off, nal.len);
            }
            key |= idr;
            var body = ms.ToArray();
            if (body.Length <= 5) return;
            body[0] = (byte)(key ? 0x17 : 0x27);
            Enqueue(new Packet { Type = 9, Ts = Ms(ptsUs), Body = body }, true, key);
        }

        public void Audio(byte[] aac, long ptsUs)
        {
            if (_stop || Failed) return;
            var b = new byte[2 + aac.Length]; b[0] = 0xAF; b[1] = 0x01; Buffer.BlockCopy(aac, 0, b, 2, aac.Length);
            Enqueue(new Packet { Type = 8, Ts = Ms(ptsUs), Body = b }, false, false);
        }

        private uint Ms(long us) => (uint)Math.Max(0, us / 1000 + _tsBase);

        private void Enqueue(Packet p, bool video, bool key)
        {
            lock (_qlock)
            {
                long max = Math.Max(400_000, _vkbps * 1000L / 8 * 3); // ~3 s of video waiting = the connection can't keep up
                if (video)
                {
                    if (_qBytes > max) _waitKey = true;
                    if (_waitKey && !key) { Dropped++; return; }
                    if (key) _waitKey = false;
                }
                else if (_qBytes > max * 2) { Dropped++; return; }
                if (p.Ts > _lastTs) _lastTs = p.Ts;
                if (video && key) _lastKey = p.Body;
                _q.Enqueue(p); _qBytes += p.Body.Length;
                Monitor.Pulse(_qlock);
            }
        }

        // ---------- network (background thread) ----------

        private void Run()
        {
            try
            {
                Connect();
                Status = "live"; Live = true;
                Mod.Log.Msg("Stream: live");
                while (!_stop)
                {
                    Packet p = default; byte[] avc = null, aac = null; bool hold = false;
                    lock (_qlock)
                    {
                        while (_q.Count == 0 && !_stop)
                        {
                            // Parked for a level change: keep the site fed with the last picture.
                            if (_parked && _lastKey != null && _sentAvcSeq)
                            {
                                long nowMs = Environment.TickCount64;
                                if (nowMs >= _nextHold) { _nextHold = nowMs + 500; _lastTs += 500; p = new Packet { Type = 9, Ts = _lastTs, Body = _lastKey }; hold = true; break; }
                            }
                            Monitor.Wait(_qlock, 250);
                        }
                        if (_stop) break;
                        if (!hold) { p = _q.Dequeue(); _qBytes -= p.Body.Length; }
                        if (p.Type == 9 && !_sentAvcSeq && _avcSeq != null) { avc = _avcSeq; _sentAvcSeq = true; }
                        if (p.Type == 8 && !_sentAacSeq && _aacSeq != null) { aac = _aacSeq; _sentAacSeq = true; }
                    }
                    if (p.Type == 9 && avc == null && !_sentAvcSeq) continue; // no SPS/PPS yet: a decoder can't use this frame
                    if (p.Type == 8 && aac == null && !_sentAacSeq) continue;
                    if (avc != null) WriteMessage(CS_VIDEO, 9, p.Ts, _streamId, avc);
                    if (aac != null) WriteMessage(CS_AUDIO, 8, p.Ts, _streamId, aac);
                    WriteMessage(p.Type == 9 ? CS_VIDEO : CS_AUDIO, p.Type, p.Ts, _streamId, p.Body);
                }
                try { if (_net != null) { SendCommand(CS_COMMAND, _streamId, "FCUnpublish", 6, null, _key); SendCommand(CS_COMMAND, 0, "deleteStream", 7, null, (double)_streamId); } } catch { }
            }
            catch (Exception e)
            {
                if (!_stop) { Error = Friendly(e); Failed = true; Live = false; Status = "failed"; Mod.Log.Warning("Stream failed: " + e.Message); }
            }
            finally { Close(); Live = false; }
        }

        private static string Friendly(Exception e)
        {
            string m = e.Message ?? "";
            if (e is SocketException || e is IOException) return "lost connection to the stream server (" + m + ")";
            return m;
        }

        private void Connect()
        {
            if (string.IsNullOrEmpty(_server)) throw new Exception("no stream server set");
            if (string.IsNullOrEmpty(_key)) throw new Exception("no stream key set");
            string url = _server.TrimEnd('/');
            bool tls = url.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase);
            if (!tls && !url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)) throw new Exception("the server must start with rtmp:// or rtmps://");
            string rest = url.Substring(url.IndexOf("://") + 3);
            int slash = rest.IndexOf('/');
            string hostPort = slash < 0 ? rest : rest.Substring(0, slash);
            string app = slash < 0 ? "" : rest.Substring(slash + 1);
            string host = hostPort; int port = tls ? 443 : 1935;
            int colon = hostPort.LastIndexOf(':');
            if (colon > 0 && int.TryParse(hostPort.Substring(colon + 1), out int pp)) { host = hostPort.Substring(0, colon); port = pp; }
            string tcUrl = (tls ? "rtmps://" : "rtmp://") + hostPort + "/" + app;

            Status = "connecting to " + host;
            Mod.Log.Msg($"Stream: connecting to {(tls ? "rtmps" : "rtmp")}://{host}:{port}/{app}");
            _tcp = new TcpClient { NoDelay = true, SendBufferSize = 1 << 20, ReceiveTimeout = 15000, SendTimeout = 15000 };
            var ar = _tcp.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(10000)) throw new Exception($"couldn't reach {host}:{port}");
            _tcp.EndConnect(ar);
            Stream s = _tcp.GetStream();
            if (tls) { var ssl = new SslStream(s, false); ssl.AuthenticateAsClient(host); s = ssl; }
            _net = s; _rd = new BufferedStream(s, 1 << 16);

            // Handshake (simple form, accepted by Twitch / YouTube / Kick).
            Status = "handshake";
            var c1 = new byte[1537]; c1[0] = 3; new Random().NextBytes(c1.AsSpan(9)); // C0 + C1 (time 0, zero 0, random)
            c1[1] = c1[2] = c1[3] = c1[4] = c1[5] = c1[6] = c1[7] = c1[8] = 0;
            _net.Write(c1, 0, c1.Length); _net.Flush();
            var s0 = ReadExact(1); if (s0[0] != 3) throw new Exception("server isn't an RTMP server (version " + s0[0] + ")");
            var s1 = ReadExact(1536); ReadExact(1536);
            _net.Write(s1, 0, s1.Length); _net.Flush(); // C2 = echo of S1

            SetChunkSize(4096);
            Status = "logging in";
            SendCommand(CS_COMMAND, 0, "connect", 1, Obj(
                ("app", app), ("type", "nonprivate"), ("flashVer", "FMLE/3.0 (compatible; QuestShade)"), ("swfUrl", tcUrl), ("tcUrl", tcUrl)));
            WaitResult(1, "connect");
            SendCommand(CS_COMMAND, 0, "releaseStream", 2, null, _key);
            SendCommand(CS_COMMAND, 0, "FCPublish", 3, null, _key);
            SendCommand(CS_COMMAND, 0, "createStream", 4, null);
            var res = WaitResult(4, "createStream");
            _streamId = res.Count > 3 && res[3] is double d ? (uint)d : 1;
            SendCommand(CS_COMMAND, _streamId, "publish", 5, null, _key, "live");
            WaitStatus("NetStream.Publish.Start");

            // Stream info, then hand the socket to the reader thread for pings / acks.
            var meta = new MemoryStream();
            AmfString(meta, "@setDataFrame"); AmfString(meta, "onMetaData");
            AmfEcma(meta, new List<(string, object)> {
                ("duration", 0.0), ("width", (double)_w), ("height", (double)_h), ("videodatarate", (double)_vkbps), ("framerate", (double)_fps),
                ("videocodecid", 7.0), ("audiodatarate", (double)_akbps), ("audiosamplerate", (double)_arate), ("audiosamplesize", 16.0),
                ("stereo", true), ("audiocodecid", 10.0), ("encoder", "QuestShade") });
            WriteMessage(CS_DATA, 18, 0, _streamId, meta.ToArray());
            _tcp.ReceiveTimeout = 0;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "QuestShade RTMP reader" };
            _reader.Start();
        }

        private void SetChunkSize(int n)
        {
            var b = new byte[4]; b[0] = (byte)(n >> 24); b[1] = (byte)(n >> 16); b[2] = (byte)(n >> 8); b[3] = (byte)n;
            WriteMessage(CS_CONTROL, 1, 0, 0, b);
            _outChunk = n;
        }

        private List<object> WaitResult(double txn, string what)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                var (type, body) = ReadMessage();
                if (type != 20) continue;
                var vals = AmfReadAll(body);
                if (vals.Count < 2) continue;
                string name = vals[0] as string;
                if (vals[1] is double t && Math.Abs(t - txn) < 0.01)
                {
                    if (name == "_result") return vals;
                    if (name == "_error") throw new Exception($"server refused {what}: {Describe(vals)}");
                }
                if (name == "onStatus") CheckStatus(vals);
            }
            throw new Exception($"server didn't answer {what}");
        }

        private void WaitStatus(string code)
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < until)
            {
                var (type, body) = ReadMessage();
                if (type != 20) continue;
                var vals = AmfReadAll(body);
                if (vals.Count > 0 && vals[0] as string == "onStatus" && CheckStatus(vals) == code) return;
            }
            throw new Exception("server didn't start the stream (check the stream key)");
        }

        // Returns the status code; throws on errors (e.g. a wrong stream key).
        private static string CheckStatus(List<object> vals)
        {
            foreach (var v in vals)
                if (v is Dictionary<string, object> o)
                {
                    o.TryGetValue("code", out var c); o.TryGetValue("level", out var l); o.TryGetValue("description", out var dsc);
                    string code = c as string ?? "";
                    if ((l as string) == "error" || code.Contains("BadName") || code.Contains("Failed") || code.Contains("Rejected"))
                        throw new Exception(code.Contains("BadName") ? "the stream key was refused" : $"server said {code} {dsc}".Trim());
                    return code;
                }
            return "";
        }

        private static string Describe(List<object> vals)
        {
            foreach (var v in vals) if (v is Dictionary<string, object> o) { o.TryGetValue("code", out var c); o.TryGetValue("description", out var d); return $"{c} {d}".Trim(); }
            return "unknown error";
        }

        private void ReadLoop()
        {
            try
            {
                while (!_stop)
                {
                    var (type, body) = ReadMessage();
                    if (type == 20)
                    {
                        var vals = AmfReadAll(body);
                        if (vals.Count > 0 && vals[0] as string == "onStatus") CheckStatus(vals);
                    }
                }
            }
            catch (Exception e)
            {
                if (_stop) return;
                Error = Friendly(e); Failed = true; Live = false; Status = "failed";
                Mod.Log.Warning("Stream: server connection ended: " + e.Message);
                lock (_qlock) { _stop = true; Monitor.PulseAll(_qlock); }
            }
        }

        // ---------- chunk stream: reading ----------

        private sealed class InChunk { public uint Ts, Len, StreamId; public byte Type; public bool Ext; public byte[] Buf; public int Got; }
        private readonly Dictionary<int, InChunk> _in = new Dictionary<int, InChunk>();

        private (byte type, byte[] body) ReadMessage()
        {
            while (true)
            {
                int b0 = ReadByte();
                int fmt = b0 >> 6, csid = b0 & 0x3f;
                if (csid == 0) csid = 64 + ReadByte();
                else if (csid == 1) { int a = ReadByte(), b = ReadByte(); csid = 64 + a + b * 256; }
                if (!_in.TryGetValue(csid, out var st)) _in[csid] = st = new InChunk();
                if (fmt <= 2)
                {
                    var h = ReadExact(fmt == 0 ? 11 : fmt == 1 ? 7 : 3);
                    uint ts = (uint)(h[0] << 16 | h[1] << 8 | h[2]);
                    if (fmt <= 1) { st.Len = (uint)(h[3] << 16 | h[4] << 8 | h[5]); st.Type = h[6]; }
                    if (fmt == 0) st.StreamId = (uint)(h[7] | h[8] << 8 | h[9] << 16 | h[10] << 24);
                    st.Ext = ts == 0xFFFFFF;
                    if (st.Ext) { var e = ReadExact(4); ts = (uint)(e[0] << 24 | e[1] << 16 | e[2] << 8 | e[3]); }
                    st.Ts = fmt == 0 ? ts : st.Ts + ts;
                }
                else if (st.Ext) ReadExact(4);
                if (st.Got == 0) st.Buf = new byte[st.Len];
                int n = (int)Math.Min(_inChunk, st.Len - st.Got);
                ReadInto(st.Buf, st.Got, n); st.Got += n;
                if (st.Got < st.Len) continue;
                st.Got = 0;
                var body = st.Buf;
                switch (st.Type)
                {
                    case 1: if (body.Length >= 4) _inChunk = (body[0] & 0x7f) << 24 | body[1] << 16 | body[2] << 8 | body[3]; break;
                    case 5: if (body.Length >= 4) _ackWindow = Math.Max(4096, body[0] << 24 | body[1] << 16 | body[2] << 8 | body[3]); break;
                    case 4: // user control: answer pings so the server doesn't drop us
                        if (body.Length >= 6 && body[0] == 0 && body[1] == 6)
                        {
                            var pong = new byte[6]; pong[1] = 7; Buffer.BlockCopy(body, 2, pong, 2, 4);
                            WriteMessage(CS_CONTROL, 4, 0, 0, pong);
                        }
                        break;
                }
                return (st.Type, body);
            }
        }

        private void Received(int n)
        {
            _inBytes += n;
            if (_inBytes - _inAcked >= _ackWindow)
            {
                _inAcked = _inBytes;
                uint v = (uint)_inBytes; var b = new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
                WriteMessage(CS_CONTROL, 3, 0, 0, b);
            }
        }

        private int ReadByte() { int b = _rd.ReadByte(); if (b < 0) throw new IOException("server closed the connection"); Received(1); return b; }
        private byte[] ReadExact(int n) { var b = new byte[n]; ReadInto(b, 0, n); return b; }
        private void ReadInto(byte[] b, int off, int n)
        {
            while (n > 0) { int r = _rd.Read(b, off, n); if (r <= 0) throw new IOException("server closed the connection"); off += r; n -= r; Received(r); }
        }

        // ---------- chunk stream: writing ----------

        private readonly MemoryStream _wbuf = new MemoryStream(1 << 16);

        private void WriteMessage(int csid, byte type, uint ts, uint streamId, byte[] payload)
        {
            lock (_wlock)
            {
                var o = _wbuf; o.SetLength(0);
                bool ext = ts >= 0xFFFFFF;
                o.WriteByte((byte)(csid & 0x3f)); // fmt 0
                uint t3 = ext ? 0xFFFFFF : ts;
                o.WriteByte((byte)(t3 >> 16)); o.WriteByte((byte)(t3 >> 8)); o.WriteByte((byte)t3);
                int len = payload.Length;
                o.WriteByte((byte)(len >> 16)); o.WriteByte((byte)(len >> 8)); o.WriteByte((byte)len);
                o.WriteByte(type);
                o.WriteByte((byte)streamId); o.WriteByte((byte)(streamId >> 8)); o.WriteByte((byte)(streamId >> 16)); o.WriteByte((byte)(streamId >> 24));
                if (ext) WriteExt(o, ts);
                for (int off = 0; off < len; off += _outChunk)
                {
                    if (off > 0) { o.WriteByte((byte)(0xC0 | (csid & 0x3f))); if (ext) WriteExt(o, ts); }
                    o.Write(payload, off, Math.Min(_outChunk, len - off));
                }
                _net.Write(o.GetBuffer(), 0, (int)o.Length);
                _net.Flush();
                BytesSent += o.Length;
            }
        }

        private static void WriteExt(Stream o, uint ts) { o.WriteByte((byte)(ts >> 24)); o.WriteByte((byte)(ts >> 16)); o.WriteByte((byte)(ts >> 8)); o.WriteByte((byte)ts); }

        private void SendCommand(int csid, uint streamId, string name, double txn, object cmdObj, params object[] args)
        {
            var ms = new MemoryStream();
            AmfString(ms, name); AmfNumber(ms, txn); AmfValue(ms, cmdObj);
            foreach (var a in args) AmfValue(ms, a);
            WriteMessage(csid, 20, 0, streamId, ms.ToArray());
        }

        // ---------- AMF0 ----------

        private static List<(string, object)> Obj(params (string, object)[] kv) => new List<(string, object)>(kv);

        private static void AmfValue(Stream s, object v)
        {
            switch (v)
            {
                case null: s.WriteByte(5); break;
                case string str: AmfString(s, str); break;
                case double d: AmfNumber(s, d); break;
                case int i: AmfNumber(s, i); break;
                case bool b: s.WriteByte(1); s.WriteByte((byte)(b ? 1 : 0)); break;
                case List<(string, object)> o:
                    s.WriteByte(3);
                    foreach (var (k, val) in o) { var kb = Encoding.UTF8.GetBytes(k); U16(s, kb.Length); s.Write(kb); AmfValue(s, val); }
                    s.WriteByte(0); s.WriteByte(0); s.WriteByte(9);
                    break;
                default: AmfString(s, v.ToString()); break;
            }
        }

        private static void AmfEcma(Stream s, List<(string, object)> o)
        {
            s.WriteByte(8); U32(s, o.Count);
            foreach (var (k, val) in o) { var kb = Encoding.UTF8.GetBytes(k); U16(s, kb.Length); s.Write(kb); AmfValue(s, val); }
            s.WriteByte(0); s.WriteByte(0); s.WriteByte(9);
        }

        private static void AmfString(Stream s, string v) { var b = Encoding.UTF8.GetBytes(v); s.WriteByte(2); U16(s, b.Length); s.Write(b); }
        private static void AmfNumber(Stream s, double d)
        {
            s.WriteByte(0);
            var b = BitConverter.GetBytes(d); if (BitConverter.IsLittleEndian) Array.Reverse(b); s.Write(b);
        }

        private static List<object> AmfReadAll(byte[] b)
        {
            var list = new List<object>(); int p = 0;
            try { while (p < b.Length) list.Add(AmfRead(b, ref p)); } catch { }
            return list;
        }

        private static object AmfRead(byte[] b, ref int p)
        {
            byte t = b[p++];
            switch (t)
            {
                case 0: { var d = new byte[8]; Buffer.BlockCopy(b, p, d, 0, 8); p += 8; if (BitConverter.IsLittleEndian) Array.Reverse(d); return BitConverter.ToDouble(d, 0); }
                case 1: return b[p++] != 0;
                case 2: { int n = b[p] << 8 | b[p + 1]; p += 2; var s = Encoding.UTF8.GetString(b, p, n); p += n; return s; }
                case 3: case 8:
                    {
                        if (t == 8) p += 4;
                        var o = new Dictionary<string, object>();
                        while (p + 3 <= b.Length)
                        {
                            int n = b[p] << 8 | b[p + 1]; p += 2;
                            if (n == 0 && b[p] == 9) { p++; break; }
                            var k = Encoding.UTF8.GetString(b, p, n); p += n;
                            o[k] = AmfRead(b, ref p);
                        }
                        return o;
                    }
                case 5: case 6: return null;
                case 10: { int n = b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]; p += 4; var arr = new List<object>(); for (int i = 0; i < n; i++) arr.Add(AmfRead(b, ref p)); return arr; }
                case 12: { int n = b[p] << 24 | b[p + 1] << 16 | b[p + 2] << 8 | b[p + 3]; p += 4; var s = Encoding.UTF8.GetString(b, p, n); p += n; return s; }
                default: throw new Exception("AMF type " + t);
            }
        }

        // ---------- helpers ----------

        private static IEnumerable<(int off, int len)> SplitAnnexB(byte[] b, int start, int end)
        {
            int i = start, nalStart = -1;
            while (i + 3 <= end)
            {
                bool sc3 = b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1;
                bool sc4 = i + 4 <= end && b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1;
                if (sc3 || sc4)
                {
                    if (nalStart >= 0) { int e = i; while (e > nalStart && b[e - 1] == 0) e--; if (e > nalStart) yield return (nalStart, e - nalStart); }
                    i += sc4 ? 4 : 3; nalStart = i; continue;
                }
                i++;
            }
            if (nalStart < 0) { if (end > start) yield return (start, end - start); } // no start codes: one raw NAL
            else if (nalStart < end) yield return (nalStart, end - nalStart);
        }

        private static byte[] Slice(byte[] b, int off, int len) { var r = new byte[len]; Buffer.BlockCopy(b, off, r, 0, len); return r; }
        private static void U16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void U32(Stream s, int v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

        private void Close()
        {
            try { _rd?.Dispose(); } catch { } try { _net?.Dispose(); } catch { }
            try { _tcp?.Close(); } catch { }
            _net = null; _rd = null; _tcp = null;
        }

        public void Dispose()
        {
            lock (_qlock) { _stop = true; Monitor.PulseAll(_qlock); }
            try { _thread?.Join(1500); } catch { }
            Close();
            try { _reader?.Join(500); } catch { }
        }
    }
}
