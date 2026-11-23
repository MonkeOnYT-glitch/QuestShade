using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace QuestShade
{
    // Live chat reader (beta) for the wrist chat. Read-only, no login:
    //  - Twitch: the anonymous chat connection (just the channel name).
    //  - YouTube: the live chat feed YouTube's own chat window uses (your channel handle, or a video link / ID).
    // Runs on background threads; messages are handed to the game through a queue.
    internal static class ChatReader
    {
        public struct Msg { public string Site, User, Text; }
        public static readonly ConcurrentQueue<Msg> Incoming = new ConcurrentQueue<Msg>();
        public static volatile string TwitchStatus = "off", YouTubeStatus = "off";

        private static Thread _twitch, _youtube;
        private static volatile int _gen; // bumped to stop the running readers
        private static string _twitchChan, _ytTarget;

        public static void Restart(string twitch, string youtube)
        {
            _gen++;
            _twitchChan = (twitch ?? "").Trim().TrimStart('#').ToLowerInvariant();
            _ytTarget = (youtube ?? "").Trim();
            int g = _gen;
            TwitchStatus = _twitchChan.Length > 0 ? "connecting" : "off";
            YouTubeStatus = _ytTarget.Length > 0 ? "connecting" : "off";
            if (_twitchChan.Length > 0) { _twitch = new Thread(() => TwitchLoop(g, _twitchChan)) { IsBackground = true, Name = "QuestShade Twitch chat" }; _twitch.Start(); }
            if (_ytTarget.Length > 0) { _youtube = new Thread(() => YouTubeLoop(g, _ytTarget)) { IsBackground = true, Name = "QuestShade YouTube chat" }; _youtube.Start(); }
        }

        public static void Stop() { _gen++; TwitchStatus = YouTubeStatus = "off"; }

        private static void Post(string site, string user, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Incoming.Enqueue(new Msg { Site = site, User = user ?? "?", Text = text });
            while (Incoming.Count > 200 && Incoming.TryDequeue(out _)) { }
        }

        private static void Nap(int gen, int ms) { for (int t = 0; t < ms && gen == _gen; t += 100) Thread.Sleep(100); }

        // ---------- Twitch ----------

        private static void TwitchLoop(int gen, string chan)
        {
            while (gen == _gen)
            {
                try
                {
                    using var tcp = new TcpClient { ReceiveTimeout = 360000 };
                    tcp.Connect("irc.chat.twitch.tv", 6667);
                    using var s = tcp.GetStream();
                    var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                    var r = new StreamReader(s, Encoding.UTF8);
                    w.WriteLine("PASS SCHMOOPIIE");
                    w.WriteLine("NICK justinfan" + new Random().Next(10000, 99999));
                    w.WriteLine("JOIN #" + chan);
                    TwitchStatus = "connected to #" + chan;
                    Mod.Log.Msg("Chat: Twitch connected (#" + chan + ")");
                    string line;
                    while (gen == _gen && (line = r.ReadLine()) != null)
                    {
                        if (line.StartsWith("PING")) { w.WriteLine("PONG" + line.Substring(4)); continue; }
                        int pm = line.IndexOf(" PRIVMSG #", StringComparison.Ordinal);
                        if (pm < 0 || !line.StartsWith(":")) continue;
                        int bang = line.IndexOf('!');
                        int colon = line.IndexOf(" :", pm, StringComparison.Ordinal);
                        if (bang < 1 || colon < 0) continue;
                        Post("Twitch", line.Substring(1, bang - 1), line.Substring(colon + 2));
                    }
                }
                catch (Exception e) { if (gen == _gen) { TwitchStatus = "reconnecting (" + e.Message + ")"; Mod.Log.Warning("Chat: Twitch: " + e.Message); } }
                Nap(gen, 5000);
            }
        }

        // ---------- YouTube ----------

        private static HttpClient _http;
        private static HttpClient Http
        {
            get
            {
                if (_http != null) return _http;
                _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                return _http;
            }
        }

        private static string VideoIdFrom(string s)
        {
            var m = Regex.Match(s, @"(?:v=|youtu\.be/|/live/|/shorts/)([\w-]{11})");
            if (m.Success) return m.Groups[1].Value;
            if (Regex.IsMatch(s, @"^[\w-]{11}$") && !s.StartsWith("@")) return s;
            return null;
        }

        private static string ChannelLiveUrl(string s)
        {
            s = s.Trim().TrimEnd('/');
            if (s.StartsWith("http")) return s.EndsWith("/live") ? s : s + "/live";
            if (s.StartsWith("UC") && s.Length == 24) return "https://www.youtube.com/channel/" + s + "/live";
            return "https://www.youtube.com/" + (s.StartsWith("@") ? s : "@" + s) + "/live";
        }

        private static void YouTubeLoop(int gen, string target)
        {
            while (gen == _gen)
            {
                try
                {
                    string vid = VideoIdFrom(target);
                    if (vid == null)
                    {
                        YouTubeStatus = "looking for your live stream";
                        string page = Http.GetStringAsync(ChannelLiveUrl(target)).GetAwaiter().GetResult();
                        var m = Regex.Match(page, "<link rel=\"canonical\" href=\"https://www\\.youtube\\.com/watch\\?v=([\\w-]{11})\"");
                        if (!m.Success || !page.Contains("\"isLive\":true")) { YouTubeStatus = "not live right now (checking again in 30 s)"; Nap(gen, 30000); continue; }
                        vid = m.Groups[1].Value;
                    }
                    string chat = Http.GetStringAsync("https://www.youtube.com/live_chat?is_popout=1&v=" + vid).GetAwaiter().GetResult();
                    string key = Rx(chat, "\"INNERTUBE_API_KEY\":\"([^\"]+)\"");
                    string ver = Rx(chat, "\"INNERTUBE_CLIENT_VERSION\":\"([^\"]+)\"") ?? Rx(chat, "\"clientVersion\":\"([^\"]+)\"") ?? "2.20240101.00.00";
                    string cont = Rx(chat, "\"(?:invalidationContinuationData|timedContinuationData|reloadContinuationData)\":\\{[^}]*?\"continuation\":\"([^\"]+)\"");
                    if (key == null || cont == null) { YouTubeStatus = "chat not available for this stream (retrying)"; Nap(gen, 20000); continue; }
                    YouTubeStatus = "connected";
                    Mod.Log.Msg("Chat: YouTube connected (" + vid + ")");
                    bool first = true;
                    while (gen == _gen && cont != null)
                    {
                        string body = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"" + ver + "\"}},\"continuation\":\"" + cont + "\"}";
                        var resp = Http.PostAsync("https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key=" + key + "&prettyPrint=false",
                                                  new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
                        string json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        int wait = 3000;
                        cont = ParseYouTube(json, first, ref wait);
                        first = false;
                        Nap(gen, Math.Clamp(wait, 1000, 8000));
                    }
                    if (gen == _gen) { YouTubeStatus = "stream ended (checking again)"; Nap(gen, 15000); }
                }
                catch (Exception e)
                {
                    if (gen != _gen) break;
                    string why = (e.InnerException ?? e).Message;
                    YouTubeStatus = "reconnecting (" + why + ")";
                    Mod.Log.Warning("Chat: YouTube: " + why);
                    Nap(gen, 10000);
                }
            }
        }

        private static string Rx(string s, string pattern) { var m = Regex.Match(s, pattern); return m.Success ? m.Groups[1].Value : null; }

        // Pulls the chat messages out of one response and returns the next continuation (null = chat ended).
        private static string ParseYouTube(string json, bool first, ref int wait)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("continuationContents", out var cc) || !cc.TryGetProperty("liveChatContinuation", out var lc)) return null;
            string next = null;
            if (lc.TryGetProperty("continuations", out var conts))
                foreach (var c in conts.EnumerateArray())
                    foreach (var kind in new[] { "invalidationContinuationData", "timedContinuationData", "reloadContinuationData" })
                        if (c.TryGetProperty(kind, out var d))
                        {
                            if (d.TryGetProperty("continuation", out var ct)) next = ct.GetString();
                            if (d.TryGetProperty("timeoutMs", out var tm) && tm.TryGetInt32(out int ms)) wait = ms;
                        }
            if (!lc.TryGetProperty("actions", out var actions)) return next;
            int count = 0, skip = first ? Math.Max(0, actions.GetArrayLength() - 5) : 0; // on connect, only show the last few
            foreach (var a in actions.EnumerateArray())
            {
                if (count++ < skip) continue;
                if (!a.TryGetProperty("addChatItemAction", out var add) || !add.TryGetProperty("item", out var item)) continue;
                JsonElement r;
                bool paid = false;
                if (!item.TryGetProperty("liveChatTextMessageRenderer", out r) && !(paid = item.TryGetProperty("liveChatPaidMessageRenderer", out r))) continue;
                string user = r.TryGetProperty("authorName", out var an) && an.TryGetProperty("simpleText", out var st) ? st.GetString() : "?";
                var sb = new StringBuilder();
                if (paid && r.TryGetProperty("purchaseAmountText", out var pa) && pa.TryGetProperty("simpleText", out var pat)) sb.Append('[').Append(pat.GetString()).Append("] ");
                if (r.TryGetProperty("message", out var msg) && msg.TryGetProperty("runs", out var runs))
                    foreach (var run in runs.EnumerateArray())
                    {
                        if (run.TryGetProperty("text", out var t)) sb.Append(t.GetString());
                        else if (run.TryGetProperty("emoji", out var em) && em.TryGetProperty("shortcuts", out var sc) && sc.GetArrayLength() > 0) sb.Append(sc[0].GetString());
                    }
                Post("YouTube", user, sb.ToString());
            }
            return next;
        }
    }
}
