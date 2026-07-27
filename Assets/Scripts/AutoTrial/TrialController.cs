using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Owns the capture loop once AutoTrialBootstrap has finished setup: samples on Time.time at
    /// 1/fps (never Time.captureFramerate/timeScale, which would desync from wall-clock move_base),
    /// renders the POV camera to disk as JPGs, appends frames.csv, and on termination writes
    /// meta.json and exits the process. Session 10 (D5): the third-person/chase camera is gone --
    /// POV only, per the output-format spec (REPORT.md Session 10).
    /// </summary>
    public class TrialController : MonoBehaviour
    {
        private AutoTrialConfig config;
        private Scenario.Robot robot;
        private Camera povCam;
        private Transform pedestrian;
        private string appearanceZone;
        private string appearanceResourcePath;
        private List<string> agentCensus;

        private RenderTexture povRT;
        private Texture2D readBuffer;
        private string povDir;
        private StreamWriter csv;

        private float startTime;
        private float lastSampleTime;
        private Vector3 lastSamplePos;
        private int frameIdx;
        private float minDistSeen = float.PositiveInfinity;
        // Session 35 BLOCK 4: whole-trial running minimum of pedestrian1<->pedestrianN distance,
        // for the SAME "extend zero-collision discipline to pedestrian-pedestrian clearance too"
        // reason minDistSeen tracks robot<->pedestrian1 -- logged into meta.json alongside it.
        private float minPedPedDistSeen = float.PositiveInfinity;
        private string terminationReason = "unknown";

        // Session 10 (D2 diagnosis): latest cmd_vel seen, for correlating commanded steering
        // against actual camera/body rotation. Subscribed the same way DiagCmdVel.cs already
        // proved works (topic string read live off the scene's live ControlSubscriber, never
        // assumed) -- best-effort: if this fails, the two columns are just blank, nothing else
        // about the trial is affected.
        private double lastCmdLinX;
        private double lastCmdAngZ;
        private bool cmdVelAvailable;

        // Session 14 (SLATE v2): distance-triggered "t=0" -- see PollForTrigger. preRollDurationSec
        // is wall-clock time from AutoTrialBootstrap.Run()'s very first line to the trigger firing;
        // robotSpeedAtTrigger is the robot's actual instantaneous speed at that instant (gated,
        // acceptance wants >=0.3 m/s -- "cruising, not standing"); triggerTimedOut flags the 30s
        // guard path (dist never crossed triggerDistanceMeters -- capture started anyway).
        private float preRollDurationSec;
        private float robotSpeedAtTrigger;
        private bool triggerTimedOut;

        // Session 43: the robot's own physics body, for measured (signed, vector) velocity instead
        // of the unsigned finite difference robot_speed carries. SEAN.Control.VelocityController
        // drives exactly one of these two -- ArticulationBody root if the robot has one (legged,
        // e.g. Unitree A1), else the base_link Rigidbody (wheeled, e.g. Kuri) -- so we resolve the
        // same pair the same way and read back whichever one is live. Best-effort: if neither
        // exists the seven new columns are blank and nothing else about the trial changes.
        private Rigidbody robotRb;
        private ArticulationBody robotArt;
        private string robotBodySource = "none";
        // Unwrapped ROS-convention yaw, accumulated across frames so the column never jumps by 2*pi
        // at the wrap point (a consumer differentiating or plotting it would otherwise see a spike).
        private float yawRosUnwrappedRad;
        private float lastYawRosRawRad;
        private bool yawRosSeeded;
        // Session 17 (Step 3.5): true if one or more implausible-speed readings were rejected and
        // resampled before the trigger actually fired -- see PollForTrigger's own comment for why
        // this recurred even after Session 14's capture-cadence fix.
        private bool triggerSpeedResampled;

        // Session 14 (SLATE v2) Initialize() inputs, stashed for PollForTrigger to use once its
        // own coroutine starts (not passed as coroutine params -- IEnumerator methods can't take
        // ref/out and these need to survive across yields anyway).
        private IVI.INavigable pedestrianNavAgent;
        private Vector3 pedestrianReleaseDest;

        // Session 35 BLOCK 4 (FIX 8/9): dyad/ped-count-3 extra pedestrians. Empty for every
        // single-pedestrian trial (the overwhelming majority) -- see SpawnExtraPedestrian's own
        // doc comment in AutoTrialBootstrap for how these get spawned.
        private List<Transform> extraPedestrianTransforms = new List<Transform>();
        private List<IVI.INavigable> extraPedestrianNavAgents = new List<IVI.INavigable>();
        private List<Vector3> extraPedestrianReleaseDests = new List<Vector3>();

        private float bootstrapStartTime;
        private float triggerDistanceMeters;
        // Session 17 (Step 3, real-A1 camera pose): resolved once at rig build time
        // (AutoTrialBootstrap.ResolveCameraGroundHeight), logged into meta.json below.
        private float resolvedCamHeightWorldY;
        private bool camHeightRaycastHit;
        // Session 27 (FOV truth): resolved once at rig build time (AutoTrialBootstrap.
        // BuildPovCamera, derived from config.camera.camHfovDeg + the capture aspect), logged into
        // meta.json below alongside the requested horizontal value (already embedded via config).
        private float resolvedCamVfovDeg;

        private const float TriggerTimeoutSec = 30f;
        // Session 17 (Step 3.5): see PollForTrigger's class doc -- 2.5x max_vel_x (0.6), well
        // above every normal cruise reading observed (<=~0.9 m/s) and well below both observed
        // anomalies (2.67/3.6 m/s, Session 16).
        private const float MaxPlausibleTriggerSpeedMps = 1.5f;

        // Round 4: made public so AutoTrialBootstrap.BuildPovCamera can set povCam.aspect from
        // the same authoritative numbers used to build the RenderTexture -- see that method's
        // comment for the aspect-mismatch bug this closes.
        public const int CaptureWidth = 1280;
        public const int CaptureHeight = 720;
        private const float GoalArrivalDistMeters = 0.5f;

        public void Initialize(AutoTrialConfig config, Scenario.Robot robot, Camera povCam,
            Transform pedestrian, string appearanceZone, string appearanceResourcePath, List<string> agentCensus,
            IVI.INavigable pedestrianNavAgent, Vector3 pedestrianReleaseDest,
            float bootstrapStartTime, float triggerDistanceMeters,
            float resolvedCamHeightWorldY, bool camHeightRaycastHit, float resolvedCamVfovDeg,
            List<Transform> extraPedestrianTransforms = null,
            List<IVI.INavigable> extraPedestrianNavAgents = null,
            List<Vector3> extraPedestrianReleaseDests = null)
        {
            this.extraPedestrianTransforms = extraPedestrianTransforms ?? new List<Transform>();
            this.extraPedestrianNavAgents = extraPedestrianNavAgents ?? new List<IVI.INavigable>();
            this.extraPedestrianReleaseDests = extraPedestrianReleaseDests ?? new List<Vector3>();
            this.config = config;
            this.robot = robot;
            this.povCam = povCam;
            this.pedestrian = pedestrian;
            this.appearanceZone = appearanceZone;
            this.appearanceResourcePath = appearanceResourcePath;
            this.agentCensus = agentCensus;
            this.pedestrianNavAgent = pedestrianNavAgent;
            this.pedestrianReleaseDest = pedestrianReleaseDest;
            this.bootstrapStartTime = bootstrapStartTime;
            this.triggerDistanceMeters = triggerDistanceMeters;
            this.resolvedCamHeightWorldY = resolvedCamHeightWorldY;
            this.camHeightRaycastHit = camHeightRaycastHit;
            this.resolvedCamVfovDeg = resolvedCamVfovDeg;

            Directory.CreateDirectory(config.outDir);
            povDir = Path.Combine(config.outDir, "pov");
            Directory.CreateDirectory(povDir);

            povRT = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32);
            povCam.targetTexture = povRT;
            readBuffer = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);

            csv = new StreamWriter(Path.Combine(config.outDir, "frames.csv"), false);
            // Session 35 BLOCK 4 (FIX 8/9): one extra pedestrianN_x/z + dist_to_pedestrianN column
            // pair per extra pedestrian (dyad=1, ped-count-3=2) -- appended after the existing
            // fixed columns so every pre-existing single-pedestrian trial's own frames.csv schema
            // (and every downstream tool that reads it positionally by name via csv.DictReader,
            // which every analysis script in this project already uses) is completely unaffected.
            string header = "t,frame_idx,robot_x,robot_y,robot_z,robot_yaw_deg,robot_speed,pedestrian_appearance,pedestrian_personality,pedestrian_x,pedestrian_z,dist_to_pedestrian,min_dist,cmd_lin_x,cmd_ang_z,pov_cam_yaw_deg,pov_cam_pitch_deg,pov_cam_roll_deg";
            for (int i = 0; i < extraPedestrianTransforms.Count; i++)
            {
                int n = i + 2; // pedestrian2, pedestrian3, ...
                header += ",pedestrian" + n + "_x,pedestrian" + n + "_z,dist_to_pedestrian" + n
                    + ",dist_pedestrian1_to_pedestrian" + n;
            }
            // Session 43: measured body velocity, straight off the physics body. Appended at the
            // very END -- after the variable-width extra-pedestrian block -- so BOTH the
            // single-pedestrian and the dyad/ped_count_3 schemas keep their exact existing prefix
            // and every historical frames.csv stays byte-comparable through its old columns.
            // robot_speed is deliberately NOT touched: it is the trigger-speed gate's input and
            // the comparison basis for every trial ever run.
            header += ",robot_vel_x,robot_vel_y,robot_vel_z,robot_speed_ground,robot_ang_vel_y"
                + ",robot_yaw_ros_rad,robot_ang_vel_ros";
            // Session 44 (3.5): the pedestrian's VERTICAL position. Its absence is why
            // Stroke_Shaking_Head's "floating" defect could only ever be judged by eye --
            // frames.csv carried pedestrian_x and pedestrian_z but no y, so a displacement
            // measurement was evidence about gliding and no evidence at all about grounding.
            // Appended last, after every other column, so no existing schema position moves.
            header += ",pedestrian_y,pedestrian_ground_y";
            csv.WriteLine(header);

            ResolveRobotBody();

            TrySubscribeCmdVel();

            // Session 14 (SLATE v2): the robot's goal was already published early, back in
            // AutoTrialBootstrap.Run() (restored to pre-Session-13/Round-4 timing) -- by the time
            // we get here it's typically already cruising toward it. The pedestrian is frozen
            // (InitDest(spawnPos) in SpawnPedestrian) further out than triggerDistanceMeters.
            // Capture does NOT start yet -- poll every frame until the live distance crosses the
            // trigger threshold, defined by construction to be the trial's actual dist0.
            StartCoroutine(PollForTrigger());
        }

        /// <summary>
        /// Session 14 (SLATE v2): "t=0" is redefined as a live event, not a fixed instant --
        /// the frame the robot<->pedestrian ground-plane distance first drops to
        /// triggerDistanceMeters (== --ped-distance) or below. Until then the robot cruises
        /// (goal already published, pre-roll) toward the still-frozen pedestrian; the moment the
        /// trigger fires, the pedestrian is released (InitDest toward its real destination) and
        /// the capture loop starts in the SAME frame -- dist0 is therefore the trigger threshold
        /// itself, to within one frame's worth of robot travel (typically &lt;0.05m), and the
        /// robot is already moving, not standing. A 30s guard starts capture anyway (loudly
        /// flagged, never silently) if the distance never crosses -- see triggerTimedOut.
        ///
        /// Session 17 (Step 3.5): Session 14's capture-cadence fix (refresh the speed reference
        /// only once per ~1/fps, not every render tick) closed the ORIGINAL bug (a 16.5 m/s
        /// reading from a single-tick-scale window) but didn't close the class -- Session 16's
        /// powered N=6 battery still logged 2.67/3.6 m/s in 2/18 trials. Root cause, established
        /// by direct comparison against frame 1's own (normal, ~0.57-0.59 m/s) reading in both
        /// runs: the implied single-window displacement at those two triggers (&asymp;0.18-0.24m)
        /// is a REAL position delta over that window, 3-4x a normal capture-interval's worth of
        /// cruise motion (&asymp;0.05m) -- not sensor/timing noise a coarser sampling window can
        /// average away, but an occasional genuine sub-frame position correction (this project's
        /// velocity-driven gait syncing against the NavMeshAgent's own position, most plausibly --
        /// not independently confirmed, `VelocityController`/nav internals are off-limits/
        /// unedited) that can land inside ANY window regardless of its width. Fixed properly below
        /// by rejecting an implausible reading outright rather than widening the averaging window
        /// further: MaxPlausibleTriggerSpeedMps is comfortably above every normal cruise reading
        /// observed (&le;~0.9 m/s) and comfortably below both observed anomalies (2.67/3.6 m/s).
        /// </summary>
        private IEnumerator PollForTrigger()
        {
            // Speed is measured over a window matching the capture cadence (~1/fps), refreshed
            // below only once that much time has passed -- polling the distance check itself
            // every render tick (every yield) but sampling the speed reference at a coarser,
            // fixed cadence avoids single-Update()-tick jitter (observed empirically: sampling
            // speed over a raw per-tick delta produced a one-off 16.5 m/s spike at trigger time on
            // an otherwise ~0.5-0.6 m/s cruise -- a real but sub-frame position micro-correction
            // that a 1/fps-scale window (matching what frames.csv's own robot_speed column uses
            // from frame 1 onward) averages away, same as it would for any other frame).
            float captureInterval = 1f / Mathf.Max(config.fps, 1);
            float pollStart = Time.time;
            Vector3 lastPollPos = robot.position;
            float lastPollTime = pollStart;

            while (true)
            {
                float dist = pedestrian != null
                    ? Util.Geometry.GroundPlaneDist(robot.position, pedestrian.position)
                    : float.PositiveInfinity;
                float pollDt = Mathf.Max(Time.time - lastPollTime, 0.0001f);
                float speedNow = Vector3.Distance(robot.position, lastPollPos) / pollDt;

                bool distTriggered = dist <= triggerDistanceMeters;
                bool timedOut = Time.time - pollStart >= TriggerTimeoutSec;

                if ((distTriggered || timedOut) && speedNow > MaxPlausibleTriggerSpeedMps)
                {
                    // Session 17 (Step 3.5): known-implausible reading -- refuse to fire and
                    // record it. Force an immediate reference refresh (bypassing the normal
                    // captureInterval gate below) and retry next frame; the distance/timeout
                    // condition that wanted to fire this frame is still true next frame (dist
                    // doesn't un-close, the timeout clock doesn't un-elapse), so this costs at
                    // most a frame or two of extra pre-roll, never blocks the trigger outright.
                    triggerSpeedResampled = true;
                    Debug.LogWarning("[AutoTrial] SLATE v2 TRIGGER: rejected implausible robotSpeedAtTrigger candidate "
                        + speedNow.ToString("F3") + " m/s (> " + MaxPlausibleTriggerSpeedMps.ToString("F1")
                        + " m/s sanity bound) -- resampling next frame instead of recording it.");
                    lastPollPos = robot.position;
                    lastPollTime = Time.time;
                    yield return null;
                    continue;
                }

                if (distTriggered || timedOut)
                {
                    triggerTimedOut = timedOut && !distTriggered;
                    preRollDurationSec = Time.time - bootstrapStartTime;
                    robotSpeedAtTrigger = speedNow;

                    if (triggerTimedOut)
                    {
                        Debug.LogWarning("[AutoTrial] SLATE v2 TRIGGER TIMEOUT: dist_to_pedestrian never <= "
                            + triggerDistanceMeters.ToString("F2") + "m within " + TriggerTimeoutSec.ToString("F0")
                            + "s of pre-roll (last dist=" + dist.ToString("F2") + "m, robot speed=" + speedNow.ToString("F3")
                            + " m/s). Starting capture anyway -- meta.json.triggerTimedOut=true.");
                    }
                    else
                    {
                        Debug.Log("[AutoTrial] SLATE v2 TRIGGER: dist_to_pedestrian=" + dist.ToString("F3")
                            + "m <= " + triggerDistanceMeters.ToString("F2") + "m after " + preRollDurationSec.ToString("F2")
                            + "s of pre-roll (robotSpeedAtTrigger=" + robotSpeedAtTrigger.ToString("F3")
                            + " m/s) -- releasing pedestrian, starting capture now (t=0).");
                    }

                    pedestrianNavAgent?.InitDest(pedestrianReleaseDest);
                    // Session 35 BLOCK 4: release every extra pedestrian at the exact same SLATE
                    // instant as the primary one -- otherwise they'd stay frozen at spawn for the
                    // rest of the trial (InitDest(spawnPos) in SpawnExtraPedestrian never gets a
                    // second call any other way).
                    for (int i = 0; i < extraPedestrianNavAgents.Count; i++)
                    {
                        extraPedestrianNavAgents[i]?.InitDest(extraPedestrianReleaseDests[i]);
                    }

                    // Session 32 FIX B: if this pedestrian carries the Assertive straight-line
                    // guardian (only true for PersonalityType.Assertive, see AutoTrialBootstrap),
                    // arm it at the exact release instant -- pre-release frozen-facing behavior
                    // is completely untouched, this only takes over from here.
                    if (pedestrianNavAgent != null)
                    {
                        var straightLineGuardian = pedestrianNavAgent.GetComponent<S32AssertiveStraightLineGuardian>();
                        if (straightLineGuardian != null)
                        {
                            straightLineGuardian.Activate(pedestrianNavAgent.transform.position, pedestrianReleaseDest);
                        }
                    }

                    // The trigger frame's own robot position hasn't moved since the check above
                    // (same frame, no yield) -- seed frame 0's own speed calc off the PREVIOUS
                    // poll sample so frames.csv's robot_speed column reflects the real cruising
                    // speed at t=0 (matches robotSpeedAtTrigger exactly), not an artificial ~0
                    // from a zero-elapsed first sample (see CaptureFrame's dt/speed calc).
                    startTime = Time.time;
                    lastSampleTime = -pollDt;
                    lastSamplePos = lastPollPos;

                    StartCoroutine(RunLoop());
                    yield break;
                }

                if (pollDt >= captureInterval)
                {
                    lastPollPos = robot.position;
                    lastPollTime = Time.time;
                }
                yield return null;
            }
        }

        /// <summary>
        /// Session 43: find the physics body SEAN.Control.VelocityController is actually driving.
        /// Mirrors that class's own resolution order exactly (ArticulationBody root first, then the
        /// base_link Rigidbody) so we never read a body the controller isn't writing -- reading the
        /// wrong one is the failure mode that would silently produce all-zero velocity columns.
        /// </summary>
        private void ResolveRobotBody()
        {
            try
            {
                GameObject baseLink = robot != null ? robot.base_link : null;
                if (baseLink == null)
                {
                    Debug.LogWarning("[AutoTrial] no robot.base_link -- Session 43 velocity columns will be blank.");
                    return;
                }
                foreach (ArticulationBody body in baseLink.GetComponentsInChildren<ArticulationBody>())
                {
                    if (body.isRoot)
                    {
                        robotArt = body;
                        robotBodySource = "ArticulationBody:" + body.name;
                        break;
                    }
                }
                if (robotArt == null)
                {
                    robotRb = baseLink.GetComponent<Rigidbody>();
                    if (robotRb != null)
                    {
                        robotBodySource = "Rigidbody:" + robotRb.name;
                    }
                }
                Debug.Log("[AutoTrial] Session 43 velocity source: " + robotBodySource);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AutoTrial] could not resolve the robot physics body (non-fatal, "
                    + "Session 43 velocity columns will be blank): " + e.Message);
            }
        }

        private void TrySubscribeCmdVel()
        {
            try
            {
                SEAN sean = SEAN.instance;
                if (sean == null)
                {
                    return;
                }
                Control.ControlSubscriber controller = sean.controller;
                if (controller == null)
                {
                    return;
                }
                string topic = controller.Topic;
                ROSConnection.instance.Subscribe<RosMessageTypes.Geometry.MTwist>(topic, OnCmdVel);
                cmdVelAvailable = true;
                Debug.Log("[AutoTrial] TrialController subscribed to cmd_vel topic '" + topic + "' for D2 diagnostics.");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AutoTrial] TrialController could not subscribe to cmd_vel (non-fatal, "
                    + "cmd_lin_x/cmd_ang_z columns will be blank): " + e.Message);
            }
        }

        private void OnCmdVel(RosMessageTypes.Geometry.MTwist msg)
        {
            lastCmdLinX = msg.linear.x;
            lastCmdAngZ = msg.angular.z;
        }

        // Session 15: tracks whether the pedestrian has genuinely been passed (dist first went
        // below triggerDistanceMeters -- true almost immediately after t=0 by construction -- and
        // has since climbed back above it, meaning the pass is over and it's moving away again),
        // and when that happened, for --post-encounter-grace. See RunLoop's termination check.
        private bool everWithinTriggerDist;
        private bool encounterConcluded;
        private float encounterConcludedAtElapsed;

        private IEnumerator RunLoop()
        {
            float interval = 1f / Mathf.Max(config.fps, 1);
            float nextTick = startTime;

            while (true)
            {
                float elapsed = Time.time - startTime;

                if (elapsed >= config.durationSec)
                {
                    terminationReason = "duration";
                    break;
                }
                if (config.hasGoalPose)
                {
                    float distToGoal = Util.Geometry.GroundPlaneDist(robot.position, config.goalPose.Position);
                    if (distToGoal <= GoalArrivalDistMeters)
                    {
                        terminationReason = "goal_reached";
                        break;
                    }
                }
                if (config.hasPostEncounterGrace && pedestrian != null)
                {
                    float distToPed = Util.Geometry.GroundPlaneDist(robot.position, pedestrian.position);
                    if (distToPed < config.triggerDistanceMeters)
                    {
                        everWithinTriggerDist = true;
                    }
                    else if (everWithinTriggerDist && !encounterConcluded)
                    {
                        encounterConcluded = true;
                        encounterConcludedAtElapsed = elapsed;
                        Debug.Log("[AutoTrial] post-encounter grace timer started at t=" + elapsed.ToString("F2")
                            + "s (dist_to_pedestrian=" + distToPed.ToString("F2") + "m re-exceeded triggerDistanceMeters="
                            + config.triggerDistanceMeters.ToString("F2") + "m) -- capture ends "
                            + config.postEncounterGraceSec.ToString("F1") + "s from now unless duration/goal_reached fires first.");
                    }
                    if (encounterConcluded && elapsed - encounterConcludedAtElapsed >= config.postEncounterGraceSec)
                    {
                        terminationReason = "post_encounter_grace";
                        break;
                    }
                }

                if (Time.time >= nextTick)
                {
                    CaptureFrame(elapsed);
                    nextTick += interval;
                }

                yield return null;
            }

            // One final frame at the terminal instant (arrival/duration-end/grace-elapsed), then finish.
            CaptureFrame(Time.time - startTime);
            FinishTrial();
        }

        private void CaptureFrame(float t)
        {
            RenderAndSave(povCam, povRT, Path.Combine(povDir, "pov_" + frameIdx.ToString("D5", CultureInfo.InvariantCulture) + ".jpg"));

            Vector3 pos = robot.position;
            float yawDeg = robot.transform.eulerAngles.y;
            float dt = Mathf.Max(t - lastSampleTime, 0.0001f);
            float speed = Vector3.Distance(pos, lastSamplePos) / dt;

            // Instantaneous nearest-pedestrian distance for this frame. Loop 1 Bug 1 fix: this used
            // to stop at distToPed (pedestrian1 only) -- for dyad/ped_count_3, extraPedestrianTransforms'
            // own robot-distance was computed further down and written per-column into frames.csv
            // but never folded into frameMinDist/minDistSeen, so every safety verdict ever reported
            // for those configs (this project's own "min_dist"/"minDistanceMeters") silently ignored
            // whichever extra pedestrian the robot might actually be closest to. frameMinDist is now
            // finalized below as min(distToPed, all distRobotToExtra) before the CSV row is built.
            float distToPed = pedestrian != null ? Util.Geometry.GroundPlaneDist(pos, pedestrian.position) : float.NaN;
            float frameMinDist = distToPed;

            // Extra-pedestrian robot-distances (dyad/ped_count_3) computed here, BEFORE
            // frameMinDist/minDistSeen are finalized below, so the whole-trial safety minimum
            // reflects whichever pedestrian the robot is actually closest to, not just pedestrian1.
            int extraCount = extraPedestrianTransforms.Count;
            float[] extraRobotDist = extraCount > 0 ? new float[extraCount] : null;
            float[] extraPed1Dist = extraCount > 0 ? new float[extraCount] : null;
            for (int i = 0; i < extraCount; i++)
            {
                Transform extraT = extraPedestrianTransforms[i];
                bool alive = extraT != null;
                float distRobotToExtra = alive ? Util.Geometry.GroundPlaneDist(pos, extraT.position) : float.NaN;
                float distPed1ToExtra = (alive && pedestrian != null)
                    ? Util.Geometry.GroundPlaneDist(pedestrian.position, extraT.position) : float.NaN;
                extraRobotDist[i] = distRobotToExtra;
                extraPed1Dist[i] = distPed1ToExtra;
                if (!float.IsNaN(distPed1ToExtra))
                {
                    minPedPedDistSeen = Mathf.Min(minPedPedDistSeen, distPed1ToExtra);
                }
                if (!float.IsNaN(distRobotToExtra))
                {
                    frameMinDist = float.IsNaN(frameMinDist) ? distRobotToExtra : Mathf.Min(frameMinDist, distRobotToExtra);
                }
            }
            if (!float.IsNaN(frameMinDist))
            {
                minDistSeen = Mathf.Min(minDistSeen, frameMinDist);
            }

            string appearanceLabel = pedestrian != null ? appearanceResourcePath : "";
            string personalityLabel = pedestrian != null
                ? (pedestrian.GetComponent<Scenario.Agents.PedestrianModulator>()?.personality.ToString() ?? config.personality)
                : "";
            string pedXLabel = pedestrian != null ? pedestrian.position.x.ToString("F3", CultureInfo.InvariantCulture) : "";
            string pedZLabel = pedestrian != null ? pedestrian.position.z.ToString("F3", CultureInfo.InvariantCulture) : "";

            // Session 10 (D2 diagnosis): commanded steering (cmd_vel, if the subscription landed)
            // and the POV camera's own world rotation, logged every frame so shake can be
            // attributed to "commanded" vs "body/gait physics" vs both by correlating against the
            // existing robot_yaw_deg column in post-processing (never inferred in-engine).
            Vector3 camEuler = povCam.transform.eulerAngles;
            float camPitch = camEuler.x > 180f ? camEuler.x - 360f : camEuler.x;
            float camRoll = camEuler.z > 180f ? camEuler.z - 360f : camEuler.z;

            var row = new List<string>
            {
                t.ToString("F3", CultureInfo.InvariantCulture),
                frameIdx.ToString(CultureInfo.InvariantCulture),
                pos.x.ToString("F3", CultureInfo.InvariantCulture),
                pos.y.ToString("F3", CultureInfo.InvariantCulture),
                pos.z.ToString("F3", CultureInfo.InvariantCulture),
                yawDeg.ToString("F2", CultureInfo.InvariantCulture),
                speed.ToString("F3", CultureInfo.InvariantCulture),
                appearanceLabel,
                personalityLabel,
                pedXLabel,
                pedZLabel,
                float.IsNaN(distToPed) ? "" : distToPed.ToString("F3", CultureInfo.InvariantCulture),
                float.IsNaN(frameMinDist) ? "" : frameMinDist.ToString("F3", CultureInfo.InvariantCulture),
                cmdVelAvailable ? lastCmdLinX.ToString("F4", CultureInfo.InvariantCulture) : "",
                cmdVelAvailable ? lastCmdAngZ.ToString("F4", CultureInfo.InvariantCulture) : "",
                camEuler.y.ToString("F3", CultureInfo.InvariantCulture),
                camPitch.ToString("F3", CultureInfo.InvariantCulture),
                camRoll.ToString("F3", CultureInfo.InvariantCulture),
            };

            // Session 35 BLOCK 4 (FIX 8/9): extra pedestrian columns -- empty strings for every
            // single-pedestrian trial (extraPedestrianTransforms is empty), so this loop is a pure
            // no-op there. Distances themselves were already computed above (folded into
            // frameMinDist/minDistSeen/minPedPedDistSeen); this just writes the per-column values.
            for (int i = 0; i < extraCount; i++)
            {
                Transform extraT = extraPedestrianTransforms[i];
                bool alive = extraT != null;
                float distRobotToExtra = extraRobotDist[i];
                float distPed1ToExtra = extraPed1Dist[i];
                row.Add(alive ? extraT.position.x.ToString("F3", CultureInfo.InvariantCulture) : "");
                row.Add(alive ? extraT.position.z.ToString("F3", CultureInfo.InvariantCulture) : "");
                row.Add(float.IsNaN(distRobotToExtra) ? "" : distRobotToExtra.ToString("F3", CultureInfo.InvariantCulture));
                row.Add(float.IsNaN(distPed1ToExtra) ? "" : distPed1ToExtra.ToString("F3", CultureInfo.InvariantCulture));
            }

            // Session 43: measured body velocity + the ROS-convention heading pair.
            //
            // Unity here is left-handed Y-up: yaw grows CLOCKWISE seen from above, starting at +Z.
            // ROS map frame is right-handed Z-up: yaw grows COUNTER-clockwise, starting at +x, and
            // Unity->FLU maps +Z to +x. The two therefore differ by exactly a sign, which is the
            // same sign SEAN.TF.OdometryPublisher already applies when it negates its angular
            // velocity before sending. So: ros = -unity, for both the heading and its rate.
            //
            // The Unity-convention robot_yaw_deg column is left exactly as it was -- there must not
            // be two different histories of the same column name.
            bool haveBody = robotArt != null || robotRb != null;
            Vector3 vel = robotArt != null ? robotArt.velocity : (robotRb != null ? robotRb.velocity : Vector3.zero);
            Vector3 angVel = robotArt != null ? robotArt.angularVelocity
                : (robotRb != null ? robotRb.angularVelocity : Vector3.zero);

            float yawRosRaw = -yawDeg * Mathf.Deg2Rad;
            if (!yawRosSeeded)
            {
                yawRosUnwrappedRad = yawRosRaw;
                lastYawRosRawRad = yawRosRaw;
                yawRosSeeded = true;
            }
            else
            {
                float d = Mathf.DeltaAngle(lastYawRosRawRad * Mathf.Rad2Deg, yawRosRaw * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                yawRosUnwrappedRad += d;
                lastYawRosRawRad = yawRosRaw;
            }

            string F4(float v) { return v.ToString("F4", CultureInfo.InvariantCulture); }

            row.Add(haveBody ? F4(vel.x) : "");
            row.Add(haveBody ? F4(vel.y) : "");
            row.Add(haveBody ? F4(vel.z) : "");
            // Ground-plane speed only. The existing robot_speed is a 3D displacement difference and
            // so folds in vertical bob -- real on the legged robot, whose body rises and falls with
            // every step. robot_speed_ground <= robot_speed is the invariant to check.
            row.Add(haveBody ? F4(Mathf.Sqrt(vel.x * vel.x + vel.z * vel.z)) : "");
            row.Add(haveBody ? F4(angVel.y) : "");
            row.Add(F4(yawRosUnwrappedRad));
            row.Add(haveBody ? F4(-angVel.y) : "");
            // Session 44 (3.5): the pedestrian's own height AND the ground height beneath it.
            // Both are needed: grounding is a DEVIATION, and this scene's terrain is not
            // flat -- a trial measured a clean 0.21m step-down at t=37.4s that then held,
            // which is a character walking onto lower ground, not a character floating.
            // Comparing pedestrian_y against a fixed 0.0 would call that a grounding
            // failure and would equally miss a real float over raised terrain.
            row.Add(pedestrian != null
                ? pedestrian.position.y.ToString("F3", CultureInfo.InvariantCulture) : "");
            row.Add(pedestrian != null
                ? GroundHeightUnder(pedestrian).ToString("F3", CultureInfo.InvariantCulture) : "");

            csv.WriteLine(string.Join(",", row));

            lastSampleTime = t;
            lastSamplePos = pos;
            frameIdx++;
        }

        /// <summary>
        /// Session 44 (3.5): world Y of the ground directly beneath a transform. Raycasts down from
        /// above it, skipping triggers and the character's own colliders, so the grounding check
        /// measures a DEVIATION from the surface rather than from an assumed y=0. Returns the
        /// transform's own y when nothing is hit, which makes the deviation read 0 rather than
        /// inventing a spurious failure off the edge of the world.
        /// </summary>
        private static float GroundHeightUnder(Transform t)
        {
            Vector3 origin = t.position + Vector3.up * 1.0f;
            float best = float.NegativeInfinity;
            foreach (RaycastHit hit in Physics.RaycastAll(origin, Vector3.down, 6.0f))
            {
                if (hit.collider.isTrigger) { continue; }
                if (hit.collider.transform == t || hit.collider.transform.IsChildOf(t)) { continue; }
                if (hit.point.y > best) { best = hit.point.y; }
            }
            return float.IsNegativeInfinity(best) ? t.position.y : best;
        }

        private void RenderAndSave(Camera cam, RenderTexture rt, string path)
        {
            cam.Render();
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            readBuffer.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            readBuffer.Apply(false);
            RenderTexture.active = prevActive;

            byte[] jpg = readBuffer.EncodeToJPG(config.jpgQuality);
            File.WriteAllBytes(path, jpg);
        }

        private void FinishTrial()
        {
            csv.Flush();
            csv.Close();

            povRT.Release();
            WriteMetaJson();

            Debug.Log("[AutoTrial] trial finished (" + terminationReason + "), " + frameIdx + " frames captured.");
#if UNITY_EDITOR
            EditorApplication.Exit(0);
#else
            Application.Quit(0);
#endif
        }

        [Serializable]
        private class TrialMeta
        {
            public AutoTrialConfig config;
            public string terminationReason;
            public int frameCount;
            public float minDistanceMeters;
            // Session 35 BLOCK 4: -1 (same "no valid samples" convention as minDistanceMeters
            // above) for every single-pedestrian trial.
            public float minPedPedDistanceMeters;
            public string startedAtUtc;
            public string endedAtUtc;
            public string unityGitSha;
            public string resolvedAppearanceZone;
            public string resolvedAppearanceResourcePath;
            public string resolvedAppearanceNote;
            // Session 10 (D3): every IVI.INavigable found in the scene at spawn time, one line
            // each -- "AutoTrial: <name> [<type>]" for the one this trial spawned, "STRAY
            // (destroyed): <name> [<type>]" for anything else that had to be destroyed. Expected
            // to contain exactly one "AutoTrial:" line and zero "STRAY" lines every trial; a
            // non-empty STRAY entry here means the pre-Start disable pass (see
            // AutoTrialBootstrap.DisableSceneHygieneHazards) didn't fully work and the
            // census/destroy pass had to catch it instead.
            public List<string> agentCensus;
            // Round 4: the actual povCam.aspect this trial rendered with, plus the RT-derived
            // target it should equal (CaptureWidth/CaptureHeight) -- lets run_trial.py's
            // permanent aspect gate verify from meta.json on every run, not just trust the
            // in-engine assert at rig build time (AutoTrialBootstrap.BuildPovCamera) fired clean.
            public float povCameraAspect;
            public float targetAspect;
            // Session 14 (SLATE v2): see the fields of the same name on TrialController --
            // preRollDurationSec/robotSpeedAtTrigger are gated by run_trial.py (trigger speed
            // gate, dist0 gate); triggerTimedOut is a loud, never-silent flag for the 30s guard
            // path (capture started without the distance trigger ever firing).
            public float preRollDurationSec;
            public float robotSpeedAtTrigger;
            public bool triggerTimedOut;
            // Session 43: which physics body the new robot_vel_*/robot_ang_vel_y columns were read
            // off ("Rigidbody:<name>", "ArticulationBody:<name>", or "none"). Recorded so a blank
            // velocity column is always attributable from meta.json alone, rather than being
            // mistaken downstream for a robot that genuinely never moved.
            public string velocitySource;
            // Session 17 (Step 3.5): true if one or more implausible-speed readings (see
            // PollForTrigger's MaxPlausibleTriggerSpeedMps) were rejected before the recorded
            // robotSpeedAtTrigger value above was accepted -- never itself a bad value (a rejected
            // reading is never written to robotSpeedAtTrigger), just a transparency flag.
            public bool triggerSpeedResampled;
            // Session 17 (Step 3, real-A1 camera pose): the ACTUAL resolved values, promoted to
            // top level for visibility -- config.camera.camHeightMeters/fixedPitchDeg are the
            // requested inputs; these are what the rig actually built (may differ from the naive
            // mount-Y + requested-height sum if camHeightRaycastHit is false, i.e. the ground
            // raycast missed and fell back to robot.transform.position.y as a proxy).
            public float resolvedCamHeightWorldY;
            public bool camHeightRaycastHit;
            public float resolvedCamPitchDeg;
            // Session 27 (FOV truth): config.camera.camHfovDeg is the requested horizontal input
            // (already embedded via config); this is the actual resolved vertical Camera.fieldOfView
            // Unity used, promoted to top level for the same visibility reason as the fields above.
            public float resolvedCamHfovDeg;
            public float resolvedCamVfovDeg;
        }

        private void WriteMetaJson()
        {
            var meta = new TrialMeta
            {
                config = config,
                terminationReason = terminationReason,
                frameCount = frameIdx,
                minDistanceMeters = float.IsInfinity(minDistSeen) ? -1f : minDistSeen,
                minPedPedDistanceMeters = float.IsInfinity(minPedPedDistSeen) ? -1f : minPedPedDistSeen,
                startedAtUtc = DateTime.UtcNow.AddSeconds(-(Time.time - startTime)).ToString("o", CultureInfo.InvariantCulture),
                endedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                unityGitSha = TryReadGitSha(),
                resolvedAppearanceZone = appearanceZone,
                resolvedAppearanceResourcePath = appearanceResourcePath,
                resolvedAppearanceNote = config.appearance == "wheelchair_user"
                    ? "wheelchair_user resolves to WheelChairUserContainer -> Rocketbox/Wheelchair_Female (female avatar only; male package has no importable prefab, out of scope for v1)"
                    : "",
                agentCensus = agentCensus ?? new List<string>(),
                povCameraAspect = povCam.aspect,
                targetAspect = CaptureWidth / (float)CaptureHeight,
                preRollDurationSec = preRollDurationSec,
                robotSpeedAtTrigger = robotSpeedAtTrigger,
                triggerTimedOut = triggerTimedOut,
                velocitySource = robotBodySource,
                triggerSpeedResampled = triggerSpeedResampled,
                resolvedCamHeightWorldY = resolvedCamHeightWorldY,
                camHeightRaycastHit = camHeightRaycastHit,
                resolvedCamPitchDeg = config.camera.fixedPitchDeg,
                resolvedCamHfovDeg = config.camera.camHfovDeg,
                resolvedCamVfovDeg = resolvedCamVfovDeg,
            };

            string json = JsonUtility.ToJson(meta, true);
            File.WriteAllText(Path.Combine(config.outDir, "meta.json"), json);
        }

        private static string TryReadGitSha()
        {
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                string headPath = Path.Combine(projectRoot, ".git", "HEAD");
                if (!File.Exists(headPath))
                {
                    return "unknown";
                }
                string head = File.ReadAllText(headPath).Trim();
                if (head.StartsWith("ref: "))
                {
                    string refPath = Path.Combine(projectRoot, ".git", head.Substring("ref: ".Length));
                    if (File.Exists(refPath))
                    {
                        return File.ReadAllText(refPath).Trim();
                    }
                    return "unknown";
                }
                return head;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
