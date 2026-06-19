// M2: build a Unity Humanoid Avatar from a parsed PMX skeleton.
//
// MMD "準標準" skeletons split at the waist (上半身/下半身 are siblings under 腰), so
// Hips maps to 腰 — the only common ancestor of spine and both legs. Twist/helper
// bones (腕捩, 手捩, 肩P, 腰キャンセル, グルーブ ...) stay unmapped but remain in the
// skeleton hierarchy. MMD models are authored in A-pose, so arms are leveled to
// T-pose before AvatarBuilder.BuildHumanAvatar (required for correct muscle axes).
//
// See Docs/DECISIONS_RECORD.md ADR-0008.
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public class HumanoidBuildResult
    {
        public Avatar Avatar;
        public bool Valid => Avatar != null && Avatar.isValid && Avatar.isHuman;
        public readonly List<string> Mapped = new();
        public readonly List<string> MissingRequired = new();
        public readonly List<string> MissingOptional = new();
    }

    public static class PmxHumanoid
    {
        // Unity humanoid bone -> ordered MMD candidate names (local JP). First match wins.
        private static readonly Dictionary<HumanBodyBones, string[]> Map = new()
        {
            { HumanBodyBones.Hips,            new[]{ "腰", "下半身", "センター" } },
            { HumanBodyBones.Spine,           new[]{ "上半身" } },
            { HumanBodyBones.Chest,           new[]{ "上半身2" } },
            { HumanBodyBones.Neck,            new[]{ "首" } },
            { HumanBodyBones.Head,            new[]{ "頭" } },
            { HumanBodyBones.LeftEye,         new[]{ "左目" } },
            { HumanBodyBones.RightEye,        new[]{ "右目" } },

            { HumanBodyBones.LeftShoulder,    new[]{ "左肩" } },
            { HumanBodyBones.LeftUpperArm,    new[]{ "左腕" } },
            { HumanBodyBones.LeftLowerArm,    new[]{ "左ひじ" } },
            { HumanBodyBones.LeftHand,        new[]{ "左手首" } },
            { HumanBodyBones.RightShoulder,   new[]{ "右肩" } },
            { HumanBodyBones.RightUpperArm,   new[]{ "右腕" } },
            { HumanBodyBones.RightLowerArm,   new[]{ "右ひじ" } },
            { HumanBodyBones.RightHand,       new[]{ "右手首" } },

            { HumanBodyBones.LeftUpperLeg,    new[]{ "左足" } },
            { HumanBodyBones.LeftLowerLeg,    new[]{ "左ひざ" } },
            { HumanBodyBones.LeftFoot,        new[]{ "左足首" } },
            { HumanBodyBones.LeftToes,        new[]{ "左つま先" } },
            { HumanBodyBones.RightUpperLeg,   new[]{ "右足" } },
            { HumanBodyBones.RightLowerLeg,   new[]{ "右ひざ" } },
            { HumanBodyBones.RightFoot,       new[]{ "右足首" } },
            { HumanBodyBones.RightToes,       new[]{ "右つま先" } },

            { HumanBodyBones.LeftThumbProximal,     new[]{ "左親指０", "左親指0" } },
            { HumanBodyBones.LeftThumbIntermediate, new[]{ "左親指１", "左親指1" } },
            { HumanBodyBones.LeftThumbDistal,       new[]{ "左親指２", "左親指2" } },
            { HumanBodyBones.LeftIndexProximal,     new[]{ "左人指１", "左人指1" } },
            { HumanBodyBones.LeftIndexIntermediate, new[]{ "左人指２", "左人指2" } },
            { HumanBodyBones.LeftIndexDistal,       new[]{ "左人指３", "左人指3" } },
            { HumanBodyBones.LeftMiddleProximal,     new[]{ "左中指１", "左中指1" } },
            { HumanBodyBones.LeftMiddleIntermediate, new[]{ "左中指２", "左中指2" } },
            { HumanBodyBones.LeftMiddleDistal,       new[]{ "左中指３", "左中指3" } },
            { HumanBodyBones.LeftRingProximal,     new[]{ "左薬指１", "左薬指1" } },
            { HumanBodyBones.LeftRingIntermediate, new[]{ "左薬指２", "左薬指2" } },
            { HumanBodyBones.LeftRingDistal,       new[]{ "左薬指３", "左薬指3" } },
            { HumanBodyBones.LeftLittleProximal,     new[]{ "左小指１", "左小指1" } },
            { HumanBodyBones.LeftLittleIntermediate, new[]{ "左小指２", "左小指2" } },
            { HumanBodyBones.LeftLittleDistal,       new[]{ "左小指３", "左小指3" } },

            { HumanBodyBones.RightThumbProximal,     new[]{ "右親指０", "右親指0" } },
            { HumanBodyBones.RightThumbIntermediate, new[]{ "右親指１", "右親指1" } },
            { HumanBodyBones.RightThumbDistal,       new[]{ "右親指２", "右親指2" } },
            { HumanBodyBones.RightIndexProximal,     new[]{ "右人指１", "右人指1" } },
            { HumanBodyBones.RightIndexIntermediate, new[]{ "右人指２", "右人指2" } },
            { HumanBodyBones.RightIndexDistal,       new[]{ "右人指３", "右人指3" } },
            { HumanBodyBones.RightMiddleProximal,     new[]{ "右中指１", "右中指1" } },
            { HumanBodyBones.RightMiddleIntermediate, new[]{ "右中指２", "右中指2" } },
            { HumanBodyBones.RightMiddleDistal,       new[]{ "右中指３", "右中指3" } },
            { HumanBodyBones.RightRingProximal,     new[]{ "右薬指１", "右薬指1" } },
            { HumanBodyBones.RightRingIntermediate, new[]{ "右薬指２", "右薬指2" } },
            { HumanBodyBones.RightRingDistal,       new[]{ "右薬指３", "右薬指3" } },
            { HumanBodyBones.RightLittleProximal,     new[]{ "右小指１", "右小指1" } },
            { HumanBodyBones.RightLittleIntermediate, new[]{ "右小指２", "右小指2" } },
            { HumanBodyBones.RightLittleDistal,       new[]{ "右小指３", "右小指3" } },
        };

        // Unity-required minimum humanoid bones.
        private static readonly HashSet<HumanBodyBones> Required = new()
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        public static HumanoidBuildResult Build(PmxModel model, Transform root, Transform[] boneTf, string[] boneNames)
        {
            var result = new HumanoidBuildResult();

            // Resolve humanoid bone -> bone index.
            var nameToIndex = new Dictionary<string, int>();
            for (int i = 0; i < model.Bones.Count; i++)
                nameToIndex[model.Bones[i].NameLocal ?? ""] = i;

            var hbbToIndex = new Dictionary<HumanBodyBones, int>();
            foreach (var kv in Map)
            {
                int idx = -1;
                foreach (var cand in kv.Value)
                    if (nameToIndex.TryGetValue(cand, out idx)) break;
                if (idx >= 0)
                {
                    hbbToIndex[kv.Key] = idx;
                    result.Mapped.Add(kv.Key.ToString());
                }
                else if (Required.Contains(kv.Key)) result.MissingRequired.Add(kv.Key.ToString());
                else result.MissingOptional.Add(kv.Key.ToString());
            }

            if (result.MissingRequired.Count > 0)
            {
                Debug.LogError($"[PmxHumanoid] Missing required bones: {string.Join(", ", result.MissingRequired)}");
                return result;
            }

            // Enforce T-pose on arms (A-pose -> arms horizontal), after bindposes are captured.
            LevelArm(hbbToIndex, boneTf, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
            LevelArm(hbbToIndex, boneTf, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);

            // Build HumanBone[] using Unity's canonical human-bone name strings.
            var human = new List<HumanBone>();
            foreach (var kv in hbbToIndex)
            {
                human.Add(new HumanBone
                {
                    boneName = boneNames[kv.Value],
                    humanName = HumanTrait.BoneName[(int)kv.Key],
                    limit = new HumanLimit { useDefaultValues = true }
                });
            }

            // Build SkeletonBone[] from current (T-posed) local transforms: root first, then all bones.
            var skeleton = new List<SkeletonBone>
            {
                new() { name = root.name, position = root.localPosition, rotation = root.localRotation, scale = root.localScale }
            };
            for (int i = 0; i < boneTf.Length; i++)
            {
                var t = boneTf[i];
                skeleton.Add(new SkeletonBone { name = boneNames[i], position = t.localPosition, rotation = t.localRotation, scale = t.localScale });
            }

            var desc = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f, lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                armStretch = 0.05f, legStretch = 0.05f,
                feetSpacing = 0f, hasTranslationDoF = false
            };

            var avatar = AvatarBuilder.BuildHumanAvatar(root.gameObject, desc);
            avatar.name = root.name + "_Avatar";
            result.Avatar = avatar;

            var sb = new StringBuilder();
            sb.AppendLine($"[PmxHumanoid] avatar isValid={avatar.isValid} isHuman={avatar.isHuman} mapped={result.Mapped.Count}");
            if (result.MissingOptional.Count > 0) sb.AppendLine($"  optional unmapped: {string.Join(", ", result.MissingOptional)}");
            Debug.Log(sb.ToString());
            return result;
        }

        // Rotate the upper+lower arm so the shoulder->hand chain is horizontal (T-pose).
        private static void LevelArm(Dictionary<HumanBodyBones, int> map, Transform[] bones,
            HumanBodyBones upperB, HumanBodyBones lowerB, HumanBodyBones handB)
        {
            if (!map.TryGetValue(upperB, out int ui) || !map.TryGetValue(lowerB, out int li)) return;
            Transform upper = bones[ui], lower = bones[li];
            Transform hand = map.TryGetValue(handB, out int hi) ? bones[hi] : null;

            LevelSegment(upper, lower.position);
            if (hand != null) LevelSegment(lower, hand.position);
        }

        // Rotate `joint` so the vector to `childPos` becomes horizontal along ±X (sign preserved).
        private static void LevelSegment(Transform joint, Vector3 childPos)
        {
            Vector3 cur = childPos - joint.position;
            if (cur.sqrMagnitude < 1e-8f || Mathf.Abs(cur.x) < 1e-5f) return;
            Vector3 tgt = new Vector3(Mathf.Sign(cur.x), 0f, 0f) * cur.magnitude;
            joint.rotation = Quaternion.FromToRotation(cur, tgt) * joint.rotation;
        }
    }
}
