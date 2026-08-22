using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 79, GATE 3. Drive the locomotion blend tree to idle while the pedestrian's body is
    /// being held stationary by an external position owner, so it stands still instead of cycling
    /// a walk clip on the spot.
    ///
    /// THE DEFECT, PRECISELY. S78 filed this as "a Kimodo single-state controller can never go
    /// idle". That attribution is only half right, and S79 measured the other half: a stock
    /// business_male_01 x assertive trial with NO gait applied shows the same treadmill
    /// (clip=HumanoidWalk while ground speed is 0.04-0.41 m/s). The real chain is
    ///
    ///   Base.Move() (Update)            Forward = localVelocity.z / ANIMATION_SMOOTHING
    ///   PedestrianModulator             ModulateAssertive() pins |velocity| to its full pace,
    ///                                   permanently -- it never reports "I am blocked"
    ///   S32AssertiveStraightLineGuardian (LateUpdate) freezes the BODY by not advancing its own
    ///                                   `traveled` scalar, and writes transform.position itself
    ///
    /// so the command stays at full pace while the transform does not move, and the blend tree
    /// keeps selecting the forward node. Restoring the parameters (S79GaitOverrideBuilder) is
    /// necessary but not sufficient: it makes the idle node REACHABLE, and this makes it reached.
    ///
    /// WHY THIS CANNOT DEADLOCK, AND WHY IT IS SCOPED THE WAY IT IS. Zeroing Forward off measured
    /// motion is a closed loop -- idle clip, no root motion, still stationary, Forward stays zero,
    /// the agent never restarts. That is a real hazard for a root-motion-driven agent, and it is
    /// exactly why this component refuses to run unless an S32AssertiveStraightLineGuardian is
    /// present: that component writes transform.position directly (its line 189), so the body is
    /// moved by the guardian and NOT by root motion. Recovery is therefore driven by the guardian
    /// advancing again, which is independent of anything written here -- the loop is open.
    ///
    /// Every other stopping case in this project already zeroes the COMMAND and so needs nothing:
    /// the Surprised freeze returns Vector3.zero from ModulateSurprised, S68CuriousCrouch's Stop
    /// state drives its velocity override to zero, and goal arrival goes through
    /// Base.StopNavigation() -> StopAnimator(). All three drop Forward on their own; measured in
    /// S79 (surprised: cmd 0.0000 through the freeze) and S78 (S68: cmd 0.9274 -> 0 at Stop).
    /// </summary>
    [DefaultExecutionOrder(560)]   // after Base.Update()'s Move() (order 0) writes Forward
    public class S79StalledGaitIdler : MonoBehaviour
    {
        /// <summary>Below this realised ground speed the body counts as not moving.</summary>
        public float stallSpeedMps = 0.12f;
        /// <summary>Commanded speed must exceed this for a stall to be a CONTRADICTION rather than
        /// simply a stopped agent (a genuinely stopped agent already has Forward at 0).</summary>
        public float commandedFloorMps = 0.20f;
        /// <summary>How long the contradiction must hold before idling. Long enough to ignore the
        /// per-frame jitter of a normal footfall, short enough to catch the stop on camera.</summary>
        public float dwellSeconds = 0.30f;

        private Animator animator;
        private Scenario.Agents.Base baseAgent;
        private S32AssertiveStraightLineGuardian guardian;
        private Vector3 lastPos;
        private bool havePrev;
        private float smoothed;
        private float stalledSince = -1f;
        private bool idling;
        private bool resolved;

        private const float SmoothingTau = 0.20f;

        private void Resolve()
        {
            resolved = true;
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            baseAgent = GetComponent<Scenario.Agents.Base>();
            guardian = GetComponent<S32AssertiveStraightLineGuardian>();
            if (animator == null || baseAgent == null || guardian == null)
            {
                // Not an externally-position-owned agent. Disabling rather than running a
                // no-op keeps the deadlock argument above true by construction.
                enabled = false;
            }
        }

        private void Update()
        {
            if (!resolved) { Resolve(); if (!enabled) { return; } }

            Vector3 p = transform.position; p.y = 0f;
            if (!havePrev) { lastPos = p; havePrev = true; return; }
            float dt = Time.deltaTime;
            if (dt <= 1e-5f) { return; }
            float instant = (p - lastPos).magnitude / dt;
            lastPos = p;
            float alpha = 1f - Mathf.Exp(-dt / SmoothingTau);
            smoothed = Mathf.Lerp(smoothed, instant, alpha);

            Vector3 v = baseAgent.velocity; v.y = 0f;
            bool contradiction = v.magnitude > commandedFloorMps && smoothed < stallSpeedMps;

            if (contradiction)
            {
                if (stalledSince < 0f) { stalledSince = Time.time; }
                if (!idling && Time.time - stalledSince >= dwellSeconds)
                {
                    idling = true;
                    Debug.Log(string.Format("[S79Idle] ENTER t={0:F3} commanded={1:F3} realised={2:F3}"
                        + " -- body held by the assertive guardian; driving Forward to 0.",
                        Time.time, v.magnitude, smoothed));
                }
            }
            else
            {
                if (idling)
                {
                    Debug.Log(string.Format("[S79Idle] EXIT  t={0:F3} commanded={1:F3} realised={2:F3}",
                        Time.time, v.magnitude, smoothed));
                }
                idling = false;
                stalledSince = -1f;
            }

            if (idling)
            {
                // Overwrite what Base.Move() wrote this frame. Turn is zeroed too so the tree
                // lands on the idle node rather than a stand-turn.
                animator.SetFloat("Forward", 0f);
                animator.SetFloat("Turn", 0f);
            }
        }
    }
}
