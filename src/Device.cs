using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace QuestShade
{
    internal static class Device
    {
        public enum Model { PC, Quest3, Quest3S, QuestPro, Quest2, UnknownQuest }

        private static Model? _model;
        public static Model Current => _model ??= Detect();

        public static string Name => Current switch
        {
            Model.Quest3 => "Quest 3", Model.Quest3S => "Quest 3S", Model.QuestPro => "Quest Pro",
            Model.Quest2 => "Quest 2", Model.UnknownQuest => "Quest (unknown model)", _ => "PC",
        };

        public static long RecordPixelBudget => Current switch
        {
            Model.Quest3 => 1920L * 1080,
            Model.Quest3S or Model.QuestPro => 1280L * 720,
            Model.PC => 3840L * 2160,
            _ => 960L * 540,
        };

        [DllImport("libc", EntryPoint = "__system_property_get")]
        private static extern int SystemPropertyGet(string name, StringBuilder value);

        private static Model Detect()
        {
            if (Application.platform != RuntimePlatform.Android) return Model.PC;
            string dev = "";
            try { var sb = new StringBuilder(128); SystemPropertyGet("ro.product.device", sb); dev = sb.ToString().Trim().ToLowerInvariant(); }
            catch { }
            var m = dev switch
            {
                "eureka" => Model.Quest3,
                "panther" => Model.Quest3S,
                "seacliff" => Model.QuestPro,
                "hollywood" or "miramar" => Model.Quest2,
                _ => Model.UnknownQuest,
            };
            Mod.Log.Msg($"Headset: {(m == Model.UnknownQuest ? $"unknown ('{dev}')" : m.ToString())}");
            return m;
        }
    }
}
