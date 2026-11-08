using System;
using System.Runtime.InteropServices;
using System.Text;

namespace QuestShade
{
    internal static unsafe class Jni
    {
        private static IntPtr _vm;
        private static bool _tried;

        public static bool Init()
        {
            if (_tried) return _vm != IntPtr.Zero;
            _tried = true;
            try
            {
                if (!NativeLibrary.TryLoad("libnativehelper.so", out var lib)) { Mod.Log.Warning("Gallery: libnativehelper not loadable"); return false; }
                if (!NativeLibrary.TryGetExport(lib, "JNI_GetCreatedJavaVMs", out var fn)) { Mod.Log.Warning("Gallery: JNI_GetCreatedJavaVMs missing"); return false; }
                IntPtr vm = IntPtr.Zero; int n = 0;
                int r = ((delegate* unmanaged<IntPtr*, int, int*, int>)fn)(&vm, 1, &n);
                if (r != 0 || n < 1 || vm == IntPtr.Zero) { Mod.Log.Warning($"Gallery: no Java VM ({r}, {n})"); return false; }
                _vm = vm;
                return true;
            }
            catch (Exception e) { Mod.Log.Warning("Gallery: JNI unavailable (" + e.Message + ")"); return false; }
        }

        private static IntPtr VmFn(int i) => (*(IntPtr**)_vm)[i];

        public static Env Attach(out bool attached)
        {
            if (_vm == IntPtr.Zero) throw new Exception("no Java VM");
            IntPtr env = IntPtr.Zero;
            attached = false;
            if (((delegate* unmanaged<IntPtr, IntPtr*, int, int>)VmFn(6))(_vm, &env, 0x00010006) == 0 && env != IntPtr.Zero) return new Env(env);
            env = IntPtr.Zero;
            int r = ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr, int>)VmFn(4))(_vm, &env, IntPtr.Zero);
            if (r != 0 || env == IntPtr.Zero) throw new Exception("AttachCurrentThread failed " + r);
            attached = true;
            return new Env(env);
        }

        public static void Detach() { try { ((delegate* unmanaged<IntPtr, int>)VmFn(5))(_vm); } catch { } }

        private static byte[] Z(string s) { var b = new byte[Encoding.UTF8.GetByteCount(s) + 1]; Encoding.UTF8.GetBytes(s, 0, s.Length, b, 0); return b; }

        public readonly struct Env
        {
            private readonly IntPtr _e;
            private readonly IntPtr* _f;
            public Env(IntPtr e) { _e = e; _f = *(IntPtr**)e; }

            private const int FindClassI = 6, ExceptionDescribeI = 16, ExceptionClearI = 17, PushLocalFrameI = 19, PopLocalFrameI = 20,
                DeleteLocalRefI = 23, NewObjectAI = 30, GetMethodIDI = 33, CallObjectMethodAI = 36, CallIntMethodAI = 51,
                CallVoidMethodAI = 63, GetStaticMethodIDI = 113, CallStaticObjectMethodAI = 116, GetStaticFieldIDI = 144,
                GetStaticObjectFieldI = 145, NewStringUTFI = 167, ExceptionCheckI = 228;

            private void Check(string what)
            {
                if (((delegate* unmanaged<IntPtr, byte>)_f[ExceptionCheckI])(_e) == 0) return;
                ((delegate* unmanaged<IntPtr, void>)_f[ExceptionDescribeI])(_e);
                ((delegate* unmanaged<IntPtr, void>)_f[ExceptionClearI])(_e);
                throw new JavaException(what);
            }

            private IntPtr NotNull(IntPtr p, string what) { if (p == IntPtr.Zero) throw new JavaException(what + " returned null"); return p; }

            public void PushFrame(int n) { ((delegate* unmanaged<IntPtr, int, int>)_f[PushLocalFrameI])(_e, n); Check("PushLocalFrame"); }
            public void PopFrame() { ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)_f[PopLocalFrameI])(_e, IntPtr.Zero); }
            public void Delete(IntPtr o) { if (o != IntPtr.Zero) ((delegate* unmanaged<IntPtr, IntPtr, void>)_f[DeleteLocalRefI])(_e, o); }

            public IntPtr Class(string name)
            {
                var n = Z(name);
                fixed (byte* p = n) { var r = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)_f[FindClassI])(_e, p); Check("FindClass " + name); return NotNull(r, name); }
            }

            public IntPtr Method(IntPtr cls, string name, string sig)
            {
                var a = Z(name); var b = Z(sig);
                fixed (byte* pa = a) fixed (byte* pb = b) { var r = ((delegate* unmanaged<IntPtr, IntPtr, byte*, byte*, IntPtr>)_f[GetMethodIDI])(_e, cls, pa, pb); Check(name + sig); return NotNull(r, name); }
            }

            public IntPtr StaticMethod(IntPtr cls, string name, string sig)
            {
                var a = Z(name); var b = Z(sig);
                fixed (byte* pa = a) fixed (byte* pb = b) { var r = ((delegate* unmanaged<IntPtr, IntPtr, byte*, byte*, IntPtr>)_f[GetStaticMethodIDI])(_e, cls, pa, pb); Check(name + sig); return NotNull(r, name); }
            }

            public IntPtr StaticObject(IntPtr cls, string name, string sig)
            {
                var a = Z(name); var b = Z(sig);
                IntPtr fid;
                fixed (byte* pa = a) fixed (byte* pb = b) { fid = ((delegate* unmanaged<IntPtr, IntPtr, byte*, byte*, IntPtr>)_f[GetStaticFieldIDI])(_e, cls, pa, pb); }
                Check(name); NotNull(fid, name);
                var r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)_f[GetStaticObjectFieldI])(_e, cls, fid);
                Check(name); return NotNull(r, name);
            }

            public IntPtr Str(string s)
            {
                var b = Z(s);
                fixed (byte* p = b) { var r = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)_f[NewStringUTFI])(_e, p); Check("NewStringUTF"); return NotNull(r, "NewStringUTF"); }
            }

            public IntPtr New(IntPtr cls, IntPtr ctor, params long[] args)
            {
                fixed (long* a = args) { var r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, long*, IntPtr>)_f[NewObjectAI])(_e, cls, ctor, a); Check("new"); return NotNull(r, "new"); }
            }

            public IntPtr CallObject(IntPtr obj, IntPtr mid, string what, params long[] args)
            {
                fixed (long* a = args) { var r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, long*, IntPtr>)_f[CallObjectMethodAI])(_e, obj, mid, a); Check(what); return r; }
            }

            public int CallInt(IntPtr obj, IntPtr mid, string what, params long[] args)
            {
                fixed (long* a = args) { var r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, long*, int>)_f[CallIntMethodAI])(_e, obj, mid, a); Check(what); return r; }
            }

            public void CallVoid(IntPtr obj, IntPtr mid, string what, params long[] args)
            {
                fixed (long* a = args) { ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, long*, void>)_f[CallVoidMethodAI])(_e, obj, mid, a); Check(what); }
            }

            public IntPtr CallStaticObject(IntPtr cls, IntPtr mid, string what, params long[] args)
            {
                fixed (long* a = args) { var r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, long*, IntPtr>)_f[CallStaticObjectMethodAI])(_e, cls, mid, a); Check(what); return r; }
            }
        }

        public sealed class JavaException : Exception { public JavaException(string m) : base("Java: " + m) { } }

        public static long O(IntPtr p) => (long)p;
    }
}
