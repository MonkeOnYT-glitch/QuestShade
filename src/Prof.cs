using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace QuestShade
{
    // Where QuestShade's CPU time goes: each big part of the frame is timed, and every 10 seconds the log gets the top
    // costs (average and worst ms per frame). Cheap enough to leave on: two timestamp reads per section.
    internal static class Prof
    {
        private sealed class Slot { public double Sum, Max; }
        private static readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>();
        private static int _frames; private static float _next = 30f;
        private static readonly double ToMs = 1000.0 / Stopwatch.Frequency;

        public static long Now => Stopwatch.GetTimestamp();

        public static void Add(string name, long start)
        {
            double ms = (Stopwatch.GetTimestamp() - start) * ToMs;
            if (!_slots.TryGetValue(name, out var s)) _slots[name] = s = new Slot();
            s.Sum += ms; if (ms > s.Max) s.Max = ms;
        }

        public static void EndFrame()
        {
            _frames++;
            float now = Time.unscaledTime;
            if (now < _next) return;
            _next = now + 10f;
            if (_frames > 0 && _slots.Count > 0)
            {
                var list = new List<KeyValuePair<string, Slot>>(_slots);
                list.Sort((a, b) => b.Value.Sum.CompareTo(a.Value.Sum));
                var sb = new StringBuilder("Perf breakdown (avg / worst ms per frame): ");
                for (int i = 0; i < list.Count && i < 8; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(list[i].Key).Append(' ').Append((list[i].Value.Sum / _frames).ToString("0.00")).Append(" / ").Append(list[i].Value.Max.ToString("0.0"));
                }
                Mod.Log.Msg(sb.ToString());
            }
            _slots.Clear(); _frames = 0;
        }
    }
}
