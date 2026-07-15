using UnityEngine;

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// Independent MonoBehaviour that implements IVelocityModulator to give a spawned
    /// pedestrian a personality-driven reaction to the robot's position, plus an
    /// appearance-driven walk speed multiplier. Meant to be added (via AddComponent) to the
    /// same GameObject as an Agents.Base subclass (e.g. IVI.SFAgent) -- see Base.cs.
    ///
    /// Indifferent pedestrians should simply not have this component attached (Base.cs's
    /// ModulateVelocity() then no-ops via a null GetComponent<IVelocityModulator>() result),
    /// but the Indifferent branch below is also safe to use directly if a component is present.
    ///
    /// State machine design: PERSONALITY_BEHAVIOR_DESIGN_V2.md. All per-frame state advancement
    /// happens inside Modulate() (called synchronously from Base.Update(), see V2 §1.5) -- this
    /// component intentionally has no Unity Update() of its own, to avoid an unspecified
    /// execution-order dependency against Base.Update()/SFAgent.
    ///
    /// Surprised facing is the one exception and lives in OnAnimatorMove() instead (see below):
    /// the Surprised reaction clip's root rotation delta was fighting any rotation set from
    /// Update()/LateUpdate(), because with no OnAnimatorMove() implemented anywhere on this
    /// GameObject, Unity auto-applies root motion on its own schedule -- not reliably before or
    /// after any particular script's LateUpdate() (see SURPRISED_FACING_V2_DIAGNOSIS.md).
    /// Implementing OnAnimatorMove() here takes that scheduling question off the table
    /// entirely: Unity stops auto-applying root motion for this GameObject and calls this
    /// method instead, so this is the only place root motion gets applied at all.
    /// </summary>
    public class PedestrianModulator : MonoBehaviour, IVelocityModulator
    {
        public enum PersonalityType
        {
            Scared,
            Curious,
            Surprised,
            Indifferent,
            Assertive,
        }

        public PersonalityType personality = PersonalityType.Indifferent;

        // Cached for OnAnimatorMove() -- Base.cs's own animator field is private, so this
        // component GetComponent<Animator>()s the same GameObject once instead of every call.
        private Animator animator;

        // Cached for ModulateAssertive() -- resolves the sibling SFAgent once instead of
        // GetComponent<IVI.SFAgent>() every call. Modulator and SFAgent live on the same
        // GameObject (see PedestrianSpawner.SpawnAgent()).
        private IVI.SFAgent sfAgent;

        void Awake()
        {
            animator = GetComponent<Animator>();
            sfAgent = GetComponent<IVI.SFAgent>();
        }

        // Only Curious uses a three-phase state machine (V2 §2.2); Scared/Surprised/Indifferent
        // don't share this enum since their own "state" is simpler (see ModulateScared/
        // ModulateSurprised below).
        private enum CuriousState
        {
            Wander,
            Approach,
            Follow,
        }
        private CuriousState curiousState = CuriousState.Wander;

        // Curious: InitDest() retarget throttle (V2 §1.2 -- don't call InitDest() every frame).
        private float nextRetargetTime = 0f;

        // Curious/Follow: robot speed estimate via position delta, not Rigidbody/ArticulationBody
        // (V2 §1.3 -- works for both wheeled and legged robots).
        private Vector3 lastRobotPos;
        private bool hasLastRobotPos = false;

        // Surprised: cross-frame state for rising-edge detection + freeze + cooldown (V2 §2.5).
        private bool wasInSurpriseRadius = false;
        private float frozenUntil = -1f;
        private float cooldownUntil = -1f;

        // Assertive: guards the one-time robotRepulsion suppression so it's only pushed to
        // the sibling SFAgent once, not re-applied every Modulate() call.
        private bool assertiveInitialized = false;

        [Header("Scared")]
        public float scaredRadius = 3.0f;
        public float scaredStrength = 1.5f;
        public float scaredMaxSpeed = 1.2f;

        [Header("Curious")]
        public float detectRadius = 4.0f;
        public float detectExitMargin = 1.3f;
        public float followDist = 1.8f;
        public float followExitMargin = 1.3f;
        public float approachMaxSpeed = 1.0f;
        public float followBehindOffset = 1.2f;
        public float followSpeedMatchGain = 1.0f;
        public float retargetInterval = 0.3f;

        [Header("Surprised")]
        public float surpriseRadius = 4.0f;
        public float freezeDuration = 1.5f;
        public float cooldownDuration = 4.0f;
        // How fast (LookRotation Slerp factor per second) a frozen Surprised pedestrian turns
        // to face the robot in OnAnimatorMove() -- see class doc comment above.
        public float facingTurnSpeed = 10f;

        [Header("Assertive")]
        // Robot repulsion damping pushed onto the sibling SFAgent on first Modulate() call --
        // 0 = fully ignores the robot's repulsion force, higher = partially yields (see
        // SFAgent.RobotRepulsion / ModulateAssertive() below).
        public float assertiveRobotRepulsion = 0f;

        [Header("General")]
        // Appearance-driven walk speed scaling, shares this same modulation hook per
        // PEDESTRIAN_SPAWNER_DESIGN.md §2.4. Simple appearance uses 1.0 (no change).
        public float walkSpeedMultiplier = 1.0f;

        /// <summary>
        /// True while Curious is in Approach or Follow and is actively driving destPos via
        /// InitDest() -- lets PedestrianSpawner.Update() skip its random-walk retarget for this
        /// agent (V2 §2.6) so the two don't fight over destPos.
        /// </summary>
        public bool IsControllingDestination =>
            personality == PersonalityType.Curious &&
            (curiousState == CuriousState.Approach || curiousState == CuriousState.Follow);

        public Vector3 Modulate(Vector3 socialForceVelocity, Base self)
        {
            Scenario.Robot robot;
            try
            {
                if (SEAN.instance == null)
                {
                    return Scale(socialForceVelocity);
                }
                robot = SEAN.instance.robot;
            }
            catch (System.Exception)
            {
                // No (or more than one) active robot in the scene -- leave velocity untouched.
                return Scale(socialForceVelocity);
            }

            switch (personality)
            {
                case PersonalityType.Scared:
                    return ModulateScared(socialForceVelocity, self, robot);
                case PersonalityType.Curious:
                    return ModulateCurious(socialForceVelocity, self, robot);
                case PersonalityType.Surprised:
                    return ModulateSurprised(socialForceVelocity, self, robot);
                case PersonalityType.Assertive:
                    return ModulateAssertive(socialForceVelocity, self, robot);
                case PersonalityType.Indifferent:
                default:
                    return Scale(socialForceVelocity);
            }
        }

        // Kept to satisfy IVelocityModulator -- Surprised facing is now forced directly in
        // OnAnimatorMove() (see class doc comment), not read back through this hook by Base.Move().
        public bool TryGetFacingOverride(out Vector3 facingDirection)
        {
            facingDirection = Vector3.zero;
            return false;
        }

        // Tells Base.Move() to skip its own goalDir/RotateAround turning entirely while frozen
        // Surprised, so OnAnimatorMove()'s robot-facing Slerp above is the only thing writing
        // transform.rotation that frame (see SURPRISED_TURN_DIAGNOSIS.md).
        public bool IsRotationSuppressed()
        {
            return personality == PersonalityType.Surprised && Time.time < frozenUntil;
        }

        // Implementing this callback anywhere on this GameObject switches Unity's root motion
        // handling from "auto-applied on Unity's own schedule" to "only applied here" -- so the
        // else-branch below has to manually reproduce the default behavior (position + rotation
        // delta), or every other personality/state loses root-motion-driven movement entirely.
        void OnAnimatorMove()
        {
            if (animator == null) { return; }

            bool frozenSurprised = personality == PersonalityType.Surprised && Time.time < frozenUntil;

            if (!frozenSurprised)
            {
                transform.position += animator.deltaPosition;
                transform.rotation *= animator.deltaRotation;
                return;
            }

            // Frozen Surprised: discard the clip's own root motion entirely (both translation
            // and rotation) -- the pedestrian should stay put and only face the robot, not
            // wander per whatever the SurprisedReaction clip's root track happens to do.
            Scenario.Robot robot;
            try
            {
                if (SEAN.instance == null) { return; }
                robot = SEAN.instance.robot;
            }
            catch (System.Exception)
            {
                // No (or more than one) active robot in the scene -- nothing to face.
                return;
            }

            Vector3 toRobot = robot.position - transform.position;
            toRobot.y = 0;
            if (toRobot.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            // Vector3.up as the up-vector keeps this a pure yaw turn -- the pedestrian can
            // never get tipped onto its side/back no matter what the clip's root track does.
            Quaternion targetRot = Quaternion.LookRotation(toRobot.normalized, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * facingTurnSpeed);

            Debug.DrawLine(transform.position, robot.position, Color.red);
            Debug.DrawRay(transform.position, transform.forward * 2f, Color.blue);
        }

        private float DistanceToRobot(Base self, Scenario.Robot robot, out Vector3 toSelf)
        {
            Vector3 robotPos = robot.position;
            Vector3 selfPos = self.transform.position;
            toSelf = selfPos - robotPos;
            toSelf.y = 0;
            return toSelf.magnitude;
        }

        private Vector3 ModulateScared(Vector3 socialForceVelocity, Base self, Scenario.Robot robot)
        {
            Vector3 result = socialForceVelocity;
            float distanceToRobot = DistanceToRobot(self, robot, out Vector3 toSelf);

            if (distanceToRobot < scaredRadius)
            {
                Vector3 fleeDir = distanceToRobot > Mathf.Epsilon ? toSelf.normalized : Vector3.zero;
                float closeness = 1f - Mathf.Clamp01(distanceToRobot / scaredRadius);
                result += fleeDir * scaredStrength * closeness;
                if (result.magnitude > scaredMaxSpeed)
                {
                    result = result.normalized * scaredMaxSpeed;
                }
            }

            return Scale(result);
        }

        private Vector3 ModulateCurious(Vector3 socialForceVelocity, Base self, Scenario.Robot robot)
        {
            float distanceToRobot = DistanceToRobot(self, robot, out _);

            CuriousState previousState = curiousState;

            switch (curiousState)
            {
                case CuriousState.Wander:
                    if (distanceToRobot <= detectRadius)
                    {
                        curiousState = CuriousState.Approach;
                    }
                    break;

                case CuriousState.Approach:
                    if (distanceToRobot <= followDist)
                    {
                        curiousState = CuriousState.Follow;
                    }
                    else if (distanceToRobot > detectRadius * detectExitMargin)
                    {
                        curiousState = CuriousState.Wander;
                    }
                    break;

                case CuriousState.Follow:
                    if (distanceToRobot > detectRadius * detectExitMargin)
                    {
                        curiousState = CuriousState.Wander;
                    }
                    else if (distanceToRobot > followDist * followExitMargin)
                    {
                        curiousState = CuriousState.Approach;
                    }
                    break;
            }

            bool justEnteredApproach = previousState != CuriousState.Approach && curiousState == CuriousState.Approach;
            bool justEnteredFollow = previousState != CuriousState.Follow && curiousState == CuriousState.Follow;

            switch (curiousState)
            {
                case CuriousState.Wander:
                    // Don't touch destPos -- PedestrianSpawner.Update()'s random-walk loop
                    // handles this exactly like Indifferent (V2 §2.4).
                    return Scale(socialForceVelocity);

                case CuriousState.Approach:
                    {
                        if (justEnteredApproach || Time.time >= nextRetargetTime)
                        {
                            self.InitDest(robot.position);
                            nextRetargetTime = Time.time + retargetInterval;
                        }

                        Vector3 result = socialForceVelocity;
                        if (result.sqrMagnitude > 0.0001f)
                        {
                            result = result.normalized * approachMaxSpeed;
                        }
                        return Scale(result);
                    }

                case CuriousState.Follow:
                    {
                        if (justEnteredFollow)
                        {
                            lastRobotPos = robot.position;
                            hasLastRobotPos = true;
                        }

                        if (justEnteredFollow || Time.time >= nextRetargetTime)
                        {
                            self.InitDest(robot.position - robot.transform.forward * followBehindOffset);
                            nextRetargetTime = Time.time + retargetInterval;
                        }

                        Vector3 dir = socialForceVelocity.sqrMagnitude > 0.0001f
                            ? socialForceVelocity.normalized
                            : self.transform.forward;
                        float robotSpeed = EstimateRobotSpeed(robot);
                        Vector3 result = dir * Mathf.Max(robotSpeed * followSpeedMatchGain, 0.05f);
                        return Scale(result);
                    }
            }

            return Scale(socialForceVelocity);
        }

        private float EstimateRobotSpeed(Scenario.Robot robot)
        {
            if (!hasLastRobotPos)
            {
                lastRobotPos = robot.position;
                hasLastRobotPos = true;
                return 0f;
            }
            float speed = (robot.position - lastRobotPos).magnitude / Time.deltaTime;
            lastRobotPos = robot.position;
            return speed;
        }

        private Vector3 ModulateSurprised(Vector3 socialForceVelocity, Base self, Scenario.Robot robot)
        {
            float distanceToRobot = DistanceToRobot(self, robot, out _);
            bool inRadius = distanceToRobot <= surpriseRadius;
            float now = Time.time;

            // Rising-edge detection; don't re-trigger during cooldown. Cooldown is counted from
            // the trigger instant, not from when the freeze ends (per V2 §2.5 confirmed design).
            if (inRadius && !wasInSurpriseRadius && now >= cooldownUntil)
            {
                frozenUntil = now + freezeDuration;
                cooldownUntil = frozenUntil + cooldownDuration;
                self.TriggerAnimation("Surprised");
            }
            wasInSurpriseRadius = inRadius;

            if (now < frozenUntil)
            {
                return Vector3.zero;
            }
            return Scale(socialForceVelocity);
        }

        // Assertive holds its own route and doesn't yield to the robot: on first call, suppress
        // the sibling SFAgent's robotRepulsion damping (see SFAgent.CalculateAgentForce()) so the
        // robot-repulsion term stops being dampened for this agent -- the robot must plan around
        // it instead. No velocity-space reaction here; the suppression happens upstream in
        // SFAgent's own force computation, so the social-force velocity passes straight through.
        private Vector3 ModulateAssertive(Vector3 socialForceVelocity, Base self, Scenario.Robot robot)
        {
            if (!assertiveInitialized)
            {
                if (sfAgent != null)
                {
                    sfAgent.RobotRepulsion = assertiveRobotRepulsion;
                }
                assertiveInitialized = true;
            }

            return Scale(socialForceVelocity);
        }

        private Vector3 Scale(Vector3 v)
        {
            return v * walkSpeedMultiplier;
        }
    }
}
