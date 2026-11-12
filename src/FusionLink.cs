using System;
using System.Reflection;
using UnityEngine;

namespace QuestShade
{
    internal static class FusionLink
    {
        private static PropertyInfo _hasServer;
        private static bool _searched, _present;
        private static float _nextSearch, _nextPoll;

        public static bool InLobby { get; private set; }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            if (!_present)
            {
                if (_searched && now < _nextSearch) return;
                _searched = true; _nextSearch = now + 15f;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "LabFusion") continue;
                    _hasServer = asm.GetType("LabFusion.Network.NetworkInfo")?.GetProperty("HasServer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    _present = _hasServer != null;
                    if (_present) Mod.Log.Msg("Fusion found: camera and mic work in lobbies");
                    break;
                }
                if (!_present) return;
            }
            if (now < _nextPoll) return;
            _nextPoll = now + 1f;
            try { InLobby = (bool)_hasServer.GetValue(null); }
            catch { InLobby = false; _present = false; }
        }
    }
}
