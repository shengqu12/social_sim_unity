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
    /// behavior -- reads Base.velocity, a pre-existing PUBLIC property, so this needs no
    /// PedestrianModulator.cs/Base.cs edit). animator.speed = currentSpeed / ReferenceSpeedMps,
    /// clamped to [MinSpeedScale, MaxSpeedScale] so an extreme multiplier (e.g. scooter's ~3.5
    /// m/s) doesn't produce an absurd, blurring playback rate -- clamped rather than unclamped
    /// because this project's appearances don't all share one "natural" authored clip speed
    /// (scooter/cyclist likely use their own riding animations, not the walk-cycle this reference
    /// speed was chosen against); FIX E's own verification target is white_cane_user and
    /// wheelchair_user specifically, where the reference speed is directly applicable.
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

        void Awake()
        {
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            baseAgent = GetComponent<Scenario.Agents.Base>();
        }

        void Update()
        {
            if (animator == null || baseAgent == null) { return; }
            float currentSpeed = baseAgent.velocity.magnitude;
            float scale = referenceSpeedMps > 0.01f ? currentSpeed / referenceSpeedMps : 1.0f;
            scale = Mathf.Clamp(scale, minSpeedScale, maxSpeedScale);
            animator.speed = scale;
        }
    }
}
