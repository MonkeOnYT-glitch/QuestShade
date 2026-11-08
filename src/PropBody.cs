using System;
using System.Collections.Generic;
using Il2CppSLZ.Marrow.Pool;
using UnityEngine;

namespace QuestShade
{
    internal sealed class PropBody
    {
        public GameObject Root { get; private set; }
        public Rigidbody Body { get; private set; }
        public Bounds LocalBounds { get; private set; }
        public Vector3 MountLocal { get; private set; }
        public bool Frozen { get; private set; }

        public bool Alive
        {
            get { try { return Root != null && Root.activeInHierarchy; } catch { return false; } }
        }

        private static readonly string[] Unwanted = { "Gun", "SpawnGun", "ConstrainerGun", "NimbusGun", "BoardGun", "BalloonGun",
            "MeleeWeapon", "Projectile", "PropFlag", "GravityGrabber" };

        private static readonly HashSet<int> Adopted = new HashSet<int>();

        public static PropBody Adapt(GameObject root, float mountHeight, Vector3 grabSize, Vector3 grabOffset)
        {
            var p = new PropBody { Root = root };
            try { Adopted.Add(root.GetInstanceID()); } catch { }
            try { p.Body = root.GetComponentInChildren<Rigidbody>(true); } catch { }

            int hidden = 0;
            try { foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (r != null) { r.enabled = false; hidden++; } } catch { }
            try { foreach (var l in root.GetComponentsInChildren<Light>(true)) if (l != null) l.enabled = false; } catch { }

            var off = new List<string>();
            try
            {
                foreach (var b in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (b == null) continue;
                    string n; try { n = b.GetIl2CppType().Name; } catch { continue; }
                    if (Array.IndexOf(Unwanted, n) >= 0) { b.enabled = false; off.Add(n); }
                }
            }
            catch { }

            try
            {
                var col = root.GetComponentInChildren<Collider>(true);
                if (col != null)
                {
                    var wb = col.bounds;
                    p.LocalBounds = new Bounds(root.transform.InverseTransformPoint(wb.center), wb.size);
                }
            }
            catch { p.LocalBounds = new Bounds(Vector3.zero, new Vector3(0.1f, 0.1f, 0.1f)); }
            p.MountLocal = p.LocalBounds.center + Vector3.up * mountHeight;

            Mod.Log.Msg($"Camera body ready: '{root.name}', hid {hidden} renderer(s){(off.Count > 0 ? ", disabled " + string.Join(", ", off) : "")}, rigidbody {(p.Body != null ? "yes" : "no")}");
            return p;
        }

        public void PlaceMountAt(Vector3 mountPos, Quaternion rot)
        {
            if (Root == null) return;
            try
            {
                var t = Root.transform;
                var pos = mountPos - rot * MountLocal;
                t.SetPositionAndRotation(pos, rot);
                if (Body != null)
                {
                    Body.position = Body.transform.position;
                    Body.rotation = Body.transform.rotation;
                    Body.velocity = Vector3.zero;
                    Body.angularVelocity = Vector3.zero;
                }
            }
            catch { }
        }

        public void HoldAt(Vector3 pos, Quaternion rot)
        {
            if (Body == null) return;
            try
            {
                var t = Root.transform;
                if ((t.position - pos).sqrMagnitude < 1e-6f && Quaternion.Angle(t.rotation, rot) < 0.1f) return;
                t.SetPositionAndRotation(pos, rot);
                Body.position = pos; Body.rotation = rot;
                Body.velocity = Vector3.zero; Body.angularVelocity = Vector3.zero;
            }
            catch { }
        }

        public void Brake(bool on)
        {
            if (Body == null) return;
            try
            {
                if (_drag < 0f) { _drag = Body.drag; _angDrag = Body.angularDrag; }
                if (on) { Body.velocity = Vector3.zero; Body.angularVelocity = Vector3.zero; Body.useGravity = false; Body.drag = 30f; Body.angularDrag = 30f; }
                else if (!Frozen) { Body.useGravity = true; Body.drag = _drag; Body.angularDrag = _angDrag; }
            }
            catch { }
        }

        private float _drag = -1f, _angDrag;

        public void SetFrozen(bool frozen)
        {
            if (Body == null || frozen == Frozen) return;
            try
            {
                if (_drag < 0f) { _drag = Body.drag; _angDrag = Body.angularDrag; }
                Body.velocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
                Body.useGravity = !frozen;
                Body.drag = frozen ? 60f : _drag;
                Body.angularDrag = frozen ? 60f : _angDrag;
                Frozen = frozen;
            }
            catch { }
        }

        public void IgnoreCollisions(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Collider> others, bool quiet = false)
        {
            if (Root == null || others == null) return;
            int n = 0;
            try
            {
                var mine = Root.GetComponentsInChildren<Collider>(true);
                foreach (var a in mine)
                {
                    if (a == null) continue;
                    foreach (var b in others) if (b != null) { Physics.IgnoreCollision(a, b, true); n++; }
                }
            }
            catch (Exception e) { Mod.Log.Warning("Camera body: couldn't ignore player collisions: " + e.Message); }
            if (!quiet) Mod.Log.Msg($"Camera body: won't collide with you ({n} pairs)");
        }

        public static GameObject FindSpawnedNear(Vector3 pos, float radius, string barcode)
        {
            try
            {
                GameObject best = null; float bestD = radius * radius;
                foreach (var o in UnityEngine.Object.FindObjectsOfType<Poolee>())
                {
                    if (o == null || !o.gameObject.activeInHierarchy) continue;
                    if (o.gameObject.name.StartsWith("QuestShade")) continue;
                    string id = null;
                    try { id = o.SpawnableCrate != null ? o.SpawnableCrate.Barcode.ID : null; } catch { }
                    if (!string.Equals(id, barcode, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Adopted.Contains(o.gameObject.GetInstanceID())) continue;
                    float d = (o.transform.position - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = o.gameObject; }
                }
                return best;
            }
            catch { return null; }
        }

        public void Despawn()
        {
            var r = Root;
            Root = null; Body = null;
            if (r == null) return;
            try { Adopted.Remove(r.GetInstanceID()); } catch { }
            try
            {
                var pool = r.GetComponent<Poolee>();
                if (pool != null) { pool.Despawn(); return; }
            }
            catch { }
            try { UnityEngine.Object.Destroy(r); } catch { }
        }
    }
}
