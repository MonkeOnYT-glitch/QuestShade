using System;
using System.IO;
using System.Text;

namespace QuestShadeUpdater
{
    internal static class Http
    {
        private const string UserAgent = "QuestShadeUpdater";

        public static string GetString(string url) => Encoding.UTF8.GetString(GetBytes(url, "application/vnd.github+json"));

        private static IntPtr _vm;

        public static unsafe byte[] GetBytes(string url, string accept = null)
        {
            if (_vm == IntPtr.Zero)
            {
                if (!System.Runtime.InteropServices.NativeLibrary.TryLoad("libnativehelper.so", out var lib) ||
                    !System.Runtime.InteropServices.NativeLibrary.TryGetExport(lib, "JNI_GetCreatedJavaVMs", out var fn))
                    throw new Exception("Android's Java isn't reachable");
                IntPtr vm; int n;
                if (((delegate* unmanaged<IntPtr*, int, int*, int>)fn)(&vm, 1, &n) != 0 || n < 1) throw new Exception("no Java VM");
                _vm = vm;
            }
            var vmFns = *(IntPtr**)_vm;
            IntPtr envPtr;
            bool attached = false;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int, int>)vmFns[6])(_vm, &envPtr, 0x00010006) != 0 || envPtr == IntPtr.Zero)
            {
                if (((delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int>)vmFns[4])(_vm, &envPtr, IntPtr.Zero) != 0) throw new Exception("can't attach to Java");
                attached = true;
            }
            var e = new Env(envPtr);
            e.PushFrame(64);
            try
            {
                var urlCls = e.Class("java/net/URL");
                var u = e.New(urlCls, e.Method(urlCls, "<init>", "(Ljava/lang/String;)V"), (long)e.Str(url));
                var conn = e.CallObject(u, e.Method(urlCls, "openConnection", "()Ljava/net/URLConnection;"));
                var hc = e.Class("java/net/HttpURLConnection");
                e.CallVoid(conn, e.Method(hc, "setConnectTimeout", "(I)V"), 5000);
                e.CallVoid(conn, e.Method(hc, "setReadTimeout", "(I)V"), 6000);
                var setProp = e.Method(hc, "setRequestProperty", "(Ljava/lang/String;Ljava/lang/String;)V");
                e.CallVoid(conn, setProp, (long)e.Str("User-Agent"), (long)e.Str(UserAgent));
                if (accept != null) e.CallVoid(conn, setProp, (long)e.Str("Accept"), (long)e.Str(accept));
                int code = e.CallInt(conn, e.Method(hc, "getResponseCode", "()I"));
                if (code != 200) throw new Exception($"GitHub answered {code}");

                var stream = e.CallObject(conn, e.Method(hc, "getInputStream", "()Ljava/io/InputStream;"));
                var isCls = e.Class("java/io/InputStream");
                var read = e.Method(isCls, "read", "([B)I");
                const int Chunk = 64 * 1024;
                var jbuf = e.NewBytes(Chunk);
                var buf = new byte[Chunk];
                using var ms = new MemoryStream();
                while (true)
                {
                    int got = e.CallInt(stream, read, (long)jbuf);
                    if (got < 0) break;
                    if (got == 0) continue;
                    e.CopyBytes(jbuf, got, buf);
                    ms.Write(buf, 0, got);
                    if (ms.Length > 32 * 1024 * 1024) throw new Exception("download too big");
                }
                e.CallVoid(stream, e.Method(isCls, "close", "()V"));
                e.CallVoid(conn, e.Method(hc, "disconnect", "()V"));
                return ms.ToArray();
            }
            finally
            {
                e.PopFrame();
                if (attached) ((delegate* unmanaged<IntPtr, int>)vmFns[5])(_vm);
            }
        }

        private readonly unsafe struct Env
        {
            private readonly IntPtr _e;
            private readonly IntPtr* _f;
            public Env(IntPtr e) { _e = e; _f = *(IntPtr**)e; }

            private void Check(string what)
            {
                if (((delegate* unmanaged<IntPtr, byte>)_f[228])(_e) == 0) return;
                ((delegate* unmanaged<IntPtr, void>)_f[17])(_e);
                throw new Exception("Java error in " + what);
            }
            private static byte[] Z(string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); return b; }

            public void PushFrame(int n) { ((delegate* unmanaged<IntPtr, int, int>)_f[19])(_e, n); }
            public void PopFrame() { ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)_f[20])(_e, IntPtr.Zero); }
            public IntPtr Class(string name)
            {
                var n = Z(name);
                fixed (byte* p = n) { var r = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)_f[6])(_e, p); Check(name); return r; }
            }
            public IntPtr Method(IntPtr cls, string name, string sig)
            {
                var a = Z(name); var b = Z(sig);
                fixed (byte* pa = a) fixed (byte* pb = b) { var r = ((delegate* unmanaged<IntPtr, IntPtr, byte*, byte*, IntPtr>)_f[33])(_e, cls, pa, pb); Check(name); return r; }
            }
            public IntPtr Str(string s)
            {
                var b = Z(s);
                fixed (byte* p = b) { var r = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)_f[167])(_e, p); Check("string"); return r; }
            }
            public IntPtr New(IntPtr cls, IntPtr ctor, params long[] args)
            {
                fixed (long* a = args) { var r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, long*, IntPtr>)_f[30])(_e, cls, ctor, a); Check("new"); return r; }
            }
            public IntPtr CallObject(IntPtr o, IntPtr m, params long[] args)
            {
                if (args.Length == 0) args = new long[1];
                fixed (long* a = args) { var r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, long*, IntPtr>)_f[36])(_e, o, m, a); Check("call"); return r; }
            }
            public int CallInt(IntPtr o, IntPtr m, params long[] args)
            {
                if (args.Length == 0) args = new long[1];
                fixed (long* a = args) { var r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, long*, int>)_f[51])(_e, o, m, a); Check("call"); return r; }
            }
            public void CallVoid(IntPtr o, IntPtr m, params long[] args)
            {
                if (args.Length == 0) args = new long[1];
                fixed (long* a = args) { ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, long*, void>)_f[63])(_e, o, m, a); Check("call"); }
            }
            public IntPtr NewBytes(int n) { var r = ((delegate* unmanaged<IntPtr, int, IntPtr>)_f[176])(_e, n); Check("byte[]"); return r; }
            public void CopyBytes(IntPtr arr, int n, byte[] into)
            {
                fixed (byte* p = into) ((delegate* unmanaged<IntPtr, IntPtr, int, int, byte*, void>)_f[200])(_e, arr, 0, n, p);
                Check("read");
            }
        }
    }
}
