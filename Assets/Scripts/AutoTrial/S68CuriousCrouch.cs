using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 68. Curious, redesigned: approach the robot, step out of its way, kneel to watch it
    /// from a distance, then stand up and leave as it bears down.
    ///
    /// S68-D changed what the hold waits FOR. It used to wait for the robot to go past; it now waits
    /// for the robot to get close, and gets out of the way. "Robot has passed" survives only as a
    /// fallback for a robot that detours so widely it never comes within standUpDistance.
    ///
    ///   APPROACH      walk at the robot (absolute destination, the existing Curious mechanism)
    ///                 -> dist &lt;= stopDistance
    ///   SIDESTEP      walk clear of the robot's path line, normal pace
    ///                 -> lateral clearance &gt;= lateralClearance (or sidestepTimeout)
    ///   STOP          velocity target 0, standing, for pauseBeforeCrouch seconds
    ///   CROUCH_ENTER  crouch clip seeked 1 -> 0 (stand -> kneel); velocity 0; -> clip length elapsed
    ///   CROUCH_HOLD   pinned on the kneel pose; velocity 0
    ///                 -> the robot is closing and within standUpDistance (primary, S68-D),
    ///                    or it has passed (fallback), or holdTimeout expires
    ///   CROUCH_EXIT   crouch clip seeked 0 -> 1 (kneel -> stand); velocity 0; -> clip length elapsed
    ///   LEAVE         walk to the original release destination, normal SFM pace
    ///
    /// It reaches PedestrianModulator through IPedestrianVelocityOverride rather than living inside
    /// it, so the existing Curious Wander/Approach/Follow code is untouched and merely not entered
    /// (§4). Removing this component restores the old behaviour exactly.
    ///
    /// OFF unless AUTOTRIAL_S68_CROUCH is set. dataset_planD has already shipped with the old
    /// Curious, and an exploratory redesign must not silently change what a re-run of it produces.
    ///
    /// Mounting order matters and is not incidental: this component resolves the modulator and the
    /// Animator in Awake(), so AutoTrialBootstrap attaches it AFTER PedestrianModulator (the S61
    /// freeze-gate lesson -- a GetComponent dependency added before the thing it depends on
    /// silently resolves null and the gate never engages).
    /// </summary>
    [DefaultExecutionOrder(600)]
    public class S68CuriousCrouch : MonoBehaviour, IPedestrianVelocityOverride
    {
        public enum State
        {
            /// <summary>Pre-release. Exists so the freeze gate has somewhere to hold (§2.4).</summary>
            Frozen,
            Approach,
            /// <summary>S68-B §3: walk clear of the robot's path before stopping to crouch.</summary>
            Sidestep,
            Stop,
            CrouchEnter,
            CrouchHold,
            CrouchExit,
            Leave,
        }

        // ---- tunables, all defaulted to the ticket's numbers ----

        /// <summary>
        /// Distance at which the pedestrian stops approaching and prepares to crouch.
        ///
        /// History, because the number is empirical: 3.0 left the pedestrian finishing its crouch
        /// with the robot 1.16 m away ("crouches too late", S68-A); 5.0 fixed that; S68-D doubles it
        /// again to 10.0 so the watching starts from a distance.
        ///
        /// Overridable at runtime via AUTOTRIAL_S68_STOP_DIST for eyeball retuning without a
        /// recompile.
        /// </summary>
        public float stopDistance = 10.0f;
        public const string StopDistanceEnv = "AUTOTRIAL_S68_STOP_DIST";

        /// <summary>
        /// S68-D. The robot is "about to arrive" at or inside this range, and the pedestrian stands
        /// up and leaves rather than waiting for it to go by.
        ///
        /// This is the knob that sets how long the watch lasts, together with stopDistance. At the
        /// ~0.71 m/s closing rate measured in run7/run8 the pair (10.0, 4.0) yields a hold of only
        /// about 4 s -- an emergent consequence of the two distances, not a target. Raise
        /// stopDistance or lower this to watch for longer.
        /// </summary>
        public float standUpDistance = 4.0f;
        public const string StandUpDistanceEnv = "AUTOTRIAL_S68_STANDUP_DIST";

        /// <summary>
        /// S68-B §3. Required perpendicular distance from the robot's path line before the
        /// pedestrian will stop and crouch.
        ///
        /// Measured motivation, from the S68-A run4: crouching on the robot's centre line left the
        /// robot unable to get past, and it drove into the kneeling pedestrian and pushed it 1.510 m
        /// while the separation sat pinned at 0.33-0.34 m for ten seconds. Standing aside to watch
        /// something go by is also simply what a curious bystander does.
        /// </summary>
        public float lateralClearance = 1.2f;
        /// <summary>Overshoot, because the robot advances while the step is being taken and the
        /// clearance is measured against a line that moves with it.</summary>
        public float sidestepMargin = 0.5f;
        /// <summary>§1.2's rule again: a state that waits on a geometric condition needs a timeout.
        /// The pedestrian may be boxed in by a wall and unable to reach the target clearance.</summary>
        public float sidestepTimeout = 8.0f;
        /// <summary>A sidestep destination must be farther than Parameters.CLOSE_ENOUGH_MIN_DIST
        /// (1.0 m) or the navigation layer treats it as already reached and never moves.</summary>
        private const float MinNavReach = 1.8f;
        public float pauseBeforeCrouch = 1.0f;
        public float passDistance = 4.0f;
        /// <summary>How long the robot must be continuously receding before "it has passed" is
        /// believed. Anti-chatter -- see RobotHasPassed.</summary>
        public float recedeHysteresis = 1.0f;
        public float holdTimeout = 15.0f;
        /// <summary>§1.2's rule generalised: EVERY state that waits on the robot needs a timeout,
        /// because the robot is not guaranteed to ever do the thing. scooter_user's robot stalls
        /// permanently; a robot that never arrives would strand APPROACH the same way a robot that
        /// never leaves would strand CROUCH_HOLD.</summary>
        public float approachTimeout = 60.0f;

        public string crouchControllerResource = "S68_CuriousCrouch";
        /// <summary>The single crouch state. Its own speed is 0 -- this component sets
        /// normalizedTime explicitly every frame, so playback direction and rate are code, not an
        /// Animator property. See SeekCrouch.</summary>
        public const string StatePose = "S68CrouchPose";

        /// <summary>
        /// Which end of the clip is the KNEEL.
        ///
        /// True for "Kneeling Down" (S68-C), which runs stand -> kneel: the descent is its forward
        /// half and the stand-up is the reversed one. False for the "Crouch To Stand" family, which
        /// runs the other way. Measured, not assumed -- the smoke runner's grounded depth scan
        /// reports where the clip actually bottoms out (Kneeling Down: standing ~1.87 m through
        /// u~0.40, descending to ~1.50 m by u~0.55, and kneeling through to 1.558 m at u=1.00).
        ///
        /// Everything downstream is expressed as a lerp between UStand and UKneel, so swapping clip
        /// families is this one flag rather than three edited call sites.
        /// </summary>
        public bool kneelAtClipEnd = true;
        private float UStand { get { return kneelAtClipEnd ? 0f : 1f; } }
        private float UKneel { get { return kneelAtClipEnd ? 1f : 0f; } }

        /// <summary>Where to walk once the robot has gone -- the destination the trial released the
        /// pedestrian toward. Passed in by AutoTrialBootstrap rather than read back off the agent:
        /// INavigable.destPos is protected and lives in IVI, which is a red line.</summary>
        public Vector3 leaveDestination;
        public bool hasLeaveDestination;

        private State state = State.Frozen;
        private float stateEnteredAt;
        private Scenario.Agents.PedestrianModulator modulator;
        private Animator animator;
        private RuntimeAnimatorController originalController;
        private RuntimeAnimatorController crouchController;
        private float crouchClipLength = 0f;
        private bool leaveDestSent;

        // Robot physics body, resolved once (§1.1: never position-difference the robot -- that path
        // has been fixed here before and must not be reopened). Same resolution
        // PedestrianModulator.ResolveRobotBody and TrialController.ResolveRobotBody use.
        private Rigidbody robotRb;
        private ArticulationBody robotArt;
        private bool robotBodyResolved;
        private bool robotBodyWarned;

        // Continuous-recession accumulator for RobotHasPassed.
        private float recedingSince = -1f;

        /// <summary>Read a float override from the environment, or keep the default. Logged either
        /// way when it fires, so a run's actual parameters are recoverable from its own log.</summary>
        private static float EnvOverride(string envName, float current, string label)
        {
            string raw = System.Environment.GetEnvironmentVariable(envName);
            float parsed;
            if (!string.IsNullOrEmpty(raw)
                && float.TryParse(raw, System.Globalization.NumberStyles.Float,
                                  System.Globalization.CultureInfo.InvariantCulture, out parsed)
                && parsed > 0f)
            {
                Debug.Log("[S68Curious] " + label + " " + current.ToString("F2") + " -> "
                    + parsed.ToString("F2") + " m (" + envName + ")");
                return parsed;
            }
            return current;
        }

        public static bool Enabled
        {
            get { return !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S68_CROUCH")); }
        }

        void Awake()
        {
            modulator = GetComponent<Scenario.Agents.PedestrianModulator>();
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (modulator == null)
            {
                Debug.LogError("[S68Curious] no PedestrianModulator on '" + name
                    + "' -- this component must be attached after it. Disabling.");
                enabled = false;
                return;
            }
            crouchController = Resources.Load<RuntimeAnimatorController>(crouchControllerResource);
            if (crouchController == null)
            {
                Debug.LogError("[S68Curious] Resources.Load failed for '" + crouchControllerResource
                    + "' -- run S68CrouchImport.Apply first. Disabling.");
                enabled = false;
                return;
            }
            stopDistance = EnvOverride(StopDistanceEnv, stopDistance, "stopDistance");
            standUpDistance = EnvOverride(StandUpDistanceEnv, standUpDistance, "standUpDistance");

            modulator.velocityOverride = this;
            stateEnteredAt = Time.time;
            Debug.Log(string.Format("[S68Curious] params stopDistance={0:F2} pauseBeforeCrouch={1:F2} "
                + "standUpDistance={2:F2} passDistance={3:F2} holdTimeout={4:F1} "
                + "lateralClearance={5:F2} controller={6}",
                stopDistance, pauseBeforeCrouch, standUpDistance, passDistance, holdTimeout,
                lateralClearance, crouchController.name));
        }

        /// <summary>
        /// True for every state except the pre-release hold -- including the four in which the
        /// pedestrian is deliberately STANDING STILL.
        ///
        /// The stationary states were originally excluded, on the reasoning that an agent that is
        /// not moving is not "controlling" a destination. That was wrong, and the demo measured it:
        /// at t=40.51, mid-CROUCH_HOLD, the pedestrian jumped 0.186 m and snapped 9 deg of yaw in a
        /// single frame, with base_vel 0.000 and the animator still parked on the kneel -- so
        /// neither the social-force velocity nor the animation produced it. Declaring no interest in
        /// the destination is what leaves the agent eligible to be retargeted by something else
        /// while it is supposed to be holding a pose.
        /// </summary>
        public bool IsControllingDestination
        {
            get { return state != State.Frozen; }
        }

        public bool TryModulate(Vector3 socialForceVelocity, Scenario.Agents.Base self,
                                Scenario.Robot robot, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (!enabled || modulator == null || self == null || robot == null) { return false; }

            float dist = GroundDistanceToRobot(self, robot);

            // §2.4. rootMotionTranslationFrozen IS the existing FROZEN/RELEASED signal -- set by
            // AutoTrialBootstrap at the SLATE frozen spawn, cleared by TrialController at the release
            // instant. No transition may fire before it clears. Without this the pedestrian can
            // already be inside stopDistance at spawn and would kneel down during the freeze, before
            // capture has even started.
            //
            // Answering TRUE with a zero velocity rather than declining: declining would hand the
            // frame back to the legacy Curious path, whose Approach branch calls InitDest(robot
            // .position) and would unpin the destination the freeze is built on.
            if (modulator.rootMotionTranslationFrozen)
            {
                if (state != State.Frozen) { Transition(State.Frozen, dist, "re-frozen"); }
                return true;
            }

            if (state == State.Frozen)
            {
                LatchRoute(robot);
                Transition(State.Approach, dist, "SLATE released");
            }

            float inState = Time.time - stateEnteredAt;

            switch (state)
            {
                case State.Approach:
                    if (dist <= stopDistance)
                    {
                        float clr0 = LateralClearance(self, robot);
                        if (clr0 >= lateralClearance)
                        {
                            // Already clear of the robot's path -- no reason to shuffle sideways.
                            PinAndStop(self, dist, string.Format(
                                "reached stopDistance, already clear ({0:F2} m)", clr0));
                            velocity = Vector3.zero;
                            return true;
                        }
                        Transition(State.Sidestep, dist, string.Format(
                            "reached stopDistance, lateral {0:F2} m < {1:F2} -- stepping aside",
                            clr0, lateralClearance));
                        velocity = Vector3.zero;
                        return true;
                    }
                    if (inState >= approachTimeout)
                    {
                        Transition(State.Leave, dist, "TIMEOUT approach " + approachTimeout.ToString("F1") + "s");
                        return LeaveVelocity(socialForceVelocity, self, out velocity);
                    }
                    // The existing Curious Approach mechanism, unchanged in kind: an absolute
                    // destination retargeted on the robot, with the SFM steering to it.
                    RetargetTo(self, robot.position);
                    velocity = Absolute(socialForceVelocity, modulator.approachMaxSpeed * modulator.walkSpeedMultiplier);
                    return true;

                case State.Sidestep:
                    {
                        float clr = LateralClearance(self, robot);
                        if (clr >= lateralClearance)
                        {
                            PinAndStop(self, dist, string.Format("clear of robot path ({0:F2} m)", clr));
                            velocity = Vector3.zero;
                            return true;
                        }
                        if (inState >= sidestepTimeout)
                        {
                            // Crouch anyway rather than stand in the road indefinitely. Reported as
                            // a timeout so a demo that failed to get clear is never mistaken for one
                            // that did.
                            PinAndStop(self, dist, string.Format(
                                "TIMEOUT sidestep {0:F1}s at {1:F2} m (target {2:F2})",
                                sidestepTimeout, clr, lateralClearance));
                            velocity = Vector3.zero;
                            return true;
                        }
                        Vector3 stepTarget = SidestepTarget(self, robot);
                        RetargetTo(self, stepTarget);
                        // Direction normally comes from the social force, but it has no opinion when
                        // it believes the agent has arrived -- and a zero direction becomes a zero
                        // velocity. Fall back to steering straight at the step target so the state
                        // can always make progress. Magnitude is still an absolute target (e).
                        Vector3 stepDir = socialForceVelocity;
                        stepDir.y = 0f;
                        if (stepDir.sqrMagnitude < 1e-6f)
                        {
                            stepDir = stepTarget - self.transform.position;
                            stepDir.y = 0f;
                        }
                        velocity = Absolute(stepDir,
                            modulator.baseWalkSpeedMps * modulator.walkSpeedMultiplier);
                        return true;
                    }

                case State.Stop:
                    if (inState >= pauseBeforeCrouch)
                    {
                        BeginCrouch(dist);
                    }
                    velocity = Vector3.zero;
                    return true;

                case State.CrouchEnter:
                    // Descend: standing end -> kneel end, driven off elapsed time.
                    SeekCrouch(Mathf.Lerp(UStand, UKneel, Progress(inState)));
                    if (inState >= ClipLength())
                    {
                        SeekCrouch(UKneel);
                        Transition(State.CrouchHold, dist, "crouch-in complete");
                    }
                    velocity = Vector3.zero;
                    return true;

                case State.CrouchHold:
                    // Re-pinned every frame rather than set once: nothing else may drift it.
                    SeekCrouch(UKneel);
                    // S68-D: the PRIMARY exit is now the robot getting close, not the robot having
                    // gone. The behaviour it expresses changed with it -- watch from a distance,
                    // then get up and clear out as the thing bears down -- so "robot passed" drops
                    // to a fallback for the case where the robot detours so widely it never comes
                    // within standUpDistance at all.
                    if (RobotIsApproaching(self, robot, dist))
                    {
                        Transition(State.CrouchExit, dist, string.Format(
                            "robot approaching (<= {0:F1} m and closing)", standUpDistance));
                    }
                    else if (RobotHasPassed(self, robot, dist))
                    {
                        Transition(State.CrouchExit, dist, "robot passed");
                    }
                    else if (inState >= holdTimeout)
                    {
                        // §1.2. This firing is not a failure of the run -- it is the release valve,
                        // and whether it fires is one of the things the demo exists to show.
                        Transition(State.CrouchExit, dist, "TIMEOUT hold " + holdTimeout.ToString("F1") + "s");
                    }
                    velocity = Vector3.zero;
                    return true;

                case State.CrouchExit:
                    // Rise: kneel end -> standing end.
                    SeekCrouch(Mathf.Lerp(UKneel, UStand, Progress(inState)));
                    if (inState >= ClipLength())
                    {
                        SeekCrouch(UStand);
                        RestoreController();
                        Transition(State.Leave, dist, "stand-up complete");
                        return LeaveVelocity(socialForceVelocity, self, out velocity);
                    }
                    velocity = Vector3.zero;
                    return true;

                case State.Leave:
                    return LeaveVelocity(socialForceVelocity, self, out velocity);
            }

            return false;
        }

        // ---- transitions ----

        private void BeginCrouch(float dist)
        {
            if (animator == null)
            {
                // Nothing to animate. Skip the two animation states rather than sit in them until
                // their backstop timeouts expire -- and say so, because a silent skip would read on
                // the video as "the crouch just did not happen".
                Debug.LogWarning("[S68Curious] no Animator resolved -- skipping the crouch animation "
                    + "states entirely. The velocity state machine still runs.");
                Transition(State.CrouchHold, dist, "no animator");
                return;
            }

            originalController = animator.runtimeAnimatorController;
            animator.runtimeAnimatorController = crouchController;
            // S68-A §1.2.4: read the length off the clip that is actually loaded, every time. The
            // enter/exit completion test is an elapsed-time comparison against this number, so a
            // stale constant would cut a longer clip short or sit past the end of a shorter one --
            // and the clip was just replaced. Logged into the transition line so the delivered trace
            // shows which length the run actually used.
            crouchClipLength = ResolveClipLength();
            if (crouchClipLength <= 0f)
            {
                Debug.LogWarning("[S68Curious] could not resolve the crouch clip length from '"
                    + crouchController.name + "' -- falling back to 3.4 s, so the crouch timing is "
                    + "NOT derived from the clip being played.");
            }
            // Start from the clip's STANDING end so the descent has somewhere to descend from.
            // (An earlier version relied on a speed -1 state and produced no descent at all -- the
            // pedestrian was upright at t=15.20 s and kneeling at t=15.45 s in the S68-A demo, a
            // snap, because Play() inverts normalizedTime on a negative-speed state.)
            SeekCrouch(UStand);
            Transition(State.CrouchEnter, dist, string.Format("controller -> {0}, clipLen={1:F3}s",
                crouchController.name, crouchClipLength));
        }

        private void RestoreController()
        {
            if (animator == null) { return; }
            if (originalController != null)
            {
                animator.runtimeAnimatorController = originalController;
                originalController = null;
            }
        }

        /// <summary>
        /// Put the crouch clip at normalized time u, where 0 is the kneel and 1 is standing.
        ///
        /// This is the ONLY thing that advances the crouch animation. The state's own speed is 0, so
        /// the Animator never moves it on its own and every pose is a direct consequence of the
        /// elapsed time this component measured -- which makes the reverse a descending parameter
        /// instead of a negative playback rate, and sidesteps the inverted-normalizedTime behaviour
        /// that made the previous version snap. It also leaves the global animator.speed untouched,
        /// which is what S32AnimatorSpeedScaler overwrites every frame.
        /// </summary>
        private void SeekCrouch(float u)
        {
            if (animator == null) { return; }
            animator.Play(poseHash, 0, Mathf.Clamp01(u));
            animator.Update(0f);
        }

        private static readonly int poseHash = Animator.StringToHash(StatePose);

        private float ClipLength()
        {
            return crouchClipLength > 0f ? crouchClipLength : 3.4f;
        }

        private float Progress(float inState)
        {
            float len = ClipLength();
            return len <= 0f ? 1f : Mathf.Clamp01(inState / len);
        }

        private float ResolveClipLength()
        {
            if (animator == null) { return 0f; }
            var infos = animator.runtimeAnimatorController != null
                ? animator.runtimeAnimatorController.animationClips : null;
            if (infos != null && infos.Length > 0 && infos[0] != null) { return infos[0].length; }
            return 0f;
        }

        private void Transition(State next, float dist, string why)
        {
            // §1.3. The video shows the result; only this shows whether the ORDER and the TIMING
            // were right, which is what the design is actually being judged on.
            Debug.Log(string.Format("[S68Curious] {0} -> {1}  t={2:F2}  dist_robot={3:F2}  ({4})",
                state, next, Time.time, dist, why));
            state = next;
            stateEnteredAt = Time.time;
            recedingSince = -1f;
        }

        // ---- the "robot has passed" test (§1.1) ----

        /// <summary>
        /// Two conditions, both required: far enough away, AND continuously getting further for at
        /// least recedeHysteresis seconds.
        ///
        /// "Getting further" is read as the robot's physics velocity projected onto the line from
        /// the pedestrian to it -- a positive radial component IS the distance increasing, taken
        /// from the body rather than reconstructed by differencing positions across frames. The
        /// differencing route is the one this file's neighbour already had to fix once
        /// (PedestrianModulator.EstimateRobotSpeed, Session 48): transform.position advances in
        /// discrete steps unrelated to the frame rate, so a per-frame difference reads zero on most
        /// frames and spikes on the rest -- which for a "has it been receding for a whole second"
        /// test would chatter the timer back to zero constantly.
        /// </summary>
        /// <summary>
        /// S68-D. True once the robot is within standUpDistance AND still closing.
        ///
        /// Both halves are required. Distance alone would also fire on a robot that has already
        /// swept past and is sitting just inside the threshold on its way out, which is the opposite
        /// situation and would have the pedestrian stand up for nothing.
        ///
        /// "Closing" is the robot's physics velocity projected onto the pedestrian->robot direction,
        /// negative meaning the gap is shrinking -- the same construction RobotHasPassed uses with
        /// the opposite sign, and for the same reason: the robot's transform advances in discrete
        /// steps, so differencing positions across frames reads zero on most frames and spikes on
        /// the rest (PedestrianModulator.EstimateRobotSpeed, Session 48).
        ///
        /// No hysteresis here, deliberately, where RobotHasPassed needs a full second of it. That
        /// test has to distinguish a real departure from noise on a stationary robot; this one is
        /// guarded by a distance threshold that noise cannot satisfy, and reacting late is the
        /// failure mode that matters -- the whole point is to be up and moving before the robot
        /// arrives.
        /// </summary>
        private bool RobotIsApproaching(Scenario.Agents.Base self, Scenario.Robot robot, float dist)
        {
            if (dist > standUpDistance) { return false; }

            Vector3 toRobot = RobotPosition(robot) - self.transform.position;
            toRobot.y = 0f;
            if (toRobot.sqrMagnitude < 1e-6f) { return true; }   // on top of us; get up

            Vector3 v = RobotVelocity(robot);
            v.y = 0f;
            float radial = Vector3.Dot(v, toRobot.normalized);
            // Negative radial component = the gap is closing.
            return radial < -0.05f;
        }

        private bool RobotHasPassed(Scenario.Agents.Base self, Scenario.Robot robot, float dist)
        {
            if (dist <= passDistance)
            {
                recedingSince = -1f;
                return false;
            }

            Vector3 toRobot = RobotPosition(robot) - self.transform.position;
            toRobot.y = 0f;
            if (toRobot.sqrMagnitude < 1e-6f) { recedingSince = -1f; return false; }

            Vector3 v = RobotVelocity(robot);
            v.y = 0f;
            float radial = Vector3.Dot(v, toRobot.normalized);

            // A small positive threshold, not > 0: a stationary robot's body still reports millimetre
            // -per-second noise, and zero would let that noise satisfy "receding".
            if (radial <= 0.05f)
            {
                recedingSince = -1f;
                return false;
            }

            if (recedingSince < 0f) { recedingSince = Time.time; }
            return (Time.time - recedingSince) >= recedeHysteresis;
        }

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

        private Vector3 RobotPosition(Scenario.Robot robot)
        {
            if (!robotBodyResolved) { ResolveRobotBody(robot); }
            if (robotArt != null) { return robotArt.transform.position; }
            if (robotRb != null) { return robotRb.position; }
            return robot.position;
        }

        private Vector3 RobotVelocity(Scenario.Robot robot)
        {
            if (!robotBodyResolved) { ResolveRobotBody(robot); }
            if (robotArt != null) { return robotArt.velocity; }
            if (robotRb != null) { return robotRb.velocity; }
            if (!robotBodyWarned)
            {
                robotBodyWarned = true;
                Debug.LogWarning("[S68Curious] no robot physics body resolved -- the 'robot has "
                    + "passed' test cannot read a velocity and will fall back to the hold timeout.");
            }
            return Vector3.zero;
        }

        // ---- S68-B §3: get out of the robot's way before crouching ----

        /// <summary>
        /// The robot's path direction. Physics velocity when the robot is actually moving, its
        /// transform heading otherwise.
        ///
        /// Velocity first because it is the physics source this file is required to read from, and
        /// because it is what the robot is DOING rather than which way its base_link happens to be
        /// modelled. The fallback matters at the moment of interest: the robot may be crawling when
        /// the pedestrian decides where to stand, and a near-zero velocity has no reliable direction.
        /// </summary>
        private Vector3 RobotPathDirection(Scenario.Robot robot, out string source)
        {
            // A LATCHED route, captured once at SLATE release -- not the robot's instantaneous
            // heading.
            //
            // The heading version is degenerate and the runs prove it. In run5 the pedestrian was
            // 1.39 m off the robot's heading line when it stopped, and 0.04 m off it 3.6 s later at
            // the crouch -- the pedestrian had not moved at all; the robot had turned. A clearance
            // defined against a line that rotates with the robot can always be nulled by the robot
            // rotating, so it constrains nothing. Across run3/4/5 the clearance measured that way
            // had no relationship to the outcome at all: the run with the LARGEST value (run4,
            // 1.18 m) is the one where the robot bulldozed the pedestrian 1.5 m.
            //
            // Latched at release, the line is the robot's actual route down the corridor -- which is
            // what "get out of the robot's way" has to mean for the offset to be worth anything.
            if (routeLatched)
            {
                source = "latched@release";
                return routeDir;
            }
            Vector3 v = RobotVelocity(robot);
            v.y = 0f;
            if (v.magnitude > 0.10f) { source = "velocity"; return v.normalized; }
            Vector3 f = robot.transform.forward;
            f.y = 0f;
            source = "heading";
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }

        // The robot's route, captured at the SLATE release instant and held. routeOrigin is a point
        // on it; routeDir is its direction.
        private Vector3 routeDir, routeOrigin;
        private bool routeLatched;

        private void LatchRoute(Scenario.Robot robot)
        {
            Vector3 v = RobotVelocity(robot); v.y = 0f;
            Vector3 f = robot.transform.forward; f.y = 0f;
            // At release the robot is cruising straight down the corridor at its trigger speed, so
            // its velocity is the cleanest available statement of where it is going.
            routeDir = v.magnitude > 0.10f ? v.normalized
                     : (f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward);
            routeOrigin = RobotPosition(robot);
            routeLatched = true;
            Debug.Log(string.Format("[S68Curious] route latched at release: origin={0} dir={1} "
                + "(from {2})", routeOrigin.ToString("F2"), routeDir.ToString("F2"),
                v.magnitude > 0.10f ? "velocity" : "heading"));
        }

        /// <summary>
        /// Perpendicular distance from the pedestrian to the robot's path line (robot position +
        /// heading), on the ground plane. This is the number §3 is specified in and the number the
        /// transition log reports.
        /// </summary>
        private float LateralClearance(Scenario.Agents.Base self, Scenario.Robot robot)
        {
            string src;
            Vector3 fwd = RobotPathDirection(robot, out src);
            Vector3 basePt = routeLatched ? routeOrigin : RobotPosition(robot);
            Vector3 toPed = self.transform.position - basePt;
            toPed.y = 0f;
            Vector3 perp = toPed - fwd * Vector3.Dot(toPed, fwd);
            return perp.magnitude;
        }

        /// <summary>
        /// Where to step to: the same distance along the robot's path the pedestrian already stands
        /// at, pushed out sideways to the target clearance.
        ///
        /// The side is whichever side the pedestrian is ALREADY on, so it never crosses the robot's
        /// nose to reach the other verge. Overshoots the requirement by sidestepMargin, because the
        /// robot keeps advancing while the step is being taken and the clearance is measured against
        /// a line that moves with it.
        /// </summary>
        private Vector3 SidestepTarget(Scenario.Agents.Base self, Scenario.Robot robot)
        {
            string src;
            Vector3 robotPos = routeLatched ? routeOrigin : RobotPosition(robot);
            Vector3 fwd = RobotPathDirection(robot, out src);
            Vector3 toPed = self.transform.position - robotPos;
            toPed.y = 0f;
            float along = Vector3.Dot(toPed, fwd);
            Vector3 perp = toPed - fwd * along;

            Vector3 side = perp.sqrMagnitude > 1e-4f
                ? perp.normalized
                // Dead ahead on the line: no side is implied, so pick the robot's left
                // deterministically rather than letting floating-point noise choose.
                : Vector3.Cross(Vector3.up, fwd).normalized;

            Vector3 target = robotPos + fwd * along + side * (lateralClearance + sidestepMargin);

            // Push the target out until it is comfortably beyond CLOSE_ENOUGH_MIN_DIST (1.0 m).
            //
            // Not cosmetic -- this is why the first attempt did not move at all. The step needed was
            // only ~0.94 m, INavigable.CloseEnough() therefore reported "arrived" the instant the
            // destination was set, StopNavigation() zeroed the social-force velocity, and the
            // absolute-speed helper turns a zero direction into a zero velocity. The pedestrian
            // stood still for the full 8 s sidestep timeout while the robot closed from 4.99 m to
            // 1.40 m. Direction is unchanged; only the reach is extended.
            Vector3 here = self.transform.position; here.y = 0f;
            Vector3 flat = target; flat.y = here.y;
            float reach = Vector3.Distance(flat, here);
            if (reach < MinNavReach)
            {
                target = here + (flat - here).normalized * MinNavReach;
            }
            return target;
        }

        /// <summary>Pin the destination to where the pedestrian stands and enter STOP, logging the
        /// lateral clearance actually achieved (§3.2's required log line).</summary>
        private void PinAndStop(Scenario.Agents.Base self, float dist, string why)
        {
            // Same technique the SLATE freeze uses (AutoTrialBootstrap's InitDest(spawnPos)): a
            // stopped agent whose destination is somewhere else is still an agent with somewhere to
            // be, and the navigation layer is entitled to act on that.
            self.InitDest(self.transform.position);
            Transition(State.Stop, dist, why);
        }

        private float GroundDistanceToRobot(Scenario.Agents.Base self, Scenario.Robot robot)
        {
            Vector3 d = self.transform.position - RobotPosition(robot);
            d.y = 0f;
            return d.magnitude;
        }

        // ---- velocity helpers ----

        /// <summary>Absolute target speed with the direction taken from the input -- Session 47's
        /// solution (e). Never a multiple of the input's magnitude, or it re-enters the compounding
        /// loop (e) exists to break.</summary>
        private static Vector3 Absolute(Vector3 direction, float targetSpeed)
        {
            if (targetSpeed <= 0f || direction.sqrMagnitude < 1e-8f) { return Vector3.zero; }
            return direction.normalized * targetSpeed;
        }

        private bool LeaveVelocity(Vector3 socialForceVelocity, Scenario.Agents.Base self, out Vector3 velocity)
        {
            if (!leaveDestSent && hasLeaveDestination)
            {
                leaveDestSent = true;
                self.InitDest(leaveDestination);
            }
            velocity = Absolute(socialForceVelocity, modulator.baseWalkSpeedMps * modulator.walkSpeedMultiplier);
            return true;
        }

        private float nextRetargetTime = 0f;
        private void RetargetTo(Scenario.Agents.Base self, Vector3 target)
        {
            // Same throttle the existing Curious Approach uses -- InitDest() replans a NavMesh path,
            // which is not a per-frame operation.
            if (Time.time < nextRetargetTime) { return; }
            nextRetargetTime = Time.time + modulator.retargetInterval;
            self.InitDest(target);
        }
    }
}
