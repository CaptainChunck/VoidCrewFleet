using System;
using System.Collections.Generic;
using CG.Game;
using CG.Game.Player;
using Opsive.UltimateCharacterController.Character.Identifiers;
using UnityEngine;

namespace VoidCrewFleet
{
    /// <summary>
    /// The fighting stance and punches on the character models, seen by everyone: while a player holds their stance, both
    /// arms come up with the fists in front of the face; a punch drives one fist straight out from the shoulder and back.
    /// The game has no such animation, so the arms are posed after the game's own animation every frame: a two-bone IK from
    /// the shoulder to the wrist, toward points in front of the chest, with the fingers curled into fists (the fist is the
    /// avatar's own "fingers closed" pose, read once). Only the arms move, never the head (the first-person camera hangs
    /// on it). The local player, who sees no body in first person, sees their own arms while their fists are up.
    /// </summary>
    internal static class FistPose
    {
        private const float RaiseRate = 8f;
        private const float PoseDistance = 40f;
        private const float Extend = 0.09f;
        private const float HoldOut = 0.04f;
        private const float Retract = 0.16f;

        private static readonly HumanBodyBones[] LeftFingers =
        {
            HumanBodyBones.LeftThumbProximal, HumanBodyBones.LeftThumbIntermediate, HumanBodyBones.LeftThumbDistal,
            HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftIndexIntermediate, HumanBodyBones.LeftIndexDistal,
            HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal,
            HumanBodyBones.LeftRingProximal, HumanBodyBones.LeftRingIntermediate, HumanBodyBones.LeftRingDistal,
            HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal
        };

        private static readonly HumanBodyBones[] RightFingers =
        {
            HumanBodyBones.RightThumbProximal, HumanBodyBones.RightThumbIntermediate, HumanBodyBones.RightThumbDistal,
            HumanBodyBones.RightIndexProximal, HumanBodyBones.RightIndexIntermediate, HumanBodyBones.RightIndexDistal,
            HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal,
            HumanBodyBones.RightRingProximal, HumanBodyBones.RightRingIntermediate, HumanBodyBones.RightRingDistal,
            HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal
        };

        /// <summary>One arm: its bones, the game's own pose of them this frame, and its punch.</summary>
        private sealed class Arm
        {
            public bool Left;
            public Transform Upper;
            public Transform Lower;
            public Transform Hand;
            public Transform[] Fingers;
            public Quaternion BaseUpper;
            public Quaternion BaseLower;
            public Quaternion[] BaseFingers;
            public Quaternion WrittenUpper;
            public Quaternion WrittenLower;
            public bool Written;
            public float PunchStart = -10f;
            public Vector3 PunchAim;
            public Player PunchTarget;
        }

        private sealed class Rig
        {
            public Player Character;
            public Animator Animator;
            public Transform Chest;
            public Arm Left;
            public Arm Right;
            public float Weight;
            public ThirdPersonObject ArmsMesh;
            public bool ArmsShown;
        }

        private static readonly Dictionary<Player, Rig> Rigs = new Dictionary<Player, Rig>();
        private static Quaternion[] _fistLeft;
        private static Quaternion[] _fistRight;
        private static bool _fistFailed;

        /// <summary>A punch with one arm (0 left, 1 right) at a target (or along the aim): shown on that player's model.</summary>
        public static void Punch(Player character, int arm, Player target, Vector3 aim)
        {
            Rig rig = RigOf(character);
            if (rig == null) return;
            Arm side = arm == 0 ? rig.Left : rig.Right;
            side.PunchStart = Time.time;
            side.PunchTarget = target;
            side.PunchAim = aim.sqrMagnitude > 0.0001f ? aim.normalized : character.transform.forward;
        }

        private static bool Punching(Arm arm)
        {
            return Time.time - arm.PunchStart < Extend + HoldOut + Retract;
        }

        /// <summary>How far out the punching fist is (0 at guard, 1 at full reach) at this moment of its punch.</summary>
        private static float PunchOut(Arm arm)
        {
            float t = Time.time - arm.PunchStart;
            if (t < 0f || t >= Extend + HoldOut + Retract) return 0f;
            if (t < Extend)
            {
                float x = t / Extend;
                return 1f - (1f - x) * (1f - x);
            }
            if (t < Extend + HoldOut) return 1f;
            return 1f - Mathf.SmoothStep(0f, 1f, (t - Extend - HoldOut) / Retract);
        }

        // ---------------------------------------------------------------- every frame, after the animation

        public static void Late()
        {
            ClientGame game = ClientGame.Current;
            if (game == null || game.Players == null)
            {
                Clear();
                return;
            }
            bool active = PvpCombat.Active;
            Camera camera = Camera.main;
            foreach (Player player in game.Players)
            {
                if (player == null) continue;
                Rig rig;
                Rigs.TryGetValue(player, out rig);
                bool alive = !player.IsDead;
                bool raised = active && alive && PvpPunch.StanceOf(player);
                bool punching = rig != null && alive && (Punching(rig.Left) || Punching(rig.Right));
                if (rig == null && !raised && !punching) continue;
                if (rig == null && (rig = RigOf(player)) == null) continue;
                rig.Weight = Mathf.MoveTowards(rig.Weight, raised || punching ? 1f : 0f, Time.deltaTime * RaiseRate);
                bool local = player == LocalPlayer.Instance;
                ShowArms(rig, local && rig.Weight > 0.001f && alive);
                if (rig.Weight <= 0.001f || !alive)
                {
                    rig.Left.Written = rig.Right.Written = false;
                    continue;
                }
                if (!local && camera != null && (player.transform.position - camera.transform.position).sqrMagnitude > PoseDistance * PoseDistance) continue;
                try
                {
                    Pose(rig, local);
                }
                catch (Exception e)
                {
                    Log.Failure("Posing a fighting stance", e);
                    Rigs.Remove(player);
                }
            }
            Prune(game);
        }

        private static void Pose(Rig rig, bool local)
        {
            Transform body = rig.Character.transform;
            Quaternion frame = body.rotation;
            Vector3 chest = rig.Chest.position;
            // The local player's view starts at their eyes: a little higher and further out, so the fists are in sight.
            float lift = local ? 0.06f : 0f;
            Vector3 guardLeft = chest + frame * new Vector3(-0.12f, 0.10f + lift, 0.30f + lift);
            Vector3 guardRight = chest + frame * new Vector3(0.13f, 0.08f + lift, 0.24f + lift);
            PoseArm(rig, rig.Left, guardLeft, frame);
            PoseArm(rig, rig.Right, guardRight, frame);
        }

        private static void PoseArm(Rig rig, Arm arm, Vector3 guard, Quaternion frame)
        {
            // The blend base is the game's pose of the arm this frame. The local player's model animates in the physics step,
            // so on some frames the bones still hold what was written here last frame: the last real pose is reused then.
            bool stale = arm.Written && arm.Upper.localRotation == arm.WrittenUpper && arm.Lower.localRotation == arm.WrittenLower;
            if (stale)
            {
                arm.Upper.localRotation = arm.BaseUpper;
                arm.Lower.localRotation = arm.BaseLower;
            }
            else
            {
                arm.BaseUpper = arm.Upper.localRotation;
                arm.BaseLower = arm.Lower.localRotation;
                for (int i = 0; i < arm.Fingers.Length; i++)
                {
                    if (arm.Fingers[i] != null) arm.BaseFingers[i] = arm.Fingers[i].localRotation;
                }
            }

            Vector3 shoulder = arm.Upper.position;
            float upperLength = (arm.Lower.position - shoulder).magnitude;
            float lowerLength = (arm.Hand.position - arm.Lower.position).magnitude;
            Vector3 target = guard;
            float out_ = PunchOut(arm);
            if (out_ > 0f)
            {
                Vector3 direction = arm.PunchAim;
                Player victim = arm.PunchTarget;
                if (victim != null && !victim.IsDead)
                {
                    Vector3 head = victim.transform.position + victim.transform.up * 1.5f;
                    if ((head - shoulder).sqrMagnitude < 9f) direction = (head - shoulder).normalized;
                }
                target = Vector3.Lerp(guard, shoulder + direction * (0.95f * (upperLength + lowerLength)), out_);
            }
            // The elbow points out to the side and down.
            Vector3 hint = shoulder + frame * new Vector3(arm.Left ? -0.15f : 0.15f, -0.30f, 0.05f);
            Solve(arm, target, hint, upperLength, lowerLength);

            float weight = rig.Weight;
            Quaternion ikUpper = arm.Upper.localRotation;
            Quaternion ikLower = arm.Lower.localRotation;
            arm.Upper.localRotation = Quaternion.Slerp(arm.BaseUpper, ikUpper, weight);
            arm.Lower.localRotation = Quaternion.Slerp(arm.BaseLower, ikLower, weight);
            arm.WrittenUpper = arm.Upper.localRotation;
            arm.WrittenLower = arm.Lower.localRotation;
            arm.Written = true;

            Quaternion[] fist = arm.Left ? _fistLeft : _fistRight;
            if (fist == null) return;
            for (int i = 0; i < arm.Fingers.Length && i < fist.Length; i++)
            {
                if (arm.Fingers[i] != null) arm.Fingers[i].localRotation = Quaternion.Slerp(arm.BaseFingers[i], fist[i], weight);
            }
        }

        /// <summary>
        /// Two-bone IK: turns the upper arm so the elbow sits where both bones reach the target (bending toward the hint),
        /// then the forearm so the wrist is on the target. Each bone keeps its twist.
        /// </summary>
        private static void Solve(Arm arm, Vector3 target, Vector3 hint, float upperLength, float lowerLength)
        {
            Vector3 shoulder = arm.Upper.position;
            Vector3 toTarget = target - shoulder;
            if (upperLength < 0.01f || lowerLength < 0.01f || toTarget.sqrMagnitude < 0.0001f) return;
            float reach = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upperLength - lowerLength) + 0.001f, upperLength + lowerLength - 0.001f);
            Vector3 direction = toTarget.normalized;
            Vector3 axis = Vector3.Cross(direction, hint - shoulder);
            if (axis.sqrMagnitude < 0.000001f) axis = Vector3.Cross(direction, arm.Upper.up);
            if (axis.sqrMagnitude < 0.000001f) return;
            axis.Normalize();
            float cosine = (upperLength * upperLength + reach * reach - lowerLength * lowerLength) / (2f * upperLength * reach);
            float angle = Mathf.Acos(Mathf.Clamp(cosine, -1f, 1f)) * Mathf.Rad2Deg;
            // Turning the reach direction about this axis moves it toward the hint.
            Vector3 elbowDirection = Quaternion.AngleAxis(angle, axis) * direction;
            arm.Upper.rotation = Quaternion.FromToRotation(arm.Lower.position - shoulder, elbowDirection) * arm.Upper.rotation;
            Vector3 elbow = arm.Lower.position;
            Vector3 wrist = shoulder + direction * reach;
            arm.Lower.rotation = Quaternion.FromToRotation(arm.Hand.position - elbow, wrist - elbow) * arm.Lower.rotation;
        }

        // ---------------------------------------------------------------- the rig

        private static Rig RigOf(Player character)
        {
            if (character == null) return null;
            Rig rig;
            if (Rigs.TryGetValue(character, out rig)) return rig;
            Animator animator = ModelAnimator(character);
            if (animator == null) return null;
            Transform chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest);
            Arm left = MakeArm(animator, true);
            Arm right = MakeArm(animator, false);
            if (chest == null || left == null || right == null)
            {
                Log.WarnOnce("fist rig", "A character model has no humanoid arms to raise the fists with (a game update?).");
                return null;
            }
            CaptureFist(animator);
            rig = new Rig { Character = character, Animator = animator, Chest = chest, Left = left, Right = right };
            if (character == LocalPlayer.Instance)
            {
                Transform arms = animator.transform.Find("Arms");
                rig.ArmsMesh = arms != null ? arms.GetComponent<ThirdPersonObject>() : null;
            }
            Rigs[character] = rig;
            return rig;
        }

        /// <summary>The character's body animator ("model"; the "camera_rig" copy has the same avatar).</summary>
        private static Animator ModelAnimator(Player character)
        {
            Animator fallback = null;
            foreach (Animator animator in character.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null || !animator.isHuman || animator.avatar == null) continue;
                if (animator.gameObject.name == "model") return animator;
                if (fallback == null && animator.gameObject.name != "camera_rig") fallback = animator;
            }
            return fallback;
        }

        private static Arm MakeArm(Animator animator, bool left)
        {
            Transform upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            Transform lower = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            Transform hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (upper == null || lower == null || hand == null) return null;
            HumanBodyBones[] bones = left ? LeftFingers : RightFingers;
            var fingers = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++) fingers[i] = animator.GetBoneTransform(bones[i]);
            return new Arm { Left = left, Upper = upper, Lower = lower, Hand = hand, Fingers = fingers, BaseFingers = new Quaternion[bones.Length] };
        }

        /// <summary>
        /// The fist, once: the avatar's own muscles say "fingers closed" (every finger joint bent fully, the thumb part way),
        /// read from one model and put straight back. Every character shares the avatar, so the same finger rotations make a
        /// fist on all of them.
        /// </summary>
        private static void CaptureFist(Animator animator)
        {
            if (_fistLeft != null || _fistFailed) return;
            Transform root = animator.transform;
            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            var positions = new Vector3[all.Length];
            var rotations = new Quaternion[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                positions[i] = all[i].localPosition;
                rotations[i] = all[i].localRotation;
            }
            HumanPoseHandler handler = null;
            try
            {
                handler = new HumanPoseHandler(animator.avatar, root);
                var pose = new HumanPose();
                handler.GetHumanPose(ref pose);
                string[] names = HumanTrait.MuscleName;
                for (int i = 0; i < names.Length && i < pose.muscles.Length; i++)
                {
                    string name = names[i];
                    if (!(name.StartsWith("Left ") || name.StartsWith("Right "))) continue;
                    bool thumb = name.Contains("Thumb");
                    if (!(thumb || name.Contains("Index") || name.Contains("Middle") || name.Contains("Ring") || name.Contains("Little"))) continue;
                    if (name.EndsWith("Stretched")) pose.muscles[i] = thumb ? -0.6f : -1f;
                    else if (name.EndsWith("Spread")) pose.muscles[i] = 0f;
                }
                handler.SetHumanPose(ref pose);
                _fistLeft = ReadFingers(animator, LeftFingers);
                _fistRight = ReadFingers(animator, RightFingers);
            }
            catch (Exception e)
            {
                _fistFailed = true;
                Log.Failure("Reading the fist pose (the arms still rise, with open hands)", e);
            }
            finally
            {
                if (handler != null) handler.Dispose();
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] == null) continue;
                    all[i].localPosition = positions[i];
                    all[i].localRotation = rotations[i];
                }
            }
        }

        private static Quaternion[] ReadFingers(Animator animator, HumanBodyBones[] bones)
        {
            var result = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = animator.GetBoneTransform(bones[i]);
                result[i] = bone != null ? bone.localRotation : Quaternion.identity;
            }
            return result;
        }

        /// <summary>The local player's own arms, normally shadow-only in first person, show while the fists are up.</summary>
        private static void ShowArms(Rig rig, bool show)
        {
            if (rig.ArmsMesh == null || rig.ArmsShown == show) return;
            rig.ArmsShown = show;
            try
            {
                rig.ArmsMesh.ForceVisible = show;
            }
            catch (Exception e)
            {
                Log.Failure("Showing the local player's arms", e);
            }
        }

        private static readonly List<Player> Gone = new List<Player>();

        private static void Prune(ClientGame game)
        {
            Gone.Clear();
            foreach (Player player in Rigs.Keys)
            {
                if (player == null || !game.Players.Contains(player)) Gone.Add(player);
            }
            foreach (Player player in Gone) Rigs.Remove(player);
        }

        private static void Clear()
        {
            foreach (Rig rig in Rigs.Values)
            {
                if (rig.Character != null) ShowArms(rig, false);
            }
            Rigs.Clear();
        }

        /// <summary>Self-test: the local player's rig, weight and fists, for the log.</summary>
        public static string Describe()
        {
            LocalPlayer me = LocalPlayer.Instance;
            Rig rig;
            if (me == null || !Rigs.TryGetValue(me, out rig)) return "no rig";
            return "weight " + rig.Weight.ToString("0.00") + ", fist " + (_fistLeft != null ? "read" : (_fistFailed ? "failed" : "not read")) + ", arms shown " + rig.ArmsShown +
                   ", left wrist " + (rig.Left.Hand.position - rig.Chest.position).ToString("0.00") + " from the chest";
        }
    }
}
