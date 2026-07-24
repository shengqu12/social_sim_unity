using System.Collections.Generic;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 38 FIX 1: generalizes Session 32's pedestrian-side "absolute last-resort lateral
    /// evasion" (see S32AssertiveStraightLineGuardian's own class doc) to the ROBOT, for every
    /// config, always active. Session 34 already measured the robot's own clearance against a
    /// non-yielding/near-stationary obstacle at ~0.333m (a `--ped-motion standing` isolation
    /// trial) -- BELOW the 0.36m physical floor -- independent of anything a pedestrian does.
    /// Session 37's N=5 census confirmed this isn't a one-off: white_cane_user (slow, near-
    /// stationary to the planner) and even plain `indifferent` both breach the floor some
    /// fraction of the time. The pedestrian-side reaction gate (S34PedestrianReactDistGate,
    /// Session 34/37) fixes the case where the PEDESTRIAN can react; it cannot fix the case
    /// where the ROBOT's own path/replanning imprecision closes the gap regardless.
    ///
    /// Mechanism: the robot's actual navigation (ROS move_base/TEB, applied to this transform by
    /// VelocityController.cs -- forbidden to edit, confirmed via Update()/FixedUpdate() there,
    /// never LateUpdate()) cannot be intercepted from writable scope. This component instead runs
    /// in LateUpdate() (after VelocityController's own Update()/FixedUpdate() have already moved
    /// the robot for the frame) and, exactly mirroring S32AssertiveStraightLineGuardian's own
    /// "distance < threshold -> step sideways, perpendicular to direction of travel" logic, nudges
    /// SEAN.instance.robot's OWN transform.position sideways (away from whichever tracked
    /// pedestrian is closest) whenever that distance drops below reactThresholdMeters. This is the
    /// same deliberate, documented compromise Session 32 already accepted for the pedestrian side:
    /// a small, rare, safety-only correction, not a replacement for the robot's own navigation.
    /// Desyncing the robot's rendered Unity transform from ROS's own belief about robot pose
    /// (costmap/odometry) is a real, accepted risk here, same as it was there -- kept minimal
    /// (small step, only engages this close) to limit how far the two can drift apart within the
    /// brief window this ever fires.
    ///
    /// Always active from the moment pedestrians are registered (no explicit Activate() gate like
    /// S32's own component) -- pedestrians are frozen pre-SLATE-release regardless, so this simply
    /// never has anything close enough to react to until the trial's own encounter phase begins.
    /// Tracks an arbitrary list of pedestrian transforms so it generalizes to dyad/ped-count-3
    /// (checks against ALL registered pedestrians, evades away from whichever is closest) as well
    /// as the single-pedestrian case.
    /// </summary>
    [DefaultExecutionOrder(9999)]
    public class S38RobotLateralEvasionBackstop : MonoBehaviour
    {
        // Iteration 2 (same session): the first landed values (0.5m / 0.8 m/s) measured a real
        // N=5 STALEMATE against white_cane_user specifically -- frames.csv showed distance sitting
        // flat at ~0.36m for 1.5+ continuous seconds before dipping to 0.31m, i.e. the lateral
        // push and the robot's own TEB path-return were roughly canceling rather than the
        // evasion winning cleanly. Widened the threshold (more lead time to build separation
        // before the robot commits to its closest-pass geometry) and the step speed (a stronger
        // push per frame) so the evasion can outrun the path-return instead of merely offsetting
        // it. Re-verify N=5 against white_cane specifically before trusting this value further.
        public float reactThresholdMeters = 0.9f;
        public float stepSpeedMps = 1.2f;

        private readonly List<Transform> pedestrians = new List<Transform>();

        public void RegisterPedestrian(Transform pedestrian)
        {
            if (pedestrian != null && !pedestrians.Contains(pedestrian))
            {
                pedestrians.Add(pedestrian);
            }
        }

        void LateUpdate()
        {
            if (pedestrians.Count == 0) { return; }

            Vector3 robotPos = transform.position;
            Transform closest = null;
            float closestDist = float.MaxValue;
            for (int i = 0; i < pedestrians.Count; i++)
            {
                Transform p = pedestrians[i];
                if (p == null) { continue; }
                Vector3 delta = p.position - robotPos;
                delta.y = 0f;
                float d = delta.magnitude;
                if (d < closestDist)
                {
                    closestDist = d;
                    closest = p;
                }
            }
            if (closest == null || closestDist >= reactThresholdMeters) { return; }

            // Perpendicular to the robot's own current heading (transform.forward), signed away
            // from the closest pedestrian -- same "step sideways, not backward/forward" discipline
            // as the pedestrian-side backstop, so this doesn't fight the robot's own forward
            // navigation intent, just adds clearance laterally.
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) { return; }
            Vector3 perp = Vector3.Cross(Vector3.up, forward.normalized);

            Vector3 awayFromPed = robotPos - closest.position;
            awayFromPed.y = 0f;
            float sign = Vector3.Dot(awayFromPed.normalized, perp) >= 0f ? 1f : -1f;

            transform.position += perp * (sign * stepSpeedMps * Time.deltaTime);
        }
    }
}
