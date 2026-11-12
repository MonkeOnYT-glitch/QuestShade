using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace QuestShade
{
    internal static class SunShafts
    {
        public static Func<int> PlayerLayers;

        private const int Grid = 16, Cells = Grid * Grid, MaxBeams = 56, ProbesPerFrame = 12;
        private const float CellSize = 1.1f, Top = 5f, MaxFloor = 9f, HalfWidth = 0.55f, Refresh = 2f;

        private struct Cell { public int X, Z; public bool Known, Lit; public float Floor, Glow, At; }

        private static readonly Cell[] _cells = new Cell[Cells];
        private static readonly Vector3[] _v = new Vector3[MaxBeams * 8];
        private static readonly Vector3[] _n = new Vector3[MaxBeams * 8];
        private static readonly Color32[] _c = new Color32[MaxBeams * 8];
        private static Mesh _mesh;
        private static Material _matHead, _matLens;
        private static GameObject _head, _lens;
        private static Light _sun;
        private static float _sunAt, _height = float.NaN, _stale;
        private static Vector3 _lastDown;
        private static int _cursor, _drawn, _mask;
        private static bool _failed, _logged;
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        public static void OnLevel()
        {
            _sun = null; _sunAt = 0f; _height = float.NaN; _logged = false;
            for (int i = 0; i < Cells; i++) _cells[i].Known = false;
            Show(false, false);
        }

        public static void Tick(float strength, Transform head)
        {
            if (_failed || strength <= 0.01f || head == null || ShaderPack.Shaft == null) { Show(false, false); return; }
            try { Run(strength, head); }
            catch (Exception e) { _failed = true; Show(false, false); Mod.Log.Warning("Sun shafts off after an error: " + e.Message); }
        }

        private static void Run(float strength, Transform head)
        {
            float now = Time.unscaledTime;
            if (now >= _sunAt) { _sunAt = now + 5f; if (_sun == null || !_sun.isActiveAndEnabled) _sun = FindSun(); }
            if (_sun == null || !_sun.isActiveAndEnabled || _sun.intensity < 0.05f) { Show(false, false); return; }
            Vector3 down = _sun.transform.forward;
            if (down.y > -0.1f) { Show(false, false); return; }
            Vector3 up = -down;
            Ensure();

            int pl = 0; try { pl = PlayerLayers != null ? PlayerLayers() : 0; } catch { }
            int hl = FlashBeams.HeadsetLayer != null ? FlashBeams.HeadsetLayer() : -1;
            int ll = FlashBeams.LensLayer != null ? FlashBeams.LensLayer() : -1;
            _mask = ~(1 << 2) & ~pl & (hl >= 0 ? ~(1 << hl) : ~0) & (ll >= 0 ? ~(1 << ll) : ~0);

            var hp = head.position;
            float h = hp.y - 0.25f;
            if (float.IsNaN(_height) || Mathf.Abs(h - _height) > 1.2f) { _height = h; for (int i = 0; i < Cells; i++) _cells[i].Known = false; }
            if (Vector3.Dot(down, _lastDown) < 0.9995f) { _lastDown = down; _stale = now; }

            int cx = Mathf.FloorToInt(hp.x / CellSize) - Grid / 2, cz = Mathf.FloorToInt(hp.z / CellSize) - Grid / 2;
            for (int k = 0, budget = ProbesPerFrame; k < Cells && budget > 0; k++)
            {
                _cursor = (_cursor + 1) % Cells;
                int x = cx + _cursor % Grid, z = cz + _cursor / Grid;
                ref var c = ref _cells[Slot(x, z)];
                if (Here(c, x, z) && c.At >= _stale && now - c.At < Refresh) continue;
                budget--;
                Probe(ref c, x, z, up, now);
            }

            int known = 0, shaded = 0;
            for (int j = 0; j < Grid; j++)
            for (int i = 0; i < Grid; i++)
            {
                var c = _cells[Slot(cx + i, cz + j)];
                if (!Here(c, cx + i, cz + j)) continue;
                known++; if (!c.Lit) shaded++;
            }
            float cover = known > 30 ? Smooth(0.4f, 0.8f, shaded / (float)known) : 0f;

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            Vector3 a = Vector3.Cross(up, Mathf.Abs(up.y) > 0.95f ? Vector3.forward : Vector3.up).normalized;
            Vector3 b = Vector3.Cross(up, a).normalized;
            int beams = 0;
            for (int j = 0; j < Grid; j++)
            for (int i = 0; i < Grid; i++)
            {
                int x = cx + i, z = cz + j;
                ref var c = ref _cells[Slot(x, z)];
                if (!Here(c, x, z)) continue;
                float target = 0f;
                if (c.Lit && cover > 0f)
                {
                    int around = 0, dark = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        var o = _cells[Slot(x + dx, z + dz)];
                        if (!Here(o, x + dx, z + dz)) continue;
                        around++; if (!o.Lit) dark++;
                    }
                    if (around > 0) target = cover * Smooth(0.25f, 0.7f, dark / (float)around);
                }
                c.Glow = Mathf.MoveTowards(c.Glow, target, dt * 0.9f);
                if (c.Glow > 0.01f && beams < MaxBeams)
                    Beam(beams++, new Vector3((x + 0.5f) * CellSize, _height, (z + 0.5f) * CellSize), up, a, b, c.Floor, c.Glow);
            }
            for (int i = beams * 8; i < _drawn * 8; i++) { _v[i] = Vector3.zero; _c[i] = new Color32(0, 0, 0, 0); }
            bool any = beams > 0 || _drawn > 0;
            _drawn = beams;
            if (!any) { Show(false, false); return; }

            _mesh.vertices = _v;
            _mesh.normals = _n;
            _mesh.colors32 = _c;

            var sc = _sun.color;
            float amount = Mathf.Clamp(strength, 0f, 3f) * 0.11f * Mathf.Clamp(_sun.intensity, 0.4f, 1.5f);
            var col = new Color(sc.r, sc.g * 0.96f, sc.b * 0.88f, amount);
            _matHead.SetColor(ColorId, col);
            _matLens.SetColor(ColorId, col);
            if (hl >= 0 && _head.layer != hl) _head.layer = hl;
            if (ll >= 0 && _lens.layer != ll) _lens.layer = ll;
            Show(beams > 0, beams > 0 && ll >= 0);
            if (!_logged && beams > 0) { _logged = true; Mod.Log.Msg($"Sun shafts: {beams} beams through gaps ({shaded}/{known} spots in shade, sun '{_sun.name}')"); }
        }

        private static bool Here(Cell c, int x, int z) => c.Known && c.X == x && c.Z == z;
        private static int Slot(int x, int z) => ((x % Grid) + Grid) % Grid + (((z % Grid) + Grid) % Grid) * Grid;
        private static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

        private static void Probe(ref Cell c, int x, int z, Vector3 up, float now)
        {
            if (!Here(c, x, z)) c.Glow = 0f;
            c.X = x; c.Z = z; c.Known = true; c.At = now;
            var p = new Vector3((x + 0.5f) * CellSize, _height, (z + 0.5f) * CellSize);
            c.Lit = !Physics.Raycast(p, up, 160f, _mask, QueryTriggerInteraction.Ignore);
            c.Floor = !c.Lit ? 0f : Physics.Raycast(p, -up, out var hit, MaxFloor, _mask, QueryTriggerInteraction.Ignore) ? hit.distance : MaxFloor;
        }

        private static void Beam(int n, Vector3 at, Vector3 up, Vector3 a, Vector3 b, float floor, float glow)
        {
            Vector3 top = at + up * Top, bottom = at - up * floor;
            byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(glow * 255f), 0, 255);
            int o = n * 8;
            Quad(o, top, bottom, a * HalfWidth, b, g);
            Quad(o + 4, top, bottom, b * HalfWidth, a, g);
        }

        private static void Quad(int o, Vector3 top, Vector3 bottom, Vector3 side, Vector3 normal, byte g)
        {
            _v[o] = top - side; _v[o + 1] = top + side; _v[o + 2] = bottom - side; _v[o + 3] = bottom + side;
            for (int i = 0; i < 4; i++) { _n[o + i] = normal; _c[o + i] = new Color32(255, 255, 255, g); }
        }

        private static void Ensure()
        {
            if (_mesh != null && _head != null && _lens != null) return;
            if (_mesh == null)
            {
                var uv = new Vector2[MaxBeams * 8];
                var tri = new int[MaxBeams * 12];
                for (int q = 0; q < MaxBeams * 2; q++)
                {
                    int v = q * 4, t = q * 6;
                    uv[v] = new Vector2(-1f, 0f); uv[v + 1] = new Vector2(1f, 0f); uv[v + 2] = new Vector2(-1f, 1f); uv[v + 3] = new Vector2(1f, 1f);
                    tri[t] = v; tri[t + 1] = v + 2; tri[t + 2] = v + 1; tri[t + 3] = v + 1; tri[t + 4] = v + 2; tri[t + 5] = v + 3;
                }
                _mesh = new Mesh { name = "QuestShade_SunShafts", hideFlags = HideFlags.HideAndDontSave };
                _mesh.MarkDynamic();
                _mesh.vertices = _v; _mesh.normals = _n; _mesh.colors32 = _c; _mesh.uv = uv; _mesh.triangles = tri;
                _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            }
            if (_matHead == null) { _matHead = new Material(ShaderPack.Shaft) { hideFlags = HideFlags.HideAndDontSave }; ShaderPack.SetStereo(_matHead, true); }
            if (_matLens == null) { _matLens = new Material(ShaderPack.Shaft) { hideFlags = HideFlags.HideAndDontSave }; ShaderPack.SetStereo(_matLens, false); }
            if (_head == null) _head = Make("QuestShade_SunShafts", _matHead);
            if (_lens == null) _lens = Make("QuestShade_SunShaftsLens", _matLens);
        }

        private static GameObject Make(string name, Material mat)
        {
            var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return go;
        }

        private static void Show(bool head, bool lens)
        {
            if (_head != null && _head.activeSelf != head) _head.SetActive(head);
            if (_lens != null && _lens.activeSelf != lens) _lens.SetActive(lens);
        }

        private static Light FindSun()
        {
            Light best = null;
            try { best = RenderSettings.sun; } catch { }
            if (best != null && best.isActiveAndEnabled) return best;
            best = null; float top = 0.05f;
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                if (l != null && l.type == LightType.Directional && l.isActiveAndEnabled && l.intensity > top) { top = l.intensity; best = l; }
            return best;
        }
    }
}
