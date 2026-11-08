using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using UnityEngine;

namespace QuestShade
{
    internal static class PhotoSaver
    {
        public static string Folder { get; private set; }
        public static string VideoFolder { get; private set; }
        public static string RecordFolder => _store ? TempFolder : VideoFolder;
        public static string TempFolder { get; private set; }

        private static bool _store;
        private static readonly ConcurrentQueue<(string msg, string scan)> _done = new ConcurrentQueue<(string, string)>();
        private static int _pending;
        public static bool Busy => Volatile.Read(ref _pending) > 0;

        private static readonly string[] PhotoDirs = { "Oculus/Screenshots/", "Pictures/BONELAB/" };
        private static readonly string[] VideoDirs = { "Oculus/VideoShots/", "Movies/BONELAB/" };

        public static void ResolveFolder(string configured)
        {
            bool android = Application.platform == RuntimePlatform.Android;
#if QUEST
            _store = android && Jni.Init();
#endif
            Folder = null;
            bool custom = !string.IsNullOrWhiteSpace(configured) && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase);
            if (custom)
            {
                if (Writable(configured, ".png")) { Folder = configured; _store = false; }
                else Mod.Log.Warning($"SaveFolder '{configured}' isn't writable, falling back to auto");
            }
            if (Folder == null && android)
                foreach (var f in new[] { "/sdcard/Oculus/Screenshots", "/sdcard/Pictures/BONELAB", "/sdcard/DCIM/BONELAB" })
                    if (Writable(f, ".png")) { Folder = f; break; }
            Folder ??= Path.Combine(Config.Root, "Photos");
            if (android && !custom)
                foreach (var f in new[] { "/sdcard/Oculus/VideoShots", "/sdcard/Movies/BONELAB", "/sdcard/DCIM/BONELAB" })
                    if (Writable(f, ".mp4")) { VideoFolder = f; break; }
            VideoFolder ??= custom ? Folder : Path.Combine(Config.Root, "Videos");

            TempFolder = null;
            if (android && Writable("/sdcard/Android/data/com.StressLevelZero.BONELAB/files/QuestShade", ".tmp")) TempFolder = "/sdcard/Android/data/com.StressLevelZero.BONELAB/files/QuestShade";
            TempFolder ??= Path.Combine(Config.Root, "Temp");
            try { Directory.CreateDirectory(VideoFolder); Directory.CreateDirectory(Folder); Directory.CreateDirectory(TempFolder); } catch { }
            CleanTemp();
            Mod.Log.Msg(_store ? $"Gallery: MediaStore ready (photos -> {PhotoDirs[0]} or {PhotoDirs[1]}, videos -> {VideoDirs[0]} or {VideoDirs[1]})"
                               : $"Gallery: folder mode. Photos -> {Folder}   Videos -> {VideoFolder}");
        }

        private static bool Writable(string dir, string ext)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, "questshade_probe" + ext);
                File.WriteAllBytes(probe, Array.Empty<byte>());
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        private static void CleanTemp()
        {
            if (!_store) return;
            try
            {
                foreach (var f in Directory.GetFiles(TempFolder, "*.mp4"))
                    if (new FileInfo(f).Length > 4096) PublishVideo(f); else File.Delete(f);
            }
            catch { }
        }


        public static void SaveAsync(byte[] rgba, int w, int h, string tag = null)
        {
            var name = MediaScan.QuestName("png", DateTime.Now, tag);
            Interlocked.Increment(ref _pending);
            Run("QuestShade photo", () =>
            {
                try
                {
                    var png = Png.EncodeBottomUp(rgba, w, h, 4);
                    if (_store)
                    {
                        try { var where = Store(false, name, "image/png", png, null); Mod.Log.Msg("Saved photo to gallery: " + where); _done.Enqueue(("Photo saved", null)); return; }
                        catch (Exception e) { Mod.Log.Warning("Gallery: MediaStore photo save failed, using a folder (" + e.Message + ")"); }
                    }
                    var file = Path.Combine(Folder, name);
                    File.WriteAllBytes(file, png);
                    Mod.Log.Msg("Saved photo " + file);
                    _done.Enqueue(("Photo saved", file));
                }
                catch (Exception e) { Mod.Log.Error("Photo save failed: " + e.Message); _done.Enqueue(("Photo save failed (see log)", null)); }
                finally { Interlocked.Decrement(ref _pending); }
            });
        }


        public static void PublishVideo(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            if (!_store) { _done.Enqueue(("Video saved to the Quest gallery", file)); return; }
            Interlocked.Increment(ref _pending);
            Run("QuestShade video", () =>
            {
                try
                {
                    var where = Store(true, Path.GetFileName(file), "video/mp4", null, file);
                    File.Delete(file);
                    Mod.Log.Msg("Saved video to gallery: " + where);
                    _done.Enqueue(("Video saved to the Quest gallery", null));
                }
                catch (Exception e)
                {
                    Mod.Log.Warning("Gallery: MediaStore video save failed, moving it to a folder (" + e.Message + ")");
                    try
                    {
                        var dst = Path.Combine(VideoFolder, Path.GetFileName(file));
                        File.Move(file, dst, true);
                        _done.Enqueue(("Video saved", dst));
                    }
                    catch (Exception e2) { Mod.Log.Error("Video kept at " + file + " (" + e2.Message + ")"); _done.Enqueue(("Video save failed (see log)", null)); }
                }
                finally { Interlocked.Decrement(ref _pending); }
            });
        }

        public static string PumpMainThread()
        {
            if (!_done.TryDequeue(out var r)) return null;
            if (r.scan != null) MediaScan.Scan(r.scan);
            return r.msg;
        }

        private static void Run(string name, Action a) => new Thread(() => a()) { IsBackground = true, Name = name }.Start();


        private static string Store(bool video, string name, string mime, byte[] bytes, string srcFile)
        {
#if QUEST
            var e = Jni.Attach(out bool attached);
            try
            {
                e.PushFrame(64);
                try { return StoreOn(e, video, name, mime, bytes, srcFile); }
                finally { e.PopFrame(); }
            }
            finally { if (attached) Jni.Detach(); }
#else
            throw new PlatformNotSupportedException();
#endif
        }

#if QUEST
        private static string StoreOn(Jni.Env e, bool video, string name, string mime, byte[] bytes, string srcFile)
        {
            var at = e.Class("android/app/ActivityThread");
            var app = e.CallStaticObject(at, e.StaticMethod(at, "currentApplication", "()Landroid/app/Application;"), "currentApplication");
            if (app == IntPtr.Zero) throw new Exception("no Application yet");
            var ctx = e.Class("android/content/Context");
            var cr = e.CallObject(app, e.Method(ctx, "getContentResolver", "()Landroid/content/ContentResolver;"), "getContentResolver");
            if (cr == IntPtr.Zero) throw new Exception("no ContentResolver");

            var media = e.Class(video ? "android/provider/MediaStore$Video$Media" : "android/provider/MediaStore$Images$Media");
            var baseUri = e.StaticObject(media, "EXTERNAL_CONTENT_URI", "Landroid/net/Uri;");

            var cvC = e.Class("android/content/ContentValues");
            var cvNew = e.Method(cvC, "<init>", "()V");
            var putS = e.Method(cvC, "put", "(Ljava/lang/String;Ljava/lang/String;)V");
            var putI = e.Method(cvC, "put", "(Ljava/lang/String;Ljava/lang/Integer;)V");
            var intC = e.Class("java/lang/Integer");
            var intOf = e.StaticMethod(intC, "valueOf", "(I)Ljava/lang/Integer;");
            var one = e.CallStaticObject(intC, intOf, "Integer.valueOf", 1L);
            var zero = e.CallStaticObject(intC, intOf, "Integer.valueOf", 0L);

            var crC = e.Class("android/content/ContentResolver");
            var insert = e.Method(crC, "insert", "(Landroid/net/Uri;Landroid/content/ContentValues;)Landroid/net/Uri;");
            var openFd = e.Method(crC, "openFileDescriptor", "(Landroid/net/Uri;Ljava/lang/String;)Landroid/os/ParcelFileDescriptor;");
            var update = e.Method(crC, "update", "(Landroid/net/Uri;Landroid/content/ContentValues;Ljava/lang/String;[Ljava/lang/String;)I");
            var delete = e.Method(crC, "delete", "(Landroid/net/Uri;Ljava/lang/String;[Ljava/lang/String;)I");
            var pfdC = e.Class("android/os/ParcelFileDescriptor");
            var detachFd = e.Method(pfdC, "detachFd", "()I");

            var kName = e.Str("_display_name"); var kMime = e.Str("mime_type"); var kRel = e.Str("relative_path"); var kPend = e.Str("is_pending");
            var vName = e.Str(name); var vMime = e.Str(mime); var vMode = e.Str("w");

            Exception last = null;
            foreach (var rel in video ? VideoDirs : PhotoDirs)
            {
                var cv = e.New(cvC, cvNew);
                e.CallVoid(cv, putS, "put name", Jni.O(kName), Jni.O(vName));
                e.CallVoid(cv, putS, "put mime", Jni.O(kMime), Jni.O(vMime));
                var vRel = e.Str(rel);
                e.CallVoid(cv, putS, "put path", Jni.O(kRel), Jni.O(vRel));
                e.CallVoid(cv, putI, "put pending", Jni.O(kPend), Jni.O(one));

                IntPtr item;
                try { item = e.CallObject(cr, insert, "insert " + rel, Jni.O(baseUri), Jni.O(cv)); }
                catch (Exception ex) { last = ex; Mod.Log.Msg($"Gallery: {rel} not allowed here, trying the next folder"); continue; }
                finally { e.Delete(cv); e.Delete(vRel); }
                if (item == IntPtr.Zero) { last = new Exception("insert returned null for " + rel); continue; }

                try
                {
                    var pfd = e.CallObject(cr, openFd, "openFileDescriptor", Jni.O(item), Jni.O(vMode));
                    if (pfd == IntPtr.Zero) throw new Exception("openFileDescriptor returned null");
                    int fd = e.CallInt(pfd, detachFd, "detachFd");
                    if (fd < 0) throw new Exception("bad fd");
                    using (var dst = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)fd, true), FileAccess.Write, 1 << 16))
                    {
                        if (bytes != null) dst.Write(bytes, 0, bytes.Length);
                        else using (var src = new FileStream(srcFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20)) src.CopyTo(dst, 1 << 20);
                        dst.Flush(true);
                    }
                    var done = e.New(cvC, cvNew);
                    e.CallVoid(done, putI, "put pending", Jni.O(kPend), Jni.O(zero));
                    e.CallInt(cr, update, "update", Jni.O(item), Jni.O(done), 0L, 0L);
                    return rel + name;
                }
                catch (Exception ex)
                {
                    try { e.CallInt(cr, delete, "delete", Jni.O(item), 0L, 0L); } catch { }
                    last = ex;
                    Mod.Log.Msg($"Gallery: writing to {rel} failed ({ex.Message})");
                }
            }
            throw last ?? new Exception("no gallery folder accepted the file");
        }
#endif
    }
}
