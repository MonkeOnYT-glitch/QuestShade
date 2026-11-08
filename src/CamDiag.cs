using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace QuestShade
{
    internal static class CamDiag
    {
        private static readonly HashSet<string> Seen = new HashSet<string>();
        private static float _until;
        private static string FilePath => Path.Combine(Config.Root, "camera_debug.txt");

        public static void Begin()
        {
            Seen.Clear();
            _until = Time.unscaledTime + 4f;
            try { File.WriteAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] camera opening\n"); } catch { }
        }

        public static void Step(string what)
        {
            if (Time.unscaledTime > _until || !Seen.Add(what)) return;
            try { File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] f{Time.frameCount} {what}\n"); } catch { }
        }
    }
}
