using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 44 TASK 5.2 / 5.3: per-clip staging that a bare animation cannot supply.
    ///
    /// 5.2 Sitting -- the character sits on nothing. A stool is added under it: a primitive at seat
    /// height 0.45 m, plain and non-reflective (a specular highlight is a distractor for a VLM
    /// judging behaviour), WITH a collider.
    ///
    /// The collider is the interesting decision, and it differs from TASK 4's carried box on
    /// purpose. The box sits at ~1.1 m, entirely above the 0.32 m laser plane, so a collider there
    /// would be invisible to the robot and would only add phantom obstacle cost. The stool spans
    /// 0 to 0.45 m and therefore CROSSES that plane -- the robot can physically see it, and without
    /// a collider it would plan straight through a solid object, which reads as obviously wrong.
    ///
    /// Consequence to record rather than tune away: a person plus a stool occupies more space than
    /// a person, so this scenario is harder to navigate. That is the intended physical situation,
    /// not a parameter to compensate for.
    ///
    /// 5.3 Standing_Arguing -- the source clip is one person, so an argument needs a second. A
    /// mirrored instance is spawned facing the first at ~1.2 m with a randomised animation phase
    /// offset, because two figures gesturing in perfect lockstep read as obviously synthetic.
    /// </summary>
    public class S44ClipProps : MonoBehaviour
    {
        public string clipName = "";

        // 5.2 stool geometry. Seat at 0.45 m, i.e. straddling the 0.32 m sensor plane.
        public float seatHeightMeters = 0.45f;
        public Vector3 stoolSize = new Vector3(0.40f, 0.45f, 0.40f);
        public Color stoolColor = new Color(0.45f, 0.42f, 0.38f);

        // 5.3 partner placement.
        public float partnerSpacingMeters = 1.2f;
        public float partnerPhaseMinSec = 0.3f;
        public float partnerPhaseMaxSec = 1.0f;

        private bool done;

        /// <summary>Session 46 (2): in-place clips must not apply root motion at all.</summary>
        public bool suppressRootMotion = false;

        private Animator rootMotionTarget;
        private int verifyFrames;

        private void Start()
        {
            if (done) { return; }
            done = true;
            if (suppressRootMotion) { SuppressRootMotion(); }
            if (clipName == "Sitting") { AddStool(); }
            else if (clipName == "Standing_Arguing") { AddArguingPartner(); }
        }

        /// <summary>
        /// Session 46 (2), authorised after three failed attempts at the rotation itself.
        ///
        /// A character that must not travel should not be applying root motion. Base.cs:32
        /// hardcodes applyRootMotion = true and is off-limits to edit, but the Animator field is
        /// writable at runtime -- the same pattern as Session 44's Idling override and execution
        /// order, i.e. drive the public surface rather than modify upstream source.
        ///
        /// This removes the cause rather than fighting the symptom. The 7.779 m drift appeared only
        /// once a component started forcing transform.rotation every LateUpdate, which conflicts
        /// with Unity's root-motion auto-apply writing position and rotation in the same frame. The
        /// clip's own root motion nets to zero over a loop (avgSpeed 0.0000 across its 20.8 s), so
        /// accumulation was never the explanation.
        /// </summary>
        private void SuppressRootMotion()
        {
            rootMotionTarget = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (rootMotionTarget == null)
            {
                Debug.LogWarning("[S46RootMotion] no animator resolved -- root motion NOT suppressed.");
                return;
            }
            rootMotionTarget.applyRootMotion = false;
            Debug.Log("[S46RootMotion] applyRootMotion set false on '" + rootMotionTarget.gameObject.name
                + "' (clip=" + clipName + ")");
        }

        // Base.Start() sets applyRootMotion = true from its own hardcoded field, so the write above
        // has to be confirmed to survive rather than assumed. Logged for the first frames after
        // init; if any of these reports True the suppression lost a race and the result is void.
        private void LateUpdate()
        {
            if (!suppressRootMotion || rootMotionTarget == null || verifyFrames >= 5) { return; }
            verifyFrames++;
            Debug.Log("[S46RootMotion] verify frame " + verifyFrames + ": applyRootMotion="
                + rootMotionTarget.applyRootMotion);
            if (rootMotionTarget.applyRootMotion)
            {
                rootMotionTarget.applyRootMotion = false;
                Debug.LogWarning("[S46RootMotion] was re-enabled by another component -- re-suppressed.");
            }
        }

        private void AddStool()
        {
            var stool = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stool.name = "S44Stool";
            stool.transform.SetParent(transform, false);
            stool.transform.localScale = stoolSize;
            // Centre the box so its TOP face is the seat: the character's root is at ground level,
            // so the seat must land at seatHeightMeters above that root.
            stool.transform.localPosition = new Vector3(0f, seatHeightMeters - stoolSize.y * 0.5f, 0f);
            stool.transform.localRotation = Quaternion.identity;

            var rend = stool.GetComponent<Renderer>();
            if (rend != null)
            {
                // Fully matte. A highlight travelling across the prop as the robot moves is exactly
                // the kind of artefact that draws a VLM's attention away from the behaviour.
                var mat = new Material(Shader.Find("Standard"));
                mat.color = stoolColor;
                mat.SetFloat("_Glossiness", 0f);
                mat.SetFloat("_Metallic", 0f);
                rend.material = mat;
            }
            // Collider deliberately KEPT (CreatePrimitive supplies one) -- see the class doc.
            Debug.Log("[S44Props] Sitting: stool added, seat " + seatHeightMeters.ToString("F2")
                + " m, size " + stoolSize + ", collider ON (crosses the 0.32 m sensor plane)");
        }

        private void AddArguingPartner()
        {
            var animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator == null)
            {
                Debug.LogWarning("[S44Props] Standing_Arguing: no animator found, partner not added.");
                return;
            }

            // Clone the visual body only. Copying the whole agent would bring its navigation and
            // social-force components along, giving the scene a second independently-steering
            // pedestrian rather than the second half of one conversation.
            var src = animator.gameObject;
            var partner = Instantiate(src, transform.parent);
            partner.name = "S44ArguingPartner";
            foreach (var c in partner.GetComponentsInChildren<MonoBehaviour>())
            {
                if (c is Animator) { continue; }
                if (c != null && c.GetType().Namespace != null
                    && (c.GetType().Namespace.StartsWith("SEAN") || c.GetType().Namespace.StartsWith("IVI")))
                {
                    Destroy(c);
                }
            }

            // Session 45 (1.4): rotate the pair 90 degrees about the vertical axis so their
            // conversation axis is PERPENDICULAR to the robot's approach. Facing the robot head-on,
            // one figure occluded the other and the camera saw a front and a back; side-on, both
            // are visible in profile and the exchange reads as an exchange. The pair still faces
            // each other at partnerSpacingMeters with its phase offset -- only the unit's heading
            // relative to the robot changes.
            Vector3 axis = Quaternion.AngleAxis(90f, Vector3.up) * transform.forward;
            partner.transform.position = transform.position + axis * partnerSpacingMeters;
            // Session 46 (1.1): the facing has to be HELD, not set once. Setting the visual's world
            // rotation in Start() lasted only until the next frame -- the visual is a child of the
            // agent root, Base rewrites that root's rotation every Update, and the child's world
            // rotation follows it. The result was two figures standing on the correct perpendicular
            // axis while one of them faced the robot.
            //
            // Both figures now hold an explicit world yaw each LateUpdate, after Base has run. The
            // agent root is never written to (Session 45: writing it made the character translate
            // 7.130 m in a trial where it must not move at all).
            var holdSelf = src.AddComponent<HoldWorldYaw>();
            holdSelf.worldForward = axis;
            var holdPartner = partner.AddComponent<HoldWorldYaw>();
            holdPartner.worldForward = -axis;

            var pa = partner.GetComponent<Animator>();
            if (pa != null)
            {
                pa.applyRootMotion = false;   // the partner holds station; only the pose animates
                var st = pa.GetCurrentAnimatorStateInfo(0);
                // Deterministic per-position offset rather than Random, so a rerun of the same
                // configuration reproduces the same staging.
                float span = Mathf.Max(partnerPhaseMaxSec - partnerPhaseMinSec, 0.01f);
                float phase = partnerPhaseMinSec
                    + Mathf.Abs(Mathf.Sin(transform.position.x * 12.9898f + transform.position.z * 78.233f)) * span;
                pa.Play(st.fullPathHash, 0, phase);
                Debug.Log("[S44Props] Standing_Arguing: partner at " + partnerSpacingMeters.ToString("F2")
                    + " m facing back, phase offset " + phase.ToString("F2") + " s");
            }
        }

        /// <summary>
        /// Session 46 (1.1): pins a transform's world yaw every LateUpdate.
        ///
        /// LateUpdate specifically -- Base.Move() rewrites the agent root's rotation in Update, and
        /// this transform is a child of it, so anything applied earlier is inherited away. The
        /// three conditions the staging has to satisfy simultaneously are: the pair's axis
        /// perpendicular to the robot's approach, the two yaws 180 degrees apart, and both yaws
        /// themselves perpendicular to the approach so the camera sees two profiles.
        /// </summary>
        private class HoldWorldYaw : MonoBehaviour
        {
            public Vector3 worldForward = Vector3.forward;

            private void LateUpdate()
            {
                Vector3 f = worldForward;
                f.y = 0f;
                if (f.sqrMagnitude < 1e-6f) { return; }
                transform.rotation = Quaternion.LookRotation(f.normalized, Vector3.up);
            }
        }
    }
}
