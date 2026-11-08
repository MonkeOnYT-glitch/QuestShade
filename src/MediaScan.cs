using System;


namespace QuestShade
{
    internal static class MediaScan
    {
        private static bool _broken;
        public static string Notice;

        public static string QuestName(string ext, DateTime t, string tag = null)
            => $"com.StressLevelZero.BONELAB-{t:yyyyMMdd-HHmmss}{(t.Millisecond > 0 && ext == "png" ? "-" + t.Millisecond.ToString("000") : "")}{tag}.{ext}";

        public static void Scan(string path)
        {
#if QUEST
            if (_broken || string.IsNullOrEmpty(path)) return;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("/system/bin/am",
                        "broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d file://" + path)
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    using var p = System.Diagnostics.Process.Start(psi);
                    if (p == null) throw new Exception("am didn't start");
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    Mod.Log.Msg("Gallery: added " + System.IO.Path.GetFileName(path));
                }
                catch (Exception e) { _broken = true; Mod.Log.Warning("Gallery: couldn't add to the Quest gallery (" + e.Message + ")"); }
            });
#endif
        }    }
}
