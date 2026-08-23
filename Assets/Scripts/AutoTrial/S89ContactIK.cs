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
            public float palmStandoff;   // metres in front of the landmark, so the palm rests rather than sinks
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

        public const string StateName = "SurprisedReaction";
        private static readonly int StateHash = Animator.StringToHash(StateName);

        private Animator animator;
        private Transform shoulder, elbow, wrist, head, midProx, idxProx, chest;
        private ContactSpec spec;
        private Vector3 mouthLocal;
        private bool ready, announced;
        public float LastWeight { get; private set; }
        public float LastPalmDist { get; private set; } = -1f;
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
            Debug.Log("[S89IK] armed on '" + gameObject.name + "' body='" + body + "' landmark="
                      + mouthLocal.ToString("F5") + " ready=" + ready);
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
            // face outward normal, from the head centre through the landmark
            Vector3 faceN = (mouth - head.position); faceN.y *= 0.35f; faceN.Normalize();
            Vector3 palmTarget = mouth + faceN * spec.palmStandoff;

            Vector3 S = shoulder.position;
            Vector3 palm = Palm();
            Vector3 wristTarget = palmTarget - (palm - wrist.position);

            float l1 = Vector3.Distance(S, elbow.position);
            float l2 = Vector3.Distance(elbow.position, wrist.position);
            Vector3 toT = wristTarget - S;
            float d = toT.magnitude;
            // GUARD: never hyperextend, never fold past the chain minimum.
            float dClamped = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
            if (d < 1e-4f) return;
            Vector3 dir = toT / d;
            wristTarget = S + dir * dClamped;

            // Elbow pole: DOWN and slightly outward. S87's failure mode -- the elbow riding at
            // shoulder height so the forearm swept across the face -- is the anti-goal here.
            Vector3 outward = (shoulder.position - (chest != null ? chest.position : S)); outward.y = 0f;
            if (outward.sqrMagnitude < 1e-6f) outward = Vector3.Cross(Vector3.up, dir);
            Vector3 pole = (Vector3.down * 1.0f + outward.normalized * 0.35f).normalized;

            float cosA = Mathf.Clamp((l1 * l1 + dClamped * dClamped - l2 * l2) / (2f * l1 * dClamped), -1f, 1f);
            float a1 = Mathf.Acos(cosA) * Mathf.Rad2Deg;
            Vector3 axis = Vector3.Cross(dir, pole);
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.Cross(dir, Vector3.forward);
            axis.Normalize();
            // Sign matters and the first iteration had it backwards. axis = cross(dir, pole), and a
            // POSITIVE rotation about that axis carries dir TOWARD pole (dir=+X, pole=+Y, axis=+Z,
            // +90 deg takes X to Y). Negating it drove the elbow away from the pole -- up to head
            // height -- and reproduced S87's failure exactly: forearm across the face, fingers out
            // past the far cheek, 129 head vertices inside the forearm capsule.
            Vector3 elbowTarget = S + (Quaternion.AngleAxis(a1, axis) * dir) * l1;

            // upper arm: current S->E onto S->elbowTarget
            Quaternion q1 = Quaternion.FromToRotation(elbow.position - S, elbowTarget - S);
            Quaternion before1 = shoulder.rotation;
            shoulder.rotation = Quaternion.Slerp(shoulder.rotation, q1 * shoulder.rotation, w);
            DeltaShoulderDeg = Quaternion.Angle(before1, shoulder.rotation);

            // forearm: current E->W onto E->wristTarget, after the shoulder moved
            Vector3 E = elbow.position;
            Quaternion q2 = Quaternion.FromToRotation(wrist.position - E, wristTarget - E);
            Quaternion before2 = elbow.rotation;
            elbow.rotation = Quaternion.Slerp(elbow.rotation, q2 * elbow.rotation, w);
            DeltaElbowDeg = Quaternion.Angle(before2, elbow.rotation);

            // wrist: turn the palm toward the landmark
            Vector3 palmDir = Palm() - wrist.position;
            if (palmDir.sqrMagnitude > 1e-8f)
            {
                Quaternion q3 = Quaternion.FromToRotation(palmDir, mouth - wrist.position);
                Quaternion before3 = wrist.rotation;
                wrist.rotation = Quaternion.Slerp(wrist.rotation, q3 * wrist.rotation, w);
                DeltaWristDeg = Quaternion.Angle(before3, wrist.rotation);
            }
            LastPalmDist = Vector3.Distance(Palm(), mouth);
            WriteFrames++;
        }
    }
}
