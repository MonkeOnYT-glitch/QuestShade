using UnityEngine;

namespace QuestShade
{
    internal static class Focus
    {
        public static System.Func<Transform> PlayerRoot;
        private static Transform _player; private static float _playerAt;
        public static float Raycast(Vector3 from, Vector3 dir, int excludeLayer, Transform ignore, float last)
        {
            int mask = ~0;
            if (excludeLayer >= 0) mask &= ~(1 << excludeLayer);
            float best = float.MaxValue;
            if (Time.unscaledTime > _playerAt) { _playerAt = Time.unscaledTime + 2f; try { _player = PlayerRoot?.Invoke(); } catch { _player = null; } }
            try
            {
                var hits = Physics.RaycastAll(from + dir * 0.05f, dir, 150f, mask, QueryTriggerInteraction.Ignore);
                for (int k = 0; hits != null && k < hits.Length; k++)
                {
                    var h = hits[k];
                    if (h.collider == null) continue;
                    if (ignore != null && h.collider.transform.IsChildOf(ignore)) continue;
                    if (_player != null && h.collider.transform.IsChildOf(_player)) continue;
                    if (h.distance < best) best = h.distance;
                }
            }
            catch { }
            if (best == float.MaxValue) return last > 0f ? last : 10f;
            return Mathf.Clamp(best + 0.05f, 0.3f, 60f);
        }

        public static float Smooth(float current, float target, float dt, float rate = 2.2f)
        {
            if (current <= 0f) return target;
            if (Mathf.Abs(Mathf.Log(target / current)) < 0.14f) return current;
            float k = 1f - Mathf.Exp(-dt * rate);
            return Mathf.Exp(Mathf.Lerp(Mathf.Log(current), Mathf.Log(target), k));
        }
    }
}
