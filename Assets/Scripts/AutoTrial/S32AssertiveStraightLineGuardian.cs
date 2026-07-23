using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 32 FIX B: assertive personality should hold its course ABSOLUTELY -- "the robot
    /// must be the one to yield." Diagnosis this session found that ModulateAssertive()'s
    /// existing sfAgent.RobotRepulsion=0 suppression (PedestrianModulator.cs, untouched, outside
    /// writable scope) fully zeroes the robot-specific agent-force term ONLY while the robot's own
    /// rigidbody speed stays above 0.1 m/s (SFAgent.CalculateAgentForce()'s own dampenFactor
    /// ternary reverts to 1.0, i.e. full repulsion, whenever the robot dips below that threshold).
    /// Measured empirically this session: assertive's pre-FIX-B lateral spread (~1.72m
    /// post-release) was actually LARGER than indifferent's (~1.55m) -- confirming assertive does
    /// NOT hold a straight line today, not just a theoretical risk. This component sidesteps the
    /// social-force system entirely once released: it drives transform.position along a fixed
    /// straight line from release position to release destination, every LateUpdate, AFTER
    /// Base.cs's own Update()/LateUpdate() ([DefaultExecutionOrder] guarantees this ordering).
    ///
    /// SAFETY BACKSTOP -- IMPORTANT, READ BEFORE TUNING: a pure open-loop straight line (no
    /// proximity awareness at all) measured min_dist as low as 0.299-0.333m -- BELOW the 0.36m
    /// physical floor -- and this PERSISTED even under the historically-safe, compiled-in-default
    /// TEB settings (0.3/0.5/weight_obstacle=50, i.e. NOT a FIX-A-tuning problem at all). Root
    /// cause, confirmed directly from a real trial's frames.csv: the ROBOT's own path grazed a
    /// PEDESTRIAN THAT WAS ALREADY FROZEN/STATIONARY (mid-emergency-pause) down to 0.303m --
    /// i.e. TEB's own planning/replanning imprecision against a static obstacle, at settings that
    /// have worked for 30+ prior sessions' worth of COMPLIANT pedestrians, can still shave
    /// clearance this thin when the pedestrian contributes zero avoidance of its own. A
    /// forward-progress freeze alone (the first attempt) cannot fix this -- freezing only stops
    /// the pedestrian from making things worse, it does nothing about the robot's own trajectory
    /// already carrying it close to a fixed point. Zero-collision is absolute, so this component
    /// now has a genuine, if minimal, LATERAL EVASION as a last resort: if distance to the robot
    /// drops below EmergencyLateralStepDistanceMeters (stricter/closer than
    /// EmergencyStopDistanceMeters, which still governs forward-progress pausing), the pedestrian
    /// steps sideways (perpendicular to its own direction of travel, away from the robot) at
    /// EmergencyLateralStepSpeedMps, on top of whatever forward progress state it's in. This is a
    /// deliberate, necessary compromise on the literal "never yields" ideal -- documented here
    /// plainly rather than silently: the video will show assertive holding an exactly straight
    /// line for the overwhelming majority of any encounter, with at most a small, brief sideways
    /// correction only in the rare case the robot's own planner imprecision would otherwise bring
    /// it inside the collision floor. Absolute safety wins over literal designed behavior; see
    /// REPORT.md Session 32 FIX B for the full progression of measurements that led here (the
    /// pure freeze was tried first, at three different stop distances, and never fixed this).
    ///
    /// Inactive (a pure no-op) until Activate() is called -- wired at the exact SLATE v2 release
    /// moment in TrialController.PollForTrigger(), mirroring S21PedestrianPositionGuardian's own
    /// "spawn, SLATE release" convention, so the pre-release frozen-facing behavior is completely
    /// unaffected.
    /// </summary>
    [DefaultExecutionOrder(9999)]
    public class S32AssertiveStraightLineGuardian : MonoBehaviour
    {
        // Matches the established, session-verified business_male_01 reference walking speed
        // (Session 30R/31: ~1.29-1.30 m/s measured on-disk).
        public float speedMps = 1.3f;

        // Forward-progress pause threshold (first-line defense, keeps the pedestrian from closing
        // distance further once the robot is already near).
        public float emergencyStopDistanceMeters = 1.5f;

        // Absolute last-resort lateral evasion -- deliberately smaller/closer than
        // emergencyStopDistanceMeters so it almost never engages; only fires when forward-pausing
        // alone isn't enough because the ROBOT's own path is what's closing the gap.
        public float emergencyLateralStepDistanceMeters = 0.8f;
        public float emergencyLateralStepSpeedMps = 0.8f;

        // Session 33 FIX 2: user observed the pedestrian sliding forward WHILE the point_backwards
        // gesture plays -- root cause was two independent components (this one's forward-progress
        // freeze at 1.5m, and the separate S31AssertiveGestureTrigger's own proximity check at a
        // looser 5.0m) with no shared state, so the gesture fired mid-walk long before any stop.
        // Retired S31AssertiveGestureTrigger for Assertive (no longer attached, see
        // AutoTrialBootstrap.cs) and folded gesture-firing into THIS component's own state machine,
        // gated explicitly on having actually reached a stop (traveled frozen this frame due to
        // proximity), not on robot distance alone -- an explicit walk -> stop -> gesture -> resume
        // sequence rather than two independently-triggered behaviors that could overlap.
        // gestureHoldSeconds matches point_backwards.fbx's own clip length (3.60s, S31 FIX 6(b)).
        public float gestureHoldSeconds = 3.6f;
        public float gestureCooldownSeconds = 6.0f;

        private enum State { Walking, Gesturing }
        private State state = State.Walking;
        private float gestureEndsAt = -1f;
        private float cooldownUntil = -1f;

        private Animator animator;
        private Vector3 startPos;
        private Vector3 destPos;
        private float traveled;
        private float lateralOffset;
        private bool active;

        void Awake()
        {
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
        }

        public void Activate(Vector3 start, Vector3 dest)
        {
            startPos = start;
            destPos = dest;
            traveled = 0f;
            lateralOffset = 0f;
            active = true;
            state = State.Walking;
            gestureEndsAt = -1f;
            cooldownUntil = -1f;
        }

        void LateUpdate()
        {
            if (!active) { return; }
            Vector3 delta = destPos - startPos;
            float totalDist = delta.magnitude;
            if (totalDist < 1e-4f) { return; }
            Vector3 unit = delta / totalDist;
            Vector3 perp = Vector3.Cross(Vector3.up, unit).normalized;

            float now = Time.time;

            // Gesturing: hold position entirely (no forward advance, no lateral evasion either --
            // the lateral evasion backstop below still runs regardless of state since zero-collision
            // is absolute and must never be suppressed by an animation, but forward walking pauses).
            if (state == State.Gesturing && now >= gestureEndsAt)
            {
                state = State.Walking;
            }

            float proposedTraveled = traveled;
            if (state == State.Walking)
            {
                proposedTraveled = Mathf.Min(traveled + speedMps * Time.deltaTime, totalDist);
            }
            Vector3 proposedForwardPos = startPos + unit * proposedTraveled + perp * lateralOffset;

            Vector3? robotPos = TryGetRobotPosition(proposedForwardPos.y);

            bool safeToAdvanceForward = state == State.Walking;
            bool stoppedByProximityThisFrame = false;
            if (robotPos.HasValue && Vector3.Distance(proposedForwardPos, robotPos.Value) < emergencyStopDistanceMeters)
            {
                safeToAdvanceForward = false;
                stoppedByProximityThisFrame = true;
            }
            if (!robotPos.HasValue)
            {
                // Can't confirm safety -- fail safe, don't advance.
                safeToAdvanceForward = false;
            }
            if (safeToAdvanceForward)
            {
                traveled = proposedTraveled;
            }

            // Fire the gesture exactly once per approach, on the frame proximity FIRST forces a
            // stop (not every frame while stopped) -- rising-edge, same shape as the retired
            // S31AssertiveGestureTrigger but now gated on an actual stop, not just raw distance.
            if (state == State.Walking && stoppedByProximityThisFrame && now >= cooldownUntil
                && animator != null)
            {
                animator.SetTrigger("AssertiveGesture");
                state = State.Gesturing;
                gestureEndsAt = now + gestureHoldSeconds;
                cooldownUntil = gestureEndsAt + gestureCooldownSeconds;
            }

            Vector3 currentLinePos = startPos + unit * traveled;
            Vector3 currentPos = currentLinePos + perp * lateralOffset;

            // Absolute last-resort lateral evasion: only engages if the robot is closer than the
            // (stricter) lateral-step threshold even at the CURRENT position -- i.e. forward
            // pausing alone wasn't enough because the robot's own path is closing the gap. Runs
            // regardless of Walking/Gesturing state -- zero-collision safety is never suppressed
            // by an animation being mid-playback.
            if (robotPos.HasValue)
            {
                float distNow = Vector3.Distance(currentPos, robotPos.Value);
                if (distNow < emergencyLateralStepDistanceMeters)
                {
                    Vector3 awayFromRobot = currentPos - robotPos.Value;
                    awayFromRobot.y = 0f;
                    float sign = Vector3.Dot(awayFromRobot.normalized, perp) >= 0f ? 1f : -1f;
                    lateralOffset += sign * emergencyLateralStepSpeedMps * Time.deltaTime;
                }
            }

            transform.position = startPos + unit * traveled + perp * lateralOffset;
            if (traveled < totalDist)
            {
                transform.rotation = Quaternion.LookRotation(unit, Vector3.up);
            }
        }

        private Vector3? TryGetRobotPosition(float yMatch)
        {
            if (SEAN.instance == null) { return null; }
            try
            {
                Vector3 p = SEAN.instance.robot.position;
                p.y = yMatch;
                return p;
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
