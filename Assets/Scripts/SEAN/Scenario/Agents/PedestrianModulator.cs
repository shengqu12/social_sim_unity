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
    /// Surprised facing is the one exception and lives in ApplyAnimatorRootMotion() instead
    /// (see below): the Surprised reaction clip's root rotation delta was fighting any rotation
    /// set from Update()/LateUpdate(), because with no OnAnimatorMove() implemented anywhere on
    /// this GameObject, Unity auto-applies root motion on its own schedule -- not reliably
    /// before or after any particular script's LateUpdate() (see SURPRISED_FACING_V2_DIAGNOSIS.md).
    /// Implementing OnAnimatorMove() here takes that scheduling question off the table
    /// entirely: Unity stops auto-applying root motion for this GameObject and calls this
    /// method instead, so this is the only place root motion gets applied at all.
    ///
    /// That only works when the Animator is on this same GameObject, which is where Unity
    /// dispatches OnAnimatorMove(). Character packages that put the Animator on a nested child
    /// instead (e.g. White_Cane_User) never trigger this callback at all -- for those,
    /// Base.LateUpdate() calls ApplyAnimatorRootMotion() directly once it detects the mismatch
    /// (see Base.cs's animatorOnRoot/RootMotionSink), reusing this exact same logic instead of
    /// duplicating it.
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
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
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

        /// <summary>
        /// Session 59. While true, ApplyAnimatorRootMotion() discards the clip's translation and
        /// keeps its rotation, so the character animates in place. Set by AutoTrialBootstrap at the
        /// SLATE frozen spawn and cleared by TrialController at the release instant, mirroring the
        /// InitDest(spawnPos) / InitDest(releaseDest) pair it exists to complete.
        ///
        /// Defaults to FALSE on purpose. Defaulting to frozen would silently immobilise any
        /// modulator-bearing agent that nobody releases -- ambient pedestrians spawned by
        /// PedestrianSpawner never go through TrialController's release path.
        /// </summary>
        [System.NonSerialized] public bool rootMotionTranslationFrozen = false;

        // Patrol: ping-pongs destPos between two fixed points. Orthogonal to personality --
        // not a PersonalityType case, so e.g. Surprised can react AND patrol (see
        // EnablePatrol() and the arrival check at the top of Modulate()).
        private bool patrolEnabled = false;
        private Vector3 patrolA;
        private Vector3 patrolB;
        private int patrolTarget = 0;

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

        // Session 47 (defect A, solution (e)): the absolute pace an unmodulated pedestrian walks at.
        // walkSpeedMultiplier is now a multiplier ON THIS, not on the incoming velocity -- see
        // Scale().
        //
        // Session 54: 1.3 -> 1.0476. The old value came from "Rocketbox walks at ~1.3 m/s", a
        // figure derived from the retracted 3.2 slide invariant and never valid. Its effect was
        // that Zone A's jitter, designed as N(1.05, 0.17) to land near 1.10 m/s, was multiplied by
        // 1.3 and actually commanded mean 1.365 / stdev 0.221. With the loop now open, commanded
        // speed IS realised ground speed, so this constant sets the dataset's walking pace
        // directly: 1.0476 * 1.05 = 1.100 m/s mean, 1.0476 * 0.17 = 0.178 stdev, which is the
        // S46-D target (1.05-1.15 mean, ~0.18 stdev).
        //
        // MUST stay in step with tools/run_trial.py's BASE_PED_SPEED_MPS -- that file divides a
        // target m/s by it to produce walkSpeedMultiplier, and this file multiplies it back. A
        // mismatch silently rescales every Mixamo and Zone B pace.
        public float baseWalkSpeedMps = 1.0476f;

        // Below this, the incoming velocity carries no usable direction. See SetSpeed().
        private const float MinDirectionSqrMagnitude = 1e-8f;

        /// <summary>
        /// True while patrolling, or while Curious is in Approach or Follow and is actively
        /// driving destPos via InitDest() -- lets PedestrianSpawner.Update() skip its
        /// random-walk retarget for this agent (V2 §2.6) so the two don't fight over destPos.
        /// </summary>
        /// <summary>
        /// Session 68. Optional external driver for this agent's velocity, installed at runtime by
        /// an AutoTrial component (S68CuriousCrouch). Null on every agent that does not have one,
        /// which is all of them by default.
        ///
        /// It is consulted at the top of ModulateCurious() and may decline, in which case the
        /// personality's own code below runs unchanged. That is the point of the hook: the existing
        /// Curious Wander/Approach/Follow machine is not modified or removed, only bypassed while
        /// something else is driving (S68 §4).
        /// </summary>
        [System.NonSerialized] public AutoTrial.IPedestrianVelocityOverride velocityOverride;

        public bool IsControllingDestination =>
            patrolEnabled ||
            (velocityOverride != null && velocityOverride.IsControllingDestination) ||
            (personality == PersonalityType.Curious &&
            (curiousState == CuriousState.Approach || curiousState == CuriousState.Follow));

        /// <summary>
        /// Turns on patrol ping-pong between two fixed points. Orthogonal to personality --
        /// safe to call regardless of PersonalityType (see Modulate()'s arrival check).
        /// </summary>
        public void EnablePatrol(Vector3 a, Vector3 b)
        {
            patrolEnabled = true;
            patrolA = a;
            patrolB = b;
            patrolTarget = 0;
        }

        public Vector3 Modulate(Vector3 socialForceVelocity, Base self)
        {
            // Patrol arrival check runs ahead of the personality switch below, and ahead of
            // the robot lookup, since patrol doesn't need the robot and must keep ping-ponging
            // regardless of personality (Surprised's freeze zeroes velocity but never clears
            // destPos, so it just resumes toward the current patrol point once unfrozen; same
            // for Scared -- its flee force perturbs the path but the patrol dest re-anchors it).
            // Skipped while Curious is actively driving destPos itself (Approach/Follow) so the
            // two never fight over InitDest() in the same frame.
            bool curiousControllingDest = (velocityOverride != null && velocityOverride.IsControllingDestination)
                || (personality == PersonalityType.Curious &&
                (curiousState == CuriousState.Approach || curiousState == CuriousState.Follow));
            if (patrolEnabled && !curiousControllingDest && self.CloseEnough())
            {
                patrolTarget = 1 - patrolTarget;
                self.InitDest(patrolTarget == 0 ? patrolA : patrolB);
            }

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

        // True while the Animator is in (or crossfading into) SurprisedReaction -- covers the
        // gap between frozenUntil expiring (freezeDuration=1.5s) and the clip actually finishing
        // (SurprisedReaction runs 4.0s, exit transition doesn't start until 3.6s in), so rotation
        // suppression doesn't release mid-reaction (see SURPRISED_ROOTMOTION_DIAGNOSIS.md).
        // Checks both current and next state: during the 0.25s entry crossfade, SurprisedReaction
        // is only the "next" state, not yet "current".
        bool SurpriseAnimationActive()
        {
            if (animator == null) { return false; }
            var cur = animator.GetCurrentAnimatorStateInfo(0);
            var next = animator.GetNextAnimatorStateInfo(0);
            return cur.IsName("SurprisedReaction") || next.IsName("SurprisedReaction");
        }

        // Tells Base.Move() to skip its own goalDir/RotateAround turning entirely while frozen
        // Surprised, so OnAnimatorMove()'s robot-facing Slerp above is the only thing writing
        // transform.rotation that frame (see SURPRISED_TURN_DIAGNOSIS.md). Extended past
        // frozenUntil for as long as SurpriseAnimationActive() -- otherwise this flips false
        // mid-clip and Move()'s own steering resumes turning the pedestrian away from the robot.
        public bool IsRotationSuppressed()
        {
            return personality == PersonalityType.Surprised && (Time.time < frozenUntil || SurpriseAnimationActive());
        }

        // Implementing this callback anywhere on this GameObject switches Unity's root motion
        // handling from "auto-applied on Unity's own schedule" to "only applied here" -- so the
        // else-branch below has to manually reproduce the default behavior (position + rotation
        // delta), or every other personality/state loses root-motion-driven movement entirely.
        // Only fires when the Animator is on this same GameObject (see class doc comment) --
        // the nested-Animator case calls ApplyAnimatorRootMotion() directly from
        // Base.LateUpdate() instead, since Unity never dispatches this callback there.
        void OnAnimatorMove()
        {
            ApplyAnimatorRootMotion();
        }

        // Extracted from OnAnimatorMove() so Base.LateUpdate() can invoke the exact same logic
        // explicitly when the resolved Animator lives on a nested child GameObject and Unity's
        // OnAnimatorMove() dispatch therefore never reaches this component (see class doc
        // comment and Base.cs's animatorOnRoot/RootMotionSink).
        public void ApplyAnimatorRootMotion()
        {
            if (animator == null) { return; }

            // Session 59: the SLATE frozen spawn holds the pedestrian in place by pinning its
            // destination (InitDest(spawnPos)), which gates Base.Move() -- and Base.Move() is not
            // what translates a root-motion agent. This method is. So a Mixamo pedestrian, whose
            // generated single-state controller has no Forward/Idling to fall to zero, simply kept
            // walking through the freeze. Measured drift before capture even starts: Old_Man_Walk
            // 2.87 m, Drunk_Walk 3.12 m, carry_and_walk 4.79 m, Pacing_Phone 7.73 m. dist0 -- the
            // controlled variable of the whole encounter geometry -- consequently ranged 3.98 to
            // 8.0 m across configurations, so two configurations were not comparable.
            //
            // Translation is discarded, the clip keeps playing: a frozen character should still
            // look alive, and holding animator.speed at 0 would freeze it into a single pose.
            //
            // NOT implemented by toggling animator.applyRootMotion, which was the obvious route and
            // is unsafe: S44ClipProps (Session 46) deliberately holds applyRootMotion false for
            // in-place clips and re-asserts it for five frames, S44ClipProps again for the
            // Standing_Arguing partner, and S39DirectVelocityDriveAnimatorSync for
            // directVelocityDrive agents. Restoring it to true on release would revert Session 46's
            // verified fix, and capturing-then-restoring races that five-frame re-assert. Gating
            // the application instead touches none of them.
            if (rootMotionTranslationFrozen)
            {
                // Apply NEITHER position nor rotation. An earlier version of this branch applied
                // rotation only, reasoning that a frozen agent might still need to turn toward its
                // release heading. That was speculation and it was wrong: Standing_Arguing drifted
                // 7.419 m after release, against 0.000 m on the same configuration before this
                // change, and the drift was entirely post-release. Session 46 had already recorded
                // the mechanism -- its 7.779 m drift "appeared only once a component started
                // forcing transform.rotation every LateUpdate, which conflicts with Unity's
                // root-motion auto-apply writing position and rotation in the same frame". Applying
                // one without the other is exactly that pathological pair, and the animator's root
                // state does not recover when translation resumes.
                return;
            }

            // Same extended condition as IsRotationSuppressed() -- see that method's comment.
            bool frozenSurprised = personality == PersonalityType.Surprised && (Time.time < frozenUntil || SurpriseAnimationActive());

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
                // Session 47 (e): flee has its own ABSOLUTE target, applied here rather than by a
                // second multiplication in Scale(). The direction is what the flee force shapes;
                // the magnitude is pinned, so the escape heading is preserved while the speed
                // cannot compound.
                return SetSpeed(result, scaredMaxSpeed * walkSpeedMultiplier);
            }

            return Scale(result);
        }

        private Vector3 ModulateCurious(Vector3 socialForceVelocity, Base self, Scenario.Robot robot)
        {
            // Session 68. An installed override answers first. Everything below is left exactly as
            // it was and is still reachable -- the override declines whenever it is not driving, and
            // there is no override at all unless an AutoTrial component installed one.
            if (velocityOverride != null)
            {
                Vector3 overridden;
                if (velocityOverride.TryModulate(socialForceVelocity, self, robot, out overridden))
                {
                    return overridden;
                }
            }

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
                        // Session 47 (e): absolute target, not a further multiplication.
                        return SetSpeed(result, approachMaxSpeed * walkSpeedMultiplier);
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
                        // Session 47 (e): matching the robot's pace is already an absolute target.
                        return SetSpeed(dir, Mathf.Max(robotSpeed * followSpeedMatchGain, 0.05f)
                                              * walkSpeedMultiplier);
                    }
            }

            return Scale(socialForceVelocity);
        }

        // Session 48 (1.1): read the robot's physics body, not its position delta.
        //
        // This one matters more than the identically-shaped bug in the trigger-speed gate, because
        // it is not instrumentation -- Curious/Follow feeds this straight into the pedestrian's
        // TARGET SPEED. transform.position advances as a discrete event unrelated to the frame
        // rate, so the old expression read ~0 on frames the robot's transform had not moved and
        // spiked when it jumped; combined with the Mathf.Max(..., 0.05f) floor at the call site, a
        // Curious follower would alternate between crawling and lurching. That is consistent with
        // `curious` appearing on the "speeds up / erratic" list.
        //
        // Resolved the same way TrialController.ResolveRobotBody() does, and cached: the robot's
        // body does not change during a trial. Falls back to the old estimate only if neither body
        // exists, and says so once rather than silently returning a bad number.
        private Rigidbody robotRb;
        private ArticulationBody robotArt;
        private bool robotBodyResolved;
        private bool robotBodyWarned;

        private void ResolveRobotBody(Scenario.Robot robot)
        {
            robotBodyResolved = true;
            GameObject baseLink = robot != null ? robot.base_link : null;
            if (baseLink == null) { return; }
            foreach (ArticulationBody b in baseLink.GetComponentsInChildren<ArticulationBody>())
            {
                if (b.isRoot) { robotArt = b; return; }
            }
            robotRb = baseLink.GetComponent<Rigidbody>();
        }

        private float EstimateRobotSpeed(Scenario.Robot robot)
        {
            if (!robotBodyResolved) { ResolveRobotBody(robot); }

            if (robotArt != null)
            {
                Vector3 v = robotArt.velocity; v.y = 0f; return v.magnitude;
            }
            if (robotRb != null)
            {
                Vector3 v = robotRb.velocity; v.y = 0f; return v.magnitude;
            }

            if (!robotBodyWarned)
            {
                robotBodyWarned = true;
                Debug.LogWarning("[PedestrianModulator] no robot physics body resolved -- Curious "
                    + "follow speed falls back to position differencing, which is NOT a speed.");
            }
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

                // Snap facing to the robot immediately: a ~180° approach-from-behind turn
                // cannot complete within freezeDuration via OnAnimatorMove()'s Slerp alone,
                // so the pedestrian would otherwise appear to face the wrong way mid-turn.
                Vector3 snapDir = robot.position - self.transform.position;
                snapDir.y = 0f;
                if (snapDir.sqrMagnitude > 1e-6f)
                    self.transform.rotation = Quaternion.LookRotation(snapDir.normalized, Vector3.up);

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

        /// <summary>
        /// Session 47, defect A, solution (e): return an ABSOLUTE target speed, never a multiple of
        /// the input.
        ///
        /// The old body was `v * walkSpeedMultiplier`, and that compounds. Base.cs:122 writes this
        /// method's result back into `Base.velocity`, and SFAgent.cs:71 integrates the next frame
        /// FROM that field (`velocity + accel * dt`). So a multiplicative modulation is re-applied
        /// to its own previous output every frame: v_n = (v_{n-1} + a*dt) * k, i.e. geometric in k.
        /// k &gt; 1 runs away until Parameters.MAX_VEL clamps it; k &lt; 1 collapses (0.96^60 ~ 0.09),
        /// which is the "stands still, then darts" behaviour reported for white_cane and
        /// zoneA_seed2.
        ///
        /// Both halves of that loop are in red-line files (Base.cs, SFAgent.cs), so the fix has to
        /// live here. Returning an absolute magnitude makes this function IDEMPOTENT: f(f(v)) =
        /// f(v), because the output's magnitude no longer depends on the input's. However polluted
        /// the base it integrated from, the result is the target speed.
        ///
        /// Direction still comes from `v`, so steering, avoidance and flee headings are untouched --
        /// only the magnitude is pinned. This also directly implements the requested behaviour:
        /// constant-speed pedestrians with no acceleration ramp, which removes a confound from the
        /// encounter geometry.
        ///
        /// NOTE this is a workaround for an upstream defect, not a repair of it. Any other
        /// IVelocityModulator in this project or elsewhere is still exposed. Remove once
        /// SFAgent/Base integrate from an unmodulated base.
        /// </summary>
        private Vector3 Scale(Vector3 v)
        {
            return SetSpeed(v, baseWalkSpeedMps * walkSpeedMultiplier);
        }

        /// <summary>
        /// Pin |v| to targetSpeed, preserving direction.
        ///
        /// The near-zero guard is load-bearing rather than defensive: Modulate() is called every
        /// frame including while the agent is frozen at spawn and after it reaches its goal, so
        /// v ~ 0 is the common case, not an edge case, and Vector3.normalized on it is undefined.
        /// Returning zero there keeps a stopped agent stopped instead of launching it in whatever
        /// direction floating-point noise happened to point.
        /// </summary>
        private Vector3 SetSpeed(Vector3 v, float targetSpeed)
        {
            if (targetSpeed <= 0f || v.sqrMagnitude < MinDirectionSqrMagnitude)
            {
                return Vector3.zero;
            }
            return v.normalized * targetSpeed;
        }
    }
}
