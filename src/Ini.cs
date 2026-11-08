using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace QuestShade
{
    internal sealed class Ini
    {
        private readonly Dictionary<string, Dictionary<string, string>> _data =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static Ini Parse(string text)
        {
            var ini = new Ini();
            var section = ini.GetOrAdd("");
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("//")) continue;
                int sc = line.IndexOf(';');
                if (sc >= 0) line = line.Substring(0, sc).Trim();
                if (line.Length == 0) continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = ini.GetOrAdd(line.Substring(1, line.Length - 2).Trim());
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                section[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim().Trim('"');
            }
            return ini;
        }

        private Dictionary<string, string> GetOrAdd(string name)
        {
            if (!_data.TryGetValue(name, out var d))
            {
                d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _data[name] = d;
            }
            return d;
        }

        public bool Has(string section) => _data.ContainsKey(section);

        public Dictionary<string, string> Section(string name)
            => _data.TryGetValue(name, out var d) ? new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Ini WithFallback(Ini from, params string[] sections)
        {
            var o = new Ini();
            foreach (var kv in _data) o._data[kv.Key] = kv.Value;
            foreach (var s in sections)
                if (!Has(s) && from._data.TryGetValue(s, out var d)) o._data[s] = d;
            return o;
        }

        public bool Enabled(string section) => Has(section) && Bool(section, "Enabled", true);

        public string Str(string s, string k, string def = null)
            => _data.TryGetValue(s, out var d) && d.TryGetValue(k, out var v) && v.Length > 0 ? v : def;

        public bool HasKey(string s, string k) => Str(s, k) != null;

        public float Float(string s, string k, float def)
        {
            var v = Str(s, k);
            return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
        }

        public int Int(string s, string k, int def)
        {
            var v = Str(s, k);
            return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : def;
        }

        public bool Bool(string s, string k, bool def)
        {
            var v = Str(s, k);
            if (v == null) return def;
            switch (v.ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on": return true;
                case "0": case "false": case "no": case "off": return false;
                default: return def;
            }
        }

        public Color Color(string s, string k, Color def)
        {
            var v = Str(s, k);
            if (v == null) return def;
            if (v[0] == '#')
            {
                var hex = v.Substring(1);
                if ((hex.Length == 6 || hex.Length == 8) &&
                    uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h))
                {
                    if (hex.Length == 6) h = (h << 8) | 0xFF;
                    return new Color(((h >> 24) & 0xFF) / 255f, ((h >> 16) & 0xFF) / 255f, ((h >> 8) & 0xFF) / 255f, (h & 0xFF) / 255f);
                }
                return def;
            }
            var f = Floats(v);
            if (f.Length < 3) return def;
            float scale = (f[0] > 1f || f[1] > 1f || f[2] > 1f) ? 1f / 255f : 1f;
            return new Color(f[0] * scale, f[1] * scale, f[2] * scale, f.Length > 3 ? f[3] * scale : 1f);
        }

        public Vector4 Vec4(string s, string k, Vector4 def)
        {
            var v = Str(s, k);
            if (v == null) return def;
            var f = Floats(v);
            return new Vector4(
                f.Length > 0 ? f[0] : def.x, f.Length > 1 ? f[1] : def.y,
                f.Length > 2 ? f[2] : def.z, f.Length > 3 ? f[3] : def.w);
        }

        public Vector2 Vec2(string s, string k, Vector2 def)
        {
            var v4 = Vec4(s, k, new Vector4(def.x, def.y, 0, 0));
            return new Vector2(v4.x, v4.y);
        }

        public Vector3 Vec3(string s, string k, Vector3 def)
        {
            var v4 = Vec4(s, k, new Vector4(def.x, def.y, def.z, 0));
            return new Vector3(v4.x, v4.y, v4.z);
        }

        private static float[] Floats(string v)
        {
            var parts = v.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<float>(4);
            foreach (var p in parts)
                if (float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) list.Add(f);
            return list.ToArray();
        }
    }
}
