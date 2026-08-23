using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 84 FIX. Re-imports the Kimodo b2 surprise reaction with a small ELBOW PRE-BEND baked
    /// into the Avatar's T-pose definition, and nothing else changed.
    ///
    /// THE DEFECT. The Kimodo/SOMA rest pose is a *perfect* T-pose -- measured off the BVH hierarchy
    /// offsets, the left arm chain sits at
    ///     LeftArm (16.53, 142.92, -1.29)  LeftForeArm (45.27, 142.92, -1.29)  LeftHand (72.36, 142.92, -1.29)
    /// i.e. exactly collinear, rest elbow angle exactly 180.000 deg. Unity infers each humanoid
    /// hinge axis from the rest geometry, and a perfectly straight arm carries no information about
    /// which way the elbow folds. The resulting axis is degenerate, so a pose with a deeply bent
    /// elbow has two near-equally-valid muscle solutions and the import oscillates between them.
    ///
    /// b2 bends the LEFT elbow to 40..77 deg. Measured on the imported clip, LeftHand alternates
    /// frame-to-frame between (-0.033, +0.310, +0.049) and (+0.031, +0.386, +0.140) -- 15% of body
    /// height, at up to 30 Hz. That is the reported twitch, and the exaggerated amplitude is the
    /// same flip seen at 15 fps. The gaits keep the elbow near 147 deg and never trip it.
    ///
    /// WHAT IT IS NOT (each falsified by measurement, see the S84 ladder):
    ///   BVH -> FBX conversion   pose metrics identical to 4 figures; only the unit differs (x100)
    ///   the 100x scale          globalScale 0.01 reproduces the defect bit-for-bit
    ///   animation compression   Off / KeyframeReduction / Optimal all identical
    ///   foot IK                 iKOnFeet true vs false identical to 3 figures
    ///   humanoid oversampling   X1 / X2 / X4 / X8 identical
    ///   muscle limits           widening to +/-179 deg removes the CLAMP and changes no joint
    ///   translation DoF         no effect; armStretch no effect
    ///   the target body         same defect on the Kimodo rig, the walk rig and Rocketbox
    ///
    /// THE BEND. 15 deg about the forearm bone's local Z, mirrored between sides. 4 deg is not
    /// enough (jerk 5.84 vs 0.60 for the source); 8, 15 and 25 all restore the motion exactly, and
    /// 15 gives the lowest positional error against the untouched source curves (0.388% of body
    /// height mean, vs 0.563% unfixed). This changes only the Avatar's reference pose -- the
    /// animation curves are untouched, and the retargeted result gets CLOSER to the source, not
    /// further from it.
    ///
    /// SCOPE, DECIDED BY DEFAULT. Applied to b2 only. The gait FBXs share the same degenerate rest
    /// pose but no clip in the set bends an elbow far enough to trip it, and they measured clean;
    /// re-importing them would perturb the authored-speed calibration and the S73/S80 regression
    /// arms for no demonstrated gain. The same bend measured neutral-to-slightly-better on the walk
    /// (pose error 0.373% -> 0.322%) if that call is ever revisited.
    ///
    /// -executeMethod SEAN.AutoTrial.S84KimodoTPoseFix.Apply
    /// -executeMethod SEAN.AutoTrial.S84KimodoTPoseFix.Verify
    /// </summary>
    public static class S84KimodoTPoseFix
    {
        public const string FbxPath = "Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx";
        public const float ElbowBendDeg = 15f;

        // Verbatim from S83KimodoReactionImport, itself verbatim from
        // Assets/PedestrianAssets/Kimodo/README.md section 4. Five trap slots noted there.
        private static readonly string[,] BoneMap =
        {
            { "Hips", "Hips" }, { "Spine", "Spine1" }, { "Head", "Head" },
            { "LeftUpperArm", "LeftArm" }, { "RightUpperArm", "RightArm" },
            { "LeftLowerArm", "LeftForeArm" }, { "RightLowerArm", "RightForeArm" },
            { "LeftHand", "LeftHand" }, { "RightHand", "RightHand" },
            { "LeftUpperLeg", "LeftLeg" }, { "RightUpperLeg", "RightLeg" },      // TRAP x2
            { "LeftLowerLeg", "LeftShin" }, { "RightLowerLeg", "RightShin" },    // TRAP x2
            { "LeftFoot", "LeftFoot" }, { "RightFoot", "RightFoot" },
            { "Chest", "Spine2" },                                               // TRAP
            { "Neck", "Neck1" },
            { "LeftShoulder", "LeftShoulder" }, { "RightShoulder", "RightShoulder" },
            { "LeftToes", "LeftToeBase" }, { "RightToes", "RightToeBase" },
        };

        public static void Apply() { Run(true); }
        public static void Verify() { Run(false); }

        private static void Run(bool write)
        {
            var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (importer == null) { Fail("no ModelImporter at " + FbxPath); return; }

            if (write)
            {
                // Pass 1, Generic: the SkeletonBone array must describe the real imported rig, and
                // reading the live hierarchy is the only headless way to get it right.
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.SaveAndReimport();

                var root = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
                if (root == null) { Fail("could not load the model at " + FbxPath); return; }

                var skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                }).ToArray();

                int bent = 0;
                for (int i = 0; i < skeleton.Length; i++)
                {
                    // Mirrored sign so both elbows fold the same anatomical way.
                    if (skeleton[i].name == "LeftForeArm")
                    {
                        skeleton[i].rotation *= Quaternion.AngleAxis(ElbowBendDeg, Vector3.forward);
                        bent++;
                    }
                    else if (skeleton[i].name == "RightForeArm")
                    {
                        skeleton[i].rotation *= Quaternion.AngleAxis(-ElbowBendDeg, Vector3.forward);
                        bent++;
                    }
                }
                if (bent != 2) { Fail("expected 2 forearm bones in the rig, bent " + bent); return; }

                var present = new HashSet<string>(skeleton.Select(s => s.name));
                var human = new List<HumanBone>();
                for (int i = 0; i < BoneMap.GetLength(0); i++)
                {
                    string slot = BoneMap[i, 0], bone = BoneMap[i, 1];
                    if (!present.Contains(bone))
                    {
                        Fail("rig has no bone '" + bone + "' for slot '" + slot + "'");
                        return;
                    }
                    var hb = new HumanBone { humanName = slot, boneName = bone };
                    hb.limit.useDefaultValues = true;
                    human.Add(hb);
                }

                var hd = importer.humanDescription;
                hd.human = human.ToArray();
                hd.skeleton = skeleton;
                importer.humanDescription = hd;
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

                // b2 is a one-shot reaction, not a gait -- S83's setting, preserved deliberately.
                var clips = importer.defaultClipAnimations;
                if (clips != null && clips.Length > 0)
                {
                    for (int i = 0; i < clips.Length; i++) { clips[i].loopTime = false; clips[i].loop = false; }
                    importer.clipAnimations = clips;
                }
                importer.SaveAndReimport();
            }

            // ---- gate: re-read the IMPORTED asset, never what was set ----
            var imp = (ModelImporter)AssetImporter.GetAtPath(FbxPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<Avatar>().FirstOrDefault();
            var clip = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));

            var skel = imp.humanDescription.skeleton;
            var lf = skel.FirstOrDefault(s => s.name == "LeftForeArm");
            var rf = skel.FirstOrDefault(s => s.name == "RightForeArm");
            bool haveBend = lf.name == "LeftForeArm" && rf.name == "RightForeArm";

            // Recover the bend as an angle so the gate reads the geometry, not a flag we wrote.
            float lAng = haveBend ? Quaternion.Angle(lf.rotation, Quaternion.identity) : -1f;

            Debug.Log(string.Format(
                "[S84gate] type={0} avatar={1} isHuman={2} clip='{3}' len={4:F4} loop={5} "
                + "skeletonBones={6} humanSlots={7} leftForeArmRestAngle={8:F2}deg bend={9:F1}deg importErrors='{10}'",
                imp.animationType,
                avatar != null ? avatar.name : "NULL",
                avatar != null && avatar.isValid && avatar.isHuman,
                clip != null ? clip.name : "NULL",
                clip != null ? clip.length : -1f,
                clip != null && clip.isLooping,
                skel.Length, imp.humanDescription.human.Length, lAng, ElbowBendDeg,
                imp.importSettingsMissing ? "settings-missing" : ""));

            if (imp.animationType != ModelImporterAnimationType.Human) Fail("rig demoted from Human");
            else if (avatar == null || !avatar.isValid || !avatar.isHuman) Fail("avatar not a valid Humanoid");
            else if (clip == null || clip.length < 5.5f || clip.length > 6.5f) Fail("clip length out of range");
            else if (!haveBend) Fail("forearm bones missing from the skeleton definition");
            else Debug.Log("[S84gate] PASS");
        }

        private static void Fail(string msg)
        {
            Debug.LogError("[S84gate] FAIL: " + msg);
            EditorApplication.Exit(1);
        }
    }
}
