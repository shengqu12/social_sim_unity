using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 86. Configures a Kimodo reaction FBX as a Humanoid rig whose Avatar reference pose is
    /// the RIG'S REST POSE — a T-pose — instead of the clip's first animated frame.
    ///
    /// ================= WHAT WENT WRONG BEFORE, AND WHY THIS FILE EXISTS =================
    /// S83's importer built humanDescription.skeleton from
    ///     root.GetComponentsInChildren&lt;Transform&gt;().localPosition / localRotation
    /// of the Generic-imported model. For a Blender-exported BVH those node transforms are the pose
    /// at export time — the clip's FRAME 1 — not the armature rest pose. So every Kimodo reaction
    /// avatar stored its own clip's opening pose as its T-pose:
    ///
    ///   reference pose            L/R upper-arm elevation   L/R elbow      offset from the true T-pose
    ///   BVH rest pose (truth)        +0.00 / +0.00 deg      179.99 deg     --
    ///   kimodo_relaxed_walk          -1.34 / -1.43 deg      177.2  deg     1.35 / 1.45 deg
    ///   b2  (S83 headless build)    -69.01 / -76.16 deg     145.6 / 160.3  69.05 / 76.26 deg
    ///   b6  (same method)           -74.22 / -71.57 deg      64.0 /  60.2  74.49 / 72.21 deg
    ///
    /// Every frame is then encoded against an arms-down zero and replayed against a true T-pose,
    /// which adds ~70-76 deg of arm rotation to every frame. Measured on b2: the left hand stops
    /// reaching the head (min |Hand-Head| / head height 0.1365 -> 0.3762) and BOTH hands rise above
    /// the shoulders (elev -0.080/-0.352 -> +0.198/+0.174). That is the "surrender" pose Sheng saw.
    /// b6 is the control: its authored content IS both hands raised, and it retargets to a T-pose
    /// starfish, so the defect is not b2-specific and not pose-dependent.
    ///
    /// ================= WHY THE DONOR ROUTE IS THE SANCTIONED PATH =================
    /// The rest pose is NOT recoverable from these FBXs. bvh_to_fbx.py exports
    /// object_types={"ARMATURE"} with no mesh, so there is no skin and therefore no bind pose for
    /// Unity to read; the only pose in the file is the node transforms, which are the frame-1 pose
    /// that caused the bug. Deriving it from the BVH is not available either — the .bvh files live
    /// in the generation sandbox, outside this repository.
    ///
    /// So the reference pose is taken from a DONOR asset: kimodo_relaxed_walk, the same 79-bone SOMA
    /// rig, configured by hand in the Unity Editor in S72 and independently verified against the BVH
    /// hierarchy offsets at 1.35 deg (left) and 1.45 deg (right). This is deliberate and named, not
    /// accidental — see ReferenceDonorFbx and DonorMaxOffsetDeg below. If a future Kimodo batch is
    /// ever exported WITH a mesh, prefer the bind pose and retire the donor.
    ///
    /// ================= WHAT THIS SUPERSEDES =================
    /// Commit 024b827 (S84) added a +/-15 deg elbow pre-bend to this same skeleton array. That was
    /// the wrong lever on the right defect: it silenced the twitch by distorting the reference pose
    /// FURTHER (stored elbows 145.56/160.25 -> 143.10/155.31, i.e. 2.46 and 4.94 deg further from
    /// the true 179.99), which is what turned "left hand at the head but both arms raised" into the
    /// fully symmetric surrender. S84's causal story — a perfectly collinear rest arm giving Unity a
    /// degenerate elbow hinge axis — was reasoning from the BVH rest pose, which this avatar never
    /// stored. THE PRE-BEND IS GONE; see ElbowPreBendDeg. S84's measurements and its falsifications
    /// (scale, animation compression, loop pose, foot IK, humanoid oversampling X1..X8, translation
    /// DoF, armStretch, muscle limits, and the target body) all still stand and are not revisited
    /// here; correcting the reference pose fixes the twitch on its own, with no bend at all —
    /// S84's own jerk metric, 19 mapped joints: source 0.227, frame-1 reference 6.218,
    /// rest-pose reference 0.277.
    ///
    /// -executeMethod SEAN.AutoTrial.S86KimodoAvatarRefPose.Apply
    /// -executeMethod SEAN.AutoTrial.S86KimodoAvatarRefPose.Verify
    /// </summary>
    public static class S86KimodoAvatarRefPose
    {
        /// The reference-pose donor. Read-only here: this script never reimports it, and G3 checks
        /// its .meta is byte-unchanged.
        public const string ReferenceDonorFbx = "Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk.fbx";

        /// The donor's own measured departure from the BVH rest pose, per arm. Recorded so the
        /// tolerance the whole fix inherits is visible rather than assumed.
        public const float DonorMaxOffsetDeg = 1.45f;

        /// S84's pre-bend, retained ONLY as a named zero so that "no bend" reads as a decision
        /// rather than an omission. Do not reintroduce without re-reading the block above.
        public const float ElbowPreBendDeg = 0f;

        /// The reference pose must come out a T-pose. Asserted against the imported asset, not
        /// against what we set.
        public const float TPoseElevToleranceDeg = 5f;
        public const float TPoseElbowToleranceDeg = 8f;

        public static readonly string[] Targets =
        {
            "Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx",
        };

        // Unity Humanoid slot -> SOMA bone, verbatim from Assets/PedestrianAssets/Kimodo/README.md
        // section 4. The five traps are marked there: SOMA names the thigh `LeftLeg` and the shin
        // `LeftShin`, and its four-segment spine puts Unity's Chest slot on `Spine2`.
        /// S104b reads the same map to configure scratch candidates through the same slots.
        public static string[,] BoneMapPublic { get { return BoneMap; } }

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

        /// Configure and gate ONE asset. Exposed so other sessions can drive the same importer over
        /// scratch variants without going through the environment-variable target list.
        public static void ApplyTo(string fbx)
        {
            Configure(fbx);
            Gate(fbx);
        }

        public static void Apply()
        {
            foreach (var t in TargetList()) Configure(t);
            foreach (var t in TargetList()) Gate(t);
        }

        public static void Verify()
        {
            foreach (var t in TargetList()) Gate(t);
        }

        /// AUTOTRIAL_S86_TARGETS overrides the shipped list, so the same code path can be exercised
        /// on a scratch copy (b6) without promoting anything.
        private static IEnumerable<string> TargetList()
        {
            string env = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S86_TARGETS");
            return string.IsNullOrEmpty(env)
                ? (IEnumerable<string>)Targets
                : env.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
        }

        private static void Configure(string fbx)
        {
            var imp = AssetImporter.GetAtPath(fbx) as ModelImporter;
            if (imp == null) { Fail("no ModelImporter at " + fbx); return; }

            // Pass 1, Generic: the SkeletonBone array must name the bones of the ACTUAL imported rig.
            // Only the NAMES and the hierarchy come from here; every pose value is replaced below.
            imp.animationType = ModelImporterAnimationType.Generic;
            imp.SaveAndReimport();

            var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (root == null) { Fail("could not load the model at " + fbx); return; }
            var skeleton = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
            {
                name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale,
            }).ToArray();

            // S104 Phase 0: the reference is rig data and now lives in its own file. The FBX donor
            // remains as a fallback ONLY so this keeps working if the data file is missing, and it
            // says which one it used -- a silent fallback here would reintroduce exactly the
            // clip-shaped reference S100 found.
            var donor = S104CanonicalReference.TryLoad();
            if (donor != null)
            {
                Debug.Log("[S86] reference source: canonical " + S104CanonicalReference.Path_
                          + " (" + donor.Count + " bones)");
            }
            else
            {
                var donorImp = AssetImporter.GetAtPath(ReferenceDonorFbx) as ModelImporter;
                if (donorImp == null) { Fail("reference donor missing: " + ReferenceDonorFbx); return; }
                donor = donorImp.humanDescription.skeleton.ToDictionary(s => s.name, s => s);
                Debug.LogWarning("[S86] canonical reference absent -- FELL BACK to the clip donor "
                                 + ReferenceDonorFbx + ". Run S104CanonicalReference.Export.");
            }

            var unmatched = new List<string>();
            int taken = 0;
            for (int i = 0; i < skeleton.Length; i++)
            {
                if (!donor.TryGetValue(skeleton[i].name, out var d)) { unmatched.Add(skeleton[i].name); continue; }
                skeleton[i].position = d.position;
                skeleton[i].rotation = d.rotation;
                skeleton[i].scale = d.scale;
                taken++;
            }

            // The one bone that never matches is the FBX's own model-root node, which is named after
            // the file (`kimodo_b2_surprised` vs `kimodo_relaxed_walk`). It is not a skeleton joint:
            // it is the container node carrying Blender's -Z-forward/Y-up conversion, it has no
            // parent inside the rig, and both files got it from the same exporter with the same
            // settings. It KEEPS ITS OWN value, which is correct and must not be donor-copied --
            // copying a node named after a different file would be meaningless. Asserted, not
            // assumed: anything else unmatched is a rig mismatch and fails here.
            string modelRoot = System.IO.Path.GetFileNameWithoutExtension(fbx);
            var unexpected = unmatched.Where(n => n != modelRoot).ToList();
            if (unexpected.Count > 0)
            {
                Fail("rig does not match the donor: " + unexpected.Count + " unmatched bone(s) beyond the "
                     + "model-root node '" + modelRoot + "': " + string.Join(", ", unexpected.Take(8)));
                return;
            }
            Debug.Log(string.Format(
                "[S86] {0}: reference pose taken from the donor for {1}/{2} bones; the 1 unmatched bone "
                + "is the model-root node '{3}' (container for the Blender axis conversion, not a joint) "
                + "and keeps its own transform. Elbow pre-bend = {4:F1} deg (S84's +/-15 is retired).",
                System.IO.Path.GetFileName(fbx), taken, skeleton.Length, modelRoot, ElbowPreBendDeg));

            var present = new HashSet<string>(skeleton.Select(s => s.name));
            var human = new List<HumanBone>();
            for (int i = 0; i < BoneMap.GetLength(0); i++)
            {
                string slot = BoneMap[i, 0], bone = BoneMap[i, 1];
                if (!present.Contains(bone)) { Fail("rig has no bone '" + bone + "' for slot '" + slot + "'"); return; }
                var hb = new HumanBone { humanName = slot, boneName = bone };
                hb.limit.useDefaultValues = true;
                human.Add(hb);
            }

            var hd = imp.humanDescription;
            hd.human = human.ToArray();
            hd.skeleton = skeleton;
            imp.humanDescription = hd;
            imp.animationType = ModelImporterAnimationType.Human;
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

            // These are one-shot reactions, not gaits: looping would restart the flinch forever.
            var clips = imp.defaultClipAnimations;
            if (clips != null && clips.Length > 0)
            {
                for (int i = 0; i < clips.Length; i++) { clips[i].loopTime = false; clips[i].loop = false; }
                imp.clipAnimations = clips;
            }
            imp.SaveAndReimport();
        }

        /// Re-reads the IMPORTED asset and measures the reference pose it actually stores, by writing
        /// the stored SkeletonBone transforms onto an instantiated model and reading world positions.
        /// Reconstructing this from the .meta text is a known trap: the arrays ship with every
        /// parentName blank, and losing the model-root node silently yields a Z-up skeleton.
        private static void Gate(string fbx)
        {
            var imp = AssetImporter.GetAtPath(fbx) as ModelImporter;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Avatar>().FirstOrDefault();
            var clip = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (imp == null || model == null) { Fail("cannot re-read " + fbx); return; }

            var go = UnityEngine.Object.Instantiate(model);
            var byName = go.GetComponentsInChildren<Transform>(true)
                .GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            foreach (var sb in imp.humanDescription.skeleton)
                if (byName.TryGetValue(sb.name, out var t))
                { t.localPosition = sb.position; t.localRotation = sb.rotation; t.localScale = sb.scale; }

            bool ok = imp.animationType == ModelImporterAnimationType.Human
                      && avatar != null && avatar.isValid && avatar.isHuman
                      && clip != null && clip.length > 5.5f && clip.length < 6.5f && !clip.isLooping;
            if (!ok) Fail(fbx + ": rig/avatar/clip basics failed");

            foreach (var side in new[] { "Left", "Right" })
            {
                var a = byName[side + "Arm"].position;
                var f = byName[side + "ForeArm"].position;
                var h = byName[side + "Hand"].position;
                Vector3 u = (f - a).normalized;
                float elev = Mathf.Rad2Deg * Mathf.Atan2(u.y, new Vector2(u.x, u.z).magnitude);
                float elbow = Vector3.Angle(a - f, h - f);
                bool pass = Mathf.Abs(elev) <= TPoseElevToleranceDeg
                            && Mathf.Abs(180f - elbow) <= TPoseElbowToleranceDeg;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[S86gate] {0} {1,-5} reference upper-arm elevation={2:F2} deg (tol {3:F1}) elbow={4:F2} deg "
                    + "(tol 180 +/-{5:F1}) -> {6}",
                    System.IO.Path.GetFileName(fbx), side, elev, TPoseElevToleranceDeg, elbow,
                    TPoseElbowToleranceDeg, pass ? "PASS" : "FAIL"));
                if (!pass) ok = false;
            }
            UnityEngine.Object.DestroyImmediate(go);

            Debug.Log(string.Format("[S86gate] {0}: type={1} avatar={2} isHuman={3} clip='{4}' len={5:F4} loop={6} -> {7}",
                System.IO.Path.GetFileName(fbx), imp.animationType,
                avatar != null ? avatar.name : "NULL",
                avatar != null && avatar.isValid && avatar.isHuman,
                clip != null ? clip.name : "NULL", clip != null ? clip.length : -1f,
                clip != null && clip.isLooping, ok ? "PASS" : "FAIL"));
            if (!ok) Fail(fbx + ": reference pose is not a T-pose");
        }

        private static void Fail(string msg)
        {
            Debug.LogError("[S86gate] FAIL: " + msg);
            EditorApplication.Exit(1);
        }
    }
}
