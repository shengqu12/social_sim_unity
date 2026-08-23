using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 84: the diagnosis ladder for the reported Kimodo in-engine twitch.
    ///
    /// The offline half of the ladder is already settled before this file runs: dumping per-frame
    /// world joint positions out of the BVH and out of the exported FBX (scripts/s84_dump_joints.py)
    /// gives IDENTICAL pose metrics -- jerk, >5 Hz energy fraction and amplitude all agree to four
    /// figures -- with exactly one difference: the FBX is 100x larger (hip height 97.82 vs 0.9782).
    /// So BVH -> FBX preserves the motion and changes only the unit. Everything this file measures
    /// is therefore downstream of a rig that Unity sees as ~176 units tall.
    ///
    /// Each variant below changes ONE import setting away from the shipped asset, so the rung where
    /// the metric moves names the mechanism. Nothing here touches a shipped .meta: every variant is
    /// a COPY under Assets/PedestrianAssets/Kimodo/S84/, and the shipped FBXs are read but never
    /// reimported.
    ///
    /// Sampling uses AnimationMode.SampleAnimationClip, which is the same path the Editor's own clip
    /// preview uses. For a Humanoid clip on a Humanoid target that performs the full muscle-space
    /// retarget; it does NOT run foot IK (IK is a playmode-only OnAnimatorIK concern), which is
    /// exactly the R2/R3 split -- this file is R1/R2, and R3 needs playmode.
    ///
    /// -executeMethod SEAN.AutoTrial.S84LadderProbe.Run
    /// </summary>
    public static class S84LadderProbe
    {
        private const string OutDirEnv = "AUTOTRIAL_S84_OUT";
        private const string WorkDir = "Assets/PedestrianAssets/Kimodo/S84";
        private const string TargetPrefab = "Prefabs/Rocketbox/Business_Male_01";

        // Verbatim from S83KimodoReactionImport, which took it verbatim from
        // Assets/PedestrianAssets/Kimodo/README.md section 4. Five trap slots marked there.
        private static readonly string[,] BoneMap =
        {
            { "Hips", "Hips" }, { "Spine", "Spine1" }, { "Head", "Head" },
            { "LeftUpperArm", "LeftArm" }, { "RightUpperArm", "RightArm" },
            { "LeftLowerArm", "LeftForeArm" }, { "RightLowerArm", "RightForeArm" },
            { "LeftHand", "LeftHand" }, { "RightHand", "RightHand" },
            { "LeftUpperLeg", "LeftLeg" }, { "RightUpperLeg", "RightLeg" },
            { "LeftLowerLeg", "LeftShin" }, { "RightLowerLeg", "RightShin" },
            { "LeftFoot", "LeftFoot" }, { "RightFoot", "RightFoot" },
            { "Chest", "Spine2" }, { "Neck", "Neck1" },
            { "LeftShoulder", "LeftShoulder" }, { "RightShoulder", "RightShoulder" },
            { "LeftToes", "LeftToeBase" }, { "RightToes", "RightToeBase" },
        };

        private class Variant
        {
            public string tag;
            public string src;                 // shipped FBX to copy
            public bool humanoid = true;
            public bool compressionOff = true;
            public bool loopTime;
            public float scale = 1f;           // globalScale; 1 = as shipped
            public bool onTarget = true;       // sample on Rocketbox (vs on its own rig)
            public bool useShippedAsset;       // no copy: sample the shipped asset untouched
            // Cross rung: play THIS variant's clip on ANOTHER variant's rig. Separates "is the
            // clip bad" from "is the avatar bad" -- the two are indistinguishable on a self-rig.
            public string modelFromTag;
            // Fix candidates for the b2 muscle blow-up. Both are ModelImporter/HumanDescription
            // settings on the KIMODO asset -- nothing outside the S84 write boundary.
            public bool translationDoF;
            public float armStretch = 0.05f;   // Unity default
            // Unity's per-bone muscle limits are what actually clamp here: `Left Forearm Stretch`
            // is the ELBOW FLEXION dof, and b2 bends the left elbow to 40 deg (140 deg of flexion),
            // far past the default humanoid limit. Widen the limits and the clamp should vanish.
            public float limitDeg;             // 0 = keep Unity defaults
            // Muscle space is a NONLINEAR function of the source rotations. Unity converts the
            // transform curves to muscle curves at import, sampling at the source rate unless this
            // is raised -- so a fast, large arc aliases. Documented remedy for a humanoid clip that
            // is jittery where its source is not.
            public ModelImporterHumanoidOversampling oversampling = ModelImporterHumanoidOversampling.X1;
            // THE ACTUAL SUSPECT. The Kimodo/SOMA rest pose is a PERFECT T-pose: LeftArm
            // (16.53,142.92,-1.29), LeftForeArm (45.27,142.92,-1.29), LeftHand (72.36,142.92,-1.29)
            // are exactly collinear, so the rest elbow angle is exactly 180.000 deg and Unity has
            // no geometry from which to infer the elbow hinge axis. Pre-bending the forearm in the
            // avatar's T-pose definition (NOT in the animation) is the standard cure. Axis index
            // 0/1/2 = local X/Y/Z of the forearm bone.
            public float elbowBendDeg;
            public int elbowAxis = 1;
        }

        public static void Run()
        {
            string outDir = System.Environment.GetEnvironmentVariable(OutDirEnv);
            if (string.IsNullOrEmpty(outDir)) { Fail("set " + OutDirEnv); return; }
            Directory.CreateDirectory(outDir);

            const string walk = "Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk.fbx";
            const string b2 = "Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx";

            var variants = new List<Variant>
            {
                // ---- R1: no retarget at all. Raw transform curves on the Kimodo rig itself. ----
                new Variant { tag = "walk_R1_generic",     src = walk, humanoid = false, onTarget = false },
                new Variant { tag = "b2_R1_generic",       src = b2,   humanoid = false, onTarget = false },

                // ---- R2: muscle-space retarget onto Rocketbox, foot IK not involved. ----
                // as shipped -- the state Sheng actually saw
                new Variant { tag = "walk_R2_shipped",     src = walk, useShippedAsset = true },
                new Variant { tag = "b2_R2_shipped",       src = b2,   useShippedAsset = true },
                // one variable off the shipped state, each
                new Variant { tag = "walk_R2_nocomp",      src = walk, loopTime = true  },  // compression OFF only
                new Variant { tag = "walk_R2_noloop",      src = walk, loopTime = false },  // + loop pose OFF
                new Variant { tag = "walk_R2_scale01",     src = walk, loopTime = false, scale = 0.01f },
                new Variant { tag = "b2_R2_scale01",       src = b2,   loopTime = false, scale = 0.01f },
                // retarget onto the Kimodo rig itself: isolates muscle round-trip from body swap
                new Variant { tag = "walk_R2_selfrig",     src = walk, loopTime = false, onTarget = false },
                new Variant { tag = "b2_R2_selfrig",       src = b2,   loopTime = false, onTarget = false },
                // clip and avatar swapped between the two Kimodo assets
                new Variant { tag = "b2clip_on_walkrig",   src = b2,   loopTime = false, onTarget = false,
                              modelFromTag = "walk_R2_selfrig" },
                new Variant { tag = "walkclip_on_b2rig",   src = walk, loopTime = false, onTarget = false,
                              modelFromTag = "b2_R2_selfrig" },
                // ---- candidate fixes, measured on the rig where the defect is visible ----
                new Variant { tag = "b2_fix_tdof",    src = b2, loopTime = false, onTarget = false,
                              translationDoF = true },
                new Variant { tag = "b2_fix_stretch", src = b2, loopTime = false, onTarget = false,
                              armStretch = 0.5f },
                new Variant { tag = "b2_fix_both",    src = b2, loopTime = false, onTarget = false,
                              translationDoF = true, armStretch = 0.5f },
                new Variant { tag = "b2_fix_limits",  src = b2, loopTime = false, onTarget = false,
                              limitDeg = 179f },
                new Variant { tag = "walk_fix_limits", src = walk, loopTime = false, onTarget = false,
                              limitDeg = 179f },
                new Variant { tag = "b2_fix_os2", src = b2, loopTime = false, onTarget = false,
                              oversampling = ModelImporterHumanoidOversampling.X2 },
                new Variant { tag = "b2_fix_os4", src = b2, loopTime = false, onTarget = false,
                              oversampling = ModelImporterHumanoidOversampling.X4 },
                new Variant { tag = "b2_fix_os8", src = b2, loopTime = false, onTarget = false,
                              oversampling = ModelImporterHumanoidOversampling.X8 },
                new Variant { tag = "b2_tpose_Xp", src = b2, loopTime = false, onTarget = false, elbowBendDeg =  8f, elbowAxis = 0 },
                new Variant { tag = "b2_tpose_Xn", src = b2, loopTime = false, onTarget = false, elbowBendDeg = -8f, elbowAxis = 0 },
                new Variant { tag = "b2_tpose_Yp", src = b2, loopTime = false, onTarget = false, elbowBendDeg =  8f, elbowAxis = 1 },
                new Variant { tag = "b2_tpose_Yn", src = b2, loopTime = false, onTarget = false, elbowBendDeg = -8f, elbowAxis = 1 },
                new Variant { tag = "b2_tpose_Zp", src = b2, loopTime = false, onTarget = false, elbowBendDeg =  8f, elbowAxis = 2 },
                new Variant { tag = "b2_tpose_Zn", src = b2, loopTime = false, onTarget = false, elbowBendDeg = -8f, elbowAxis = 2 },
                new Variant { tag = "b2_tpose_Z4",  src = b2,   loopTime = false, onTarget = false, elbowBendDeg =  4f, elbowAxis = 2 },
                new Variant { tag = "b2_tpose_Z15", src = b2,   loopTime = false, onTarget = false, elbowBendDeg = 15f, elbowAxis = 2 },
                new Variant { tag = "b2_tpose_Z25", src = b2,   loopTime = false, onTarget = false, elbowBendDeg = 25f, elbowAxis = 2 },
                new Variant { tag = "walk_tpose_Z8", src = walk, loopTime = false, onTarget = false, elbowBendDeg = 8f, elbowAxis = 2 },
                new Variant { tag = "walk_R2_selfrig_s01", src = walk, loopTime = false, onTarget = false, scale = 0.01f },
            };

            if (!AssetDatabase.IsValidFolder(WorkDir))
                AssetDatabase.CreateFolder("Assets/PedestrianAssets/Kimodo", "S84");

            foreach (var v in variants)
            {
                try { Sample(v, outDir); }
                catch (Exception e) { Debug.LogError("[S84] variant '" + v.tag + "' FAILED: " + e); }
            }
            Debug.Log("[S84] ladder probe complete -> " + outDir);
        }

        private static void Sample(Variant v, string outDir)
        {
            string path = v.src;
            if (!v.useShippedAsset)
            {
                path = WorkDir + "/" + v.tag + ".fbx";
                if (!File.Exists(path)) File.Copy(v.src, path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                Configure(path, v);
            }

            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (clip == null) throw new Exception("no AnimationClip at " + path);

            GameObject go;
            if (v.onTarget)
            {
                var prefab = Resources.Load<GameObject>(TargetPrefab);
                if (prefab == null) throw new Exception("no prefab at Resources/" + TargetPrefab);
                go = UnityEngine.Object.Instantiate(prefab);
            }
            else
            {
                string modelPath = string.IsNullOrEmpty(v.modelFromTag)
                    ? path : WorkDir + "/" + v.modelFromTag + ".fbx";
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                if (model == null) throw new Exception("no model at " + modelPath);
                go = UnityEngine.Object.Instantiate(model);
            }
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;

            var animator = go.GetComponentInChildren<Animator>();
            string avatarInfo = animator == null ? "NO ANIMATOR"
                : ("avatar=" + (animator.avatar == null ? "null" : animator.avatar.name)
                   + " isHuman=" + (animator.avatar != null && animator.avatar.isHuman));

            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            Debug.Log(string.Format(
                "[S84cfg] {0}: clip='{1}' len={2:F4} loop={3} type={4} comp={5} scale={6} useFileScale={7} {8}",
                v.tag, clip.name, clip.length, clip.isLooping,
                imp != null ? imp.animationType.ToString() : "?",
                imp != null ? imp.animationCompression.ToString() : "?",
                imp != null ? imp.globalScale.ToString("F4", CultureInfo.InvariantCulture) : "?",
                imp != null && imp.useFileScale, avatarInfo));

            var xforms = go.GetComponentsInChildren<Transform>(true);
            var rows = new List<string>();
            int n = Mathf.Max(2, Mathf.RoundToInt(clip.length * 30f));

            AnimationMode.StartAnimationMode();
            try
            {
                for (int i = 0; i <= n; i++)
                {
                    float t = Mathf.Min(clip.length, i / 30f);
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(go, clip, t);
                    AnimationMode.EndSampling();
                    foreach (var x in xforms)
                    {
                        Vector3 p = x.position;
                        rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F6},{3:F6},{4:F6}",
                            i, x.name, p.x, p.y, p.z));
                    }
                }
            }
            finally { AnimationMode.StopAnimationMode(); }

            UnityEngine.Object.DestroyImmediate(go);
            string csv = Path.Combine(outDir, v.tag + ".csv");
            File.WriteAllText(csv, "frame,bone,x,y,z\n" + string.Join("\n", rows) + "\n");
            Debug.Log("[S84dump] " + v.tag + " -> " + csv + " frames=" + (n + 1) + " bones=" + xforms.Length);
        }

        private static void Configure(string path, Variant v)
        {
            var imp = (ModelImporter)AssetImporter.GetAtPath(path);
            imp.animationType = ModelImporterAnimationType.Generic;
            if (v.scale != 1f) { imp.useFileScale = false; imp.globalScale = v.scale; }
            imp.animationCompression = v.compressionOff
                ? ModelImporterAnimationCompression.Off
                : ModelImporterAnimationCompression.Optimal;
            imp.SaveAndReimport();

            if (v.humanoid)
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
                {
                    name = t.name, position = t.localPosition,
                    rotation = t.localRotation, scale = t.localScale,
                }).ToArray();

                if (v.elbowBendDeg != 0f)
                {
                    var ax = v.elbowAxis == 0 ? Vector3.right : v.elbowAxis == 1 ? Vector3.up : Vector3.forward;
                    for (int i = 0; i < skeleton.Length; i++)
                    {
                        // mirror the bend so both elbows fold the same anatomical way
                        if (skeleton[i].name == "LeftForeArm")
                            skeleton[i].rotation = skeleton[i].rotation * Quaternion.AngleAxis(v.elbowBendDeg, ax);
                        else if (skeleton[i].name == "RightForeArm")
                            skeleton[i].rotation = skeleton[i].rotation * Quaternion.AngleAxis(-v.elbowBendDeg, ax);
                    }
                }
                var present = new HashSet<string>(skeleton.Select(s => s.name));
                var human = new List<HumanBone>();
                for (int i = 0; i < BoneMap.GetLength(0); i++)
                {
                    if (!present.Contains(BoneMap[i, 1])) throw new Exception("missing bone " + BoneMap[i, 1]);
                    var hb = new HumanBone { humanName = BoneMap[i, 0], boneName = BoneMap[i, 1] };
                    if (v.limitDeg > 0f)
                    {
                        hb.limit.useDefaultValues = false;
                        hb.limit.min = new Vector3(-v.limitDeg, -v.limitDeg, -v.limitDeg);
                        hb.limit.max = new Vector3(v.limitDeg, v.limitDeg, v.limitDeg);
                    }
                    else hb.limit.useDefaultValues = true;
                    human.Add(hb);
                }
                var hd = imp.humanDescription;
                hd.human = human.ToArray();
                hd.skeleton = skeleton;
                hd.hasTranslationDoF = v.translationDoF;
                hd.armStretch = v.armStretch;
                imp.humanDescription = hd;
                imp.animationType = ModelImporterAnimationType.Human;
                imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                imp.humanoidOversampling = v.oversampling;
            }

            var clips = imp.defaultClipAnimations;
            if (clips != null && clips.Length > 0)
            {
                for (int i = 0; i < clips.Length; i++)
                {
                    clips[i].loopTime = v.loopTime;
                    clips[i].loop = v.loopTime;
                }
                imp.clipAnimations = clips;
            }
            imp.SaveAndReimport();
        }

        private static void Fail(string msg)
        {
            Debug.LogError("[S84] " + msg);
            EditorApplication.Exit(1);
        }
    }
}
