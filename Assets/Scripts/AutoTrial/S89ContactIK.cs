using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 89: a runtime contact-IK layer that pins a hand to a face landmark for the clips that
    /// declare they need it. Generalises by design, ships gated to b2 alone.
    ///
    /// ================== WHY THIS LAYER EXISTS ==================
    /// S88 established the standing reason: the generator does not resolve contact points at this
    /// spatial precision. Measured on their own source rig, three clips whose prompts ranged from
    /// "covers their mouth" to "presses the palm over their mouth ... hand centered on the mouth,
    /// forearm vertical in front of the chest" all put the hand in the same place -- minimum
    /// hand-to-Head distance 0.1365 / 0.1357 / 0.1497 head-heights, i.e. at the cheek. Re-prompting
    /// is not a lever. Nor is the body: S88 swept sixteen Rocketbox appearances and the best still
    /// missed by 0.0795 m. Nor is per-joint source calibration: S87 spent six iterations proving that
    /// closing the lateral gap by shoulder rotation sweeps the forearm THROUGH the head.
    ///
    /// ================== STEP 0, THE BONE-ACCESS RULING ==================
    /// Measured in a live trial (S89BoneAccessProbe), not assumed:
    ///   * the pedestrian instance runs OPTIMIZED -- hasTransformHierarchy false, 2 transforms,
    ///     GetBoneTransform(Hips) null, SkinnedMeshRenderer.bones empty. Nothing already deoptimises it.
    ///   * OnAnimatorIK DOES fire: SocialForcesAnimatorController's Base Layer already carries
    ///     m_IKPass: 1, and that survives the S79/S83 AnimatorOverrideController. So
    ///     Animator.SetBoneLocalRotation is reachable with NO .controller edit, and writes reach the
    ///     rendered mesh (45.66 cm mean bounds difference between correction-on and correction-off).
    ///   * AnimatorUtility.DeoptimizeTransformHierarchy on the SPAWNED CLONE costs 1.05 ms once,
    ///     takes the instance from 2 to 85 transforms, rebinds the renderer (bones 0 -> 80, root
    ///     Bip01 Pelvis) and the mesh keeps animating. It touches no asset: the pedestrian is an
    ///     Instantiate() clone with no prefab link.
    ///
    /// This component uses DEOPTIMISE + direct transform writes, and that is a deliberate choice
    /// between two paths that both satisfy "zero YAML, zero prefab writes":
    ///   * the solve NEEDS world-space bone positions (shoulder, elbow, wrist, head) which an
    ///     optimized rig simply does not expose, so the deoptimise is required for READING whichever
    ///     way the write goes; and
    ///   * SetBoneLocalRotation applies in MUSCLE space and is therefore subject to the humanoid
    ///     muscle limits -- the elbow-flexion limit is exactly what S84 measured saturating at 63% of
    ///     b2's frames. Writing transforms directly bypasses that clamp.
    ///
    /// ================== SCOPE ==================
    /// Inert unless (a) the env gate is set, (b) the playing state is the declared one, (c) the clip
    /// matches a metadata entry by name AND length, and (d) the body has a landmark. Any missing
    /// piece and this component does nothing at all -- that is what makes the regression arms safe.
    /// </summary>
    public class S89ContactIK : MonoBehaviour
    {
        public const string Env = "AUTOTRIAL_S89_IK";

        // ---- per-clip metadata. The ONLY entry is b2. Adding a clip is adding a row. ----
        public class ContactSpec
        {
            public string clipName;      // clip identity is (name, length): every Kimodo clip is "Scene"
            public float clipLength;
            public bool leftHand;
            public int rampIn0, rampIn1, rampOut0, rampOut1;   // frames at 30 fps, S87's window
            public float palmStandoff;   // SUPERSEDED by FaceSpec.standoff (S90); kept so the row reads complete
        }

        public static readonly List<ContactSpec> Specs = new List<ContactSpec>
        {
            new ContactSpec { clipName = "Scene", clipLength = 5.9667f, leftHand = true,
                              rampIn0 = 6, rampIn1 = 15, rampOut0 = 105, rampOut1 = 114,
                              palmStandoff = 0.012f },
        };

        /// Mouth landmark per body, as a Head-LOCAL offset in metres. Derived in S88 from each body's
        /// own baked head mesh by the S87 midline-profile method: walk the midline surface down from
        /// the nose tip, past the philtrum recess, to the lip bulge (a real local forward maximum).
        /// 13 of these 16 were found automatically; Business_Male_04, Male_Adult_01 and
        /// Medical_Female_02 fell back to nose-29 mm, and Police_Male_02's headgear distorts its
        /// profile -- that row is present for completeness and is the one to re-derive before use.
        public static readonly Dictionary<string, Vector3> MouthLocal = new Dictionary<string, Vector3>
        {
            { "Business_Male_01", new Vector3(-0.05748f, 0.11721f, 0.00337f) },
            { "Business_Male_04", new Vector3(-0.04527f, 0.11046f, 0.00584f) },
            { "Chef_Female_01", new Vector3(-0.05094f, 0.10533f, 0.00406f) },
            { "Construction_Male_03", new Vector3(-0.05927f, 0.11574f, 0.00373f) },
            { "Female_Adult_07", new Vector3(-0.05457f, 0.10506f, 0.00369f) },
            { "Male_Adult_01", new Vector3(-0.05107f, 0.11999f, 0.00432f) },
            { "Medical_Female_02", new Vector3(-0.04439f, 0.10436f, 0.00844f) },
            { "Female_Adult_01", new Vector3(-0.05457f, 0.10510f, 0.00369f) },
            { "Female_Adult_12", new Vector3(-0.05137f, 0.10670f, 0.00677f) },
            { "Male_Adult_05", new Vector3(-0.05340f, 0.11669f, 0.00552f) },
            { "Male_Adult_15", new Vector3(-0.05632f, 0.11978f, 0.00464f) },
            { "Business_Female_02", new Vector3(-0.05077f, 0.11189f, 0.00547f) },
            { "Construction_Male_07", new Vector3(-0.05743f, 0.12216f, 0.00211f) },
            { "Medical_Male_03", new Vector3(-0.05214f, 0.11530f, 0.00628f) },
            { "Sports_Female_01", new Vector3(-0.05148f, 0.10670f, 0.00508f) },
            { "Police_Male_02", new Vector3(-0.01719f, 0.12960f, 0.00959f) },
        };

        /// Session 90. Outward face normal AT the lip landmark, head-local unit vector, and the
        /// standoff distance the palm CENTRE is aimed at along it.
        ///
        /// S89 aimed the palm centre at the lip surface point itself and gated on UNSIGNED distance,
        /// which cannot tell 5 mm outside from 5 mm inside. The hand has thickness, so putting its
        /// centre on the skin puts half the hand inside the head -- visible in profile, invisible
        /// from the front, which is exactly how S89's verdict still passed.
        ///
        /// n_face: head vertices within 3.5 cm of the landmark, PCA-fitted plane, smallest
        /// eigenvector, oriented away from the head centroid. Planarity residual 0.09 (bm01) and
        /// 0.12 (medf02) -- the lip patch is curved, so this is a local fit, not an exact normal.
        /// standoff: that body's own palm half-thickness plus a 5 mm contact allowance, less a 1 mm trim
        /// measured back from iteration 3 (penetration had 0.73 mm of headroom under the 5 mm cap). Palm
        /// thickness is measured across the PALM SLAB (hand vertices past the wrist), 59.24 mm on
        /// bm01 and 45.69 mm on medf02 -- not the 130 mm first measured, which was the whole
        /// hand+forearm+sleeve cluster.
        public class FaceSpec { public Vector3 n; public float standoff; }

        public static readonly Dictionary<string, FaceSpec> Face = new Dictionary<string, FaceSpec>
        {
            { "Business_Male_01",  new FaceSpec { n = new Vector3(0.29607f, 0.95379f, 0.05130f), standoff = 0.03512f } },
            { "Medical_Female_02", new FaceSpec { n = new Vector3(0.42432f, 0.89455f, 0.14047f), standoff = 0.02685f } },
        };

        /// S91. Weight of the anterior component in the elbow pole, against a unit "down". Tuned
        /// against the full self-intersection audit. 0.85 and 1.6 left forearm-vs-torso at -2.9 mm;
        /// 2.6 lifts it to -0.7 mm and upper-arm-vs-torso from +23.3 to +35.7 mm.
        public static float PoleForwardWeight = 2.6f;

        /// S92. Roll of the hand about the palm normal, degrees: the direction the hand's long axis
        /// takes in the face plane. S91 shipped 100, which laid the palm flat across the lower face
        /// and read as a proper mouth-cover -- but it did so by twisting the WRIST 27 deg past a
        /// joint that has no axial freedom at all, which is the symptom this ticket exists to fix.
        ///
        /// With the wrist held inside range the roll target can no longer be met: the clamp gives
        /// back 20-120 deg of it on every frame, and the more roll is asked for the more the hand is
        /// levered off the face -- deepest penetration runs 2.6 mm at roll 20, 7.5 at 40, 18.9 at
        /// 70, 23.4 at 100 against a 5 mm cap. 20 is the largest roll that passes every gate.
        ///
        /// THE COST, and it is visible: at roll 20 the hand sits vertically in front of the nose
        /// rather than lying across the mouth. It is better CENTRED than S91's (hand centroid 13.8
        /// mm above the lip landmark against S91's 23.3) but it presents its ulnar edge instead of
        /// its palm, and reads as a fist held to the face rather than a hand over the mouth. The
        /// ticket ranks the joint limit above the hand orientation -- "prefer relaxing the ROLL
        /// target before ever exceeding ROM" -- so this is what ships, but see WATCH_ME_FIRST: the
        /// untried lever is the elbow POLE, which sets the forearm direction and therefore how much
        /// wrist bend the pose demands in the first place.
        public static float HandRollDeg = 20f;

        /// S92. Arm the component for MEASUREMENT ONLY: the rest frame is built and every ROM angle
        /// is published each frame, but no bone is written. This is how the untouched source
        /// animation is put through the very same ruler that grades the IK pose -- the S91 rule that
        /// a new geometric ruler must first read clean on a known-good pose before it may judge a
        /// new one. Without it the ROM gate would have no calibration case at all.
        public static bool RomOnly;

        /// ================== S97: SOLVING ON THE MUSCLE MANIFOLD ==================
        /// S97 measured that a humanoid clip stores SEVEN muscles for the left arm's NINE
        /// rotational degrees of freedom: the elbow's off-hinge term and the wrist's axial twist
        /// have nowhere to live. Solving freely and projecting afterwards therefore threw away
        /// 39.5 deg at the forearm and 40.4 at the hand -- and it threw away the WRONG 40 deg,
        /// because the solver had already spent its budget on rotations that were about to be
        /// discarded.
        ///
        /// This hook lets the BAKE re-solve inside the representable set: the pose is snapped back
        /// onto the muscle manifold after every pass, so each following pass re-aims the hand from
        /// where the format can actually put it and spends its remaining freedom -- shoulder swing,
        /// elbow, and the hand's two muscles -- recovering the roll instead of authoring it into
        /// axes that vanish. The hand's orientation relative to the forearm is still fully
        /// controllable on the manifold (forearm twist + hand down-up + hand in-out = 3 DOF), which
        /// is why this is worth doing rather than a loss to be accepted.
        ///
        /// NULL IN THE RUNTIME PATH, AND THEREFORE INERT THERE. Only S97BakeBuild ever assigns it.
        /// The runtime layer writes transforms directly and is not subject to the muscle format at
        /// all, so it neither needs nor pays for this.
        public static System.Action BakeProject;

        /// Passes of the OrientHand/TwoBone alternation. Three is what S90 iteration 3 settled on
        /// and what ships. The bake raises it, because projecting onto the manifold after each pass
        /// perturbs the pose more than a bare pass does and the alternation needs room to settle.
        /// This is a solution-path setting, not a tuned constant: pole, roll target and standoff are
        /// untouched by it.
        public static int SolvePasses = 3;

        /// S97. Settling iterations run after the main alternation, BAKE ONLY (zero when
        /// BakeProject is null, which is always at runtime).
        ///
        /// WHY THIS IS NEEDED, mechanism first. TwoBone rotates the forearm to aim at the wrist
        /// target and the hand rides along with it, so it leaves the hand's rotation RELATIVE to the
        /// forearm -- and therefore the wrist clamp -- exactly intact. The projection is what breaks
        /// the clamp: it discards the off-hinge component TwoBone left on the forearm, and moving
        /// the forearm without moving the hand changes the very frame the twist was clamped against.
        /// Measured: a wrist clamped to -14.5 deg comes back out at +32.9.
        ///
        /// The way out is that the damage is not recurrent. Once the forearm is ON the manifold its
        /// off-hinge term is already zero, so projecting again barely moves it -- and a clamp applied
        /// at that point survives. So: project, re-run the orientation step against the frame that
        /// survived, project again. RouteTwistToForearm rotates the forearm about the elbow->wrist
        /// line, which moves the wrist not at all, so the position solve is not disturbed by this.
        public static int BakeSettleIters = 0;

        /// S92. Joint range-of-motion limits, degrees. Values are the clinical goniometry norms for
        /// the adult upper limb as tabulated by the AAOS and by Norkin & White, "Measurement of
        /// Joint Motion: A Guide to Goniometry" -- the standard reference range, not a per-subject
        /// measurement. Where the S92 ticket specifies a tighter bound than the clinical norm, the
        /// ticket wins and the norm is noted beside it: a reaction pose should sit comfortably
        /// inside the envelope, not at the anatomical extreme.
        ///
        /// WRIST. Flexion/extension +-60 (ticket; clinical norm is ~80 palmar / ~70 dorsal).
        /// Deviation -20 radial to +30 ulnar (ticket, and the clinical norm). Axial twist at the
        /// wrist <= 15: the radiocarpal joint has essentially NO axial degree of freedom, and the
        /// 15 deg allowance is slack for the rig's single-bone forearm, not an anatomical range.
        /// Pronation/supination is a FOREARM motion (radius crossing ulna, ~80/~80) and is routed
        /// to the forearm bone -- which is the entire subject of this ticket.
        public const float WristFlexMinDeg = -60f, WristFlexMaxDeg = 60f;
        public const float WristDevMinDeg = -20f, WristDevMaxDeg = 30f;      // - radial, + ulnar
        public const float WristTwistMaxDeg = 15f;
        /// FOREARM pronation/supination, +-80 clinically (radius crossing over ulna). This is the
        /// joint the twist actually belongs to, and the whole point of S92.
        public const float ForearmPronationMaxDeg = 80f;
        /// ELBOW. Flexion 0..150 (norm 0..145-150); no hyperextension, so the floor is 0 with a
        /// small tolerance for measurement noise in the source clip.
        public const float ElbowFlexMinDeg = -5f, ElbowFlexMaxDeg = 150f;
        /// SHOULDER. Elevation of the humerus from the side, 0..180 (norm: flexion 180, abduction
        /// 180). Axial rotation of the humerus about its own shaft, +-90 (norm: internal ~70-90,
        /// external ~90).
        public const float ShoulderElevMaxDeg = 180f;
        public const float ShoulderTwistMaxDeg = 90f;

        /// S92 readouts: the decomposition the solver clamps against, published so the probe logs
        /// exactly the quantity the gate grades. Nothing else may define "wrist angle".
        public float LastWristFlexDeg { get; private set; }
        public float LastWristDevDeg { get; private set; }
        public float LastWristTwistDeg { get; private set; }
        /// The pose this layer AUTHORS, at full strength, before the ramp weight blends it toward
        /// the source animation. This is the unambiguous attribution target: the blended pose is a
        /// mixture of an in-range authored pose and an out-of-range source clip, and because the
        /// blend is a quaternion Slerp its decomposed ANGLES are not the linear interpolation of the
        /// two endpoints -- treating them as such under-allowed the source's share by 1-2 deg and
        /// failed every roll candidate on a ramp frame. At full weight, which is the entire hold and
        /// everything that ships, the authored pose IS the pose.
        public float AuthoredWristFlexDeg { get; private set; }
        public float AuthoredWristDevDeg { get; private set; }
        public float AuthoredWristTwistDeg { get; private set; }
        /// Absolute forearm pronation from the bind pose -- the gated anatomical quantity.
        public float LastForearmPronationDeg { get; private set; }
        /// How much pronation this layer ADDED on top of the source animation's own.
        public float AppliedPronationDeg { get; private set; }
        public float LastElbowFlexDeg { get; private set; }
        public float LastShoulderElevDeg { get; private set; }
        public float LastShoulderTwistDeg { get; private set; }
        /// Angle between the humerus and the spine axis -- the same quantity as elevation, kept as
        /// a separate readout so the twist construction's conditioning is visible in the log.
        public float LastShoulderSwingDeg { get; private set; }
        /// Set on a frame where the ROM clamp had to give up part of the roll target. The roll is a
        /// soft goal; ROM is not.
        public float LastRollGivenUpDeg { get; private set; }
        public int RomClampFrames { get; private set; }

        public const string StateName = "SurprisedReaction";
        private static readonly int StateHash = Animator.StringToHash(StateName);

        private Animator animator;
        private Transform shoulder, elbow, wrist, head, midProx, idxProx, chest;   // idxProx: palm plane
        private Transform hips, neck;        // S92: spine axis, for shoulder elevation
        /// S92 rest frame, taken from the mesh BIND pose, not from whatever pose the character
        /// happens to be in when the component arms. Anatomical neutral for the wrist is "hand in
        /// line with the forearm", which is what the biped's bind pose is; measuring against the
        /// live pose would make the ROM reading depend on when Init ran.
        private bool restOk;
        private Quaternion restRelHand;      // hand rotation relative to the forearm, at bind
        private Vector3 axLocal;             // forearm long axis, in FOREARM local space
        private Vector3 flexLocal, devLocal; // + = palmar flexion, + = ulnar deviation
        private Vector3 palmNLocal;          // S97: bind palm normal, FOREARM-local; a rig-fixed sign reference
        private Transform smrT;              // the skinned mesh's frame; the bind pose lives here
        private Quaternion bindUpperMesh;    // upper arm's bind rotation, in mesh space
        private Vector3 upperAxLocal;        // humerus long axis, in UPPER ARM local space
        private Quaternion restRelFore;      // forearm relative to upper arm, at bind
        private Vector3 foreAxInUpper;       // forearm long axis, in UPPER ARM coords at bind
        private ContactSpec spec;
        private Vector3 mouthLocal;
        private FaceSpec face;
        private bool ready, announced;
        public float LastWeight { get; private set; }
        public float LastPalmDist { get; private set; } = -1f;
        /// Signed clearance of the palm centre along the outward face normal. POSITIVE = outside the
        /// face. This is the quantity S90 gates on; the unsigned distance S89 used could not tell
        /// which side of the skin the palm was on.
        public float LastSignedClearance { get; private set; } = -99f;
        public Vector3 LastFaceNormalWorld { get; private set; }
        public Vector3 LastMouthWorld { get; private set; }
        /// The magnitude of the correction actually applied this frame, in degrees per bone, plus a
        /// count of frames on which any write happened. G2 grades THESE rather than a cross-trial
        /// pose diff: two runs of a wall-clock-paced trial never land on identical world poses, so
        /// "unchanged outside the window" has to be shown by the component writing nothing, not by
        /// two runs agreeing to sub-millimetre.
        public float DeltaShoulderDeg { get; private set; }
        public float DeltaElbowDeg { get; private set; }
        public float DeltaWristDeg { get; private set; }
        public int WriteFrames { get; private set; }
        public float LastFrameF { get; private set; } = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(Env))) return;
            var host = new GameObject("S89ContactIKHost");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<S89ContactIK>().StartCoroutine(nameof(FindTarget));
        }

        private IEnumerator FindTarget()
        {
            Scenario.Agents.PedestrianModulator mod = null;
            float deadline = Time.time + 30f;
            while (mod == null && Time.time < deadline)
            {
                mod = Object.FindObjectOfType<Scenario.Agents.PedestrianModulator>();
                if (mod == null) yield return new WaitForSeconds(0.25f);
            }
            if (mod == null) { Debug.LogWarning("[S89IK] no PedestrianModulator -- inert"); yield break; }
            if (mod.GetComponent<S89ContactIK>() == null) mod.gameObject.AddComponent<S89ContactIK>().Setup();
        }

        public void Setup()
        {
            animator = GetComponentInChildren<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            { Debug.LogWarning("[S89IK] no humanoid Animator -- inert"); return; }

            string body = BodyKey(gameObject.name);
            if (!MouthLocal.TryGetValue(body, out mouthLocal))
            { Debug.Log("[S89IK] no landmark for body '" + body + "' -- inert (this is the intended default)"); return; }
            if (!Face.TryGetValue(body, out face))
            { Debug.Log("[S89IK] no face normal/standoff for body '" + body + "' -- inert"); return; }

            if (!animator.hasTransformHierarchy)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AnimatorUtility.DeoptimizeTransformHierarchy(gameObject);
                sw.Stop();
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[S89IK] deoptimised the spawned clone in {0:F2} ms (memory only, no asset touched)",
                    sw.Elapsed.TotalMilliseconds));
            }

            bool L = true;
            shoulder = animator.GetBoneTransform(L ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            elbow = animator.GetBoneTransform(L ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            wrist = animator.GetBoneTransform(L ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            midProx = animator.GetBoneTransform(L ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            idxProx = animator.GetBoneTransform(L ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            chest = animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Spine);
            hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck) ?? head;
            ready = shoulder && elbow && wrist && head;
            CaptureRestFrame();
            // env overrides so the pole and roll can be tuned against the audit without a rebuild
            string pw = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S91_POLE");
            if (!string.IsNullOrEmpty(pw)) PoleForwardWeight = float.Parse(pw, CultureInfo.InvariantCulture);
            string hr = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S91_ROLL");
            if (!string.IsNullOrEmpty(hr)) HandRollDeg = float.Parse(hr, CultureInfo.InvariantCulture);
            RomOnly = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S92_ROM_ONLY"));
            string so = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S91_STANDOFF");
            if (!string.IsNullOrEmpty(so)) face.standoff = float.Parse(so, CultureInfo.InvariantCulture);
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[S89IK] armed on '{0}' body='{1}' landmark={2} standoff={3:F5} pole={4:F2} roll={5:F1} ready={6}",
                gameObject.name, body, mouthLocal.ToString("F5"), face.standoff,
                PoleForwardWeight, HandRollDeg, ready));
        }

        /// S92. Build the anatomical rest frame from the skinned mesh's bind pose.
        ///
        /// STEP 0 finding, which this depends on: the Rocketbox biped has NO forearm twist bone.
        /// The left arm chain is exactly Bip01 L Clavicle -> L UpperArm -> L Forearm -> L Hand ->
        /// L Finger0..4, 82 Bip01 nodes in the rig and zero matching twist/roll/helper. Confirmed
        /// on Medical_Female_02 too. So pronation has nowhere to go but the forearm bone's own
        /// long axis, applied before the wrist -- there is no bone to distribute it along.
        private void CaptureRestFrame()
        {
            restOk = false;
            if (!ready || midProx == null || idxProx == null) return;
            var smr = GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null || smr.sharedMesh == null) return;
            var bones = smr.bones;
            var bp = smr.sharedMesh.bindposes;
            if (bones == null || bp == null || bones.Length != bp.Length) return;
            int iU = System.Array.IndexOf(bones, shoulder), iF = System.Array.IndexOf(bones, elbow);
            int iH = System.Array.IndexOf(bones, wrist), iM = System.Array.IndexOf(bones, midProx);
            int iI = System.Array.IndexOf(bones, idxProx);
            if (iU < 0 || iF < 0 || iH < 0 || iM < 0 || iI < 0) return;
            // bindposes[i] maps MESH space -> bone i local space, so its inverse is the bone's bind
            // pose in mesh space. Everything below is expressed in the FOREARM's bind local frame.
            Matrix4x4 toF = bp[iF];
            Vector3 Wl = toF.MultiplyPoint3x4(bp[iH].inverse.GetColumn(3));
            Vector3 Ml = toF.MultiplyPoint3x4(bp[iM].inverse.GetColumn(3));
            Vector3 Il = toF.MultiplyPoint3x4(bp[iI].inverse.GetColumn(3));
            axLocal = Wl.normalized;                                   // elbow -> wrist
            if (axLocal.sqrMagnitude < 1e-9f) return;
            // Ulnar direction: index knuckle -> middle knuckle continues toward the little finger.
            Vector3 uln = Vector3.ProjectOnPlane(Ml - Il, axLocal);
            if (uln.sqrMagnitude < 1e-12f) return;
            uln.Normalize();
            // Palm normal from the hand's own geometry, the same construction the solver uses.
            Vector3 palmN = Vector3.Cross(Ml - Wl, Il - Wl);
            if (palmN.sqrMagnitude < 1e-12f) return;
            palmN = Vector3.ProjectOnPlane(palmN.normalized, axLocal).normalized;
            // A positive rotation about cross(axis, target) carries the axis TOWARD target, so:
            devLocal = Vector3.Cross(axLocal, uln).normalized;    // + = ulnar deviation
            flexLocal = Vector3.Cross(axLocal, palmN).normalized; // + = palmar flexion
            // S97. Stored NEGATED, so the field means what it says: out of the PALM. The cross
            // product above, in this rig's joint order, comes out of the BACK of the hand -- fixed
            // by measurement, not by inspection: signing it this way reproduces the branch S89's
            // mouth-direction test picks on the hold frames, where that test is reliable, and the
            // un-negated one reproduces the mirrored branch on every frame.
            palmNLocal = -palmN;
            restRelHand = Quaternion.Inverse(bp[iF].inverse.rotation) * bp[iH].inverse.rotation;
            smrT = smr.transform;
            bindUpperMesh = bp[iU].inverse.rotation;
            restRelFore = Quaternion.Inverse(bindUpperMesh) * bp[iF].inverse.rotation;
            foreAxInUpper = (restRelFore * axLocal).normalized;
            upperAxLocal = bp[iU].MultiplyPoint3x4(bp[iF].inverse.GetColumn(3)).normalized;
            restOk = upperAxLocal.sqrMagnitude > 1e-9f;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[S92ROM] rest frame from bind pose: ok={0} axis={1} flex(+palmar)={2} dev(+ulnar)={3}",
                restOk, axLocal.ToString("F4"), flexLocal.ToString("F4"), devLocal.ToString("F4")));
        }

        /// q = twist * swing, twist about `axis` applied FIRST (i.e. in the PARENT frame). The usual
        /// swing-twist gives q = swing * twist; this is the mirrored form, and it is the one needed
        /// here: moving twist out of the wrist and into the forearm pre-multiplies the relative
        /// rotation, so only a twist-first split lets that excess cancel cleanly.
        private static void TwistFirst(Quaternion q, Vector3 axis, out Quaternion twist, out Quaternion swing)
        {
            Quaternion qi = Quaternion.Inverse(q);
            Vector3 r = new Vector3(qi.x, qi.y, qi.z);
            Vector3 p = Vector3.Project(r, axis);
            var t = new Quaternion(p.x, p.y, p.z, qi.w);
            float n = Mathf.Sqrt(t.x * t.x + t.y * t.y + t.z * t.z + t.w * t.w);
            if (n < 1e-8f) t = Quaternion.identity;
            else { t.x /= n; t.y /= n; t.z /= n; t.w /= n; }
            twist = Quaternion.Inverse(t);
            swing = Quaternion.Inverse(qi * Quaternion.Inverse(t));
        }

        /// Signed rotation angle of `q` about `axis`, in (-180, 180].
        private static float SignedAngle(Quaternion q, Vector3 axis)
        {
            q.ToAngleAxis(out float a, out Vector3 ax);
            if (float.IsNaN(a) || a < 1e-5f) return 0f;
            if (a > 180f) a -= 360f;
            return Vector3.Dot(ax.normalized, axis) >= 0f ? a : -a;
        }

        /// Rotation-vector components of a swing on two perpendicular axes. Exact in the
        /// exponential-map sense: a swing's axis is perpendicular to the twist axis, so its
        /// rotation vector decomposes onto the flexion and deviation axes without residue.
        private static void SwingComponents(Quaternion swing, Vector3 fAxis, Vector3 dAxis,
                                            out float flex, out float dev)
        {
            swing.ToAngleAxis(out float a, out Vector3 ax);
            if (float.IsNaN(a) || a < 1e-5f) { flex = dev = 0f; return; }
            if (a > 180f) a -= 360f;
            Vector3 rv = ax.normalized * a;
            flex = Vector3.Dot(rv, fAxis);
            dev = Vector3.Dot(rv, dAxis);
        }

        /// "Business_Male_01(Clone)" -> "Business_Male_01"
        public static string BodyKey(string n)
        {
            int i = n.IndexOf("(Clone)", System.StringComparison.Ordinal);
            return i >= 0 ? n.Substring(0, i) : n;
        }

        private void LateUpdate()
        {
            LastWeight = 0f;
            if (!ready) return;

            var st = animator.GetCurrentAnimatorStateInfo(0);
            if (st.shortNameHash != StateHash) return;
            var clips = animator.GetCurrentAnimatorClipInfo(0);
            if (clips == null || clips.Length == 0) return;
            var clip = clips[0].clip;
            spec = null;
            foreach (var s in Specs)
                if (s.clipName == clip.name && Mathf.Abs(s.clipLength - clip.length) < 0.01f) { spec = s; break; }
            if (spec == null) return;

            float frame = Mathf.Repeat(st.normalizedTime, 1f) * clip.length * 30f;
            LastFrameF = frame;
            float w = Ramp(frame, spec);
            LastWeight = w;
            DeltaShoulderDeg = DeltaElbowDeg = DeltaWristDeg = 0f;
            // S92. Measure ROM on EVERY frame of the state, w == 0 included -- the gate needs the
            // untouched frames as its own control, and leaving them unmeasured froze the last
            // in-window values into every subsequent row.
            MeasureRom();
            if (w <= 0f) return;   // outside the window this component performs no write at all

            if (!announced)
            {
                announced = true;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[S89IK] engaged: state={0} clip='{1}' len={2:F4} window={3}-{4}..{5}-{6}",
                    StateName, clip.name, clip.length, spec.rampIn0, spec.rampIn1, spec.rampOut0, spec.rampOut1));
            }
            Solve(w);
        }

        /// ================== S97: THE OFFLINE BAKE ENTRY POINT ==================
        /// Runs exactly the body of LateUpdate that follows the animator-state lookup, so an offline
        /// driver exercises THE SHIPPED SOLVE rather than a second copy of it that could drift. The
        /// runtime path does not call this and is unchanged by it; every constant stays frozen where
        /// S91/S92 left it. The caller is responsible for having posed the skeleton at `frame`.
        public void BakeFrame(float frame) { BakeFrame(frame, -1f); }

        /// wOverride >= 0 drives the solve at a chosen strength instead of the ramp's. The bake uses
        /// 1.0: it needs the FULL-STRENGTH solution at every frame so it can build the ramp itself,
        /// in muscle space, where the blend is linear and cannot flip branch. Slerping the two bone
        /// rotations and re-projecting -- which is what happens if the ramp is left to Solve -- put a
        /// 93 deg hand flip between f12 and f13.
        public void BakeFrame(float frame, float wOverride)
        {
            if (!ready) return;
            spec = Specs[0];
            LastFrameF = frame;
            float w = wOverride >= 0f ? wOverride : Ramp(frame, spec);
            LastWeight = w;
            DeltaShoulderDeg = DeltaElbowDeg = DeltaWristDeg = 0f;
            MeasureRom();
            if (w <= 0f) return;   // outside the window this component performs no write at all
            Solve(w);
        }

        /// The shipped ramp, exposed so the bake harness cannot disagree with it about the window.
        public static float BakeRamp(float frame) { return Ramp(frame, Specs[0]); }

        /// S97. Measure the contact geometry WITHOUT solving anything: the signed clearance of the
        /// palm along the outward face normal, and the raw palm-to-lip distance. This is how a clip
        /// that already carries the correction gets graded with the runtime layer switched off --
        /// same landmark, same normal, same standoff datum, no writes.
        public void BakeMeasureContact()
        {
            if (!ready) return;
            Vector3 mouth = head.TransformPoint(mouthLocal);
            Vector3 faceN = head.TransformDirection(face.n).normalized;
            LastFaceNormalWorld = faceN;
            LastMouthWorld = mouth;
            LastSignedClearance = Vector3.Dot(Palm() - mouth, faceN);
            LastPalmDist = Vector3.Distance(Palm(), mouth);
        }

        /// S97. Whether Setup() found the whole chain. The offline driver has to fail loudly on
        /// a rig it could not arm on; the runtime path just goes inert.
        public bool Ready { get { return ready; } }

        private static float Ramp(float f, ContactSpec s)
        {
            if (f <= s.rampIn0 || f >= s.rampOut1) return 0f;
            if (f < s.rampIn1) return Smooth((f - s.rampIn0) / Mathf.Max(1f, s.rampIn1 - s.rampIn0));
            if (f <= s.rampOut0) return 1f;
            return Smooth((s.rampOut1 - f) / Mathf.Max(1f, s.rampOut1 - s.rampOut0));
        }

        private static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        private Vector3 Palm()
        {
            return midProx != null ? Vector3.Lerp(wrist.position, midProx.position, 0.6f) : wrist.position;
        }

        private void Solve(float w)
        {
            Vector3 mouth = head.TransformPoint(mouthLocal);
            Vector3 faceN = head.TransformDirection(face.n).normalized;
            Vector3 palmTarget = mouth + faceN * face.standoff;

            // S90 iteration 3. The two corrections are coupled: rotating the wrist moves the palm
            // away from the point the arm was just solved for, and re-solving the arm re-tilts the
            // hand. Iteration 2 did one pass of each and overshot to +77 mm clearance with the
            // nearest hand vertex 45 mm off the lip. So alternate twice, at FULL strength, and blend
            // the ramp weight once at the end on LOCAL rotations -- blending per-pass would apply the
            // weight repeatedly and break the ramp.
            LastFaceNormalWorld = faceN;   // TwoBone reads this for the anterior pole; set it first
            LastMouthWorld = mouth;
            if (RomOnly)
            {
                // measure the source animation through the same ruler, write nothing
                MeasureRom();
                LastSignedClearance = Vector3.Dot(Palm() - mouth, faceN);
                LastPalmDist = Vector3.Distance(Palm(), mouth);
                DeltaShoulderDeg = DeltaElbowDeg = DeltaWristDeg = 0f;
                return;
            }
            Quaternion l0s = shoulder.localRotation, l0e = elbow.localRotation, l0w = wrist.localRotation;
            // Three passes, not two: the pen/gap SPREAD is a property of how flat the hand lies and
            // is unaffected by standoff, which only slides both numbers together. Two passes left a
            // 20.3 mm spread against a 20 mm shell, so no standoff could satisfy both ends.
            for (int pass = 0; pass < SolvePasses; pass++)
            {
                OrientHand(mouth, faceN);
                TwoBone(palmTarget);
                // S97: no-op unless a bake installed a projector (see BakeProject).
                if (BakeProject != null) BakeProject();
            }
            if (BakeProject != null) SettleOnManifold(mouth, faceN);
            // record the authored pose BEFORE the ramp blend
            DecomposeWrist(elbow.rotation, wrist.rotation,
                           out float af, out float ad, out float at);
            AuthoredWristFlexDeg = af; AuthoredWristDevDeg = ad; AuthoredWristTwistDeg = at;
            shoulder.localRotation = Quaternion.Slerp(l0s, shoulder.localRotation, w);
            elbow.localRotation = Quaternion.Slerp(l0e, elbow.localRotation, w);
            wrist.localRotation = Quaternion.Slerp(l0w, wrist.localRotation, w);
            DeltaShoulderDeg = Quaternion.Angle(l0s, shoulder.localRotation);
            DeltaElbowDeg = Quaternion.Angle(l0e, elbow.localRotation);
            DeltaWristDeg = Quaternion.Angle(l0w, wrist.localRotation);
            // AFTER the ramp blend: the gate grades the pose that is actually on screen, not the
            // full-strength solve. At low weight the arm is mostly the source animation and its ROM
            // reading belongs to the clip -- which is what the S91b weighted attribution then uses.
            MeasureRom();

            LastFaceNormalWorld = faceN;
            LastMouthWorld = mouth;
            LastSignedClearance = Vector3.Dot(Palm() - mouth, faceN);
            LastPalmDist = Vector3.Distance(Palm(), mouth);
            WriteFrames++;
        }

        /// S97, BAKE ONLY. Hold the wrist inside its axial range WITHOUT leaving the muscle manifold.
        ///
        /// ClampWristToRom cannot do this job once the pose has to be storable. On the manifold the
        /// hand has exactly TWO muscles relative to the forearm -- down-up and in-out -- and no axial
        /// one, so the twist this ruler reads is not a free variable: it is a FUNCTION of where the
        /// hand points. Writing a clamped rotation straight onto the transform, which is what the
        /// runtime layer does and is entitled to do, produces a pose the format cannot hold, and the
        /// projection puts the twist straight back (measured: clamped to -14.5, returns at +32.9;
        /// four settling iterations of the full orientation step changed it by 0.04 deg).
        ///
        /// So run S92's own trade -- "the roll is a soft goal; ROM is not" -- inside the
        /// representable set: rotate the hand about the forearm's long axis to spend the twist
        /// excess, project back onto the manifold, and repeat. That is an alternating projection
        /// between the manifold and the ROM set, and what it converges to is the storable pose
        /// closest to satisfying the clamp. It spends ROLL to buy the twist back, exactly as the
        /// runtime clamp does; it simply pays in a currency the file can carry.
        ///
        /// Frozen constants are untouched: this changes neither the roll TARGET (20), the pole
        /// (2.6), nor the standoff. It changes only how much of the roll target is given up.
        private void SettleOnManifold(Vector3 mouth, Vector3 faceN)
        {
            BakeProject();
            if (!restOk) return;
            for (int k = 0; k < BakeSettleIters; k++)
            {
                DecomposeWrist(elbow.rotation, wrist.rotation, out _, out _, out float tw);
                float excess = tw - Mathf.Clamp(tw, -WristTwistMaxDeg, WristTwistMaxDeg);
                if (Mathf.Abs(excess) < 0.05f) break;
                Vector3 axWorld = elbow.rotation * axLocal;
                // full step, not damped: the projection is what limits progress, and halving the
                // step just halves the progress per round.
                wrist.rotation = Quaternion.AngleAxis(-excess, axWorld) * wrist.rotation;
                // Spending the twist rotates the hand about the forearm, which slides the palm
                // CENTRE off the standoff point -- measured at 12 mm, which would have handed the
                // penetration gate a pose 12 mm deeper into the face than the standoff was
                // calibrated for. TwoBone puts it back, and it is safe to run here because it
                // rotates the forearm with the hand riding along: the twist just bought is carried
                // through it unchanged.
                TwoBone(mouth + faceN * face.standoff);
                BakeProject();
            }
        }

        /// Decide the hand's world orientation, then distribute it anatomically: pronation to the
        /// forearm, flexion/deviation to the wrist, nothing outside range.
        /// Lay the palm plane flat against the face plane. The palm normal comes from the hand's own
        /// geometry -- cross(wrist->middle, wrist->index) -- oriented to point out of the palm.
        private void OrientHand(Vector3 mouth, Vector3 faceN)
        {
            if (midProx == null || idxProx == null) return;
            Vector3 palmN = Vector3.Cross(midProx.position - wrist.position, idxProx.position - wrist.position);
            if (palmN.sqrMagnitude < 1e-10f) return;
            palmN.Normalize();
            // WHICH WAY IS "OUT OF THE PALM"? The cross product's sign is fixed by the rig, so the
            // question is only which of the two the solve should aim at the face.
            //
            // S89 answered it with the direction TO THE MOUTH, and that is stable only once the hand
            // is already near the face. S97 measured what it does before then: on frames 7..12 of the
            // ramp, with the hand still low and far, the test picks the opposite normal and the solve
            // converges on a MIRRORED hand -- flexion -46 where the hold wants +55, deviation +30
            // where the hold wants -20 -- and then snaps 93 deg to the correct branch at f13. This is
            // latent in the shipped runtime layer too (its own full-strength solve reaches only
            // 21.0 mm of clearance on those frames against 35.1 from f13 on); the ramp weight and an
            // off-manifold blend hide most of it there.
            //
            // BAKE ONLY, deliberately. Disambiguate against the hand's OWN bind palm normal, which is
            // rig-fixed and cannot flip with the arm's position. The runtime path keeps the shipped
            // test untouched, so the S92 baseline it was accepted on still reproduces exactly.
            bool flip = (BakeProject != null && restOk)
                ? Vector3.Dot(palmN, elbow.rotation * palmNLocal) < 0f
                : Vector3.Dot(palmN, mouth - Palm()) < 0f;
            if (flip) palmN = -palmN;
            wrist.rotation = Quaternion.FromToRotation(palmN, -faceN) * wrist.rotation;

            // Roll about the palm normal: aligns the hand's long axis (wrist -> middle finger base)
            // to a chosen direction in the face plane, which is what decides whether the hand sits
            // over the mouth or rides up onto the nose. Aligning the plane alone leaves this free.
            Vector3 longAxis = midProx.position - wrist.position;
            longAxis = Vector3.ProjectOnPlane(longAxis, faceN);
            if (longAxis.sqrMagnitude < 1e-10f) return;
            Vector3 faceUp = Vector3.ProjectOnPlane(Vector3.up, faceN);
            if (faceUp.sqrMagnitude < 1e-10f) return;
            Vector3 want = Quaternion.AngleAxis(HandRollDeg, faceN) * faceUp.normalized;
            wrist.rotation = Quaternion.FromToRotation(longAxis.normalized, want) * wrist.rotation;

            // S92. Everything above decided the hand's WORLD orientation, and up to S91 all of it
            // was dumped on the wrist -- which is why the hand came out reversed relative to the
            // forearm. The radiocarpal joint has no axial degree of freedom worth the name; the
            // twist that turns the palm over is forearm PRONATION. Route it there, then clamp what
            // is left at the wrist to the joint's real range.
            RouteTwistToForearm();
            ClampWristToRom();
        }

        /// Move the axial component of the hand's orientation out of the wrist and into the
        /// forearm. This is pronation, and it is the whole anatomical point of S92: the radiocarpal
        /// joint has no axial degree of freedom, so the twist that turns the palm over must live in
        /// the forearm. Up to S91 all of it was dumped on the wrist, which is why the hand came out
        /// reversed relative to the forearm.
        ///
        /// WHAT PRONATION CANNOT DO -- established here, after a wrong turn worth recording. A
        /// search over the pronation angle was written first, on the theory that pronation decides
        /// WHICH wrist axis absorbs the bend, so it could steer the demand into flexion (+-60 deg
        /// of range) and away from deviation (20-30). That theory is false. Rotating the forearm
        /// about its own axis pre-multiplies the hand-relative-to-forearm rotation by a twist about
        /// that axis, and for the twist-first split q = twist * swing that leaves the SWING
        /// completely unchanged -- so flexion and deviation are untouched, exactly. The search duly
        /// returned 0-5 deg on every candidate. The bend between forearm and hand is fixed by the
        /// two directions, and pronation moves neither: the forearm axis is invariant under
        /// rotation about itself, and the hand is then pinned back to the orientation it wanted.
        ///
        /// So the only levers on an over-flexed wrist are the ROLL target (soft, relaxed by
        /// ClampWristToRom) and the elbow POLE (which moves the forearm direction). Pronation
        /// handles the axial term and nothing else, which is precisely what the wrist needs of it.
        ///
        /// Rotating the forearm about the elbow->wrist line does NOT move the wrist position -- the
        /// axis passes through both joints -- so the two-bone position solve is untouched, and
        /// TwoBone's FromToRotation deltas carry no twist of their own, so the pronation survives
        /// into the next pass.
        private void RouteTwistToForearm()
        {
            if (!restOk) return;
            Quaternion target = wrist.rotation;               // orientation the roll step asked for
            Vector3 axWorld = elbow.rotation * axLocal;
            DecomposeWrist(elbow.rotation, target, out _, out _, out float twDeg);
            float excess = twDeg - Mathf.Clamp(twDeg, -WristTwistMaxDeg, WristTwistMaxDeg);
            // Do not buy wrist range at the cost of an impossible forearm: stop at the forearm's
            // own limit and let the wrist clamp absorb whatever is left.
            if (Mathf.Abs(excess) > 1e-4f)
            {
                float now = AbsolutePronation(elbow.rotation);
                float want = Mathf.Clamp(now + excess, -ForearmPronationMaxDeg, ForearmPronationMaxDeg);
                excess = want - now;
            }
            AppliedPronationDeg = excess;
            if (Mathf.Abs(excess) < 1e-4f) return;
            // Pre-multiplying the relative rotation by a local-axis twist subtracts exactly `excess`
            // from the wrist's axial angle; in world terms that is a rotation of the forearm about
            // the live elbow->wrist direction.
            elbow.rotation = Quaternion.AngleAxis(excess, axWorld) * elbow.rotation;
            wrist.rotation = target;                          // hand keeps the orientation it wanted
        }

        /// Forearm rotation about its own shaft, relative to the bind pose: anatomical pronation.
        private float AbsolutePronation(Quaternion elbowRot)
        {
            if (!restOk || smrT == null) return 0f;
            Quaternion foreInMesh = Quaternion.Inverse(smrT.rotation) * elbowRot;
            Quaternion upInMesh = Quaternion.Inverse(smrT.rotation) * shoulder.rotation;
            Quaternion rel = Quaternion.Inverse(upInMesh) * foreInMesh;
            Quaternion dev = rel * Quaternion.Inverse(restRelFore);
            TwistFirst(dev, foreAxInUpper, out Quaternion tw, out _);
            return SignedAngle(tw, foreAxInUpper);
        }

        /// The wrist's flexion / deviation / twist for a given forearm and hand rotation.
        private void DecomposeWrist(Quaternion elbowRot, Quaternion handRot,
                                    out float flex, out float dev, out float twist)
        {
            Quaternion rel = Quaternion.Inverse(elbowRot) * handRot;
            Quaternion d = rel * Quaternion.Inverse(restRelHand);
            TwistFirst(d, axLocal, out Quaternion tw, out Quaternion sw);
            twist = SignedAngle(tw, axLocal);
            SwingComponents(sw, flexLocal, devLocal, out flex, out dev);
        }

        /// Clamp the hand's rotation relative to the forearm into the wrist's real range. If the
        /// orientation the roll asked for cannot be had inside the range, the ROLL gives way --
        /// hand orientation on the face is a soft goal, joint range is not.
        private void ClampWristToRom()
        {
            if (!restOk) return;
            Quaternion rel = Quaternion.Inverse(elbow.rotation) * wrist.rotation;
            Quaternion dev = rel * Quaternion.Inverse(restRelHand);
            TwistFirst(dev, axLocal, out Quaternion tw, out Quaternion sw);
            float twDeg = SignedAngle(tw, axLocal);
            SwingComponents(sw, flexLocal, devLocal, out float flexDeg, out float devDeg);
            float cTw = Mathf.Clamp(twDeg, -WristTwistMaxDeg, WristTwistMaxDeg);
            float cFlex = Mathf.Clamp(flexDeg, WristFlexMinDeg, WristFlexMaxDeg);
            float cDev = Mathf.Clamp(devDeg, WristDevMinDeg, WristDevMaxDeg);
            LastWristTwistDeg = cTw; LastWristFlexDeg = cFlex; LastWristDevDeg = cDev;
            float givenUp = Mathf.Abs(twDeg - cTw) + Mathf.Abs(flexDeg - cFlex) + Mathf.Abs(devDeg - cDev);
            LastRollGivenUpDeg = givenUp;
            if (givenUp < 1e-3f) return;
            RomClampFrames++;
            Vector3 rv = flexLocal * cFlex + devLocal * cDev;
            float ang = rv.magnitude;
            Quaternion swC = ang < 1e-5f ? Quaternion.identity : Quaternion.AngleAxis(ang, rv / ang);
            Quaternion twC = Quaternion.AngleAxis(cTw, axLocal);
            wrist.rotation = elbow.rotation * (twC * swC) * restRelHand;
        }

        /// S92 G-ROM measurement. Reads the pose as it stands and publishes every angle the gate
        /// grades. Called after the ramp blend, so it measures what is actually on screen -- not
        /// what the solver asked for before the weight was applied.
        private void MeasureRom()
        {
            if (!restOk) { LastElbowFlexDeg = LastShoulderElevDeg = LastShoulderTwistDeg = 0f; return; }
            Quaternion rel = Quaternion.Inverse(elbow.rotation) * wrist.rotation;
            Quaternion dev = rel * Quaternion.Inverse(restRelHand);
            TwistFirst(dev, axLocal, out Quaternion tw, out Quaternion sw);
            LastWristTwistDeg = SignedAngle(tw, axLocal);
            SwingComponents(sw, flexLocal, devLocal, out float f, out float d);
            LastWristFlexDeg = f; LastWristDevDeg = d;
            LastForearmPronationDeg = AbsolutePronation(elbow.rotation);
            // Elbow flexion: 180 minus the included angle at the elbow. Straight arm = 0.
            Vector3 up = shoulder.position - elbow.position, fo = wrist.position - elbow.position;
            LastElbowFlexDeg = (up.sqrMagnitude < 1e-12f || fo.sqrMagnitude < 1e-12f)
                ? 0f : 180f - Vector3.Angle(up, fo);
            // Shoulder elevation: angle of the humerus away from hanging straight down the spine.
            // NOT chest.up -- this is a 3ds Max biped, whose bone axes run ALONG the bone, so the
            // chest's "up" is not the spine direction at all and read elevation as 147 deg for a
            // hand-at-mouth pose whose elbow is plainly below the shoulder. The spine axis is
            // neck->hips, the same axis the S91 torso audit measures against.
            Vector3 hum = elbow.position - shoulder.position;
            Vector3 down = (hips != null && neck != null) ? (hips.position - neck.position) : Vector3.down;
            if (down.sqrMagnitude < 1e-12f) down = Vector3.down;
            LastShoulderElevDeg = hum.sqrMagnitude < 1e-12f ? 0f : Vector3.Angle(hum, down);
            // Humeral axial rotation about its own shaft, relative to the bind pose. Both poses are
            // taken into the mesh's frame first -- the bind pose lives there, the live bone lives in
            // world -- and the difference is then expressed in the bone's own local coordinates, so
            // the twist axis is the constant upperAxLocal rather than something that moves with the
            // character's heading.
            // Humeral axial rotation, from the ELBOW FLEXION PLANE rather than from the bind pose.
            //
            // The bind-referenced swing-twist was tried first and is unusable on this rig: the bind
            // pose is a T-pose, so the humerus has already swung 107-163 deg from it on EVERY frame
            // of this clip, which is deep into the region where the minimal-swing/residual-twist
            // split degenerates. It read between -176 and +179 deg on neighbouring frames of the
            // untouched source animation -- a sign flip, not a motion. Gating on that would have
            // been gating on noise.
            //
            // With the elbow flexed (it sits at 130-140 deg through the hold) the forearm's
            // direction perpendicular to the humerus IS the humeral rotation, and it needs no bind
            // reference at all. Zero is defined as the forearm swinging in the plane containing the
            // spine axis -- a convention, and declared as one, which is why this is REPORTED and
            // not gated. It degenerates only if the elbow straightens; LastElbowFlexDeg shows that.
            Vector3 hAx = (elbow.position - shoulder.position).normalized;
            Vector3 fPerp = Vector3.ProjectOnPlane(wrist.position - elbow.position, hAx);
            Vector3 refPerp = Vector3.ProjectOnPlane(down, hAx);
            LastShoulderSwingDeg = Vector3.Angle(hAx, down);
            LastShoulderTwistDeg = (fPerp.sqrMagnitude < 1e-10f || refPerp.sqrMagnitude < 1e-10f)
                ? 0f : Vector3.SignedAngle(refPerp, fPerp, hAx);
        }

        /// Analytic two-bone solve placing the PALM (not the wrist) at the target, with the elbow
        /// carried toward a down-and-slightly-outward pole and the chain never hyperextended.
        private void TwoBone(Vector3 palmTarget)
        {
            Vector3 S = shoulder.position;
            Vector3 wristTarget = palmTarget - (Palm() - wrist.position);
            float l1 = Vector3.Distance(S, elbow.position);
            float l2 = Vector3.Distance(elbow.position, wrist.position);
            Vector3 toT = wristTarget - S;
            float d = toT.magnitude;
            if (d < 1e-4f) return;
            float dC = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);   // GUARD
            Vector3 dir = toT / d;
            wristTarget = S + dir * dC;

            // S91 FIX 1. The pole was down + LATERAL (anti-S87). That put the elbow out to the
            // side, so the upper arm ran down-and-out while the forearm had to come back up and
            // across the chest to reach the face -- the arm reached the mouth THROUGH the torso.
            // Anterior is the human way: elbow in front of the sternum, forearm vertical in front
            // of the chest. The anterior direction is already measured -- it is the face normal,
            // flattened to horizontal -- so no extra bone is needed to find it.
            Vector3 anterior = LastFaceNormalWorld; anterior.y = 0f;
            if (anterior.sqrMagnitude < 1e-6f) anterior = Vector3.Cross(Vector3.up, dir);
            Vector3 pole = (Vector3.down * 1.0f + anterior.normalized * PoleForwardWeight).normalized;

            float cosA = Mathf.Clamp((l1 * l1 + dC * dC - l2 * l2) / (2f * l1 * dC), -1f, 1f);
            float a1 = Mathf.Acos(cosA) * Mathf.Rad2Deg;
            Vector3 axis = Vector3.Cross(dir, pole);
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(dir, Vector3.forward);
            axis.Normalize();
            // positive rotation about cross(dir, pole) carries dir TOWARD pole; S89 iteration 1 had
            // this negated and drove the elbow to head height
            Vector3 elbowTarget = S + (Quaternion.AngleAxis(a1, axis) * dir) * l1;

            shoulder.rotation = Quaternion.FromToRotation(elbow.position - S, elbowTarget - S) * shoulder.rotation;
            Vector3 E = elbow.position;
            elbow.rotation = Quaternion.FromToRotation(wrist.position - E, wristTarget - E) * elbow.rotation;
        }
    }
}
