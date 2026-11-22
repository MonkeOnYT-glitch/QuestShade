using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace QuestShade
{
    // Camcorder zoom sound: a short motor whirr with a click at the start, made in code (no sound files).
    // Played from the camera so it's heard in the headset and lands in recordings with the game audio.
    internal static class ZoomSfx
    {
        private static AudioSource _src;
        private static AudioClip _in, _out;
        private static bool _failed;

        // Crash latch: written before the sound is first set up, deleted once it plays. If the game dies in between,
        // the file is still there next launch and the zoom sound stays off instead of crashing again.
        private static string LatchPath => System.IO.Path.Combine(Config.Root ?? "", "zoom_sound_crashed.txt");
        private static bool _checked, _ready;

        public static void Play(bool zoomIn, Vector3 at, float volume)
        {
            if (_failed || volume <= 0f) return;
            if (!_checked)
            {
                _checked = true;
                try { if (System.IO.File.Exists(LatchPath)) { _failed = true; Mod.Log.Warning("Zoom sound: off (it crashed the game last time; delete zoom_sound_crashed.txt to try again)"); return; } } catch { }
            }
            try
            {
                if (!_ready)
                {
                    try { System.IO.File.WriteAllText(LatchPath, "zoom sound setup started"); } catch { }
                    Mod.Log.Msg("Zoom sound: making audio source");
                    var go = new GameObject("QuestShade_ZoomSfx");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _src = go.AddComponent<AudioSource>();
                    _src.playOnAwake = false;
                    _src.spatialBlend = 0.6f; // mostly from the camera, still clearly audible
                    _src.minDistance = 0.5f;
                    _src.maxDistance = 6f;
                    _src.rolloffMode = AudioRolloffMode.Linear;
                    Mod.Log.Msg("Zoom sound: making clips");
                    _in = Make("QuestShade_ZoomIn", 1f);
                    _out = Make("QuestShade_ZoomOut", 0.86f); // zooming out runs the motor a little lower
                    Mod.Log.Msg("Zoom sound: first play");
                }
                _src.transform.position = at;
                _src.PlayOneShot(zoomIn ? _in : _out, Mathf.Clamp01(volume));
                if (!_ready) { _ready = true; try { System.IO.File.Delete(LatchPath); } catch { } Mod.Log.Msg("Zoom sound: ready"); }
            }
            catch (Exception e) { _failed = true; try { System.IO.File.Delete(LatchPath); } catch { } Mod.Log.Warning("Zoom sound unavailable: " + e.Message); }
        }

        private static AudioClip Make(string name, float pitch)
        {
            const int rate = 44100;
            const float len = 0.26f;
            int n = (int)(rate * len);
            var data = new Il2CppStructArray<float>(n);
            var rng = new System.Random(1234);
            float lp = 0f, phase = 0f, phase2 = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                // Envelope: quick rise, steady whirr, fade out.
                float env = Mathf.Min(1f, t / 0.015f) * Mathf.Clamp01((len - t) / 0.07f);
                // Motor: a buzzy tone that spins up slightly, plus a gear-noise texture.
                float f = (210f + 90f * Mathf.Min(1f, t / 0.12f)) * pitch;
                phase += f / rate; phase2 += f * 2.01f / rate;
                float tone = 0.55f * Mathf.Sin(phase * 2f * Mathf.PI) + 0.25f * Mathf.Sin(phase2 * 2f * Mathf.PI)
                           + 0.18f * (2f * (phase - Mathf.Floor(phase)) - 1f); // a little saw for the motor buzz
                float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
                lp += 0.18f * (noise - lp); // softened gear hiss
                float gear = lp * (0.6f + 0.4f * Mathf.Sin(t * 2f * Mathf.PI * 38f * pitch)); // ticking gear texture
                // Click at the very start (the zoom ring engaging).
                float click = t < 0.006f ? (float)(rng.NextDouble() * 2.0 - 1.0) * (1f - t / 0.006f) * 0.9f : 0f;
                data[i] = Mathf.Clamp((tone * 0.35f + gear * 0.45f) * env + click, -1f, 1f) * 0.8f;
            }
            var clip = AudioClip.Create(name, n, 1, rate, false);
            clip.SetData(data, 0);
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return clip;
        }
    }
}
