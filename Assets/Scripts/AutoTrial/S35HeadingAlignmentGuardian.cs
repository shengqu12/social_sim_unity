using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 35 FIX 1/2: root-causes and fixes "pedestrians never actually walk face-to-face
    /// at the robot -- already angled/turning from the start" (user's own diagnosis, confirmed
    /// this session via frames.csv: heading-vs-bearing angle stayed a real, nonzero ~5-60+ degrees
    /// through the whole approach under --profile scoring, not noise).
    ///
    /// Root cause, confirmed by reading Base.cs (forbidden to edit, fine to read): movement is
    /// root-motion-driven (`transform.position += animator.deltaPosition`), and Base.Move()
    /// decomposes the social-force `velocity` into LOCAL-FRAME "Forward"/"Strafe" animator
    /// parameters using the pedestrian's CURRENT facing (`animParams = Quaternion.Euler(0,
    /// -transform.eulerAngles.y, 0) * velocity`). Base.cs already tries to rotate facing toward
    /// a blended goal direction (`goalDir = 0.5*(nearestGoalPoint-position).normalized +
    /// 0.5*velocity.normalized`, capped at ANGULAR_SPEED=120 deg/s) -- but this blend is a
    /// feedback loop (facing depends partly on velocity direction, but the ACTUAL displacement
    /// direction, via root motion, itself depends on facing), and empirically takes several
    /// seconds/meters to converge (confirmed via a long-range --profile arc control trial: same
    /// transient exists, converges to within ~10-15 degrees of the true bearing by t=8-16s /
    /// ~10-15m of travel). --profile scoring's much shorter spawn/trigger distance (8m, vs arc's
    /// 25m) never gives this convergence enough runway -- the ENTIRE visible encounter happens
    /// mid-transient, which is exactly what reads as "already angled from the start."
    ///
    /// Fix: a LateUpdate transform-rotation override (same established pattern as
    /// `S21PedestrianPositionGuardian`/`S32AssertiveStraightLineGuardian` -- a script-driven fix
    /// from outside Base.cs/PedestrianModulator.cs, not an edit to either) that forces
    /// `transform.eulerAngles.y` to the STATIC bearing from spawn toward the pedestrian's own
    /// destination (a fixed target, precomputed once at wiring time from AutoTrialConfig --
    /// NOT derived from live `Base.velocity` every frame). An earlier version of this fix tried
    /// snapping to live velocity direction instead and measured WORSE alignment (mean angle
    /// ~63 degrees, up from the unfixed baseline) plus a large min_dist regression (1.1m -> 3.1m)
    /// in direct before/after testing -- velocity is itself downstream of the same Forward/
    /// Strafe root-motion feedback loop this fix is trying to correct, so snapping to it
    /// creates a circular dependency instead of breaking one. A fixed destination bearing has
    /// no such feedback risk (it never changes), which is why this version uses it instead.
    ///
    /// Explicitly NOT applied to Assertive (owns its own straight-line transform override,
    /// `S32AssertiveStraightLineGuardian` -- this component would fight it) or Surprised (frozen
    /// this session per explicit user instruction; Surprised already has its own facing-override
    /// system via IVelocityModulator.TryGetFacingOverride()/IsRotationSuppressed(), untouched).
    /// Self-contained skip via the `personality` field rather than conditional wiring, so it's
    /// safe to attach unconditionally at bootstrap alongside S32AnimatorSpeedScaler.
    ///
    /// SECOND MECHANISM, added after direct measurement showed the facing-only fix above does
    /// NOT help every appearance: `wheelchair_user` measured a ~55-65 degree heading-vs-bearing
    /// offset even WITH the facing fix active (Session 35 FIX 1/2 diagnosis). Root cause: `Base.
    /// Move()`'s `directVelocityDrive` branch (`transform.position += velocity * Time.deltaTime`,
    /// used for avatars whose Animator has "no usable root motion... e.g. a wheelchair's looping
    /// seated-idle has zero deltaPosition every frame", per Base.cs's own comment) drives
    /// displacement DIRECTLY from `velocity` in world space -- completely independent of
    /// `transform.eulerAngles.y`, so a facing correction is a pure no-op for these appearances.
    /// Since `velocity` itself is computed inside the social-force system (outside writable
    /// scope) and can carry a genuine lateral component from the start, the only script-driven
    /// fix available from outside is a POSITION correction: each LateUpdate, project the actual
    /// position onto the straight spawn->destination line (zeroing perpendicular/lateral offset,
    /// preserving whatever along-line progress the character's own velocity produced this frame).
    /// This complements rather than replaces the facing fix -- root-motion-driven appearances get
    /// (mostly) fixed by facing alone; directVelocityDrive appearances need this too. Applying it
    /// unconditionally to both kinds of appearance is harmless: for a root-motion character
    /// already walking near-straight (post facing-fix), the lateral offset to correct is already
    /// small, so this just tightens further rather than fighting the facing fix.
    /// </summary>
    public class S35HeadingAlignmentGuardian : MonoBehaviour
    {
        public Scenario.Agents.PedestrianModulator.PersonalityType personality =
            Scenario.Agents.PedestrianModulator.PersonalityType.Indifferent;

        // Precomputed once at wiring time (SpawnPedestrian) from spawnPos -> destPos. Degrees,
        // this project's own bearing convention (yaw=0 faces +z, increases toward +x).
        public float targetHeadingDeg;
        public bool hasTargetHeading = false;

        // Same spawnPos/destPos, kept as raw points (not just the derived angle) for the position
        // correction below.
        public Vector3 lineStart;
        public Vector3 lineEnd;
        public bool hasLine = false;

        // Below this distance-to-robot, this guardian backs off entirely (no facing correction,
        // no position correction) so the pedestrian's own natural social-force avoidance and any
        // personality-specific reaction have full, unfought control. NOT the same as Session 34's
        // 1.5m react-distance gate -- an earlier version of this fix used a flat 1.5m here and
        // measured a real safety regression on wheelchair_user (min_dist collapsed from ~2.2-2.4m
        // to 0.532m): forcing a straight line even between 1.5-2m was suppressing SFAgent's own
        // baseline collision-avoidance (starts reacting within its PERCEPTION_RADIUS=2m,
        // independent of any personality-specific gate) -- the same "removes ALL avoidance, not
        // just the personality-specific kind" failure mode Session 32 first hit with assertive's
        // own straight-line guardian. A flat 4.0m fixed that for wheelchair (~0.89 m/s) -- but
        // scooter_user (~3.5 m/s) still measured 0.308m, BELOW the 0.36m physical floor, at that
        // same flat 4.0m: a fast-closing actor covers 4m in ~1.1s, not enough real time for its
        // own avoidance to actually create separation once let loose. Fix: scale the backoff
        // distance by the pedestrian's OWN current speed (target ~3s of unfought avoidance
        // runway, floor of 4.0m for slow actors so wheelchair's already-verified-safe distance
        // never shrinks) -- computed live from `baseAgent.velocity`, not a per-appearance
        // constant, so it generalizes without needing this C# file to duplicate run_trial.py's
        // own speed table.
        public float minBackOffMeters = 4.0f;
        public float backOffReactionSeconds = 3.0f;

        private Scenario.Agents.Base baseAgent;

        void Awake()
        {
            baseAgent = GetComponent<Scenario.Agents.Base>();
        }

        // Same established pattern as S32AssertiveStraightLineGuardian's own TryGetRobotPosition
        // -- SEAN.instance.robot, not a fresh FindObjectOfType every frame.
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

        void LateUpdate()
        {
            if (baseAgent == null || !hasTargetHeading) { return; }
            if (personality == Scenario.Agents.PedestrianModulator.PersonalityType.Assertive) { return; }
            if (personality == Scenario.Agents.PedestrianModulator.PersonalityType.Surprised) { return; }

            Vector3? robotPos = TryGetRobotPosition(transform.position.y);
            if (robotPos.HasValue)
            {
                float dist = Vector3.Distance(
                    new Vector3(transform.position.x, 0, transform.position.z),
                    new Vector3(robotPos.Value.x, 0, robotPos.Value.z));
                float speedNow = baseAgent.velocity.magnitude;
                float dynamicBackOff = Mathf.Max(minBackOffMeters, speedNow * backOffReactionSeconds);
                if (dist < dynamicBackOff) { return; }
            }

            Vector3 e = transform.eulerAngles;
            transform.eulerAngles = new Vector3(e.x, targetHeadingDeg, e.z);

            // Second mechanism: zero lateral/perpendicular offset from the straight line, for
            // appearances (e.g. wheelchair_user) whose displacement is directVelocityDrive and so
            // doesn't respond to the facing correction above at all. See class doc.
            if (hasLine)
            {
                Vector3 lineDelta = lineEnd - lineStart;
                lineDelta.y = 0f;
                float lineLen = lineDelta.magnitude;
                if (lineLen > 1e-3f)
                {
                    Vector3 unit = lineDelta / lineLen;
                    Vector3 fromStart = transform.position - lineStart;
                    fromStart.y = 0f;
                    float alongDist = Vector3.Dot(fromStart, unit);
                    Vector3 corrected = lineStart + unit * alongDist;
                    Vector3 p = transform.position;
                    transform.position = new Vector3(corrected.x, p.y, corrected.z);
                }
            }
        }
    }
}
