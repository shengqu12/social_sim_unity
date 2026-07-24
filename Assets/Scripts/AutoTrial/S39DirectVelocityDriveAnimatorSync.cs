using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 39 FIX: restores the walk cycle for DirectVelocityDrive appearances (currently
    /// `business_male_01` x Indifferent, wired by Session 38 FIX 3 -- see AutoTrialBootstrap.cs).
    ///
    /// Root cause, CONFIRMED via a live runtime probe (S39LocomotionStateProbe, this session --
    /// not assumed): Base.cs's motion block is an if(directVelocityDrive){...}else{...} split. The
    /// `else` branch is the ONLY place that ever calls `animator.SetFloat("Forward", ...)` /
    /// `SetFloat("Strafe", ...)` / `SetBool("Idling", ...)` -- the parameters that actually drive
    /// this rig's locomotion blend tree. Setting DirectVelocityDrive=true (Session 38's own fix
    /// for the ~0.28/~2.3 m/s speed bugs) skips that branch entirely, so `Forward` stays frozen at
    /// its initial 0 for the whole trial regardless of real movement speed -- confirmed on a live
    /// trial this session: `posDeltaSpeed` reached ~0.95 m/s while `Forward` logged exactly 0 at
    /// every one-second sample. `S32AnimatorSpeedScaler`'s own `animator.speed` scaling (which DOES
    /// still run for this appearance) only changes the PLAYBACK RATE of whatever state/blend is
    /// already showing -- with Forward pinned at 0, that's the idle end of the blend regardless of
    /// how fast animator.speed says to play it. Hence "idle while sliding forward."
    ///
    /// Fix (brief's own option (b), since option (a) -- reverting DirectVelocityDrive -- would
    /// reopen the two already-diagnosed-and-rejected speed bugs Session 37/38 explicitly moved
    /// away from): keep DirectVelocityDrive=true (it correctly produces MAX_VEL-clamped,
    /// SFAgent-computed velocity with neither broken root-motion path in the loop) but explicitly
    /// replicate Base.cs's own else-branch Forward/Strafe/Idling computation from OUTSIDE it,
    /// using only `Base.velocity` (a pre-existing public property, `Base.cs:29`) -- no Base.cs
    /// edit needed. Constants (`ANIMATION_SMOOTHING=0.6f`, `animationScale=1.0f` -- confirmed via
    /// grep this session to never be overridden anywhere in Base.cs, `idleSpeed=0.5f`) are `protected`/
    /// `private` in Base.cs so can't be referenced directly; their values are copied here as
    /// literals with this comment as the paper trail, not guessed.
    ///
    /// General mechanism, not Indifferent-specific: only actually does anything when
    /// `Base.DirectVelocityDrive` is true AND the else-branch isn't already running for this agent
    /// (i.e. it's harmless/inert to attach broadly, though currently only wired for the one
    /// appearance that needs it).
    /// </summary>
    public class S39DirectVelocityDriveAnimatorSync : MonoBehaviour
    {
        private const float AnimationSmoothing = 0.6f; // Base.cs ANIMATION_SMOOTHING, copied (protected const)
        private const float AnimationScale = 1.0f;      // Base.cs animationScale, copied (private, never overridden)
        private const float IdleSpeedThreshold = 0.5f;  // Base.cs idleSpeed, copied (private, never overridden)

        private Animator animator;
        private Scenario.Agents.Base baseAgent;

        void Awake()
        {
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            baseAgent = GetComponent<Scenario.Agents.Base>();
        }

        void Update()
        {
            if (animator == null || baseAgent == null || !baseAgent.DirectVelocityDrive) { return; }

            // Second finding, also confirmed via live probe (not assumed): Base.cs's Start()
            // unconditionally sets `animator.applyRootMotion = true` regardless of
            // DirectVelocityDrive. While Forward stayed at 0 this was invisible (an idle/zero
            // blend produces ~zero root motion), but once Forward is restored below, Unity's own
            // automatic root-motion application STACKS with DirectVelocityDrive's own
            // `transform.position += velocity * Time.deltaTime` -- measured on a live trial:
            // posDeltaSpeed jumped to 2-3 m/s (vs. the correct ~0.85-0.95 m/s) the moment Forward
            // went nonzero. Disabling applyRootMotion here decouples the two: code drives 100% of
            // translation (already correct per Session 38), the Animator drives visuals only.
            if (animator.applyRootMotion) { animator.applyRootMotion = false; }

            Vector3 local = Quaternion.Euler(0f, -transform.eulerAngles.y, 0f) * baseAgent.velocity;
            local *= AnimationScale;
            bool idle = local.magnitude < IdleSpeedThreshold;

            animator.SetBool("Idling", idle);
            animator.SetFloat("Forward", local.z / AnimationSmoothing);
            animator.SetFloat("Strafe", local.x / AnimationSmoothing);
        }
    }
}
