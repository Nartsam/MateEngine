// M3: convert PMX rigid-body/joint physics into DynamicBone chains.
//
// MMD cloth/hair physics are chains of *dynamic* rigid bodies (PhysicsMode 1/2)
// anchored to a *static* rigid body (mode 0) on a skeleton bone. We detect the
// dynamic bones, find each chain's root (a dynamic bone whose parent is not
// dynamic), and drive those roots with DynamicBone (which already ships in the
// project). This is a heuristic approximation of MMD's rigid-body sim — good
// enough for swing; per-model tuning expected. Body collision (skirt-vs-legs)
// is deferred (no colliders in v1).
//
// See Docs/DECISIONS_RECORD.md ADR-0008.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MateEngine.PmxPipeline
{
    public class PhysicsBuildResult
    {
        public int DynamicBones, Hair, Skirt, Breast, Other, ChainRoots;
    }

    public static class PmxPhysics
    {
        private enum Cat { Hair, Skirt, Breast, Other }

        public static PhysicsBuildResult Build(PmxModel model, Transform root, Transform[] boneTf)
        {
            var result = new PhysicsBuildResult();

            // Bones with a dynamic rigid body (PhysicsMode 1 = physics, 2 = physics+bone).
            var dynamic = new HashSet<int>();
            foreach (var rb in model.RigidBodies)
                if (rb.PhysicsMode != 0 && rb.BoneIndex >= 0 && rb.BoneIndex < model.Bones.Count)
                    dynamic.Add(rb.BoneIndex);
            if (dynamic.Count == 0) return result;

            // Chain root = dynamic bone whose parent is not dynamic.
            var rootsByCat = new Dictionary<Cat, List<Transform>>();
            foreach (Cat c in System.Enum.GetValues(typeof(Cat))) rootsByCat[c] = new List<Transform>();

            foreach (int bi in dynamic)
            {
                int parent = model.Bones[bi].ParentIndex;
                if (parent >= 0 && dynamic.Contains(parent)) continue; // not a root

                result.ChainRoots++;
                var cat = Classify(model, bi, dynamic);
                rootsByCat[cat].Add(boneTf[bi]);
            }

            // One holder object, one DynamicBone component per non-empty category.
            var holder = new GameObject("DynamicBones");
            holder.transform.SetParent(root, false);

            foreach (var kv in rootsByCat)
            {
                if (kv.Value.Count == 0) continue;
                AddComponentFor(holder, kv.Key, kv.Value);
                result.DynamicBones++;
                switch (kv.Key)
                {
                    case Cat.Hair: result.Hair = kv.Value.Count; break;
                    case Cat.Skirt: result.Skirt = kv.Value.Count; break;
                    case Cat.Breast: result.Breast = kv.Value.Count; break;
                    case Cat.Other: result.Other = kv.Value.Count; break;
                }
            }
            return result;
        }

        // Classify by the chain-root bone name and its first non-dynamic ancestor.
        private static Cat Classify(PmxModel model, int boneIndex, HashSet<int> dynamic)
        {
            string self = model.Bones[boneIndex].NameLocal ?? "";

            int a = model.Bones[boneIndex].ParentIndex;
            while (a >= 0 && dynamic.Contains(a)) a = model.Bones[a].ParentIndex;
            string anchor = a >= 0 ? (model.Bones[a].NameLocal ?? "") : "";

            string s = self + "|" + anchor;
            if (Has(s, "胸", "Bust", "Breast")) return Cat.Breast;
            if (Has(s, "Hair", "髪", "頭", "Head", "Earring", "メガネ")) return Cat.Hair;
            if (Has(s, "Skirt", "スカート", "下半身", "腰", "qz", "qh", "yb", "zb", "裙")) return Cat.Skirt;
            return Cat.Other;
        }

        private static bool Has(string s, params string[] keys) => keys.Any(k => s.Contains(k));

        private static void AddComponentFor(GameObject holder, Cat cat, List<Transform> roots)
        {
            var db = holder.AddComponent<DynamicBone>();
            db.m_Roots = roots;
            db.m_UpdateRate = 60f;
            db.m_FreezeAxis = DynamicBone.FreezeAxis.None;
            db.m_Radius = 0f;

            // Heuristic cloth/hair defaults (world units; model ~1.6 m). Tune per model.
            // High m_Damping + m_Stiffness keep short hair/cloth chains from oscillating
            // (violent whip on small head movements = underdamped). m_Inert kept LOW so cloth
            // follows the body when the pet is dragged (high inertia pins it in world space).
            // Balance: high m_Stiffness limits swing amplitude, m_Elasticity gives a snappy
            // return to rest, moderate m_Damping avoids both oscillation (too low) and a
            // floaty "moon" feel + slow settle (too high).
            switch (cat)
            {
                case Cat.Hair:
                    db.m_Damping = 0.30f; db.m_Elasticity = 0.25f; db.m_Stiffness = 0.55f;
                    db.m_Inert = 0.20f; db.m_Gravity = new Vector3(0, -0.0010f, 0);
                    break;
                case Cat.Skirt:
                    db.m_Damping = 0.30f; db.m_Elasticity = 0.15f; db.m_Stiffness = 0.30f;
                    db.m_Inert = 0.15f; db.m_Gravity = new Vector3(0, -0.0015f, 0);
                    break;
                case Cat.Breast:
                    db.m_Damping = 0.35f; db.m_Elasticity = 0.20f; db.m_Stiffness = 0.40f;
                    db.m_Inert = 0.20f; db.m_Gravity = Vector3.zero;
                    break;
                default:
                    db.m_Damping = 0.30f; db.m_Elasticity = 0.20f; db.m_Stiffness = 0.40f;
                    db.m_Inert = 0.20f; db.m_Gravity = new Vector3(0, -0.0010f, 0);
                    break;
            }
        }
    }
}
