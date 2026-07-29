using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SEAN.AutoTrial
{
    /// <summary>
    /// CLI-driven trial entry point. Zero effect on normal Editor use: if neither -trialConfig
    /// nor TRIAL_CONFIG is present, Init() returns immediately and nothing else in this file runs.
    ///
    /// New-files-only constraint: this spawns pedestrians and builds cameras entirely at runtime
    /// (AddComponent/Instantiate/new GameObject), and drives the robot goal through Tasks.Base's
    /// existing public API (robotGoalTransform) -- Base.cs, SFAgent.cs, scenes and prefabs are
    /// read, never written.
    /// </summary>
    public class AutoTrialBootstrap : MonoBehaviour
    {
        // Zone B: preset "special character" containers, hardcoded per PRE-FLIGHT CHECK B
        // (2026-07-15) -- all 8 confirmed present under Assets/Resources/Prefabs. Paths are
        // Resources-relative and copied verbatim, including the PedetrainAvatars folder nesting
        // for phone_user (not a typo -- the other 7 live one level up).
        private static readonly Dictionary<string, string> ZoneBContainers = new Dictionary<string, string>
        {
            { "cyclist", "Prefabs/CyclistContainer" },
            { "dog_walker", "Prefabs/DogWalkerContainer" },
            { "female_child", "Prefabs/FemaleChildContainer" },
            { "male_child", "Prefabs/MaleChildContainer" },
            // Fixed Session 21 STEP 1: canonical avatar switched from Phone_User.prefab to
            // PedetrainAvatars/PhoneUser_Ped.prefab (avatars[0]) + PhoneUser_TextingController
            // (animationController), via PrefabUtility/SerializedObject through the guarded
            // launcher -- verified by a real trial (walking gait + texting upper body, all
            // gates green). The abandoned "PhoneUserContainer 1.prefab" scratch duplicate was
            // moved to Assets/ArchivedPrefabs/ (kept, not deleted). Resources path unchanged.
            { "phone_user", "Prefabs/PedetrainAvatars/PhoneUserContainer" },
            { "scooter_user", "Prefabs/ScooterUserContainer" },
            // Canonical wheelchair per pre-flight ruling (2026-07-15): wraps
            // Rocketbox/Wheelchair_Female.prefab -- female avatar only. The wheelchair-male
            // package has no .prefab at all (raw FBX/controller only) and is out of scope for v1.
            { "wheelchair_user", "Prefabs/WheelChairUserContainer" },
            { "white_cane_user", "Prefabs/WhiteCaneUserContainer" },
        };

        private const float SeanWaitTimeoutSec = 30f;
        private const float TaskWaitTimeoutSec = 10f;
        private const float TaskRunningWaitTimeoutSec = 10f;

        // Session 3 (2026-07-16) replaces the fixed settle buffer with observable readiness
        // gates. Root cause per that session's audit: move_base's oscillation_timeout is 1.0s
        // (params confirmed live-correct, matching the July-3 fix) -- a genuinely tight budget
        // that a fixed timer can't reliably clear, since it doesn't know whether the robot has
        // actually finished settling or whether the perception round-trip (scan -> costmap) has
        // caught up after this specific Unity instance connected. Gating on observed state
        // instead of a guessed delay is what turns "usually works" into "always works."
        private const float GateTimeoutSec = 20f;
        // See the "settled" comment in WaitForReadinessGates. Session 29 STEP 1: this bound is a
        // TEST-HARNESS sanity gate, not a real navigation cap -- it was calibrated at 2x the
        // then-only max_vel_x (0.6 -> 1.2), "ruling out only a genuine spike/instability, not
        // ordinary driving." Found live, not assumed: screening TEB max_vel_x=1.0 hit this bound
        // directly (readings jumping 0.000/1.649, never settling, 20s gate timeout, 2/2 runs
        // failed identically before the cause was traced here) -- 1.2 stopped being "clearly a
        // spike" once ordinary cruise could itself approach it. Raised to preserve the SAME 2x
        // margin against the highest speed this session actually screens (1.2 -> 2.4), not
        // loosened arbitrarily. If a future session lands a higher max_vel_x, re-derive this the
        // same way (2x the new landed value), don't just bump it further on faith.
        private const float SpeedSaneUpperBound = 2.4f;
        private const float SpeedStableSustainSec = 2f;

        private bool gatesFailed;
        private bool scanReceived;
        private bool costmapReceived;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            string configPath = GetConfigPath();
            if (configPath == null)
            {
                return;
            }

            // Protects the --windowed fallback (Step 5 frame-sanity check in run_trial.py) from
            // losing render/simulation ticks if the batch host doesn't keep the window focused.
            Application.runInBackground = true;

            var go = new GameObject("AutoTrialBootstrap");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var bootstrap = go.AddComponent<AutoTrialBootstrap>();
            bootstrap.StartCoroutine(bootstrap.Run(configPath));
        }

        private static string GetConfigPath()
        {
            // Fully qualified: SEAN.Environment (the scene-environment namespace) shadows
            // System.Environment from inside namespace SEAN.AutoTrial.
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-trialConfig")
                {
                    return args[i + 1];
                }
            }
            return System.Environment.GetEnvironmentVariable("TRIAL_CONFIG");
        }

        private IEnumerator Run(string configPath)
        {
            // Session 14 (SLATE v2): reference point for TrialController's preRollDurationSec
            // logging (bootstrap start -> the distance-trigger firing, see PollForTrigger there).
            float bootstrapStartTime = Time.time;

            AutoTrialConfig config = LoadConfig(configPath);
            if (config == null)
            {
                yield break;
            }

            // Session 10 (D1/D3): must run synchronously, before this coroutine's first yield --
            // RuntimeInitializeOnLoadMethod(AfterSceneLoad) (which is what got us here, see Init()
            // above) fires after the scene's own Awake()/OnEnable() calls but before its Start()
            // calls (confirmed by this exact ordering already being load-bearing for the
            // SEAN.instance wait below, which routinely finds SEAN's own Start() not yet run).
            // Disabling these components here, before any yield, means their Start() methods --
            // which is where the actual harm happens (PlanVisualizer creates + enables its line
            // renderer; ConfigurableSpawner/PedestrianSpawner spawns agents) -- never fire at all,
            // rather than firing-then-being-cleaned-up. See REPORT.md Session 10 for the empirical
            // verification of this timing assumption (a compile-check-style probe run that logs
            // whether Start() ran on any of these after the disable pass).
            DisableSceneHygieneHazards();

            if (!ResolveAppearance(config.appearance, out GameObject appearancePrefab, out string zone, out string resourcePath))
            {
                Fail("Unknown --appearance '" + config.appearance + "'. Zone B (preset) options: "
                    + string.Join(", ", ZoneBContainers.Keys) + ". Zone A (generic Rocketbox pedestrian) "
                    + "options are any Rocketbox prefab name in snake_case, e.g. 'business_male_01' "
                    + "resolves to Resources/Prefabs/Rocketbox/Business_Male_01.");
                yield break;
            }

            if (!Enum.TryParse(config.personality, true, out Scenario.Agents.PedestrianModulator.PersonalityType personalityType))
            {
                Fail("Unknown --personality '" + config.personality + "'. Valid values: Scared, Curious, Surprised, Indifferent, Assertive.");
                yield break;
            }

            float waitStart = Time.time;
            while (SEAN.instance == null)
            {
                if (Time.time - waitStart > SeanWaitTimeoutSec)
                {
                    Fail("Timed out after " + SeanWaitTimeoutSec + "s waiting for SEAN.instance. Is the correct scene open in the Editor?");
                    yield break;
                }
                yield return null;
            }
            SEAN sean = SEAN.instance;

            Scenario.Robot robot;
            try
            {
                robot = sean.robot;
            }
            catch (Exception e)
            {
                Fail("Could not resolve SEAN.instance.robot: " + e.Message);
                yield break;
            }
            Transform pedestrianTransform = SpawnPedestrian(config, zone, appearancePrefab, personalityType,
                out IVI.INavigable pedestrianNavAgent, out Vector3 pedestrianReleaseDest);

            // Session 41 TASK 5: narrow-corridor walls. Built here, after the pedestrian exists,
            // so the corridor can be centred on the ENCOUNTER rather than on the robot -- the
            // midpoint of robot and pedestrian along the robot's own start->goal bearing is where
            // the head-on pass actually happens. See S41CorridorBuilder's class doc for why this
            // is runtime geometry in the existing Outdoor scene instead of the new scene file the
            // ticket named (navigation is map-bound to that scene via ROS, cross-repo).
            if (config.hasCorridor)
            {
                var corridorHost = new GameObject("S41CorridorHost");
                var builder = corridorHost.AddComponent<S41CorridorBuilder>();
                builder.widthMeters = config.corridorWidthMeters;
                builder.lengthMeters = config.corridorLengthMeters;
                builder.pedestrian = pedestrianTransform;
                // Build once the pair is within the corridor's own length, so the walls appear
                // around the encounter rather than around wherever the robot happened to start.
                builder.buildWhenDistanceBelow = config.corridorLengthMeters;
            }

            // Session 35 BLOCK 4 (FIX 8/9): dyad/ped-count-3 extra pedestrians, spawned via the
            // exact same SpawnPedestrian() path as the primary one above -- see SpawnExtraPedestrian's
            // own doc comment for why a throwaway sub-config is the least invasive way to reuse it.
            List<Transform> extraPedestrianTransforms = new List<Transform>();
            List<IVI.INavigable> extraPedestrianNavAgents = new List<IVI.INavigable>();
            List<Vector3> extraPedestrianReleaseDests = new List<Vector3>();
            if (config.hasPedestrian2)
            {
                Transform t2 = SpawnExtraPedestrian(config, config.pedestrian2Appearance, config.pedestrian2Personality,
                    config.pedestrian2SpawnPose, config.hasPedestrian2GoalPose, config.pedestrian2GoalPose,
                    out IVI.INavigable nav2, out Vector3 dest2);
                if (t2 != null)
                {
                    extraPedestrianTransforms.Add(t2);
                    extraPedestrianNavAgents.Add(nav2);
                    extraPedestrianReleaseDests.Add(dest2);
                }
            }
            if (config.hasPedestrian3)
            {
                Transform t3 = SpawnExtraPedestrian(config, config.pedestrian3Appearance, config.pedestrian3Personality,
                    config.pedestrian3SpawnPose, config.hasPedestrian3GoalPose, config.pedestrian3GoalPose,
                    out IVI.INavigable nav3, out Vector3 dest3);
                if (t3 != null)
                {
                    extraPedestrianTransforms.Add(t3);
                    extraPedestrianNavAgents.Add(nav3);
                    extraPedestrianReleaseDests.Add(dest3);
                }
            }

            // Session 10 (D3, belt-and-suspenders half): the disable pass above should mean no
            // stray spawner ever fired, but assert it rather than assume it -- census every
            // INavigable in the scene, destroy anything that isn't the one pedestrian AutoTrial
            // itself just spawned, and record the full census (including what was destroyed) into
            // meta.json so a failed assertion is visible in the trial output, not silent.
            List<string> agentCensus = CensusAndDestroyStrayAgents(pedestrianTransform, extraPedestrianTransforms);

            // Session 38 FIX 1: universal robot-side lateral-evasion backstop, always active,
            // every config (Session 34/37 both found the robot's own clearance alone -- with zero
            // pedestrian contribution -- is not reliably above the 0.36m physical floor). See
            // S38RobotLateralEvasionBackstop's own class doc for the full mechanism/rationale.
            var robotBackstop = sean.robot.gameObject.GetComponent<S38RobotLateralEvasionBackstop>();
            if (robotBackstop == null)
            {
                robotBackstop = sean.robot.gameObject.AddComponent<S38RobotLateralEvasionBackstop>();
            }
            robotBackstop.RegisterPedestrian(pedestrianTransform);
            foreach (Transform extra in extraPedestrianTransforms)
            {
                robotBackstop.RegisterPedestrian(extra);
            }

            // Loop 1 Bug 4 (ATTEMPTED, NOT WIRED): S41PredictiveLateralAvoidance.cs (kept in the
            // repo as documented, dormant infrastructure -- see its own class doc for the full
            // mechanism) extrapolates robot/pedestrian velocity forward and nudges early/gently
            // when a future close pass is predicted, instead of only reacting once already close.
            // Four parameter iterations were tried against white_cane_user this session (the
            // config this was built for) and none reliably beat the existing reactive-only
            // backstop above: a same-session, apples-to-apples A/B (identical code/ROS session,
            // only this component toggled) measured worst-of-5 min_dist 0.304m WITHOUT this
            // component vs 0.275m WITH it (best-tuned iteration) -- no improvement, and by this
            // specific comparison mildly worse. Diagnosis (see the class doc's own comments,
            // updated live during iteration): the robot's own TEB path-return erases most of a
            // gentle, early push faster than this component can accumulate real separation --
            // widening lead time/speed across iterations didn't change that qualitative outcome.
            // NOT instantiated/wired here as a result -- left fully inert so it cannot affect any
            // config's behavior. See REPORT.md Loop 1 Session and HOWARD_HANDOFF.md for the full
            // iteration table and the standing recommendation this reinforces (white_cane needs
            // something beyond this project's runtime-script toolkit, not another tuning pass).

            Tasks.Base activeTask = null;
            waitStart = Time.time;
            while (Time.time - waitStart < TaskWaitTimeoutSec)
            {
                activeTask = sean.robotTask;
                if (activeTask != null)
                {
                    break;
                }
                yield return null;
            }
            if (activeTask == null)
            {
                Fail("Timed out after " + TaskWaitTimeoutSec + "s waiting for an active Tasks.Base instance (SEAN.instance.robotTask).");
                yield break;
            }

            if (config.hasGoalPose)
            {
                // Wait for the task's own first automatic task-cycle (Tasks.Base.Update()'s
                // ~3s debounceStartup window) to finish claiming robotGoal before we override it
                // -- otherwise that first cycle clobbers our value the moment it fires.
                waitStart = Time.time;
                while (!activeTask.isRunning && Time.time - waitStart < TaskRunningWaitTimeoutSec)
                {
                    yield return null;
                }

                gatesFailed = false;
                yield return StartCoroutine(WaitForReadinessGates(robot));
                if (gatesFailed)
                {
                    yield break;
                }

                // Session 14 (SLATE v2): restores Session 12/Round-4's original timing -- publish
                // the goal override HERE, right after the readiness gate, well before any capture
                // starts. Session 13 deferred this to the capture-start instant and that measurably
                // regressed spin (see REPORT.md Session 13 Step 3); v2's whole point is to let the
                // robot reach a normal, settled cruise on its real goal long before t=0 -- t=0 is
                // now defined by TrialController's distance trigger instead (PollForTrigger),
                // decoupled entirely from when the goal gets published.
                var goalHolder = new GameObject("AutoTrialGoalPoseHolder");
                goalHolder.transform.position = config.goalPose.Position;
                goalHolder.transform.rotation = config.goalPose.Rotation;
                activeTask.robotGoalTransform = goalHolder.transform;
                UnityEngine.Object.Destroy(goalHolder);

                // Observed in practice (2026-07-16 probe runs): Outdoor.unity's active task
                // (CustomStartGoal) has publishInterval=60s -- far longer than most trial
                // durations, so relying solely on Tasks.Base.Update()'s own periodic Publish()
                // left the override unsent for the whole trial. Publish immediately instead, via
                // reflection since Publish(GameObject) is protected (robotGoal itself is a public
                // getter, so only the method call needs reflection). The periodic loop still runs
                // afterward as a harmless repeat of the same value.
                bool publishedImmediately = TryPublishNowBestEffort(activeTask);
                Debug.Log("[AutoTrial] goal set on active task '" + activeTask.name + "'; "
                    + (publishedImmediately ? "published immediately (reflection)" : "immediate publish failed, relying on periodic loop only")
                    + " to reach /move_base_simple/goal.");
                LogPublishIntervalBestEffort(activeTask);
            }

            // Scenario.Robot.Start() (which resolves/validates camera_first) is not guaranteed
            // to have run yet relative to this RuntimeInitializeOnLoadMethod coroutine -- wait
            // for it rather than assuming ordering.
            waitStart = Time.time;
            while (robot.camera_first == null && Time.time - waitStart < SeanWaitTimeoutSec)
            {
                yield return null;
            }
            if (robot.camera_first == null)
            {
                Fail("Timed out waiting for robot.camera_first to be resolved (Scenario.Robot.Start() may not have run yet).");
                yield break;
            }

            Camera povCam = BuildPovCamera(robot, config, out float resolvedCamHeightWorldY, out bool camHeightRaycastHit,
                out float resolvedCamVfovDeg);

            var controllerGO = new GameObject("AutoTrialController");
            var controller = controllerGO.AddComponent<TrialController>();
            controller.Initialize(config, robot, povCam, pedestrianTransform, zone, resourcePath, agentCensus,
                pedestrianNavAgent, pedestrianReleaseDest, bootstrapStartTime, config.triggerDistanceMeters,
                resolvedCamHeightWorldY, camHeightRaycastHit, resolvedCamVfovDeg,
                extraPedestrianTransforms, extraPedestrianNavAgents, extraPedestrianReleaseDests);
        }

        /// <summary>
        /// Session 10 (D1/D3): disables every scene component known to inject something into the
        /// POV capture that AutoTrial doesn't want -- the ROS global-plan line renderer (D1) and
        /// every pedestrian scenario/spawner component (D3) -- before their own Start() can fire.
        /// Runtime-only: nothing here is a scene/prefab edit, every change is undone implicitly by
        /// the next Editor domain reload (these are runtime-instantiated/-toggled component
        /// states, never serialized back to disk -- unlike the ROSConnectionPrefab/Outdoor.unity
        /// dirtying bug Sessions 1/2 hit and fixed via run_trial.py's snapshot/revert guard, which
        /// is unrelated to this and still the backstop if anything here ever misbehaves).
        /// </summary>
        private void DisableSceneHygieneHazards()
        {
            // Bare "Display...." (not "SEAN.Display...."): class SEAN (Assets/Scripts/SEAN/SEAN.cs)
            // shares its simple name with the root namespace SEAN, exactly the same shadowing
            // gotcha GetConfigPath()'s comment already documents for SEAN.Environment vs
            // System.Environment -- "SEAN.Display" would resolve the class first and then fail to
            // find a nested type "Display" on it. Unqualified "Display"/"Scenario" (used
            // elsewhere in this file) resolve correctly via the enclosing SEAN.AutoTrial ->
            // SEAN namespace search.
            int planVizCount = 0;
            foreach (var pv in UnityEngine.Object.FindObjectsOfType<Display.PlanVisualizer>(true))
            {
                pv.enabled = false;
                var lsb = pv.GetComponent<Display.VolumetricLine.VolumetricLineStripBehavior>();
                if (lsb != null)
                {
                    lsb.enabled = false;
                }
                planVizCount++;
            }

            int scenarioCount = 0;
            foreach (var b in UnityEngine.Object.FindObjectsOfType<Scenario.PedestrianBehavior.Base>(true))
            {
                b.enabled = false;
                scenarioCount++;
            }

            int spawnerCount = 0;
            foreach (var sp in UnityEngine.Object.FindObjectsOfType<Scenario.Agents.PedestrianSpawner>(true))
            {
                sp.enabled = false;
                spawnerCount++;
            }

            Debug.Log("[AutoTrial] scene hygiene: disabled " + planVizCount + " PlanVisualizer(s), "
                + scenarioCount + " PedestrianBehavior scenario component(s), " + spawnerCount
                + " PedestrianSpawner(s) before their Start() could run.");
        }

        /// <summary>
        /// Session 10 (D3): finds every IVI.INavigable in the scene (the interface every
        /// pedestrian agent class implements, per Agents.Base/RandomABNavAgentManager/Handcrafted
        /// -- confirmed by read-only recon, none of those files edited) and destroys every one
        /// except the Transform AutoTrial's own SpawnPedestrian() just created and returned.
        /// Robot is never at risk: nothing robot-side implements INavigable (confirmed by recon --
        /// only the three pedestrian-agent files above do). Returns a human-readable census for
        /// meta.json, including anything that had to be destroyed (which should be empty if the
        /// disable pass above worked as intended -- surfaced, not hidden, either way).
        /// </summary>
        private List<string> CensusAndDestroyStrayAgents(Transform ownPedestrian, List<Transform> ownExtraPedestrians = null)
        {
            var census = new List<string>();
            var strays = new List<GameObject>();
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
            {
                if (!(mb is IVI.INavigable nav))
                {
                    continue;
                }
                // Session 35 BLOCK 4: multi-pedestrian scenarios (dyad, ped-count-3) spawn extra
                // pedestrians via this same SpawnPedestrian() path -- they must NOT be destroyed
                // as strays. ownExtraPedestrians is empty/null for every single-pedestrian trial
                // (the overwhelming majority), so this is a pure no-op addition for that case.
                bool isOwn = nav.transform == ownPedestrian
                    || (ownExtraPedestrians != null && ownExtraPedestrians.Contains(nav.transform));
                census.Add((isOwn ? "AutoTrial: " : "STRAY (destroyed): ") + nav.transform.name
                    + " [" + mb.GetType().FullName + "]");
                if (!isOwn)
                {
                    strays.Add(nav.transform.gameObject);
                }
            }
            foreach (var go in strays)
            {
                UnityEngine.Object.Destroy(go);
            }
            if (strays.Count > 0)
            {
                Debug.LogWarning("[AutoTrial] census: destroyed " + strays.Count + " stray pedestrian agent(s) "
                    + "that survived the pre-Start disable pass -- see meta.json agentCensus for detail.");
            }
            else
            {
                Debug.Log("[AutoTrial] census: exactly one pedestrian agent present (AutoTrial's own) -- as expected.");
            }
            return census;
        }

        /// <summary>
        /// Blocks the goal override/publish until the robot's own motion is in a sane, controlled
        /// state -- replaces a fixed delay (Session 2) that could fire too early, colliding with
        /// VelocityController's post-teleport settle window (Session 2's root-cause finding).
        ///
        /// Session 3 note: this was originally also gated on receiving at least one /scan and one
        /// /move_base/local_costmap/costmap message via ROSConnection.Subscribe, to confirm this
        /// specific Unity instance's sensor round-trip through ROS. Empirically (2026-07-16),
        /// those two subscriptions never registered on the ROS side at all -- confirmed via
        /// `rosnode info /tcp_server` showing no new entries for either topic after the Subscribe
        /// calls ran, with no exception thrown on the Unity side either. Root cause not resolved
        /// within this session's timebox; left subscribed in a best-effort, non-blocking way
        /// (logged if they ever do fire) rather than block every trial on a gate that cannot
        /// currently pass. See REPORT.md Session 3 for the full account -- worth a fresh look with
        /// Editor-attached debugging rather than batchmode log archaeology.
        /// </summary>
        private IEnumerator WaitForReadinessGates(Scenario.Robot robot)
        {
            scanReceived = false;
            costmapReceived = false;
            ROSConnection.instance.Subscribe<RosMessageTypes.Sensor.MLaserScan>("/scan", _ => scanReceived = true);
            ROSConnection.instance.Subscribe<RosMessageTypes.Nav.MOccupancyGrid>("/move_base/local_costmap/costmap", _ => costmapReceived = true);

            float waitStart = Time.time;
            Vector3 lastPos = robot.transform.position;
            float lastCheckTime = Time.time;
            float stableSince = -1f;
            float nextStatusLog = waitStart;

            while (Time.time - waitStart < GateTimeoutSec)
            {
                float dt = Time.time - lastCheckTime;
                float speed = 0f;
                if (dt > 0.01f)
                {
                    speed = Vector3.Distance(robot.transform.position, lastPos) / dt;
                    lastPos = robot.transform.position;
                    lastCheckTime = Time.time;
                    // "Settled" here means driving in a bounded, sane speed range rather than
                    // literally motionless -- CustomStartGoal auto-publishes its own default goal
                    // before this gate ever runs (its Publish() call happens inside StartNewTask(),
                    // before OnNewTask() flips isRunning true), so by the time we get here the
                    // robot is usually already actively driving toward that default goal. A
                    // "near-zero speed sustained 2s" gate is unsatisfiable once that's true --
                    // confirmed empirically (2026-07-16 Session 3 census trial 1: this exact gate
                    // timed out at 20s with the robot cruising a steady ~0.6 m/s the whole time).
                    // What we actually want to rule out is the single-frame teleport artifact and
                    // any wild instability, not ordinary cruising -- max_vel_x is 0.6, so anything
                    // up to 2x that with no upper spike is a normal, controlled driving state.
                    if (speed <= SpeedSaneUpperBound)
                    {
                        if (stableSince < 0f)
                        {
                            stableSince = Time.time;
                        }
                    }
                    else
                    {
                        stableSince = -1f;
                    }
                }

                bool speedGateOk = stableSince >= 0f && Time.time - stableSince >= SpeedStableSustainSec;
                if (speedGateOk)
                {
                    Debug.Log("[AutoTrial] readiness gate satisfied (speed sane " + SpeedStableSustainSec
                        + "s) after " + (Time.time - waitStart).ToString("F1") + "s. scanReceived="
                        + scanReceived + " costmapReceived=" + costmapReceived + " (best-effort, not gating).");
                    yield break;
                }

                if (Time.time >= nextStatusLog)
                {
                    Debug.Log(string.Format("[AutoTrial] gate status t={0:F1} speed={1:F3} speedGateOk={2} scanReceived={3} costmapReceived={4}",
                        Time.time - waitStart, speed, speedGateOk, scanReceived, costmapReceived));
                    nextStatusLog += 2f;
                }
                yield return null;
            }

            gatesFailed = true;
            Fail("Timed out after " + GateTimeoutSec + "s waiting for readiness gate: speed-sane (last speed reading did not stay in a controlled range).");
        }

        private AutoTrialConfig LoadConfig(string path)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e)
            {
                Fail("Could not read trial config at '" + path + "': " + e.Message);
                return null;
            }

            AutoTrialConfig config;
            try
            {
                config = JsonUtility.FromJson<AutoTrialConfig>(text);
            }
            catch (Exception e)
            {
                Fail("Could not parse trial config JSON at '" + path + "': " + e.Message);
                return null;
            }

            if (config == null || string.IsNullOrEmpty(config.appearance) || string.IsNullOrEmpty(config.outDir))
            {
                Fail("Trial config at '" + path + "' is missing required fields (appearance/outDir).");
                return null;
            }
            return config;
        }

        private bool ResolveAppearance(string appearance, out GameObject prefab, out string zone, out string resourcePath)
        {
            if (ZoneBContainers.TryGetValue(appearance, out resourcePath))
            {
                prefab = Resources.Load<GameObject>(resourcePath);
                zone = "B";
                return prefab != null;
            }

            // Zone A: convention-based Rocketbox resolution, e.g. "business_male_01" ->
            // "Business_Male_01" -> Resources/Prefabs/Rocketbox/Business_Male_01.prefab. Covers
            // ~140 generic personality-capable pedestrians without hardcoding each one.
            string rocketboxName = string.Join("_", appearance.Split('_').Select(CapitalizeSegment));
            resourcePath = "Prefabs/Rocketbox/" + rocketboxName;
            prefab = Resources.Load<GameObject>(resourcePath);
            zone = "A";
            return prefab != null;
        }

        private static string CapitalizeSegment(string s)
        {
            return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>
        /// Session 13 (THE SLATE FIX): the pedestrian used to start walking toward its real
        /// destination the instant it spawned, here -- meters before capture ever started (~5-6s
        /// of pipeline setup latency later, per Session 12's REPORT.md root-cause). Now InitDest
        /// is always called with spawnPos itself (net-zero displacement -- the pedestrian stands
        /// still, facing the robot, exactly where placed) and the REAL destination is only
        /// returned via releaseDest for TrialController to hand back to InitDest at the slate
        /// moment (its first captured frame). navAgentOut is the same INavigable reference that
        /// call needs.
        /// </summary>
        // Session 17 (Step 2): --ped-distance's new 25.0 default (+ --slate-margin) pushes the
        // pedestrian's frozen spawn point to ~29m from ROBOT_START -- well within the ~43.6m
        // corridor along the bearing, but never geometrically verified before this session (every
        // prior default sat close enough to the corridor's own well-trodden centerline that this
        // was never in question). Checked, not assumed: NavMesh.SamplePosition within this
        // tolerance of the exact resolved spawn point, loud Fail() with the offending coordinates
        // if it misses -- refuses to start a trial that would spawn the pedestrian off-mesh or
        // inside geometry rather than silently placing it there anyway.
        private const float SpawnNavMeshToleranceMeters = 1.0f;

        private void ValidateSpawnOnNavMesh(Vector3 pos)
        {
            bool onMesh = UnityEngine.AI.NavMesh.SamplePosition(pos, out UnityEngine.AI.NavMeshHit hit,
                SpawnNavMeshToleranceMeters, UnityEngine.AI.NavMesh.AllAreas);
            if (!onMesh)
            {
                Fail("Pedestrian spawn point " + pos.ToString("F3") + " is not within "
                    + SpawnNavMeshToleranceMeters.ToString("F1") + "m of the NavMesh -- refusing to "
                    + "start a trial that would spawn the pedestrian off-mesh or inside geometry. "
                    + "Check --ped-distance/--slate-margin (or --spawn if given explicitly) against "
                    + "the scene's actual navigable corridor.");
                return;
            }
            float missDist = Vector3.Distance(pos, hit.position);
            Debug.Log("[AutoTrial] spawn NavMesh check: requested " + pos.ToString("F3") + ", nearest "
                + "NavMesh point " + hit.position.ToString("F3") + " (" + missDist.ToString("F3") + "m away) -- OK.");
        }

        /// <summary>
        /// Session 35 BLOCK 4 (FIX 8/9): spawns a SECOND/THIRD pedestrian (dyad, ped-count-3) by
        /// reusing the exact same ResolveAppearance()/SpawnPedestrian() path the primary
        /// pedestrian goes through -- not a parallel/duplicated spawn mechanism. Builds a
        /// throwaway `AutoTrialConfig` substituting only the fields SpawnPedestrian() actually
        /// reads (appearance/personality/spawnPose/pedGoalPose/outDir) rather than adding a
        /// second overload of SpawnPedestrian itself, since JsonUtility-style config objects in
        /// this codebase are plain data containers with no established cloning convention.
        /// Speed multiplier is left at the default 1.0 (extra pedestrians in dyad/ped-count-3 are
        /// plain walkers, not appearance-speed-tiered Zone B containers) -- can be revisited if a
        /// future session needs otherwise. Returns null (logs a warning, does not fail the whole
        /// trial) if the appearance string doesn't resolve, rather than throwing -- a malformed
        /// extra-pedestrian request shouldn't take down an otherwise-valid primary trial.
        /// </summary>
        private Transform SpawnExtraPedestrian(AutoTrialConfig primaryConfig, string appearance, string personality,
            PoseXYZYaw spawnPose, bool hasGoalPose, PoseXYZYaw goalPose,
            out IVI.INavigable extraNavAgentOut, out Vector3 extraReleaseDestOut)
        {
            extraNavAgentOut = null;
            extraReleaseDestOut = Vector3.zero;
            if (!ResolveAppearance(appearance, out GameObject prefab, out string zone, out string resourcePath))
            {
                Debug.LogWarning("[AutoTrial] extra pedestrian: unknown appearance '" + appearance + "' -- skipping this pedestrian, primary trial continues.");
                return null;
            }
            if (!Enum.TryParse(personality, true, out Scenario.Agents.PedestrianModulator.PersonalityType personalityType))
            {
                Debug.LogWarning("[AutoTrial] extra pedestrian: unknown personality '" + personality + "' -- defaulting to Indifferent.");
                personalityType = Scenario.Agents.PedestrianModulator.PersonalityType.Indifferent;
            }

            var subConfig = new AutoTrialConfig
            {
                appearance = appearance,
                personality = personality,
                spawnPose = spawnPose,
                hasPedGoalPose = hasGoalPose,
                pedGoalPose = goalPose,
                outDir = primaryConfig.outDir,
                pedSpeedMultiplier = 1.0f,
            };
            ValidateSpawnOnNavMesh(spawnPose.Position);
            // SpawnPedestrian() already calls InitDest(spawnPos) internally in both the Zone A and
            // Zone B branches (Session 13's freeze-at-spawn convention) -- no extra call needed
            // here, the extra pedestrian freezes at spawn exactly like the primary one.
            Transform t = SpawnPedestrian(subConfig, zone, prefab, personalityType,
                out extraNavAgentOut, out extraReleaseDestOut);
            return t;
        }


        /// <summary>
        /// Session 59. Freezes or releases a pedestrian's root-motion TRANSLATION, the other half of
        /// the SLATE freeze. Safe on agents with no modulator: those are the pedSpeedMultiplier==1.0
        /// case, which is directVelocityDrive and therefore takes no translation from root motion.
        /// </summary>
        private static void FreezeRootMotionTranslation(IVI.INavigable navAgent, bool frozen)
        {
            if (navAgent == null) { return; }
            var mod = navAgent.transform.GetComponent<Scenario.Agents.PedestrianModulator>();
            if (mod != null)
            {
                mod.rootMotionTranslationFrozen = frozen;
                Debug.Log("[S59Freeze] root-motion translation " + (frozen ? "FROZEN" : "released")
                    + " on '" + navAgent.transform.name + "'");
            }
        }

        private Transform SpawnPedestrian(AutoTrialConfig config, string zone, GameObject prefab, Scenario.Agents.PedestrianModulator.PersonalityType personalityType,
            out IVI.INavigable navAgentOut, out Vector3 releaseDest)
        {
            Vector3 spawnPos = config.spawnPose.Position;
            Quaternion spawnRot = config.spawnPose.Rotation;
            ValidateSpawnOnNavMesh(spawnPos);
            bool patrolValid = config.patrolWaypoints != null && config.patrolWaypoints.Length >= 2;
            if (config.patrolWaypoints != null && config.patrolWaypoints.Length > 2)
            {
                Debug.LogWarning("[AutoTrial] patrolWaypoints has " + config.patrolWaypoints.Length
                    + " points; PedestrianModulator.EnablePatrol only supports 2 (ping-pong) -- using the first two.");
            }

            GameObject instance;
            if (zone == "B")
            {
                instance = Instantiate(prefab);
                instance.name = "AutoTrialPedestrian_" + config.appearance;

                bool personalityRequested = !string.Equals(config.personality, "Indifferent", StringComparison.OrdinalIgnoreCase);
                if (personalityRequested || patrolValid)
                {
                    Debug.LogWarning("[AutoTrial] Zone B preset '" + config.appearance
                        + "' locks its own behavior -- ignoring requested personality='" + config.personality + "'"
                        + (patrolValid ? " and patrolWaypoints" : "") + ".");
                }

                IVI.INavigable navAgent = instance.GetComponentInChildren<IVI.INavigable>();
                navAgent.transform.position = spawnPos;
                navAgent.transform.rotation = spawnRot;
                // Session 10 (D4): dest defaults to spawnPos (net-zero displacement, the pre-
                // existing bug) unless a pedestrian goal was explicitly given. hasPedGoalPose is
                // orthogonal to the Zone B personality/patrol lock above -- InitDest() is the raw
                // INavigable API, not something PedestrianModulator gates.
                // Session 13: freeze at spawn (InitDest(spawnPos) unconditionally); the real
                // destination is only released at the slate moment, via releaseDest below.
                navAgent.InitDest(spawnPos);
                // Session 59: pinning destPos freezes Base.Move(), but Base.Move() is not what
                // translates a root-motion agent -- PedestrianModulator.ApplyAnimatorRootMotion()
                // is, and it ran through the whole freeze. Complete the freeze there too. Cleared
                // by TrialController at the SLATE release, paired with InitDest(releaseDest).
                FreezeRootMotionTranslation(navAgent, true);
                // Session 21 STEP 3: white_cane_user origin-reset fix. Root cause characterized
                // (not patched, both source files off-limits) via S21TransformWatcher -- destPos
                // transiently zeroes as a by-design side effect of the SLATE freeze's own
                // CloseEnough()/StopNavigation() cycle, and white_cane_user's nested-Animator
                // root-motion path (unlike every other current Zone B container) applies
                // animator.deltaPosition unconditionally, un-gated by the same destPos check
                // Base.Move() already has. Guarded here at the AutoTrial layer instead, applied
                // to all Zone B containers as a general-purpose safety net (cheap, and the
                // brief's own "possibly shared with the dormant patrol case" note means any
                // future nested-Animator container could hit the identical window).
                var positionGuardian = navAgent.transform.gameObject.AddComponent<S21PedestrianPositionGuardian>();
                positionGuardian.SetIntendedPosition(spawnPos);
                // Session 32 FIX E: locomotion animator playback rate scaled to actual movement
                // speed -- general fix, every Zone B appearance (see S32AnimatorSpeedScaler's own
                // class doc for why this wasn't already happening).
                navAgent.transform.gameObject.AddComponent<S32AnimatorSpeedScaler>();
                // Session 37 STEP 2: attach the (now TTC-based) reaction gate unconditionally to
                // every Zone B appearance too -- the N=5 safety census this session found
                // white_cane_user's own worst-case min_dist (0.321m) BELOW the 0.36m physical
                // floor under plain defaults, and this component was previously ONLY ever attached
                // inside the Zone A modulator-conditional block, gated further on an explicit
                // --ped-react-dist CLI flag -- meaning it was NEVER ACTIVE for any Zone B
                // appearance, or for plain Indifferent Zone A trials without that flag, regardless
                // of what distance/TTC value was configured. Zone B pedestrians' own
                // PedestrianModulator (added below, for speed-scaling) always runs
                // PersonalityType.Indifferent's Modulate() case (Scale() only, no robot-awareness
                // at all) -- there is no personality-specific reaction logic here the way
                // Scared/Surprised/Assertive have, so this generic gate is the only reaction
                // mechanism available for these appearances. See S34PedestrianReactDistGate's own
                // class doc for the TTC mechanism -- it only needs SFAgent (GetComponent, no
                // PedestrianModulator dependency), so attaching it here is independent of and
                // doesn't risk the STEP 0 modulator-attachment speed regression documented below.
                var reactGateB = navAgent.transform.gameObject.AddComponent<S34PedestrianReactDistGate>();
                reactGateB.personality = Scenario.Agents.PedestrianModulator.PersonalityType.Indifferent;
                reactGateB.reactDistanceMeters = 1.5f;
                // Session 34 FIX 4: force AlwaysAnimate so a reacting pedestrian's Animator never
                // silently stalls while out of camera frame -- see S34AnimatorCullingFix's own
                // class doc.
                navAgent.transform.gameObject.AddComponent<S34AnimatorCullingFix>();
                // Session 35 FIX 1/2: heading-alignment guardian -- this Zone B branch was
                // MISSING this wiring in an earlier pass this session (only the Zone A/"else"
                // branch below had it), which is exactly why wheelchair_user/white_cane_user/
                // scooter_user showed zero improvement in initial verification despite the
                // component existing and working correctly for Zone A appearances. Every Zone B
                // container needs both mechanisms (facing correction AND the position/lateral-
                // offset correction -- wheelchair_user in particular is directVelocityDrive, so
                // the facing correction alone is a no-op for it; see the component's own class
                // doc). Does not conflict with S21PedestrianPositionGuardian above (that one only
                // intervenes on an implausible multi-meter single-frame jump, this corrects a
                // small per-frame lateral offset -- different thresholds, same LateUpdate
                // ordering concerns don't apply since neither fights the other's normal range).
                {
                    var headingGuardianB = navAgent.transform.gameObject.AddComponent<S35HeadingAlignmentGuardian>();
                    headingGuardianB.personality = personalityType;
                    Vector3 destB = config.hasPedGoalPose ? config.pedGoalPose.Position : spawnPos;
                    Vector3 dB = destB - spawnPos;
                    if (new Vector2(dB.x, dB.z).magnitude > 1e-3f)
                    {
                        headingGuardianB.targetHeadingDeg = Mathf.Atan2(dB.x, dB.z) * Mathf.Rad2Deg;
                        headingGuardianB.hasTargetHeading = true;
                        headingGuardianB.lineStart = spawnPos;
                        headingGuardianB.lineEnd = destB;
                        headingGuardianB.hasLine = true;
                    }
                }
                if (config.appearance == "white_cane_user")
                {
                    // Diagnostic-only, kept for provenance: the per-frame transform watcher
                    // that originally bracketed this defect (see S21TransformWatcher.cs).
                    navAgent.transform.gameObject.AddComponent<S21TransformWatcher>();
                }
                // Session 29 STEP 2: same pedSpeedMultiplier hook Zone A already has (S28) --
                // walkSpeedMultiplier scales AFTER SFAgent.UpdateVelocity()'s own Parameters.
                // MAX_VEL clamp (Base.cs/SFAgent.cs off-limits, but this scaling happens outside
                // them, in PedestrianModulator.Scale(), so it can push the effective speed above
                // that shared cap without touching either file). Session 54: the cap is
                // Parameters.MAX_VEL = 0.95 (Parameters.cs:32) -- this comment said 0.6 until now,
                // a value Session 29 STEP 3 had already raised. An appearance with NO multiplier
                // (or one that is exactly 1.0) gets no modulator at all and is therefore held to
                // that 0.95. Zone B otherwise ignores
                // personality/patrol (see the warning above) -- a modulator forced here purely
                // for speed scaling doesn't reintroduce personality-driven reactive behavior,
                // since PersonalityType.Indifferent's own Modulate() case is scale-only.
                if (!Mathf.Approximately(config.pedSpeedMultiplier, 1.0f))
                {
                    var speedModulator = navAgent.transform.gameObject.GetComponent<Scenario.Agents.PedestrianModulator>();
                    if (speedModulator == null)
                    {
                        speedModulator = navAgent.transform.gameObject.AddComponent<Scenario.Agents.PedestrianModulator>();
                    }
                    speedModulator.walkSpeedMultiplier = config.pedSpeedMultiplier;
                }
                navAgentOut = navAgent;
                // Session 54: honour --ped-motion standing here too. Session 28 PART 3a added it
                // to the Zone A branch only, so the flag was silently a no-op for every Zone B
                // container -- measured on male_child/female_child, which have no walking animation
                // and are meant to be static obstacles: both were released to the far goal and
                // travelled the full 14.0 m. Same semantics as Zone A's zoneADest: the SLATE
                // trigger still fires (TrialController.PollForTrigger is release-destination
                // agnostic), the pedestrian simply never walks anywhere, and it remains a real
                // costmap obstacle because its position is real.
                releaseDest = config.pedMotion == "standing"
                    ? spawnPos
                    : (config.hasPedGoalPose ? config.pedGoalPose.Position : spawnPos);
                return navAgent.transform;
            }
            else
            {
                // No ready-made container exists for an arbitrary Rocketbox pick (unlike Zone B),
                // so build the minimal AppearanceAvatar wrapper at runtime instead of touching any
                // existing prefab. Left inactive until avatars[] is set so Awake() (which picks
                // and instantiates the avatar) runs with the right config the first time.
                instance = new GameObject("AutoTrialPedestrian_" + config.appearance);
                instance.SetActive(false);
                var appearanceAvatar = instance.AddComponent<Scenario.Agents.AppearanceAvatar>();
                appearanceAvatar.avatars = new GameObject[] { prefab };
                instance.SetActive(true);

                IVI.INavigable navAgent = instance.GetComponentInChildren<IVI.INavigable>();
                navAgent.transform.position = spawnPos;
                navAgent.transform.rotation = spawnRot;

                // Session 32 FIX E: locomotion animator playback rate scaled to actual movement
                // speed -- general fix, every Zone A pedestrian regardless of personality/modulator
                // presence (see S32AnimatorSpeedScaler's own class doc for why this wasn't already
                // happening).
                navAgent.gameObject.AddComponent<S32AnimatorSpeedScaler>();
                // Session 39 diagnostic: confirms/refutes the DirectVelocityDrive-skips-
                // Idling/Forward hypothesis. Env-var gated (AUTOTRIAL_S39_PROBE), no-op otherwise.
                navAgent.gameObject.AddComponent<S39LocomotionStateProbe>();
                // Session 34 FIX 4: force AlwaysAnimate so a reacting pedestrian's Animator never
                // silently stalls while out of camera frame -- see S34AnimatorCullingFix's own
                // class doc.
                navAgent.gameObject.AddComponent<S34AnimatorCullingFix>();
                // Session 35 FIX 1/2: heading-alignment guardian -- added unconditionally (this
                // transient reproduces even for business_male_01 x indifferent, which gets NO
                // PedestrianModulator at all per this codebase's own "Indifferent + no patrol =
                // no modulator" convention, so this can't live inside the modulator-conditional
                // branch below). Self-excludes Assertive/Surprised internally -- see its own
                // class doc for why and the full root-cause diagnosis.
                var headingGuardian = navAgent.gameObject.AddComponent<S35HeadingAlignmentGuardian>();
                headingGuardian.personality = personalityType;
                // Session 41 TASK 3/4: retarget a Mixamo behaviour clip onto this Rocketbox avatar
                // and/or attach the carried box. No-op unless --mixamo-clip/--carried-box was
                // given, so no existing caller's behavior changes.
                if (!string.IsNullOrEmpty(config.mixamoClip) || config.carriedBox)
                {
                    var mixamoApplier = navAgent.gameObject.AddComponent<S41MixamoClipApplier>();
                    mixamoApplier.clipControllerName = config.mixamoClip;
                    mixamoApplier.attachCarriedBox = config.carriedBox;
                }
                // Loop 1 Bug 2 (ATTEMPTED, REVERTED): indifferent's heading-vs-bearing residual
                // (~9-15 deg mean, up from Session 35's original ~6.4 deg claim) traces to this
                // guardian's own shared 4.0m/3.0s dynamic backoff -- once dist_to_pedestrian drops
                // under it, both the facing snap and the (for DirectVelocityDrive appearances, the
                // ONLY real-movement-affecting) position-line correction switch off, and SFAgent's
                // own robot-repulsion visibly bends the path for the rest of the approach. Three
                // levers were tried this session to narrow that gap for Indifferent specifically
                // (a per-pedestrian component instance, so each was scoped to Indifferent only,
                // never touching wheelchair_user/scooter_user/white_cane_user's own safety-tuned
                // instances): flat backoff shrink to 1.5m (heading mean ~15->~9-12deg, but N=5
                // min_dist worst-of-5 regressed 0.757->0.434m, missing the 0.5m operational bar);
                // flat shrink to 3.0m (heading barely moved, worst-of-5 still marginal at 0.499m);
                // and a tapered blend (see S35HeadingAlignmentGuardian's own taperRangeMeters/
                // nearBlendFloor fields, kept in that file as reusable, backward-compatible
                // infrastructure -- default 0/0 exactly reproduces the original hard cutoff)
                // easing from full correction at 4.0m down to a 0.35 floor over 3m: heading
                // improved to ~9-15deg mean but N=5 min_dist worst-of-5 was 0.310m -- BELOW the
                // 0.36m PHYSICAL floor, the worst result of all three attempts. All three confirm
                // the same underlying tension Session 35 already found for wheelchair_user/
                // scooter_user at a tighter flat backoff: this guardian's near-robot authority is
                // safety load-bearing, and reducing it (by any of these three mechanisms) trades
                // real clearance for heading tightness. Left at the original, safety-proven
                // defaults (4.0m/3.0s, blend fields at their 0/0 no-op default) -- NOT overridden
                // for Indifferent. See REPORT.md Loop 1 Session for full N=5 tables for all three
                // attempts and the honest ATTEMPTED-FAILED verdict.
                // Session 37 STEP 2: attach the (now TTC-based) reaction gate unconditionally,
                // same reasoning as the Zone B branch above -- the N=5 safety census this session
                // found plain business_male_01 x indifferent's own worst-case min_dist (0.323m)
                // BELOW the 0.36m physical floor, and this component was previously gated on an
                // explicit --ped-react-dist CLI flag inside the modulator-conditional block below,
                // so it was NEVER ACTIVE for a plain default indifferent trial (which also gets no
                // modulator at all, per the "Indifferent + no patrol = no modulator" convention
                // documented below -- meaning indifferent had ZERO reaction-gate/robot-awareness
                // mechanism of any kind under plain defaults). Attached here, before the modulator
                // block, so it applies regardless of whether personalityType==Indifferent skips
                // modulator creation. Excludes Assertive (its own permanent RobotRepulsion=0 via
                // ModulateAssertive() plus S32AssertiveStraightLineGuardian already own this
                // appearance's behavior entirely -- a gate here would fight both, matching the
                // "assertive = never" rule this component's own class doc already establishes).
                if (personalityType != Scenario.Agents.PedestrianModulator.PersonalityType.Assertive)
                {
                    var reactGateA = navAgent.gameObject.AddComponent<S34PedestrianReactDistGate>();
                    reactGateA.personality = personalityType;
                    reactGateA.reactDistanceMeters = config.hasPedReactDistOverride ? config.pedReactDistOverride : 1.5f;
                    reactGateA.scaredReactDistanceMetersOverride = config.scaredReactDistOverride;
                }
                {
                    Vector3 dest = config.hasPedGoalPose ? config.pedGoalPose.Position : spawnPos;
                    Vector3 d = dest - spawnPos;
                    if (new Vector2(d.x, d.z).magnitude > 1e-3f)
                    {
                        headingGuardian.targetHeadingDeg = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                        headingGuardian.hasTargetHeading = true;
                        // Session 35 FIX 1/2 second mechanism: also correct lateral position
                        // directly, for directVelocityDrive appearances (e.g. wheelchair_user)
                        // where the facing correction above is a no-op -- see the component's own
                        // class doc for why both are needed.
                        headingGuardian.lineStart = spawnPos;
                        headingGuardian.lineEnd = dest;
                        headingGuardian.hasLine = true;
                    }
                }

                // Indifferent + no patrol = no modulator at all, matching PedestrianSpawner's
                // existing convention (Base.ModulateVelocity() no-ops via a null GetComponent).
                // Session 28 PART 3b: ALSO force a modulator when pedSpeedMultiplier != 1.0f --
                // same "force a modulator" convention PedestrianSpawner already uses for its own
                // group.walkSpeedMultiplier (see that class).
                bool wantsSpeedScale = !Mathf.Approximately(config.pedSpeedMultiplier, 1.0f);
                // Session 37 STEP 0 investigated this condition (see REPORT.md for the full
                // diagnosis): business_male_01 x indifferent measured a real, sustained ~0.28 m/s
                // walking speed (frames.csv consecutive-frame displacement) vs. ~1.0-1.3 m/s for
                // scared/surprised/assertive under an otherwise-identical trial -- roughly 1/4-1/5
                // the intended pace. A trial fix (always attach the modulator, removing the
                // Indifferent skip below) was tested empirically and produced a WORSE result: a
                // steady ~2.3 m/s cruise, exceeding Parameters.MAX_VEL=0.95 entirely -- i.e. it
                // traded an under-speed bug for a larger, different over-speed bug via some
                // mechanism not fully understood in the time available (Scale()==v*
                // walkSpeedMultiplier is mathematically a no-op at the default 1.0, so the
                // magnitude jump isn't explained by anything this session could read in
                // PedestrianModulator.cs/SFAgent.cs, both outside writable scope). REVERTED rather
                // than shipped -- the original condition below is UNCHANGED, exactly as it was at
                // the start of this session. Real, unresolved, flagged prominently to Howard: this
                // needs live Editor/debugger instrumentation of SFAgent's own velocity computation
                // to find out what actually changes when an IVelocityModulator is present vs.
                // absent, since the difference is clearly NOT fully explained by Modulate()'s own
                // returned value.
                if (personalityType != Scenario.Agents.PedestrianModulator.PersonalityType.Indifferent
                    || patrolValid || wantsSpeedScale)
                {
                    var modulator = navAgent.gameObject.AddComponent<Scenario.Agents.PedestrianModulator>();
                    modulator.personality = personalityType;
                    modulator.walkSpeedMultiplier = config.pedSpeedMultiplier;
                    // Session 31 FIX 5(b): PedestrianModulator.scaredRadius/surpriseRadius are
                    // public runtime fields on this same component instance -- overriding them
                    // here is a plain field assignment on a script we just instantiated, not an
                    // edit to PedestrianModulator.cs itself (which stays untouched, outside this
                    // project's writable scope).
                    if (config.hasScaredRadiusOverride)
                    {
                        modulator.scaredRadius = config.scaredRadiusOverride;
                        // Session 46 (1.2): decouple trigger distance from flee strength. The
                        // override above sets BOTH, because closeness is normalised by
                        // scaredRadius. The gate holds the flee off until the trigger distance and
                        // then applies the full 7.0 profile whose shape was accepted on review.
                        var scaredGate = navAgent.gameObject.AddComponent<S46ScaredTriggerGate>();
                        scaredGate.triggerDistanceMeters = config.scaredRadiusOverride;
                        scaredGate.profileRadiusMeters = 7.0f;
                    }
                    // Session 40 STEP 2: additive lateral-flee bias for Scared (see
                    // S40ScaredLateralEvasion's own class doc for why -- the existing radial
                    // flee retreats along the approach line, not out of the robot's path).
                    if (personalityType == Scenario.Agents.PedestrianModulator.PersonalityType.Scared)
                    {
                        var scaredLateral = navAgent.gameObject.AddComponent<S40ScaredLateralEvasion>();
                        scaredLateral.scaredRadiusMeters = config.hasScaredRadiusOverride
                            ? config.scaredRadiusOverride : modulator.scaredRadius;
                    }
                    if (config.hasSurpriseRadiusOverride)
                    {
                        modulator.surpriseRadius = config.surpriseRadiusOverride;
                    }
                    if (config.hasSurpriseCooldownOverride)
                    {
                        modulator.cooldownDuration = config.surpriseCooldownOverride;
                    }
                    if (patrolValid)
                    {
                        modulator.EnablePatrol(config.patrolWaypoints[0].ToVector3(), config.patrolWaypoints[1].ToVector3());
                    }
                    // Session 31 FIX 6(b) / Session 33 FIX 2: Assertive's "back off" shooing
                    // gesture is now fired by S32AssertiveStraightLineGuardian's own walk->stop->
                    // gesture->resume state machine (not the standalone S31AssertiveGestureTrigger,
                    // retired for Assertive this session -- its independent proximity-only trigger
                    // is what caused the gesture to fire mid-walk; see S32AssertiveStraightLine
                    // Guardian's own class doc for the full sequencing fix).
                    if (personalityType == Scenario.Agents.PedestrianModulator.PersonalityType.Assertive)
                    {
                        // Session 32 FIX B: true straight-line hold, activated at SLATE release
                        // by TrialController (see S32AssertiveStraightLineGuardian's own class doc).
                        var assertiveGuardian = navAgent.gameObject.AddComponent<S32AssertiveStraightLineGuardian>();

                        // Session 41 TASK 1: reaction-latency instrumentation, env-var gated
                        // (AUTOTRIAL_S41_LATENCY_PROBE) and inert otherwise. Radius mirrors the
                        // guardian's own gesture gate -- the gesture fires on the frame proximity
                        // first forces a stop, so emergencyStopDistanceMeters IS the trigger
                        // radius here, not S31AssertiveGestureTrigger's retired 5.0m.
                        var assertiveProbe = navAgent.gameObject.AddComponent<S41ReactionLatencyProbe>();
                        assertiveProbe.triggerRadius = assertiveGuardian.emergencyStopDistanceMeters;
                        assertiveProbe.reactionStateName = "AssertiveGesture";
                        assertiveProbe.triggerParamName = "AssertiveGesture";
                    }
                    if (personalityType == Scenario.Agents.PedestrianModulator.PersonalityType.Surprised)
                    {
                        var surprisedProbe = navAgent.gameObject.AddComponent<S41ReactionLatencyProbe>();
                        surprisedProbe.triggerRadius = modulator.surpriseRadius;
                        surprisedProbe.reactionStateName = "SurprisedReaction";
                        surprisedProbe.triggerParamName = "Surprised";
                    }
                    // Session 37 STEP 2: S34PedestrianReactDistGate is now attached
                    // UNCONDITIONALLY, before this modulator block (see above) -- moved out of
                    // this `--ped-react-dist`-gated else-if so it's active for every plain-default
                    // trial too, not just ones that explicitly passed that CLI flag. Do not
                    // re-attach here -- Unity would silently add a SECOND independent instance,
                    // both writing SFAgent.RobotRepulsion in undefined per-frame order.
                    // Session 32 FIX B2 diagnostic: opt-in runtime probe (env-var gated, no-op
                    // unless AUTOTRIAL_S32_PROBE_PATH is set) -- see S32SurprisedRuntimeProbe.cs.
                    if (personalityType == Scenario.Agents.PedestrianModulator.PersonalityType.Surprised
                        && !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S32_PROBE_PATH")))
                    {
                        navAgent.gameObject.AddComponent<S32SurprisedRuntimeProbe>();
                    }
                }
                else
                {
                    // Session 38 FIX 3: root-caused the ~0.28 m/s bug Session 37 found (and the
                    // ~2.3 m/s over-correction its own naive "always attach a modulator" attempt
                    // produced instead). Root cause: Base.LateUpdate() (Assets/Scripts/SEAN/
                    // Scenario/Agents/Base.cs, forbidden to edit, read only) branches on modulator
                    // PRESENCE for how it applies root motion -- no modulator means it reproduces
                    // Unity's raw animator.deltaPosition application directly (line ~155), which
                    // for this appearance's Locomotion clip does not track the SFAgent-computed
                    // social-force velocity at all (hence ~0.28 m/s, not ~1.3). Attaching a
                    // modulator instead routes through PedestrianModulator.ApplyAnimatorRootMotion()
                    // -- a DIFFERENT application path Session 37 could not fully explain, which
                    // empirically produced ~2.3 m/s. Rather than chase that further, this sidesteps
                    // BOTH root-motion paths entirely: Base.DirectVelocityDrive (a plain public
                    // property, Base.cs:52-56, already an established externally-settable knob for
                    // exactly this "no usable root motion" class of appearance, e.g. wheelchair)
                    // makes Move() apply the SFAgent-computed `velocity` straight to the transform
                    // every frame instead of any animator-root-motion path -- the same MAX_VEL-
                    // clamped, correctly-scaled velocity every other (modulator-having) personality
                    // already gets via its own social-force computation, with neither buggy
                    // application path in the loop. S32AnimatorSpeedScaler (already attached to
                    // every appearance, see below) keeps the Locomotion clip's own playback rate
                    // visually matched to this now-correct translation speed, the same way it
                    // already does for other directVelocityDrive appearances (wheelchair/scooter).
                    var baseAgent = navAgent.gameObject.GetComponent<Scenario.Agents.Base>();
                    if (baseAgent != null)
                    {
                        baseAgent.DirectVelocityDrive = true;
                    }
                    // Session 39 FIX: DirectVelocityDrive skips Base.cs's own else-branch, which
                    // is the ONLY place Forward/Strafe/Idling ever get set -- confirmed via a live
                    // runtime probe this session that Forward stayed frozen at 0 the whole trial
                    // despite real ~0.85-0.95 m/s translation, producing "idle while sliding
                    // forward" (a real regression this session's own predecessor introduced).
                    // This replicates that computation from outside, off Base.velocity (public),
                    // restoring the walk cycle without reopening the two already-rejected
                    // DirectVelocityDrive alternatives (~0.28 m/s unmanaged, ~2.3 m/s modulator
                    // over-correction). See S39DirectVelocityDriveAnimatorSync's own class doc.
                    navAgent.gameObject.AddComponent<S39DirectVelocityDriveAnimatorSync>();
                }

                // Session 10 (D4): same dest-defaults-to-spawn fix as Zone B above. Patrol (if
                // requested) still wins, matching the pre-existing precedent for this branch.
                // Session 13: freeze at spawn (InitDest(spawnPos) unconditionally, even with a
                // modulator/patrol attached -- EnablePatrol above only configures the ping-pong
                // cycle, it doesn't itself call InitDest); zoneADest is only released at the
                // slate moment.
                // Session 28 PART 3a: --ped-motion standing overrides the release destination to
                // spawnPos regardless of patrol/pedGoalPose -- the SLATE capture-start trigger
                // still fires normally (TrialController.PollForTrigger is release-destination-
                // agnostic), the pedestrian just never actually walks anywhere. Still a live
                // costmap obstacle (position is real, just static); a modulator (if forced above)
                // keeps animating personality-driven upper-body/gaze behavior independent of
                // navigation destination.
                Vector3 zoneADest = config.pedMotion == "standing"
                    ? spawnPos
                    : (patrolValid
                        ? config.patrolWaypoints[0].ToVector3()
                        : (config.hasPedGoalPose ? config.pedGoalPose.Position : spawnPos));
                navAgent.InitDest(spawnPos);
                // Session 59: pinning destPos freezes Base.Move(), but Base.Move() is not what
                // translates a root-motion agent -- PedestrianModulator.ApplyAnimatorRootMotion()
                // is, and it ran through the whole freeze. Complete the freeze there too. Cleared
                // by TrialController at the SLATE release, paired with InitDest(releaseDest).
                FreezeRootMotionTranslation(navAgent, true);
                navAgentOut = navAgent;
                releaseDest = zoneADest;
                return navAgent.transform;
            }
        }

        /// <summary>
        /// Session 17 (Step 3, real-A1 camera pose): resolves --cam-height (an ABSOLUTE height
        /// above ground, not a blind local offset from the existing first-person camera mount) by
        /// raycasting straight down from above the robot at rig build time. A miss (no collider
        /// hit -- e.g. spawned over a gap in the ground mesh) falls back to the robot's own
        /// transform.position.y as a ground proxy, logged loudly rather than silently producing a
        /// wrong height. Both the raycast-found ground Y and the resolved world height are
        /// returned for TrialController to log into meta.json (see that class).
        /// </summary>
        private void ResolveCameraGroundHeight(Scenario.Robot robot, AutoTrialConfig config,
            out float resolvedWorldHeightY, out bool raycastHit)
        {
            Vector3 rayStart = robot.transform.position + Vector3.up * 2f;
            raycastHit = Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, 10f);
            float groundY;
            if (raycastHit)
            {
                groundY = hit.point.y;
            }
            else
            {
                groundY = robot.transform.position.y;
                Debug.LogWarning("[AutoTrial] --cam-height ground raycast missed (no collider hit within 10m "
                    + "below " + rayStart.ToString("F3") + ") -- falling back to robot.transform.position.y ("
                    + groundY.ToString("F3") + ") as a ground proxy. Resolved camera height may not be exactly "
                    + "--cam-height above the true ground -- check meta.json.camHeightRaycastHit.");
            }
            resolvedWorldHeightY = groundY + config.camera.camHeightMeters;
            Debug.Log("[AutoTrial] cam-height raycast: groundY=" + groundY.ToString("F3") + " (hit=" + raycastHit
                + ") + camHeightMeters=" + config.camera.camHeightMeters.ToString("F3")
                + " -> resolvedCamHeightWorldY=" + resolvedWorldHeightY.ToString("F3") + ".");
        }

        private Camera BuildPovCamera(Scenario.Robot robot, AutoTrialConfig config,
            out float resolvedCamHeightWorldY, out bool camHeightRaycastHit, out float resolvedCamVfovDeg)
        {
            // POV: a NEW child camera on the robot's existing first-person camera transform, at
            // zero local offset, copying only FOV/near/far -- per adjustment #6 (2026-07-15) this
            // must not retarget or share robot.camera_first itself, since that camera may back the
            // live /robot_firstperson_rgb publisher. Session 10 (D5): the chase/third-person
            // camera that used to be built alongside this one is gone -- POV only, per the
            // output-format spec (REPORT.md Session 10).
            Camera existing = robot.camera_first;
            var povGO = new GameObject("AutoTrialPovCamera");
            povGO.transform.SetParent(existing.transform, false);
            povGO.transform.localPosition = new Vector3(config.camera.povOffsetX, config.camera.povOffsetY, config.camera.povOffsetZ);
            povGO.transform.localRotation = Quaternion.identity;
            Camera povCam = povGO.AddComponent<Camera>();

            // Session 27 (FOV truth): fieldOfView is no longer copied from the legacy first-person
            // camera (existing.fieldOfView) -- that value (22.0deg vertical, -> 38.1267deg
            // horizontal at this project's own 16:9 capture aspect, per S24CameraFovProbe) was
            // inherited from Round 3 and never audited against the real A1's sensor. Computed here
            // instead from config.camera.camHfovDeg (default 69deg, the RealSense D435i's own RGB
            // horizontal FOV; 87deg selectable for the depth FOV) via the same capture aspect used
            // for povCam.aspect below -- Unity's Camera.fieldOfView is VERTICAL, so:
            // vFov = 2*atan(tan(hFov/2) / aspect). Sim-real fidelity, not a metric workaround --
            // this changes what the camera actually RENDERS, not just how a downstream metric scores it.
            float captureAspect = TrialController.CaptureWidth / (float)TrialController.CaptureHeight;
            float hFovRad = config.camera.camHfovDeg * Mathf.Deg2Rad;
            float vFovRad = 2f * Mathf.Atan(Mathf.Tan(hFovRad / 2f) / captureAspect);
            float resolvedVFovDeg = vFovRad * Mathf.Rad2Deg;
            resolvedCamVfovDeg = resolvedVFovDeg;
            povCam.fieldOfView = resolvedVFovDeg;
            Debug.Log("[AutoTrial] povCam.fieldOfView resolved from --cam-hfov=" + config.camera.camHfovDeg.ToString("F2")
                + "deg (horizontal) -> " + resolvedVFovDeg.ToString("F4") + "deg (vertical, Unity's own convention) "
                + "at captureAspect=" + captureAspect.ToString("F4") + " -- was existing.fieldOfView="
                + existing.fieldOfView.ToString("F4") + "deg (legacy camera, no longer used).");
            povCam.nearClipPlane = existing.nearClipPlane;
            povCam.farClipPlane = existing.farClipPlane;
            povCam.enabled = false; // rendered manually via Camera.Render() at each capture tick

            // Round 4 fix: a freshly AddComponent'd Camera's .aspect defaults to the current
            // Screen/GameView aspect, not the aspect of whatever RenderTexture it will later be
            // pointed at -- and since this camera is never enabled (it's rendered manually via
            // Camera.Render(), never through Unity's normal per-frame camera stack), it never
            // gets the auto aspect-follows-targetTexture recompute that an enabled/visible camera
            // gets. In this project's batchmode launch (no -screen-width/-screen-height passed --
            // see run_trial.py/unity.log) the default GameView is 4:3, so povCam.aspect silently
            // stayed 4:3 while TrialController.Initialize (called after this method returns) set
            // targetTexture to a 1280x720 (16:9) RenderTexture -- Camera.Render() then rendered a
            // 4:3 field of view squeezed into a 16:9 frame, a uniform ~1.33x horizontal stretch
            // baked into every saved JPG (confirmed via contact-sheet eyeball: pavement tiles and
            // palm trees read visibly wider than they are; ffprobe confirms the container itself
            // is correctly 1280x720/16:9, so the stretch is in the rendered pixels, not the
            // encode). Explicitly setting aspect here, from the SAME authoritative capture
            // dimensions TrialController uses for its RenderTexture (not a re-typed literal),
            // closes it regardless of GameView/batchmode defaults.
            povCam.aspect = captureAspect;
            float aspectErr = Mathf.Abs(povCam.aspect - TrialController.CaptureWidth / (float)TrialController.CaptureHeight);
            Debug.Log("[AutoTrial] povCam.aspect explicitly set to " + povCam.aspect.ToString("F4")
                + " (target " + (TrialController.CaptureWidth / (float)TrialController.CaptureHeight).ToString("F4")
                + ", CaptureWidth=" + TrialController.CaptureWidth + " CaptureHeight=" + TrialController.CaptureHeight + ").");
            if (aspectErr > 0.01f)
            {
                Fail("povCam.aspect (" + povCam.aspect.ToString("F4") + ") deviates from the render-target "
                    + "aspect (" + (TrialController.CaptureWidth / (float)TrialController.CaptureHeight).ToString("F4")
                    + ") by more than 0.01 -- aspect gate would fail this trial anyway; refusing to start it.");
            }

            // Round 3 fix (D2 re-earned): soft mount. Parenting is kept (for lifecycle/cleanup
            // convenience) but PovCameraSmoother overrides this transform's WORLD position/
            // rotation every frame, so the rigid parent-child link never actually determines the
            // rendered pose. Position rigidly follows the mount (existing.transform); rotation is a
            // world-frame horizon lock whose yaw is low-passed off the ROBOT CHASSIS's own heading
            // (robot.transform), not the mount's -- see PovCameraSmoother.cs class doc for why
            // decomposing the mount's own rotation was the Session-10 bug, why it runs in Update()
            // rather than the more conventional LateUpdate() (timing relative to TrialController's
            // capture coroutine), and the rigidMount=true passthrough case used for direct comparison.
            ResolveCameraGroundHeight(robot, config, out resolvedCamHeightWorldY, out camHeightRaycastHit);

            var smoother = povGO.AddComponent<PovCameraSmoother>();
            smoother.Initialize(existing.transform, robot.transform, config.camera, resolvedCamHeightWorldY);

            return povCam;
        }

        // Session 13: internal (not private) -- TrialController now calls these at the slate
        // moment, moved verbatim from Run() where they used to fire well before capture start.
        internal static bool TryPublishNowBestEffort(Tasks.Base task)
        {
            try
            {
                MethodInfo publish = typeof(Tasks.Base).GetMethod("Publish", BindingFlags.NonPublic | BindingFlags.Instance);
                if (publish == null)
                {
                    return false;
                }
                publish.Invoke(task, new object[] { task.robotGoal });
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[AutoTrial] immediate goal publish via reflection failed (non-fatal, falls back to periodic loop): " + e.Message);
                return false;
            }
        }

        internal static void LogPublishIntervalBestEffort(Tasks.Base task)
        {
            try
            {
                FieldInfo field = typeof(Tasks.Base).GetField("publishInterval", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    Debug.Log("[AutoTrial] active task publishInterval=" + field.GetValue(task) + "s (goal delivery to /move_base_simple/goal may take up to this long).");
                }
            }
            catch (Exception e)
            {
                Debug.Log("[AutoTrial] could not read publishInterval via reflection (non-fatal): " + e.Message);
            }
        }

        internal static void Fail(string message)
        {
            Debug.LogError("[AutoTrial] " + message);
#if UNITY_EDITOR
            EditorApplication.Exit(1);
#else
            Application.Quit(1);
#endif
        }
    }
}
