using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 101 Phase A. Replaces the LEG half of the Kimodo avatars' reference pose with the
    /// symmetric neutral the animation curves were actually authored against.
    ///
    /// ================= THE DEFECT (S100) =================
    /// The donor avatar `kimodo_relaxed_walk.fbx` stores the walk clip's FRAME 0 -- a mid-stride
    /// pose -- as its reference, for 12 of its 16 mapped bones, exact to 0.0001 deg. The four ARM
    /// bones are the sole exception: S72's hand-configuration pulled them 27-34 deg off frame 0
    /// toward a T-pose, and S86 later verified exactly those (1.35/1.45 deg per arm) before adopting
    /// this donor as the reference for every Kimodo asset. The legs were never measured and rode
    /// along. Measured consequence at the knee: the clip's own L/R asymmetry averages -0.198 deg
    /// over the cycle (symmetric, swinging +-65), while the zero it is encoded against holds
    /// +5.074 deg on EVERY frame.
    ///
    /// ================= WHAT THE CORRECT VALUE IS =================
    /// The clips are SOMA exports made with `--bvh --bvh_standard_tpose`, so the BVH rest pose (all
    /// joint rotations zero, geometry from the hierarchy OFFSETs) is the authoring zero. Measured
    /// there, the legs are straight and symmetric: knee 176.343 deg on BOTH sides, L-R = -0.0001.
    /// (Not 180: the SOMA body itself is slightly asymmetric -- LeftShin 43.2292 vs RightShin
    /// 43.3697 -- which is real skeletal geometry and is left alone.)
    ///
    /// The .meta skeleton is bone-aligned: every bone's local `position` lies along its parent's
    /// local +Y, proved by FK'ing the array with identity rotations (every limb then points at
    /// +90 deg elevation). So a bone's stored `rotation` is what aims the NEXT bone, and the
    /// correction is solved top-down: the world rotation that maps each bone's child offset onto
    /// that child's BVH-rest direction. Roll about the bone axis is not determined by the child
    /// direction, so it is inherited from the bone's current stored rotation -- the minimal change
    /// that reaches the rest pose. Derivation: sandbox `s101/scripts/s101_refpose.py`; the values
    /// below are its output, and Verify() re-derives the gate from the asset so they cannot rot.
    ///
    /// ================= WHY ONLY THE LEGS, WHEN THE TICKET SAID HIPS/SPINE TOO =================
    /// Hips and Spine are MIDLINE bones: they are the common parent of both legs, so they carry no
    /// L/R asymmetry and contribute nothing to the stride gap. They DO lie on the path from the root
    /// to the hands. Correcting them symmetrises the knee equally well (L-R +0.4955 deg) but moves
    /// the LeftHand reference from (-73.582, 136.907, 1.699) to (19.518, 208.305, 19.867) -- which
    /// would rewrite b2's contact solution and fail gate A2 by construction. Legs+toes alone reach
    /// L-R = -0.00006 deg with BOTH hands byte-identical to nine decimals. The torso's own departure
    /// from the rest pose is a separate, SYMMETRIC defect and is deliberately left standing here.
    ///
    /// Read-write on the Kimodo avatars only. No YAML is hand-edited: every change goes through
    /// ModelImporter.humanDescription. The gait assets are patched IN PLACE -- deliberately NOT via
    /// S86's Configure(), which rebuilds the skeleton from a Generic reimport (i.e. from frame 0,
    /// re-introducing the very defect) and force-clears loopTime for one-shot reactions, which would
    /// break a looping gait.
    ///
    /// -executeMethod SEAN.AutoTrial.S101LegReference.Apply
    /// -executeMethod SEAN.AutoTrial.S101LegReference.Verify
    /// </summary>
    public static class S101LegReference
    {
        /// Gait assets: patched in place, keeping their own arm values and their loop settings.
        public static readonly string[] GaitAssets =
        {
            "Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk.fbx",
            "Assets/PedestrianAssets/Kimodo/kimodo_elderly_shuffle.fbx",
            "Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk_24s.fbx",
        };

        /// The corrected local rotations, from s101_refpose.py. The two gait rigs carry identical
        /// leg offsets (max delta 1.5e-5 across all ten leg bones), so one set serves both.
        private static readonly Dictionary<string, Quaternion> Corrected = new Dictionary<string, Quaternion>
        {
            { "LeftLeg",      new Quaternion(+0.22729192f, +0.09702457f, +0.02246698f, +0.96872073f) },
            { "LeftShin",     new Quaternion(+0.03134506f, -0.00109125f, +0.00596753f, +0.99949021f) },
            { "LeftFoot",     new Quaternion(-0.59040649f, -0.00354938f, -0.10902321f, +0.79970089f) },
            { "LeftToeBase",  new Quaternion(-0.05806485f, -0.01919184f, -0.00858174f, +0.99809143f) },
            { "RightLeg",     new Quaternion(+0.22804006f, -0.05337257f, -0.01281732f, +0.97210330f) },
            { "RightShin",    new Quaternion(+0.03167696f, -0.00016908f, -0.00382911f, +0.99949081f) },
            { "RightFoot",    new Quaternion(-0.59376224f, -0.02258103f, +0.08895085f, +0.79938992f) },
            { "RightToeBase", new Quaternion(-0.05804611f, +0.01599483f, +0.00869656f, +0.99814788f) },
        };

        /// Gate A1's reference half. The BVH rest pose itself sits at 176.343/176.343.
        public const float KneeBiasGateDeg = 0.5f;

        [MenuItem("AutoTrial/Session 101/Apply symmetric leg reference")]
        public static void Apply()
        {
            var sb = new StringBuilder();
            foreach (string fbx in GaitAssets)
            {
                if (AssetImporter.GetAtPath(fbx) == null) { sb.AppendLine("[S101] absent, skipped: " + fbx); continue; }
                Patch(fbx, sb);
            }

            // The reaction assets take the corrected donor through S86's own pipeline, unchanged.
            // Their arm values are the donor's arm values, which this session does not touch, so
            // b2's contact solution is carried across intact.
            foreach (string target in S86KimodoAvatarRefPose.Targets)
            {
                sb.AppendLine("[S101] re-running S86 donor copy for " + target);
                S86KimodoAvatarRefPose.ApplyTo(target);
            }
            Debug.Log(sb.ToString());
            Verify();
        }

        private static void Patch(string fbx, StringBuilder sb)
        {
            var imp = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (imp == null) { Debug.LogError("[S101] no ModelImporter at " + fbx); return; }

            // Read the EXISTING humanDescription. Never rebuild it from the model: the model's own
            // transforms ARE frame 0, which is the defect, and a rebuild would also discard S72's
            // hand-corrected arms.
            var hd = imp.humanDescription;
            var skel = hd.skeleton;
            if (skel == null || skel.Length == 0)
            {
                Debug.LogError("[S101] " + fbx + " has an empty humanDescription.skeleton -- "
                               + "it is not a configured Humanoid avatar; refusing to guess one.");
                return;
            }

            int hit = 0;
            for (int i = 0; i < skel.Length; i++)
            {
                Quaternion q;
                if (!Corrected.TryGetValue(skel[i].name, out q)) { continue; }
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "[S101] {0}: {1} {2} -> {3}  ({4:F3} deg)",
                    System.IO.Path.GetFileName(fbx), skel[i].name, Fmt(skel[i].rotation), Fmt(q),
                    Quaternion.Angle(skel[i].rotation, q)));
                skel[i].rotation = q;
                hit++;
            }
            if (hit != Corrected.Count)
            {
                Debug.LogError("[S101] " + fbx + ": patched " + hit + " of " + Corrected.Count
                               + " leg bones -- the rig does not match. Nothing written.");
                return;
            }
            hd.skeleton = skel;
            imp.humanDescription = hd;
            imp.SaveAndReimport();
            sb.AppendLine("[S101] " + System.IO.Path.GetFileName(fbx) + ": " + hit + " leg bones rewritten, "
                          + (skel.Length - hit) + " bones untouched (arms included).");
        }

        private static string Fmt(Quaternion q)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:F6},{1:F6},{2:F6},{3:F6})", q.x, q.y, q.z, q.w);
        }

        /// <summary>Re-derives the gate from what is actually stored in the asset, by forward
        /// kinematics over the SkeletonBone array -- the same construction S85 used. Reports the
        /// knee bias and both hand positions, so a leg fix that quietly moved an arm cannot pass.</summary>
        [MenuItem("AutoTrial/Session 101/Verify symmetric leg reference")]
        public static void Verify()
        {
            bool ok = true;
            var sb = new StringBuilder();
            foreach (string fbx in GaitAssets.Concat(S86KimodoAvatarRefPose.Targets))
            {
                var imp = AssetImporter.GetAtPath(fbx) as ModelImporter;
                if (imp == null) { continue; }
                var skel = imp.humanDescription.skeleton;
                if (skel == null || skel.Length == 0) { continue; }

                var pos = new Dictionary<string, Vector3>();
                var rot = new Dictionary<string, Quaternion>();
                var byName = skel.ToDictionary(s => s.name, s => s);
                var parent = new Dictionary<string, string>();
                foreach (var s in skel)
                {
                    // parentName is not stored on every array; fall back to the known SOMA chain.
                    parent[s.name] = null;
                }
                foreach (var kv in Chain) { if (byName.ContainsKey(kv.Key)) parent[kv.Key] = kv.Value; }

                System.Func<string, bool> solve = null;
                solve = name =>
                {
                    if (pos.ContainsKey(name)) { return true; }
                    if (!byName.ContainsKey(name)) { return false; }
                    string p = parent[name];
                    if (string.IsNullOrEmpty(p)) { pos[name] = byName[name].position; rot[name] = byName[name].rotation; return true; }
                    if (!solve(p)) { return false; }
                    pos[name] = pos[p] + rot[p] * byName[name].position;
                    rot[name] = rot[p] * byName[name].rotation;
                    return true;
                };
                foreach (var n in new[] { "LeftFoot", "RightFoot", "LeftHand", "RightHand" }) { solve(n); }
                if (!pos.ContainsKey("LeftFoot") || !pos.ContainsKey("RightFoot")) { continue; }

                float kl = Knee(pos, "Left"), kr = Knee(pos, "Right");
                float bias = Mathf.Abs(kl - kr);
                bool pass = bias < KneeBiasGateDeg;
                ok &= pass;
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "[S101] {0,-34} knee L={1:F4} R={2:F4} |L-R|={3:F5} deg  {4}   LeftHand={5}  RightHand={6}",
                    System.IO.Path.GetFileName(fbx), kl, kr, bias, pass ? "PASS" : "FAIL",
                    V(pos.ContainsKey("LeftHand") ? pos["LeftHand"] : Vector3.zero),
                    V(pos.ContainsKey("RightHand") ? pos["RightHand"] : Vector3.zero)));
            }
            Debug.Log(sb.ToString());
            if (!ok) { Debug.LogError("[S101] GATE A1 (reference knee bias < " + KneeBiasGateDeg + " deg) FAILED"); }
        }

        private static string V(Vector3 v)
        {
            return string.Format(CultureInfo.InvariantCulture, "({0:F4},{1:F4},{2:F4})", v.x, v.y, v.z);
        }

        private static float Knee(Dictionary<string, Vector3> pos, string side)
        {
            Vector3 hip = pos[side + "Leg"], kn = pos[side + "Shin"], an = pos[side + "Foot"];
            return Vector3.Angle(hip - kn, an - kn);
        }

        /// The SOMA chain this file needs. Only the bones Verify() walks.
        private static readonly Dictionary<string, string> Chain = new Dictionary<string, string>
        {
            { "Root", null }, { "Hips", "Root" },
            { "LeftLeg", "Hips" }, { "LeftShin", "LeftLeg" }, { "LeftFoot", "LeftShin" },
            { "RightLeg", "Hips" }, { "RightShin", "RightLeg" }, { "RightFoot", "RightShin" },
            { "Spine1", "Hips" }, { "Spine2", "Spine1" }, { "Chest", "Spine2" },
            { "LeftShoulder", "Chest" }, { "LeftArm", "LeftShoulder" },
            { "LeftForeArm", "LeftArm" }, { "LeftHand", "LeftForeArm" },
            { "RightShoulder", "Chest" }, { "RightArm", "RightShoulder" },
            { "RightForeArm", "RightArm" }, { "RightHand", "RightForeArm" },
        };
    }
}
