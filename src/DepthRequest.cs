using UnityEngine.Rendering.Universal;

namespace QuestShade
{
    // Scene depth on the headset camera is expensive on Quest: the game draws every object twice (depth first, then colour),
    // which can halve fps on big maps. Shadows and headset depth of field both need it, so they ask through here and it
    // stays on only while at least one of them does. Otherwise the camera goes back to the game's own setting.
    internal static class DepthRequest
    {
        public const int DepthOfField = 1, Shadows = 2;
        private static UniversalAdditionalCameraData _d;
        private static System.IntPtr _ptr;
        private static bool _base;
        private static int _mask;

        public static bool Wanted(int who) => (_mask & who) != 0;

        public static void Set(UniversalAdditionalCameraData d, int who, bool on)
        {
            if (d == null) return;
            try
            {
                if (_d == null || _ptr != d.Pointer) { _d = d; _ptr = d.Pointer; _base = d.requiresDepthTexture; _mask = 0; }
                _mask = on ? _mask | who : _mask & ~who;
                bool want = _base || _mask != 0;
                if (d.requiresDepthTexture != want) d.requiresDepthTexture = want;
            }
            catch { }
        }
    }
}
