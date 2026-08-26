using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 97 PHASE 1 — the bake harness.
    ///
    /// Drives the SHIPPED runtime solve (S89ContactIK.BakeFrame, which is LateUpdate's body with the
    /// animator-state lookup removed) over every frame of the b2 clip, offline, and records what
    /// would have to be written into the clip to make the correction permanent.
    ///
    /// ============ WHY THIS IS EXPRESSIBLE AT ALL, AND WHERE IT LEAKS ============
    /// The runtime layer writes LOCAL rotations on Business_Male_01's arm bones. A humanoid clip
    /// cannot store local rotations: it stores MUSCLES. The left arm below the clavicle has nine
    /// rotational degrees of freedom (three joints) and exactly SEVEN muscles --
    ///   Left Arm Down-Up / Front-Back / Twist In-Out, Left Forearm Stretch / Twist In-Out,
    ///   Left Hand Down-Up / In-Out.
    /// The hand has no axial muscle, because the radiocarpal joint has no axial freedom to speak of
    /// -- which is the same anatomy S92 built RouteTwistToForearm around. So the bake can carry
    /// everything the solver authors EXCEPT residual wrist twist, which S92 clamps to +-15 deg and
    /// which lands at -14.3 on the shipped pose. That is a real, bounded, measurable loss and this
    /// harness measures it per frame rather than asserting it is small (column rtWristDeg).
    ///
    /// ============ THE RULER MUST READ ZERO FIRST ============
    /// Before any correction is baked, the harness runs the whole chain on the UNTOUCHED pose
    /// (w forced to 0): sample the clip on bm01, take its HumanPose, push that pose onto the SOURCE
    /// SOMA rig, and read back the source rig's local rotations. Those must reproduce the FBX's own
    /// animation curves, because nothing was changed. If they do not, the chain is not invertible
    /// and no bake built on it can be trusted. That comparison is done host-side against the FBX
    /// itself; this file's job is to emit the numbers for it (--zero mode).
    ///
    /// -executeMethod SEAN.AutoTrial.S97BakeBuild.Capture
    ///   env AUTOTRIAL_S97_OUT   output directory (required)
    ///   env AUTOTRIAL_S97_ZERO  if set, force ramp weight 0 on every frame (the ruler-reads-zero run)
    ///   env AUTOTRIAL_S97_YAW   comma-separated extra root yaws to re-solve at (invariance check)
    /// </summary>
    public static class S97BakeBuild
    {
        public const string DefaultSrcFbx = "Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx";
        /// AUTOTRIAL_S97_SRC points the harness at a variant asset, so a baked candidate is measured
        /// through the SAME code path as the source it has to match.
        public static string SrcFbx
        {
            get
            {
                string e = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_SRC");
                return string.IsNullOrEmpty(e) ? DefaultSrcFbx : e;
            }
        }
        public const string BodyPrefab = "Assets/Resources/Prefabs/Rocketbox/Business_Male_01.prefab";
        public const int Frames = 180;          // the FBX carries 180 keys, 0..179, at 30 fps

        private static readonly string[] ArmMuscles =
        {
            "Left Arm Down-Up", "Left Arm Front-Back", "Left Arm Twist In-Out",
            "Left Forearm Stretch", "Left Forearm Twist In-Out",
            "Left Hand Down-Up", "Left Hand In-Out",
        };

        public static void Capture()
        {
            string outDir = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_OUT");
            if (string.IsNullOrEmpty(outDir)) { Fail("AUTOTRIAL_S97_OUT not set"); return; }
            Directory.CreateDirectory(outDir);
            bool zero = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_ZERO"));
            string tag = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_TAG");
            if (string.IsNullOrEmpty(tag)) tag = zero ? "zero" : "bake";
            Log("source asset: " + SrcFbx);

            // ---- the clip, keyed the S83 way: (name, length), never name alone ----
            AnimationClip clip = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(SrcFbx))
            {
                var c = o as AnimationClip;
                if (c != null && !c.name.StartsWith("__preview__")) { clip = c; break; }
            }
            if (clip == null) { Fail("no AnimationClip in " + SrcFbx); return; }
            Log(string.Format(CultureInfo.InvariantCulture, "clip='{0}' len={1:F4} fps={2}",
                clip.name, clip.length, clip.frameRate));

            // ---- the target body ----
            var bodyAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefab);
            if (bodyAsset == null) { Fail("no prefab at " + BodyPrefab); return; }
            // A PLAIN clone, not a prefab instance: S89ContactIK.Setup() deoptimises the transform
            // hierarchy, and Unity refuses to restructure a prefab instance. The runtime path
            // hits the same call on a spawned clone, which is exactly what this is.
            var body = Object.Instantiate(bodyAsset);
            body.name = "Business_Male_01";              // BodyKey() reads this; keep it exact
            body.transform.position = Vector3.zero;
            body.transform.rotation = Quaternion.identity;
            var anim = body.GetComponentInChildren<Animator>();
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman)
            { Fail("Business_Male_01 has no humanoid Animator"); return; }
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // ---- the source rig, used ONLY as a muscle -> SOMA-local-rotation decoder ----
            var srcAsset = AssetDatabase.LoadAssetAtPath<GameObject>(SrcFbx);
            Avatar srcAvatar = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(SrcFbx))
            { var a = o as Avatar; if (a != null) { srcAvatar = a; break; } }
            if (srcAsset == null || srcAvatar == null) { Fail("source FBX has no model+avatar"); return; }
            if (!srcAvatar.isHuman) { Fail("source avatar is not humanoid -- run the S86 import first"); return; }
            var src = Object.Instantiate(srcAsset);
            src.transform.position = Vector3.zero;
            src.transform.rotation = Quaternion.identity;

            // The rest pose of the source rig, as Unity imported it. Host-side this is what pins the
            // FBX Euler convention down: these are the same 79 rotations the file stores as
            // Lcl Rotation, so the convention is identified by fit, not by assumption.
            WriteRest(Path.Combine(outDir, "src_rest_" + tag + ".csv"), src.transform);

            // ---- muscle indices, resolved by name, asserted present ----
            var mi = new int[ArmMuscles.Length];
            for (int i = 0; i < ArmMuscles.Length; i++)
            {
                mi[i] = System.Array.IndexOf(HumanTrait.MuscleName, ArmMuscles[i]);
                if (mi[i] < 0) { Fail("no muscle named '" + ArmMuscles[i] + "'"); return; }
            }

            // ---- arm the shipped layer ----
            var ik = body.AddComponent<S89ContactIK>();
            ik.Setup();
            if (!ik.Ready) { Fail("S89ContactIK did not arm (ready=False) -- bone lookup would be empty"); return; }

            // ORDER MATTERS. Setup() calls DeoptimizeTransformHierarchy, which REBUILDS the bone
            // transforms; a HumanPoseHandler built before that keeps the transforms it was handed and
            // then silently reads and writes nothing. That is not hypothetical -- it is the first
            // thing this harness did wrong, and it reported a flat 0.0000 round-trip error on every
            // frame while the decoded source rotations were 87-133 deg out. Build the handlers here.
            var srcHandler = new HumanPoseHandler(srcAvatar, src.transform);
            var tgtHandler = new HumanPoseHandler(anim.avatar, body.transform);

            Transform tSh = anim.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            Transform tEl = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            Transform tWr = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            Transform sSh = Find(src.transform, "LeftArm");
            Transform sEl = Find(src.transform, "LeftForeArm");
            Transform sHa = Find(src.transform, "LeftHand");
            if (!tSh || !tEl || !tWr || !sSh || !sEl || !sHa) { Fail("bone lookup failed"); return; }

            // ---- sampling rigs: the same humanoid retarget the runtime uses, on BOTH bodies ----
            // The source rig is driven by the same clip through its OWN avatar. That is what makes
            // the muscle vectors comparable: pose.muscles is normalised to each avatar's own limits,
            // so bm01's muscle 0.60 and the SOMA rig's muscle 0.60 are not the same angle. Reading
            // both for the same frame is how the relationship gets measured instead of assumed.
            var srcAnim = src.GetComponentInChildren<Animator>();
            if (srcAnim == null) { srcAnim = src.AddComponent<Animator>(); }
            srcAnim.avatar = srcAvatar;
            srcAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var graph = PlayableGraph.Create("S97Bake");
            var output = AnimationPlayableOutput.Create(graph, "out", anim);
            var cp = AnimationClipPlayable.Create(graph, clip);
            cp.SetApplyFootIK(false);
            output.SetSourcePlayable(cp);

            var sgraph = PlayableGraph.Create("S97BakeSrc");
            var soutput = AnimationPlayableOutput.Create(sgraph, "out", srcAnim);
            var scp = AnimationClipPlayable.Create(sgraph, clip);
            scp.SetApplyFootIK(false);
            soutput.SetSourcePlayable(scp);

            var sb = new StringBuilder();
            sb.Append("frame,w,");
            sb.Append("srcShX,srcShY,srcShZ,srcShW,srcElX,srcElY,srcElZ,srcElW,srcWrX,srcWrY,srcWrZ,srcWrW,");
            sb.Append("cShX,cShY,cShZ,cShW,cElX,cElY,cElZ,cElW,cWrX,cWrY,cWrZ,cWrW,");
            sb.Append("m0,m1,m2,m3,m4,m5,m6,");
            sb.Append("rtShDeg,rtElDeg,rtWrDeg,");
            sb.Append("t0_0,t0_1,t0_2,t0_3,t0_4,t0_5,t0_6,");
            sb.Append("s0_0,s0_1,s0_2,s0_3,s0_4,s0_5,s0_6,");
            sb.Append("aShX,aShY,aShZ,aShW,aElX,aElY,aElZ,aElW,aHaX,aHaY,aHaZ,aHaW,");
            sb.Append("iShX,iShY,iShZ,iShW,iElX,iElY,iElZ,iElW,iHaX,iHaY,iHaZ,iHaW,");
            sb.Append("bShX,bShY,bShZ,bShW,bElX,bElY,bElZ,bElW,bHaX,bHaY,bHaZ,bHaW,");
            sb.Append("signedClear,dSh,dEl,dWr,aFlex,aDev,aTwist,pronApplied,");
            // the RENDERED pose's ROM, measured on every frame including w = 0. This is the
            // quantity s92_rom grades, and it is the only one a clip with the correction baked
            // in can be graded on: there is no "authored" pose once the layer is off.
            sb.Append("wFlex,wDev,wTwist,eFlex,sElev,pronation\n");

            // S97. Optionally attach the SHIPPED probe and drive it frame by frame, so the baked
            // clip is graded by tools/s91_audit.py and tools/s92_rom.py reading exactly the CSV they
            // were written against -- no second implementation of penetration or the capsule audit.
            S89ContactProbe probe = null;
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(S89ContactProbe.Env)))
            {
                probe = body.AddComponent<S89ContactProbe>();
                probe.BakeDriven = true;
                // The probe's stills are rendered with cam.Render() from an edit-mode loop. Skinning
                // matrices are otherwise only refreshed as part of the normal player loop, so every
                // shot came back in the BIND pose -- a neutral standing character with the arm down,
                // while BakeMesh (which recomputes on demand) was correctly reporting the hand at
                // the mouth 35 mm off the lip. Measurement and picture disagreed, and the picture
                // was the wrong one. Force the recalculation and stop the renderer being culled.
                foreach (var r in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    r.forceMatrixRecalculationPerRender = true;
                    r.updateWhenOffscreen = true;
                }
            }

            var pose = new HumanPose();

            // S97 iteration 2. Solve ON the muscle manifold: after every pass of the shipped
            // alternation, round-trip the whole body through its own HumanPose. Get-then-Set is the
            // projection -- whatever survives it is what a humanoid clip can store -- and it leaves
            // every already-representable bone, and the body position/rotation, where they were.
            var proj = new HumanPose();
            int passes = 3;
            string pe = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_PASSES");
            if (!string.IsNullOrEmpty(pe)) passes = int.Parse(pe, CultureInfo.InvariantCulture);
            bool onManifold = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_MANIFOLD"));
            bool forceW = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_FORCEW"));
            if (onManifold)
            {
                S89ContactIK.SolvePasses = passes;
                string se = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_SETTLE");
                S89ContactIK.BakeSettleIters = string.IsNullOrEmpty(se) ? 0 : int.Parse(se, CultureInfo.InvariantCulture);
                string tw = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_TWTARGET");
                S89ContactIK.BakeTwistTarget = string.IsNullOrEmpty(tw)
                    ? float.NaN : float.Parse(tw, CultureInfo.InvariantCulture);
                S89ContactIK.BakeProject = () =>
                {
                    tgtHandler.GetHumanPose(ref proj);
                    tgtHandler.SetHumanPose(ref proj);
                };
                Log("solving ON the muscle manifold, " + passes + " passes, " + S89ContactIK.BakeSettleIters + " settle iters");
            }

            if (probe != null)
            {
                // Init partitions the baked mesh into head and hand vertex sets by proximity, so it
                // must see a frame where the hand is NOT at the face or the two sets bleed together.
                // Frame 0 is that frame, and it is also where the runtime probe first fires.
                cp.SetTime(0f); cp.SetTime(0f); graph.Evaluate(0f);
                probe.BakeInit();
                // Then re-measure the capsule radii in the humanoid NEUTRAL pose, where the limbs
                // are clear of the body -- see S89ContactProbe.BakeMeasureRadii for what measuring
                // them with the arm against the torso costs.
                var neutral = new HumanPose();
                tgtHandler.GetHumanPose(ref neutral);
                for (int i = 0; i < neutral.muscles.Length; i++) neutral.muscles[i] = 0f;
                tgtHandler.SetHumanPose(ref neutral);
                probe.BakeMeasureRadii();
            }

            for (int f = 0; f < Frames; f++)
            {
                float t = f / 30f;
                cp.SetTime(t); cp.SetTime(t);
                graph.Evaluate(0f);
                scp.SetTime(t); scp.SetTime(t);
                sgraph.Evaluate(0f);

                Quaternion s0 = tSh.localRotation, e0 = tEl.localRotation, w0 = tWr.localRotation;

                // the source rig, posed by the same clip through its own avatar. aSh/aEl/aHa are the
                // ANIMATED source-rig rotations: host-side these are checked against the FBX's own
                // Lcl Rotation curves, which is how much the humanoid round-trip and the importer's
                // keyframe reduction cost before anything is corrected.
                Quaternion a0 = sSh.localRotation, a1 = sEl.localRotation, a2 = sHa.localRotation;
                srcHandler.GetHumanPose(ref pose);
                float[] ms0 = new float[ArmMuscles.Length];
                for (int i = 0; i < ms0.Length; i++) ms0[i] = pose.muscles[mi[i]];
                var srcPose = new HumanPose { bodyPosition = pose.bodyPosition,
                                              bodyRotation = pose.bodyRotation,
                                              muscles = (float[])pose.muscles.Clone() };

                // IDENTITY LEG: push the source rig's own pose straight back at it. Whatever this
                // loses is what the b2 avatar's muscle space cannot represent, and it bounds every
                // later claim about the bake.
                srcHandler.SetHumanPose(ref srcPose);
                Quaternion i0 = sSh.localRotation, i1 = sEl.localRotation, i2 = sHa.localRotation;

                tgtHandler.GetHumanPose(ref pose);
                float[] mt0 = new float[ArmMuscles.Length];
                for (int i = 0; i < mt0.Length; i++) mt0[i] = pose.muscles[mi[i]];

                if (zero) { ik.BakeFrame(-1f); }        // -1 is outside every ramp: writes nothing
                else if (!onManifold) ik.BakeFrame(f, forceW && S89ContactIK.BakeRamp(f) > 0f ? 1f : -1f);
                else
                {
                    // S97. Build the ramp HERE, in muscle space. Solve at full strength, then blend
                    // the seven arm muscles from the source pose toward the solved one by the ramp
                    // weight. Muscle space is a plain vector space: the blend cannot leave the
                    // manifold, cannot flip branch, and is continuous in w by construction. Every
                    // other muscle, and the body position and rotation, stay the source's.
                    float wR = forceW ? (S89ContactIK.BakeRamp(f) > 0f ? 1f : 0f) : S89ContactIK.BakeRamp(f);
                    var blended = new HumanPose { bodyPosition = pose.bodyPosition,
                                                  bodyRotation = pose.bodyRotation,
                                                  muscles = (float[])pose.muscles.Clone() };
                    if (wR > 0f)
                    {
                        ik.BakeFrame(f, 1f);
                        tgtHandler.GetHumanPose(ref pose);
                        for (int i = 0; i < mi.Length; i++)
                            blended.muscles[mi[i]] = Mathf.Lerp(mt0[i], pose.muscles[mi[i]], wR);
                    }
                    tgtHandler.SetHumanPose(ref blended);
                    ik.BakeFrame(-1f);   // re-measure ROM on the pose that is actually there
                }
                // grade the pose that is actually there, solved or baked-in, on every frame
                ik.BakeMeasureContact();
                if (probe != null)
                {
                    // A baked clip carries the correction at full strength; the layer wrote nothing,
                    // so LastWeight is 0 and every frame would read as unattributable. Report the
                    // ramp the clip was baked with, which is the weight the correction is present at.
                    // Both the baked path and the on-manifold path finish with a BakeFrame(-1) to
                    // re-measure ROM on the pose that is actually there, and that call sets
                    // LastWeight to Ramp(-1) = 0. Restore the ramp the correction is present at, or
                    // the audit tools see no attributable frames and pass on an empty set.
                    if (zero || onManifold) ik.BakeSetReportWeight(S89ContactIK.BakeRamp(f));
                    probe.BakeSample(f);
                }

                Quaternion s1 = tSh.localRotation, e1 = tEl.localRotation, w1 = tWr.localRotation;

                // corrected pose -> muscles, in BM01's normalisation
                tgtHandler.GetHumanPose(ref pose);
                float[] m = new float[ArmMuscles.Length];
                for (int i = 0; i < m.Length; i++) m[i] = pose.muscles[mi[i]];

                // muscles -> back onto the SAME body: what the humanoid format can actually carry
                tgtHandler.SetHumanPose(ref pose);
                float rtSh = Quaternion.Angle(s1, tSh.localRotation);
                float rtEl = Quaternion.Angle(e1, tEl.localRotation);
                float rtWr = Quaternion.Angle(w1, tWr.localRotation);

                // -> the SOURCE rig, in the SOURCE avatar's normalisation. The correction is carried
                // as a DELTA on top of the source rig's own muscle vector, so every per-avatar offset
                // cancels and only the per-avatar SCALE is left; the scale is fitted and checked
                // host-side from the (s0, t0) columns rather than taken from the limit tables.
                for (int i = 0; i < m.Length; i++)
                    srcPose.muscles[mi[i]] = ms0[i] + (m[i] - mt0[i]);
                srcHandler.SetHumanPose(ref srcPose);
                Quaternion b0 = sSh.localRotation, b1 = sEl.localRotation, b2 = sHa.localRotation;

                sb.Append(f.ToString(CultureInfo.InvariantCulture)).Append(',');
                sb.Append(F(S89ContactIK.BakeRamp(zero ? -1f : f))).Append(',');
                Q(sb, s0); Q(sb, e0); Q(sb, w0);
                Q(sb, s1); Q(sb, e1); Q(sb, w1);
                for (int i = 0; i < m.Length; i++) sb.Append(F(m[i])).Append(',');
                sb.Append(F(rtSh)).Append(',').Append(F(rtEl)).Append(',').Append(F(rtWr)).Append(',');
                for (int i = 0; i < mt0.Length; i++) sb.Append(F(mt0[i])).Append(',');
                for (int i = 0; i < ms0.Length; i++) sb.Append(F(ms0[i])).Append(',');
                Q(sb, a0); Q(sb, a1); Q(sb, a2);
                Q(sb, i0); Q(sb, i1); Q(sb, i2);
                Q(sb, b0); Q(sb, b1); Q(sb, b2);
                sb.Append(F(ik.LastSignedClearance)).Append(',');
                sb.Append(F(ik.DeltaShoulderDeg)).Append(',').Append(F(ik.DeltaElbowDeg)).Append(',')
                  .Append(F(ik.DeltaWristDeg)).Append(',');
                sb.Append(F(ik.AuthoredWristFlexDeg)).Append(',').Append(F(ik.AuthoredWristDevDeg)).Append(',')
                  .Append(F(ik.AuthoredWristTwistDeg)).Append(',').Append(F(ik.AppliedPronationDeg)).Append(',');
                sb.Append(F(ik.LastWristFlexDeg)).Append(',').Append(F(ik.LastWristDevDeg)).Append(',')
                  .Append(F(ik.LastWristTwistDeg)).Append(',').Append(F(ik.LastElbowFlexDeg)).Append(',')
                  .Append(F(ik.LastShoulderElevDeg)).Append(',').Append(F(ik.LastForearmPronationDeg));
                sb.Append('\n');
            }
            S89ContactIK.BakeProject = null;   // never leave the hook armed
            S89ContactIK.SolvePasses = 3;
            S89ContactIK.BakeSettleIters = 0;
            S89ContactIK.BakeTwistTarget = float.NaN;
            graph.Destroy();
            sgraph.Destroy();
            File.WriteAllText(Path.Combine(outDir, "bake_" + tag + ".csv"), sb.ToString());
            Log("wrote bake_" + tag + ".csv to " + outDir);

            if (probe != null) { probe.BakeFlush(); Log("probe flushed"); }
            Object.DestroyImmediate(body);
            Object.DestroyImmediate(src);
            Log("S97 BAKE CAPTURE OK");
        }

        /// ================== S97 PHASE 1, THE INVERSE ==================
        /// The FBX -> Unity humanoid map is NOT invertible in closed form and NOT idempotent:
        /// feeding Unity's own reconstruction of the source pose back into the file moves the upper
        /// arm by up to 13.9 deg (measured, `noop` run). But it CONTRACTS -- the raw source-vs-
        /// reconstruction gap is 47 deg and the second application moves it only 8.5 -- so the
        /// inverse is found by fixed-point iteration instead of derived.
        ///
        /// The iteration runs in MUSCLE space, not in bone-local rotations, because muscle space is
        /// the one coordinate system the two rigs share: the b2 avatar and Business_Male_01 were
        /// measured (zero run, columns s0_*/t0_*) to normalise identically, s = 1.0000*t + 0.0000
        /// with a worst residual of 0.0007 over all seven arm muscles and all 180 frames. A delta in
        /// muscle space therefore means the same thing on both. In bone-local space it would not.
        ///
        ///   mu_0     = m*                       (ask the file for the pose we want)
        ///   L_k      = decode(mu_k) on the SOMA rig -> FBX curves -> import
        ///   m_k      = sample L_k on bm01, read muscles
        ///   mu_{k+1} = mu_k + (m* - m_k)
        ///
        /// -executeMethod SEAN.AutoTrial.S97BakeBuild.Iterate
        ///   AUTOTRIAL_S97_SRC  candidate asset to MEASURE (defaults to the shipped source)
        ///   AUTOTRIAL_S97_MU   muscle CSV to DECODE onto the source rig (optional)
        ///   AUTOTRIAL_S97_OUT  output directory
        public static void Iterate()
        {
            string outDir = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_OUT");
            if (string.IsNullOrEmpty(outDir)) { Fail("AUTOTRIAL_S97_OUT not set"); return; }
            Directory.CreateDirectory(outDir);
            string muPath = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_MU");
            string tag = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_TAG");
            if (string.IsNullOrEmpty(tag)) tag = "iter";

            AnimationClip clip = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(SrcFbx))
            { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview__")) { clip = c; break; } }
            if (clip == null) { Fail("no AnimationClip in " + SrcFbx); return; }

            var bodyAsset = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPrefab);
            var body = Object.Instantiate(bodyAsset);
            body.name = "Business_Male_01";
            body.transform.position = Vector3.zero; body.transform.rotation = Quaternion.identity;
            var anim = body.GetComponentInChildren<Animator>();
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            // Business_Male_01 imports with an OPTIMIZED hierarchy -- no bone transforms at all -- so
            // a playable graph poses nothing a HumanPoseHandler can read, and GetHumanPose quietly
            // returns the rest pose on EVERY frame. Capture() got this for free because
            // S89ContactIK.Setup() deoptimises; Iterate() does not arm the layer, so it must do it
            // itself. Without this the whole fixed point converges to a constant and looks stable.
            if (!anim.hasTransformHierarchy) AnimatorUtility.DeoptimizeTransformHierarchy(body);

            // the DECODE rig is always the shipped source asset's, never the candidate's: the decode
            // must not drift with whatever is being measured this round.
            var srcAsset = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultSrcFbx);
            Avatar srcAvatar = null;
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(DefaultSrcFbx))
            { var a = o as Avatar; if (a != null) { srcAvatar = a; break; } }
            var src = Object.Instantiate(srcAsset);
            src.transform.position = Vector3.zero; src.transform.rotation = Quaternion.identity;
            var srcAnim = src.GetComponentInChildren<Animator>();
            if (srcAnim == null) srcAnim = src.AddComponent<Animator>();
            srcAnim.avatar = srcAvatar; srcAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var mi = new int[ArmMuscles.Length];
            for (int i = 0; i < ArmMuscles.Length; i++)
            {
                mi[i] = System.Array.IndexOf(HumanTrait.MuscleName, ArmMuscles[i]);
                if (mi[i] < 0) { Fail("no muscle named '" + ArmMuscles[i] + "'"); return; }
            }

            Transform sSh = Find(src.transform, "LeftArm"), sEl = Find(src.transform, "LeftForeArm"),
                      sHa = Find(src.transform, "LeftHand");
            if (!sSh || !sEl || !sHa) { Fail("source bone lookup failed"); return; }

            var graph = PlayableGraph.Create("S97Iter");
            var output = AnimationPlayableOutput.Create(graph, "out", anim);
            var cp = AnimationClipPlayable.Create(graph, clip);
            cp.SetApplyFootIK(false);
            output.SetSourcePlayable(cp);

            var sgraph = PlayableGraph.Create("S97IterSrc");
            var soutput = AnimationPlayableOutput.Create(sgraph, "out", srcAnim);
            var scp = AnimationClipPlayable.Create(sgraph, AssetClip(DefaultSrcFbx));
            scp.SetApplyFootIK(false);
            soutput.SetSourcePlayable(scp);

            var srcHandler = new HumanPoseHandler(srcAvatar, src.transform);
            var tgtHandler = new HumanPoseHandler(anim.avatar, body.transform);

            float[][] mu = null;
            if (!string.IsNullOrEmpty(muPath) && File.Exists(muPath)) mu = ReadMu(muPath);

            // AUTOTRIAL_S97_TARGET closes the loop inside one launch: measure the candidate, take
            // the residual against the target muscles, and decode the UPDATED mu in the same pass.
            float[][] tgt = null;
            string tgtPath = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S97_TARGET");
            if (!string.IsNullOrEmpty(tgtPath) && File.Exists(tgtPath)) tgt = ReadMu(tgtPath);

            var meas = new StringBuilder("frame,m0,m1,m2,m3,m4,m5,m6\n");
            var muOut = new StringBuilder("frame,m0,m1,m2,m3,m4,m5,m6\n");
            float worstResid = 0f;
            var dec = new StringBuilder("frame,bShX,bShY,bShZ,bShW,bElX,bElY,bElZ,bElW,bHaX,bHaY,bHaZ,bHaW\n");
            var pose = new HumanPose();
            for (int f = 0; f < Frames; f++)
            {
                float t = f / 30f;
                cp.SetTime(t); cp.SetTime(t); graph.Evaluate(0f);
                tgtHandler.GetHumanPose(ref pose);
                meas.Append(f.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < mi.Length; i++) meas.Append(',').Append(F(pose.muscles[mi[i]]));
                meas.Append('\n');

                if (mu != null && mu[f] != null && tgt != null && tgt[f] != null)
                {
                    for (int i = 0; i < mi.Length; i++)
                    {
                        float r = tgt[f][i] - pose.muscles[mi[i]];
                        if (Mathf.Abs(r) > worstResid) worstResid = Mathf.Abs(r);
                        mu[f][i] += r;
                    }
                    muOut.Append(f.ToString(CultureInfo.InvariantCulture));
                    for (int i = 0; i < mi.Length; i++) muOut.Append(',').Append(F(mu[f][i]));
                    muOut.Append('\n');
                }

                if (mu != null && mu[f] != null)
                {
                    // the source rig's own pose at this frame supplies every muscle the bake does not
                    // own; only the seven arm muscles are overwritten.
                    scp.SetTime(t); scp.SetTime(t); sgraph.Evaluate(0f);
                    srcHandler.GetHumanPose(ref pose);
                    for (int i = 0; i < mi.Length; i++) pose.muscles[mi[i]] = mu[f][i];
                    srcHandler.SetHumanPose(ref pose);
                    dec.Append(f.ToString(CultureInfo.InvariantCulture)).Append(',');
                    Q(dec, sSh.localRotation); Q(dec, sEl.localRotation); Q(dec, sHa.localRotation);
                    dec.Length -= 1;
                    dec.Append('\n');
                }
            }
            graph.Destroy(); sgraph.Destroy();
            File.WriteAllText(Path.Combine(outDir, "measured_" + tag + ".csv"), meas.ToString());
            if (tgt != null)
            {
                File.WriteAllText(Path.Combine(outDir, "mu_" + tag + ".csv"), muOut.ToString());
                Log(string.Format(CultureInfo.InvariantCulture,
                    "residual: worst |m* - m_k| over the window = {0:F6} muscle units", worstResid));
            }
            if (mu != null) File.WriteAllText(Path.Combine(outDir, "decoded_" + tag + ".csv"), dec.ToString());
            Object.DestroyImmediate(body); Object.DestroyImmediate(src);
            Log("S97 ITERATE OK (" + tag + ") measured=" + SrcFbx + (mu != null ? " decoded=yes" : ""));
        }

        private static float[][] ReadMu(string path)
        {
            var lines = File.ReadAllLines(path);
            var m = new float[Frames][];
            for (int i = 1; i < lines.Length; i++)
            {
                var p = lines[i].Split(',');
                if (p.Length < 8) continue;
                int fr = int.Parse(p[0], CultureInfo.InvariantCulture);
                var v = new float[ArmMuscles.Length];
                for (int k = 0; k < v.Length; k++)
                    v[k] = float.Parse(p[k + 1], NumberStyles.Float, CultureInfo.InvariantCulture);
                m[fr] = v;
            }
            return m;
        }

        private static AnimationClip AssetClip(string path)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview__")) return c; }
            return null;
        }

        private static void WriteRest(string path, Transform root)
        {
            var sb = new StringBuilder("name,px,py,pz,qx,qy,qz,qw\n");
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var p = t.localPosition; var q = t.localRotation;
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "{0},{1:R},{2:R},{3:R},{4:R},{5:R},{6:R},{7:R}\n",
                    t.name, p.x, p.y, p.z, q.x, q.y, q.z, q.w);
            }
            File.WriteAllText(path, sb.ToString());
        }

        private static Transform Find(Transform root, string n)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
            return null;
        }

        private static void Q(StringBuilder sb, Quaternion q)
        {
            sb.Append(F(q.x)).Append(',').Append(F(q.y)).Append(',')
              .Append(F(q.z)).Append(',').Append(F(q.w)).Append(',');
        }

        private static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }
        private static void Log(string m) { Debug.Log("[S97bake] " + m); }
        private static void Fail(string m) { Debug.LogError("[S97bake] FAIL: " + m); }
    }
}
