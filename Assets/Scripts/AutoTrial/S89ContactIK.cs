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

        /// S91 FIX 2. Roll of the hand about the palm normal, degrees, so the hand centres on the
        /// MOUTH rather than riding up over the nose and upper lip. 0 = the hand's long axis points
        /// along the face-plane "up", which is the fist-over-the-nose pose S90 shipped. 100 deg lays
        /// the hand across the lower face; it also lifts forearm-vs-head clearance from +3.3 mm to
        /// +43.5 mm, because the forearm no longer has to stand vertically against the chin.
        public static float HandRollDeg = 100f;

        public const string StateName = "SurprisedReaction";
        private static readonly int StateHash = Animator.StringToHash(StateName);

        private Animator animator;
        private Transform shoulder, elbow, wrist, head, midProx, idxProx, chest;   // idxProx: palm plane
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
            ready = shoulder && elbow && wrist && head;
            // env overrides so the pole and roll can be tuned against the audit without a rebuild
            string pw = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S91_POLE");
            if (!string.IsNullOrEmpty(pw)) PoleForwardWeight = float.Parse(pw, CultureInfo.InvariantCulture);
            string hr = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S91_ROLL");
            if (!string.IsNullOrEmpty(hr)) HandRollDeg = float.Parse(hr, CultureInfo.InvariantCulture);
            string so = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S91_STANDOFF");
            if (!string.IsNullOrEmpty(so)) face.standoff = float.Parse(so, CultureInfo.InvariantCulture);
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[S89IK] armed on '{0}' body='{1}' landmark={2} standoff={3:F5} pole={4:F2} roll={5:F1} ready={6}",
                gameObject.name, body, mouthLocal.ToString("F5"), face.standoff,
                PoleForwardWeight, HandRollDeg, ready));
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
            Quaternion l0s = shoulder.localRotation, l0e = elbow.localRotation, l0w = wrist.localRotation;
            // Three passes, not two: the pen/gap SPREAD is a property of how flat the hand lies and
            // is unaffected by standoff, which only slides both numbers together. Two passes left a
            // 20.3 mm spread against a 20 mm shell, so no standoff could satisfy both ends.
            for (int pass = 0; pass < 3; pass++)
            {
                OrientWrist(mouth, faceN);
                TwoBone(palmTarget);
            }
            shoulder.localRotation = Quaternion.Slerp(l0s, shoulder.localRotation, w);
            elbow.localRotation = Quaternion.Slerp(l0e, elbow.localRotation, w);
            wrist.localRotation = Quaternion.Slerp(l0w, wrist.localRotation, w);
            DeltaShoulderDeg = Quaternion.Angle(l0s, shoulder.localRotation);
            DeltaElbowDeg = Quaternion.Angle(l0e, elbow.localRotation);
            DeltaWristDeg = Quaternion.Angle(l0w, wrist.localRotation);

            LastFaceNormalWorld = faceN;
            LastMouthWorld = mouth;
            LastSignedClearance = Vector3.Dot(Palm() - mouth, faceN);
            LastPalmDist = Vector3.Distance(Palm(), mouth);
            WriteFrames++;
        }

        /// Lay the palm plane flat against the face plane. The palm normal comes from the hand's own
        /// geometry -- cross(wrist->middle, wrist->index) -- oriented to point out of the palm.
        private void OrientWrist(Vector3 mouth, Vector3 faceN)
        {
            if (midProx == null || idxProx == null) return;
            Vector3 palmN = Vector3.Cross(midProx.position - wrist.position, idxProx.position - wrist.position);
            if (palmN.sqrMagnitude < 1e-10f) return;
            palmN.Normalize();
            if (Vector3.Dot(palmN, mouth - Palm()) < 0f) palmN = -palmN;
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
