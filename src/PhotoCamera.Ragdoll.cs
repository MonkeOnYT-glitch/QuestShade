using System;
using UnityEngine;

namespace QuestShade
{
    // Ragdolling (the Ragdoll mod, or dying): body-mounted cameras stop following your headset and stay locked to the body
    // bone they're mounted on (head for helmet / first person, chest for chest / police / shoulder), rotation and all.
    // Detected by your body's head separating from your real head, or the game saying you're dying, so it works with any
    // ragdoll mod. While you play normally the camera's offset from that bone is remembered every frame; when you go limp
    // that offset is frozen and the camera rides the bone until your body is back with you.
    internal sealed partial class PhotoCamera
    {
        public Func<bool, Transform> BodyBone;   // true = head, false = chest (the physics body, which goes limp)
        public Func<bool> PlayerDying;
        private bool _boneLocked, _haveRel;
        private Vector3 _relPos; private Quaternion _relRot = Quaternion.identity;
        private float _backFor;

        private void RagdollLock(Transform hmd, float dt, ref Vector3 lp, ref Quaternion lr)
        {
            if (!BodyMode || BodyBone == null || _following || hmd == null) { _boneLocked = false; _haveRel = false; return; }
            bool headMode = Mode == CamMode.FirstPerson || Mode == CamMode.Helmet || Mode == CamMode.HelmetView;
            Transform bone = null, head = null;
            try { bone = BodyBone(headMode); head = headMode ? bone : BodyBone(true); } catch { }
            if (bone == null || head == null) { _boneLocked = false; return; }
            bool dying = false; try { dying = PlayerDying != null && PlayerDying(); } catch { }
            float sep = Vector3.Distance(head.position, hmd.position);
            bool limp = dying || sep > (_boneLocked ? 0.22f : 0.35f);

            if (!_boneLocked)
            {
                if (limp && _haveRel)
                {
                    _boneLocked = true; _backFor = 0f;
                    Mod.Log.Msg($"Camera: you went limp ({(dying ? "dying" : $"body {sep:0.00} m from your head")}), locked to your {(headMode ? "head" : "chest")}");
                }
                else
                {
                    // Normal play: remember where the camera sits relative to the bone.
                    _relPos = bone.InverseTransformPoint(lp);
                    _relRot = Quaternion.Inverse(bone.rotation) * lr;
                    _haveRel = true;
                    return;
                }
            }
            // Locked: ride the bone. Let go once your body has been back with you for a moment.
            if (!limp) { _backFor += dt; if (_backFor > 0.6f) { _boneLocked = false; Mod.Log.Msg("Camera: body back, following your head again"); return; } }
            else _backFor = 0f;
            lp = bone.TransformPoint(_relPos);
            lr = bone.rotation * _relRot;
        }
    }
}
