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

        private void Start()
        {
            if (done) { return; }
            done = true;
            if (clipName == "Sitting") { AddStool(); }
            else if (clipName == "Standing_Arguing") { AddArguingPartner(); }
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

            Vector3 fwd = transform.forward;
            partner.transform.position = transform.position + fwd * partnerSpacingMeters;
            partner.transform.rotation = Quaternion.LookRotation(-fwd, Vector3.up);

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
    }
}
