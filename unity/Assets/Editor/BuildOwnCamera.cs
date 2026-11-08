using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildOwnCamera
{
    const string Dir = "Assets/QuestShade/HandheldCam";
    const float HandleX = -0.39f;
    const float Cx = HandleX * 0.5f;
    const float Scale = 1.2f;
    static Shader _body;

    public static void Build()
    {
        if (Directory.Exists(Dir)) AssetDatabase.DeleteAsset(Dir);
        Directory.CreateDirectory(Dir);
        AssetDatabase.Refresh();
        _body = AssetDatabase.LoadAssetAtPath<Shader>("Assets/QuestShade/QSBody.shader");

        var body = Mat("QS_Body", new Color(0.12f, 0.12f, 0.13f), Color.black);
        var plate = Mat("QS_TopPlate", new Color(0.21f, 0.21f, 0.23f), Color.black);
        var rubber = Mat("QS_GripRubber", new Color(0.065f, 0.065f, 0.07f), Color.black);
        var metal = Mat("QS_Metal", new Color(0.46f, 0.47f, 0.50f), Color.black);
        var darkMetal = Mat("QS_DarkMetal", new Color(0.24f, 0.25f, 0.27f), Color.black);
        var orange = Mat("QS_Orange", new Color(1f, 0.50f, 0.12f), new Color(0.22f, 0.08f, 0f));
        var red = Mat("QS_Red", new Color(0.85f, 0.08f, 0.06f), new Color(0.25f, 0.01f, 0f));
        var glass = Mat("QS_Glass", new Color(0.03f, 0.05f, 0.09f), new Color(0.02f, 0.035f, 0.07f));
        var led = Mat("QS_Led", new Color(0.2f, 1f, 0.3f), new Color(0.2f, 0.9f, 0.3f));

        var root = new GameObject("QSHandheld");
        var rig = new GameObject("Rig"); rig.transform.SetParent(root.transform, false);
        rig.transform.localScale = Vector3.one * Scale;
        GameObject Part(string name, Mesh m, Material[] mats, Vector3 pos, Quaternion rot)
        {
            var g = new GameObject(name); g.transform.SetParent(rig.transform, false);
            g.transform.localPosition = pos; g.transform.localRotation = rot;
            g.AddComponent<MeshFilter>().sharedMesh = m;
            var r = g.AddComponent<MeshRenderer>(); r.sharedMaterials = mats; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return g;
        }
        Mesh Save(Mesh m, string n) { m.name = n; AssetDatabase.CreateAsset(m, $"{Dir}/{n}.asset"); return m; }
        var fwd = Quaternion.Euler(90, 0, 0);
        var side = Quaternion.Euler(0, 0, 90);

        var grip = Save(GripMesh(), "QS_GripMesh");
        Part("QS_Handle", grip, new[] { rubber, orange }, Vector3.zero, Quaternion.identity);
        Part("QS_Handle", grip, new[] { rubber, orange }, new Vector3(HandleX, 0, 0), Quaternion.identity);
        float span = -HandleX;
        var bar = Save(Cylinder(0.0095f, span - 0.03f, 18), "QS_Bar");
        var clamp = Save(RoundedBox(new Vector3(0.036f, 0.026f, 0.034f), 0.006f, 4), "QS_Clamp");
        var screw = Save(Cylinder(0.0035f, 0.003f, 10), "QS_Screw");
        foreach (float y in new[] { 0.079f, -0.079f })
        {
            Part("QS_Bar", bar, new[] { metal }, new Vector3(Cx, y, 0), side);
            foreach (float x in new[] { 0f, HandleX })
            {
                Part("QS_Clamp", clamp, new[] { orange }, new Vector3(x, y, 0), Quaternion.identity);
                Part("QS_Screw", screw, new[] { darkMetal }, new Vector3(x, y, -0.0185f), fwd);
                Part("QS_Screw", screw, new[] { darkMetal }, new Vector3(x, y, 0.0185f), fwd);
            }
        }
        float bodyHalf = 0.20f / 2f, rodLen = span / 2f - bodyHalf;
        var rod = Save(Cylinder(0.0075f, rodLen, 14), "QS_SideRod");
        var collar = Save(Cylinder(0.011f, 0.008f, 16), "QS_Collar");
        Part("QS_SideRod", rod, new[] { metal }, new Vector3(-rodLen / 2f, -0.01f, 0), side);
        Part("QS_SideRod", rod, new[] { metal }, new Vector3(HandleX + rodLen / 2f, -0.01f, 0), side);
        Part("QS_Collar", collar, new[] { darkMetal }, new Vector3(Cx + bodyHalf + 0.004f, -0.01f, 0), side);
        Part("QS_Collar", collar, new[] { darkMetal }, new Vector3(Cx - bodyHalf - 0.004f, -0.01f, 0), side);

        Part("QS_Body", Save(RoundedBox(new Vector3(0.20f, 0.112f, 0.066f), 0.012f, 6), "QS_BodyShell"), new[] { body }, new Vector3(Cx, -0.004f, 0), Quaternion.identity);
        Part("QS_TopPlate", Save(RoundedBox(new Vector3(0.194f, 0.014f, 0.06f), 0.006f, 4), "QS_TopPlateMesh"), new[] { plate }, new Vector3(Cx, 0.06f, 0), Quaternion.identity);
        var rib = Save(RoundedBox(new Vector3(0.0045f, 0.064f, 0.003f), 0.0014f, 2), "QS_Rib");
        for (int k = 0; k < 5; k++) Part("QS_Rib", rib, new[] { rubber }, new Vector3(Cx + 0.064f + k * 0.0075f, -0.006f, 0.0335f), Quaternion.identity);
        var lug = Save(Torus(0.006f, 0.0018f, 14, 8), "QS_StrapLug");
        Part("QS_StrapLug", lug, new[] { metal }, new Vector3(Cx + 0.102f, 0.04f, 0), Quaternion.Euler(0, 90, 0));
        Part("QS_StrapLug", lug, new[] { metal }, new Vector3(Cx - 0.102f, 0.04f, 0), Quaternion.Euler(0, 90, 0));

        float z0 = 0.033f;
        Part("QS_LensMount", Save(Lathe(new[] { (0.036f, 0f), (0.036f, 0.006f), (0.032f, 0.006f) }, 32, 0), "QS_LensMountMesh"), new[] { darkMetal }, new Vector3(Cx, -0.004f, z0), fwd);
        Part("QS_Barrel", Save(Lathe(new[] { (0.030f, 0f), (0.030f, 0.05f) }, 48, 0), "QS_BarrelMesh"), new[] { body }, new Vector3(Cx, -0.004f, z0 + 0.006f), fwd);
        Part("QS_FocusRing", Save(Lathe(new[] { (0.0322f, 0f), (0.0322f, 0.018f) }, 96, 0.0005f), "QS_FocusRingMesh"), new[] { rubber }, new Vector3(Cx, -0.004f, z0 + 0.014f), fwd);
        Part("QS_AccentRing", Save(Lathe(new[] { (0.0315f, 0f), (0.0315f, 0.003f) }, 32, 0), "QS_AccentRingMesh"), new[] { orange }, new Vector3(Cx, -0.004f, z0 + 0.036f), fwd);
        Part("QS_Hood", Save(Lathe(new[] { (0.030f, 0f), (0.031f, 0.008f), (0.036f, 0.018f), (0.0335f, 0.018f), (0.028f, 0.004f) }, 32, 0), "QS_HoodMesh"), new[] { darkMetal }, new Vector3(Cx, -0.004f, z0 + 0.054f), fwd);
        Part("QS_Glass", Save(Lathe(new[] { (0.0255f, 0f), (0f, 0f) }, 32, 0), "QS_GlassMesh"), new[] { glass }, new Vector3(Cx, -0.004f, z0 + 0.06f), fwd);

        Part("QS_Shutter", Save(Lathe(new[] { (0.007f, 0f), (0.007f, 0.006f), (0f, 0.006f) }, 24, 0), "QS_ShutterBase"), new[] { metal }, new Vector3(Cx - 0.055f, 0.067f, 0.012f), Quaternion.identity);
        Part("QS_ShutterCap", Save(Lathe(new[] { (0.0055f, 0f), (0.0055f, 0.002f), (0f, 0.003f) }, 20, 0), "QS_ShutterCapMesh"), new[] { orange }, new Vector3(Cx - 0.055f, 0.073f, 0.012f), Quaternion.identity);
        Part("QS_RecButton", Save(Lathe(new[] { (0.0045f, 0f), (0.0045f, 0.003f), (0f, 0.0038f) }, 18, 0), "QS_RecMesh"), new[] { red }, new Vector3(Cx - 0.033f, 0.067f, 0.014f), Quaternion.identity);
        Part("QS_ModeDial", Save(Lathe(new[] { (0.011f, 0f), (0.011f, 0.008f), (0.009f, 0.0095f), (0f, 0.0095f) }, 72, 0.0004f), "QS_DialMesh"), new[] { darkMetal }, new Vector3(Cx + 0.05f, 0.067f, 0.004f), Quaternion.identity);
        Part("QS_HotShoe", Save(RoundedBox(new Vector3(0.026f, 0.006f, 0.022f), 0.0015f, 2), "QS_HotShoeMesh"), new[] { metal }, new Vector3(Cx, 0.07f, 0), Quaternion.identity);

        Part("QS_ScreenBezel", Save(RoundedBox(new Vector3(0.168f, 0.098f, 0.005f), 0.004f, 3), "QS_BezelMesh"), new[] { darkMetal }, new Vector3(Cx - 0.012f, 0f, -0.0345f), Quaternion.identity);
        Part("QS_BackGlass", Save(ScreenQuad(0.16f, 0.09f), "QS_BackGlassQuad"), new[] { glass }, new Vector3(Cx - 0.012f, 0f, -0.0375f), Quaternion.identity);

        float barTop = 0.079f + 0.0095f, postLen = 0.028f, monH = 0.16f, monY = barTop + postLen + monH / 2f;
        Part("QS_MonitorPost", Save(Cylinder(0.008f, postLen, 16), "QS_MonitorPostMesh"), new[] { metal }, new Vector3(Cx, barTop + postLen / 2f, 0), Quaternion.identity);
        Part("QS_MonitorClamp", Save(RoundedBox(new Vector3(0.03f, 0.016f, 0.026f), 0.004f, 3), "QS_MonitorClampMesh"), new[] { orange }, new Vector3(Cx, barTop - 0.002f, 0), Quaternion.identity);
        Part("QS_Monitor", Save(RoundedBox(new Vector3(0.27f, monH, 0.024f), 0.008f, 4), "QS_MonitorMesh"), new[] { body }, new Vector3(Cx, monY, -0.004f), Quaternion.identity);
        Part("QS_MonitorTrim", Save(RoundedBox(new Vector3(0.274f, 0.006f, 0.026f), 0.0025f, 2), "QS_MonitorTrimMesh"), new[] { orange }, new Vector3(Cx, monY + monH / 2f - 0.002f, -0.004f), Quaternion.identity);
        Part("QS_Screen", Save(ScreenQuad(0.252f, 0.142f), "QS_ScreenQuad"), new Material[] { null }, new Vector3(Cx, monY, -0.0165f), Quaternion.identity);
        var btn = Save(Lathe(new[] { (0.004f, 0f), (0.004f, 0.002f), (0f, 0.0026f) }, 16, 0), "QS_BackButton");
        for (int k = 0; k < 3; k++) Part("QS_BackButton", btn, new[] { rubber }, new Vector3(Cx + 0.084f, 0.02f - k * 0.017f, -0.033f), Quaternion.Euler(-90, 0, 0));
        Part("QS_Led", Save(Lathe(new[] { (0.0018f, 0f), (0f, 0.001f) }, 10, 0), "QS_LedMesh"), new[] { led }, new Vector3(Cx + 0.084f, -0.034f, -0.033f), Quaternion.Euler(-90, 0, 0));

        var lensMarker = new GameObject("QS_Lens"); lensMarker.transform.SetParent(rig.transform, false); lensMarker.transform.localPosition = new Vector3(Cx, -0.004f, z0 + 0.075f);
        var hl = new GameObject("QS_HandleL"); hl.transform.SetParent(rig.transform, false); hl.transform.localPosition = new Vector3(HandleX, 0, 0);
        var bodyParts = new HashSet<string> { "QS_Body", "QS_Rib", "QS_StrapLug", "QS_LensMount", "QS_Barrel", "QS_FocusRing", "QS_AccentRing", "QS_Hood",
            "QS_Glass", "QS_ScreenBezel", "QS_BackGlass", "QS_BackButton", "QS_Led", "QS_Lens", "QS_SideRod", "QS_Collar" };
        var topParts = new HashSet<string> { "QS_TopPlate", "QS_Shutter", "QS_ShutterCap", "QS_RecButton", "QS_ModeDial", "QS_HotShoe" };
        foreach (Transform tr in rig.transform)
        {
            if (bodyParts.Contains(tr.name)) tr.localPosition += Vector3.down * 0.008f;
            else if (topParts.Contains(tr.name)) tr.localPosition += Vector3.down * 0.012f;
        }

        var groups = new Dictionary<Material, List<MeshRenderer>>();
        foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
        {
            if (r.name == "QS_Screen" || r.sharedMaterials.Length != 1 || r.sharedMaterial == null) continue;
            if (!groups.TryGetValue(r.sharedMaterial, out var l)) groups[r.sharedMaterial] = l = new List<MeshRenderer>();
            l.Add(r);
        }
        foreach (var kv in groups)
        {
            if (kv.Value.Count < 2) continue;
            var ci = new List<CombineInstance>();
            foreach (var r in kv.Value)
                ci.Add(new CombineInstance { mesh = r.GetComponent<MeshFilter>().sharedMesh, transform = root.transform.worldToLocalMatrix * r.transform.localToWorldMatrix });
            var merged = new Mesh { name = "QS_Merged_" + kv.Key.name };
            merged.CombineMeshes(ci.ToArray(), true, true);
            AssetDatabase.CreateAsset(merged, $"{Dir}/QS_Merged_{kv.Key.name}.asset");
            var g = new GameObject("QS_Part_" + kv.Key.name); g.transform.SetParent(root.transform, false);
            g.AddComponent<MeshFilter>().sharedMesh = merged;
            var mr = g.AddComponent<MeshRenderer>(); mr.sharedMaterial = kv.Key; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            foreach (var r in kv.Value) Object.DestroyImmediate(r.gameObject);
        }

        PrefabUtility.SaveAsPrefabAsset(root, Dir + "/QSHandheld.prefab");
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        Snap(Dir + "/QSHandheld.prefab", new Vector3(0.35f, 0.25f, -0.55f), "C:/QSBuild/own_back.png");
        Snap(Dir + "/QSHandheld.prefab", new Vector3(-0.3f, 0.15f, 0.55f), "C:/QSBuild/own_front.png");
        Debug.Log("[QS] own camera rig built");
        EditorApplication.Exit(0);
    }

    static Material Mat(string n, Color c, Color em)
    {
        var m = new Material(_body) { name = n };
        m.SetColor("_Color", new Color(c.r, c.g, c.b, 1)); m.SetColor("_Emission", em);
        AssetDatabase.CreateAsset(m, $"{Dir}/{n}.mat");
        return m;
    }

    static Mesh Cylinder(float r, float len, int seg)
    {
        var v = new List<Vector3>(); var t = new List<int>();
        for (int i = 0; i <= seg; i++) { float a = i / (float)seg * Mathf.PI * 2; var d = new Vector3(Mathf.Sin(a) * r, 0, Mathf.Cos(a) * r); v.Add(d + Vector3.down * len / 2); v.Add(d + Vector3.up * len / 2); }
        for (int i = 0; i < seg; i++) { int a0 = i * 2, a1 = a0 + 2; t.AddRange(new[] { a0, a1, a0 + 1, a1, a1 + 1, a0 + 1 }); }
        int bc = v.Count; v.Add(Vector3.down * len / 2); int tc = v.Count; v.Add(Vector3.up * len / 2);
        int rb = v.Count; for (int i = 0; i <= seg; i++) { float a = i / (float)seg * Mathf.PI * 2; v.Add(new Vector3(Mathf.Sin(a) * r, -len / 2, Mathf.Cos(a) * r)); }
        int rt = v.Count; for (int i = 0; i <= seg; i++) { float a = i / (float)seg * Mathf.PI * 2; v.Add(new Vector3(Mathf.Sin(a) * r, len / 2, Mathf.Cos(a) * r)); }
        for (int i = 0; i < seg; i++) { t.AddRange(new[] { bc, rb + i, rb + i + 1 }); t.AddRange(new[] { tc, rt + i + 1, rt + i }); }
        var m = new Mesh(); m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }

    static Mesh Box(Vector3 size, float bevel)
    {
        var h = size / 2; var v = new List<Vector3>(); var t = new List<int>();
        void Face(Vector3 n, Vector3 u, Vector3 w)
        {
            int o = v.Count;
            Vector3 c = Vector3.Scale(n, h), du = Vector3.Scale(u, h), dw = Vector3.Scale(w, h);
            v.Add(c - du - dw); v.Add(c + du - dw); v.Add(c + du + dw); v.Add(c - du + dw);
            if (Vector3.Dot(Vector3.Cross(v[o + 1] - v[o], v[o + 2] - v[o]), n) > 0) t.AddRange(new[] { o, o + 1, o + 2, o, o + 2, o + 3 });
            else t.AddRange(new[] { o, o + 2, o + 1, o, o + 3, o + 2 });
        }
        Face(Vector3.forward, Vector3.right, Vector3.up); Face(Vector3.back, Vector3.right, Vector3.up);
        Face(Vector3.right, Vector3.forward, Vector3.up); Face(Vector3.left, Vector3.forward, Vector3.up);
        Face(Vector3.up, Vector3.right, Vector3.forward); Face(Vector3.down, Vector3.right, Vector3.forward);
        var m = new Mesh(); m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }
    static Mesh RoundedBox(Vector3 size, float r, int n)
    {
        var h = size / 2; r = Mathf.Min(r, Mathf.Min(h.x, Mathf.Min(h.y, h.z)) * 0.95f);
        var inner = h - Vector3.one * r;
        var v = new List<Vector3>(); var t = new List<int>();
        int res = Mathf.Max(2, n * 2 + 2);
        void Face(Vector3 nrm, Vector3 u, Vector3 w)
        {
            int o = v.Count;
            for (int j = 0; j <= res; j++)
                for (int i = 0; i <= res; i++)
                {
                    float a = Mathf.Lerp(-1, 1, i / (float)res), b = Mathf.Lerp(-1, 1, j / (float)res);
                    var cube = nrm + u * a + w * b;
                    var p = Vector3.Scale(cube, h);
                    var q = new Vector3(Mathf.Clamp(p.x, -inner.x, inner.x), Mathf.Clamp(p.y, -inner.y, inner.y), Mathf.Clamp(p.z, -inner.z, inner.z));
                    var d = p - q;
                    v.Add(d.sqrMagnitude > 1e-12f ? q + d.normalized * r : p);
                }
            for (int j = 0; j < res; j++)
                for (int i = 0; i < res; i++)
                {
                    int a0 = o + j * (res + 1) + i, a1 = a0 + 1, b0 = a0 + res + 1, b1 = b0 + 1;
                    bool flip = Vector3.Dot(Vector3.Cross(u, w), nrm) < 0;
                    if (!flip) t.AddRange(new[] { a0, b0, a1, a1, b0, b1 }); else t.AddRange(new[] { a0, a1, b0, a1, b1, b0 });
                }
        }
        Face(Vector3.forward, Vector3.right, Vector3.up); Face(Vector3.back, Vector3.right, Vector3.up);
        Face(Vector3.right, Vector3.forward, Vector3.up); Face(Vector3.left, Vector3.forward, Vector3.up);
        Face(Vector3.up, Vector3.right, Vector3.forward); Face(Vector3.down, Vector3.right, Vector3.forward);
        var m = new Mesh(); m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }

    static Mesh Lathe((float r, float y)[] prof, int seg, float knurl)
    {
        var v = new List<Vector3>(); var t = new List<int>();
        for (int i = 0; i < prof.Length; i++)
            for (int s = 0; s <= seg; s++)
            {
                float a = s / (float)seg * Mathf.PI * 2;
                float r = prof[i].r + (knurl > 0 && prof[i].r > 0 ? knurl * (s % 2 == 0 ? 1f : -1f) : 0f);
                v.Add(new Vector3(Mathf.Sin(a) * r, prof[i].y, Mathf.Cos(a) * r));
            }
        for (int i = 0; i < prof.Length - 1; i++)
            for (int s = 0; s < seg; s++)
            {
                int a0 = i * (seg + 1) + s, a1 = a0 + 1, b0 = a0 + seg + 1, b1 = b0 + 1;
                t.AddRange(new[] { a0, a1, b0, a1, b1, b0 });
            }
        var m = new Mesh(); m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }

    static Mesh Torus(float R, float r, int seg, int tube)
    {
        var v = new List<Vector3>(); var t = new List<int>();
        for (int i = 0; i <= seg; i++)
            for (int j = 0; j <= tube; j++)
            {
                float a = i / (float)seg * Mathf.PI * 2, b = j / (float)tube * Mathf.PI * 2;
                var c = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
                v.Add(c * (R + Mathf.Cos(b) * r) + Vector3.up * Mathf.Sin(b) * r);
            }
        for (int i = 0; i < seg; i++)
            for (int j = 0; j < tube; j++)
            {
                int a0 = i * (tube + 1) + j, a1 = a0 + 1, b0 = a0 + tube + 1, b1 = b0 + 1;
                t.AddRange(new[] { a0, b0, a1, a1, b0, b1 });
            }
        var m = new Mesh(); m.SetVertices(v); m.SetTriangles(t, 0); m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }
    static Mesh ScreenQuad(float w, float h)
    {
        var m = new Mesh();
        m.SetVertices(new List<Vector3> { new Vector3(-w / 2, -h / 2, 0), new Vector3(w / 2, -h / 2, 0), new Vector3(-w / 2, h / 2, 0), new Vector3(w / 2, h / 2, 0) });
        m.SetUVs(0, new List<Vector2> { new Vector2(1, 1), new Vector2(0, 1), new Vector2(1, 0), new Vector2(0, 0) });
        m.SetTriangles(new[] { 0, 2, 3, 0, 3, 1 }, 0);
        m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }

    static Mesh GripMesh()
    {
        const int S = 40, Rows = 64; const float H = 0.13f;
        var v = new List<Vector3>(); var t0 = new List<int>(); var t1 = new List<int>();
        float Profile(float y)
        {
            float r = 0.0155f + 0.0012f * Mathf.Sin(y * Mathf.PI);
            if (y < 0.09f) r += 0.0045f * Mathf.SmoothStep(1, 0, y / 0.09f);
            if (y > 0.90f) r += 0.0025f * Mathf.SmoothStep(0, 1, (y - 0.90f) / 0.10f);
            return r;
        }
        bool Accent(float y) => y < 0.075f || y > 0.905f;
        for (int i = 0; i <= Rows; i++)
        {
            float y = i / (float)Rows;
            for (int s = 0; s <= S; s++)
            {
                float a = s / (float)S * Mathf.PI * 2, cx = Mathf.Sin(a), cz = Mathf.Cos(a), r = Profile(y);
                float front = Mathf.Max(0, cz); front *= front;
                if (y > 0.16f && y < 0.84f)
                {
                    float g = (y - 0.16f) / 0.68f * 3f;
                    r -= 0.0022f * (0.5f - 0.5f * Mathf.Cos(g * Mathf.PI * 2)) * front;
                    r -= 0.0005f * Mathf.Max(0, -cz) * (0.5f + 0.5f * Mathf.Cos(a * 24f));
                }
                v.Add(new Vector3(cx * r * 0.92f, (y - 0.5f) * H, cz * r * 1.08f));
            }
        }
        int bc = v.Count; v.Add(new Vector3(0, -0.5f * H, 0)); int tc = v.Count; v.Add(new Vector3(0, 0.5f * H, 0));
        for (int i = 0; i < Rows; i++)
            for (int s = 0; s < S; s++)
            {
                int a0 = i * (S + 1) + s, a1 = a0 + 1, b0 = a0 + S + 1, b1 = b0 + 1;
                (Accent((i + 0.5f) / Rows) ? t1 : t0).AddRange(new[] { a0, a1, b0, a1, b1, b0 });
            }
        for (int s = 0; s < S; s++) { t1.AddRange(new[] { bc, s + 1, s }); int o = Rows * (S + 1); t1.AddRange(new[] { tc, o + s, o + s + 1 }); }
        var m = new Mesh(); m.SetVertices(v); m.subMeshCount = 2; m.SetTriangles(t0, 0); m.SetTriangles(t1, 1);
        m.RecalculateNormals(); m.RecalculateBounds(); return m;
    }

    static void Snap(string prefabPath, Vector3 eye, string file)
    {
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene);
        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath));
        var prevShader = Shader.Find("Standard");
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
        {
            var ms = r.sharedMaterials;
            for (int i = 0; i < ms.Length; i++)
            {
                var pm = new Material(prevShader);
                if (ms[i] != null) pm.color = ms[i].GetColor("_Color") + ms[i].GetColor("_Emission");
                else
                {
                    var tt = new Texture2D(64, 36); for (int y = 0; y < 36; y++) for (int x = 0; x < 64; x++)
                        tt.SetPixel(x, y, y >= 24 && x < 20 ? Color.red : y >= 24 && x >= 44 ? Color.green : y < 10 ? Color.blue : Color.white);
                    tt.Apply(); pm.mainTexture = tt; pm.mainTextureScale = new Vector2(-1, -1); pm.mainTextureOffset = new Vector2(1, 1);
                }
                ms[i] = pm;
            }
            r.sharedMaterials = ms;
        }
        var lg = new GameObject("l"); var lt = lg.AddComponent<Light>(); lt.type = LightType.Directional; lg.transform.rotation = Quaternion.LookRotation(-eye.normalized + Vector3.down * 0.6f);
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.48f);
        var b = new Bounds(); bool f = true;
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>()) { if (f) { b = r.bounds; f = false; } else b.Encapsulate(r.bounds); }
        var cg = new GameObject("c"); var cam = cg.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.25f, 0.25f, 0.27f); cam.fieldOfView = 30; cam.nearClipPlane = 0.01f;
        cam.transform.position = b.center + eye * 1.2f; cam.transform.LookAt(b.center);
        var rt = new RenderTexture(768, 512, 24); cam.targetTexture = rt; cam.Render();
        RenderTexture.active = rt; var tx = new Texture2D(768, 512); tx.ReadPixels(new Rect(0, 0, 768, 512), 0, 0); tx.Apply(); RenderTexture.active = null;
        File.WriteAllBytes(file, tx.EncodeToPNG());
        Object.DestroyImmediate(cg); Object.DestroyImmediate(go); Object.DestroyImmediate(lg);
    }
}
