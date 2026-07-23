using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 32 FIX E: locomotion animator playback rate should scale with the agent's ACTUAL
    /// movement speed. Before this fix, `animator.speed` was never touched anywhere in this
    /// codebase (grepped tools/*.py, Assets/Scripts/AutoTrial/**, PedestrianModulator.cs -- no
    /// existing `animator.speed =` or comparable hook) -- every appearance played its locomotion
    /// clip at its authored (1.0x) rate regardless of `walkSpeedMultiplier`/`--ped-speed`, so a
    /// slowed-down actor (white_cane_user, wheelchair_user after Session 31/32's speed
    /// corrections) visually "crawls while its legs/wheels churn at a normal walking cadence" --
    /// the user's own "white_cane looks too fast" complaint, even though its measured m/s speed
    /// was already correctly slow (Session 31 confirmed this: movement speed and animation rate
    /// are two independent things, and only the first had been fixed).
    ///
    /// General fix, not appearance-specific: attached to every Zone A/B pedestrian regardless of
    /// personality (a general locomotion property, orthogonal to personality-driven reaction
    /// behavior). animator.speed = currentSpeed / ReferenceSpeedMps, clamped to [MinSpeedScale,
    /// MaxSpeedScale] so an extreme multiplier (e.g. scooter's ~3.5 m/s) doesn't produce an
    /// absurd, blurring playback rate -- clamped rather than unclamped because this project's
    /// appearances don't all share one "natural" authored clip speed (scooter/cyclist likely use
    /// their own riding animations, not the walk-cycle this reference speed was chosen against);
    /// FIX E's own verification target is white_cane_user and wheelchair_user specifically, where
    /// the reference speed is directly applicable.
    ///
    /// Session 36 FIX 5 correction: originally read `Base.velocity.magnitude` (a pre-existing
    /// public property). Diagnostic instrumentation this session proved this is WRONG for
    /// white_cane_user specifically: `Base.velocity` reported a stable ~2.09 m/s the whole trial
    /// while the pedestrian's actual on-screen displacement (computed independently from
    /// frames.csv position deltas) was ~0.32-0.41 m/s -- `Base.velocity` reflects this
    /// appearance's underlying nav/rigidbody-commanded speed, not the root-motion-driven visible
    /// movement (see AutoTrialBootstrap.cs's own note that white_cane_user is "unlike every other
    /// current Zone B container" in this exact respect -- the same root-motion divergence
    /// Session 21 already characterized for its origin-reset bug). Feeding that inflated 2.09 into
    /// `scale = currentSpeed / referenceSpeedMps` clamped the result to `maxSpeedScale=1.5` --
    /// i.e. this component was SPEEDING UP white_cane's animator, the exact opposite of the
    /// intended slowdown, which is why Session 35's "confirm this applies" check evidently didn't
    /// catch the real complaint (the mechanism WAS running, just computing the wrong number).
    /// Fixed by measuring speed directly from frame-to-frame world-position displacement
    /// (`transform.position` delta / `Time.deltaTime`) instead of trusting `Base.velocity` --
    /// this is by construction exactly "how fast the character visibly moved," correct regardless
    /// of whatever internal representation any given appearance's movement path uses, and general
    /// (not a white_cane-specific branch).
    /// </summary>
    public class S32AnimatorSpeedScaler : MonoBehaviour
    {
        // The shared Zone A/B walk-cycle reference pace this project has used since Session 30R
        // (business_male_01's own measured ~1.29-1.30 m/s, walkSpeedMultiplier=1.0 case) -- the
        // speed at which the underlying Locomotion clip's own root motion/authored cadence reads
        // as natural.
        public float referenceSpeedMps = 1.3f;
        public float minSpeedScale = 0.3f;
        public float maxSpeedScale = 1.5f;

        private Animator animator;
        private Scenario.Agents.Base baseAgent;
        private Vector3 lastPos;
        private bool havePrevPos = false;
        // Smoothed so a single noisy frame (e.g. a footstep-driven root-motion micro-stutter)
        // doesn't jerk the playback rate -- exponential moving average, not a hard window buffer.
        private float smoothedSpeed = 0f;
        private const float SmoothingTau = 0.25f;

        // Session 36 FIX 5, second finding: an early diagnostic run showed a huge (300+ m/s)
        // spurious FIRST-FRAME position-delta speed reading that then decayed smoothly over
        // ~1-2s -- an implausible single-frame teleport, not real walking (matches
        // S21PedestrianPositionGuardian's own "implausible multi-meter single-frame jump"
        // concept, which exists specifically because this appearance's nested-Animator
        // root-motion path is prone to exactly this). A max plausible human/mobility-aid speed
        // (generous headroom above scooter's own ~3.5 m/s) rejects the teleport frame outright
        // (skip the EMA update, just resync lastPos) instead of smoothing garbage into the average.
        private const float MaxPlausibleSpeedMps = 6.0f;

        // Session 36 FIX 5 diagnostic: env-var gated, logs what Awake() actually found once per
        // agent so a real trial run can confirm/refute the "wrong nested Animator" hypothesis
        // without guessing from prefab YAML (containers reference their avatar via a nested
        // PrefabInstance, so the real Animator hierarchy isn't visible via static text grep).
        private static readonly bool DiagEnabled =
            !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("S36_ANIM_SCALER_DIAG"));

        void Awake()
        {
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            baseAgent = GetComponent<Scenario.Agents.Base>();
            if (DiagEnabled)
            {
                string path = animator == null ? "NULL" : GetPath(animator.transform);
                bool isHuman = animator != null && animator.avatar != null && animator.avatar.isHuman;
                Debug.Log("[S36AnimScalerDiag] host=" + gameObject.name
                    + " foundAnimatorPath=" + path + " isHuman=" + isHuman
                    + " baseAgentNull=" + (baseAgent == null));
            }
        }

        private static string GetPath(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        void Update()
        {
            if (animator == null) { return; }

            Vector3 pos = transform.position;
            float instantaneousSpeed;
            if (havePrevPos && Time.deltaTime > 1e-5f)
            {
                Vector3 delta = pos - lastPos;
                delta.y = 0f; // ground-plane displacement only -- vertical bob isn't walking speed
                instantaneousSpeed = delta.magnitude / Time.deltaTime;
            }
            else
            {
                instantaneousSpeed = 0f;
            }
            bool plausible = instantaneousSpeed <= MaxPlausibleSpeedMps;
            lastPos = pos;
            havePrevPos = true;

            if (plausible)
            {
                float alpha = 1f - Mathf.Exp(-Time.deltaTime / SmoothingTau);
                smoothedSpeed = Mathf.Lerp(smoothedSpeed, instantaneousSpeed, alpha);
            }
            // else: teleport/correction frame -- lastPos is resynced above so the NEXT frame's
            // delta is measured from the corrected position, but this frame's implausible reading
            // is discarded rather than folded into the average.

            float scale = referenceSpeedMps > 0.01f ? smoothedSpeed / referenceSpeedMps : 1.0f;
            scale = Mathf.Clamp(scale, minSpeedScale, maxSpeedScale);
            animator.speed = scale;
            if (DiagEnabled && Time.frameCount % 60 == 0)
            {
                float legacySpeed = baseAgent != null ? baseAgent.velocity.magnitude : -1f;
                Debug.Log("[S36AnimScalerDiag] host=" + gameObject.name
                    + " positionDeltaSpeed=" + smoothedSpeed + " legacyBaseVelocity=" + legacySpeed
                    + " appliedAnimatorSpeed=" + animator.speed);
            }
        }
    }
}
