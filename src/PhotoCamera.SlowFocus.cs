using UnityEngine;

namespace QuestShade
{
    // Slow-mo cinematic zoom (beta): when the game slows down, the in-game camera finds the NPC or object it's pointed at,
    // eases its zoom in to frame it and turns gently toward it; as slow-mo ends it eases back out. With nothing in front
    // of it, it just pushes in a little.
    internal sealed partial class PhotoCamera
    {
        private Transform _slowTarget; private Vector3 _slowCenterLocal; private float _slowSize = 1f;
        private float _slowTargetAt, _slowZoom = 1f;

        private bool SlowZoomOn(Config cfg) => Mod.BetaOn && cfg.SlowMoZoom && !_pathPlaying && !_movePlaying && ExternalLens == null && FollowCamera == null;

        // Turn the lens a little toward the slow-mo target.
        private void SlowAim(Config cfg, float dt, ref Quaternion rot, Vector3 pos)
        {
            float k = SlowZoomOn(cfg) ? SlowMo.Intensity : 0f;
            if (k < 0.05f) { _slowTarget = null; return; }
            float now = Time.unscaledTime;
            bool alive; try { alive = _slowTarget != null && _slowTarget.gameObject.activeInHierarchy; } catch { alive = false; }
            if (!alive) { _slowTarget = null; if (now >= _slowTargetAt) { _slowTargetAt = now + 0.5f; FindSlowTarget(pos, rot * Vector3.forward); } }
            if (_slowTarget == null) return;
            var c = _slowTarget.TransformPoint(_slowCenterLocal);
            var to = c - pos;
            if (to.sqrMagnitude < 0.04f || Vector3.Dot(to, rot * Vector3.forward) < 0f) { _slowTarget = null; return; } // too close or behind us
            rot = Quaternion.Slerp(rot, Quaternion.LookRotation(to, rot * Vector3.up), 0.35f * k);
        }

        // Field of view multiplier for the slow-mo zoom (1 = no zoom).
        private float SlowZoom(Config cfg, float dt, float baseFov, Vector3 pos)
        {
            float k = SlowZoomOn(cfg) ? SlowMo.Intensity : 0f;
            float want = 1f;
            if (k > 0.05f)
            {
                want = 1f - k * 0.3f; // nothing to frame: a gentle push in
                if (_slowTarget != null && baseFov > 1f)
                {
                    float dist = Vector3.Distance(pos, _slowTarget.TransformPoint(_slowCenterLocal));
                    float frame = Mathf.Clamp(2f * Mathf.Atan(_slowSize * 1.4f / (2f * Mathf.Max(0.3f, dist))) * Mathf.Rad2Deg, 14f, baseFov);
                    want = Mathf.Lerp(1f, frame / baseFov, k);
                }
            }
            _slowZoom = Mathf.Lerp(_slowZoom, want, 1f - Mathf.Exp(-1.5f * dt));
            return _slowZoom;
        }

        private void FindSlowTarget(Vector3 from, Vector3 dir)
        {
            Transform player = null; try { player = Focus.PlayerRoot?.Invoke(); } catch { }
            try
            {
                var hits = Physics.SphereCastAll(from + dir * 0.1f, 0.35f, dir, 30f, ~(1 << 2), QueryTriggerInteraction.Ignore);
                float best = float.MaxValue; Transform pick = null;
                foreach (var h in hits)
                {
                    var col = h.collider; if (col == null || h.distance >= best) continue;
                    var root = col.attachedRigidbody != null ? col.attachedRigidbody.transform : null;
                    if (root == null) continue; // the level itself isn't a subject
                    var pool = root.GetComponentInParent<Il2CppSLZ.Marrow.Pool.Poolee>();
                    if (pool != null) root = pool.transform;
                    if (root.name.StartsWith("QuestShade") || (player != null && (root.IsChildOf(player) || player.IsChildOf(root)))) continue;
                    best = h.distance; pick = root;
                }
                if (pick == null) return;
                var cols = pick.GetComponentsInChildren<Collider>(false);
                Bounds b = default; bool any = false;
                foreach (var c in cols) { if (c == null || c.isTrigger) continue; if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds); }
                if (!any) return;
                _slowTarget = pick;
                _slowCenterLocal = pick.InverseTransformPoint(b.center);
                _slowSize = Mathf.Clamp(b.extents.magnitude * 2f, 0.25f, 4f);
                Mod.Log.Msg($"Slow-mo zoom: framing '{pick.name}' ({_slowSize:0.0} m)");
            }
            catch { }
        }
    }
}
