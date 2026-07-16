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
            // PENDING VERIFICATION -- container rewiring in progress (editor-side, see pre-flight
            // A 2026-07-15): canonical avatar is being switched from the committed prefab's
            // Phone_User.prefab to PedetrainAvatars/PhoneUser_Ped.prefab under this same
            // committed path/name. No code change needed here either way -- the Resources path
            // does not change. run_trial.py prints its own warning when this appearance is used.
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
        // See the "settled" comment in WaitForReadinessGates -- max_vel_x is 0.6 m/s, so 1.2 is
        // "cruising normally," ruling out only a genuine spike/instability, not ordinary driving.
        private const float SpeedSaneUpperBound = 1.2f;
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
            AutoTrialConfig config = LoadConfig(configPath);
            if (config == null)
            {
                yield break;
            }

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

            Transform pedestrianTransform = SpawnPedestrian(config, zone, appearancePrefab, personalityType);

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

            BuildCameraRig(robot, config, out Camera povCam, out Camera chaseCam);

            var controllerGO = new GameObject("AutoTrialController");
            var controller = controllerGO.AddComponent<TrialController>();
            controller.Initialize(config, robot, povCam, chaseCam, pedestrianTransform, zone, resourcePath);
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

        private Transform SpawnPedestrian(AutoTrialConfig config, string zone, GameObject prefab, Scenario.Agents.PedestrianModulator.PersonalityType personalityType)
        {
            Vector3 spawnPos = config.spawnPose.Position;
            Quaternion spawnRot = config.spawnPose.Rotation;
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
                navAgent.InitDest(spawnPos);
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

                // Indifferent + no patrol = no modulator at all, matching PedestrianSpawner's
                // existing convention (Base.ModulateVelocity() no-ops via a null GetComponent).
                if (personalityType != Scenario.Agents.PedestrianModulator.PersonalityType.Indifferent || patrolValid)
                {
                    var modulator = navAgent.gameObject.AddComponent<Scenario.Agents.PedestrianModulator>();
                    modulator.personality = personalityType;
                    if (patrolValid)
                    {
                        modulator.EnablePatrol(config.patrolWaypoints[0].ToVector3(), config.patrolWaypoints[1].ToVector3());
                    }
                }

                navAgent.InitDest(patrolValid ? config.patrolWaypoints[0].ToVector3() : spawnPos);
                return navAgent.transform;
            }
        }

        private void BuildCameraRig(Scenario.Robot robot, AutoTrialConfig config, out Camera povCam, out Camera chaseCam)
        {
            // POV: a NEW child camera on the robot's existing first-person camera transform, at
            // zero local offset, copying only FOV/near/far -- per adjustment #6 (2026-07-15) this
            // must not retarget or share robot.camera_first itself, since that camera may back the
            // live /robot_firstperson_rgb publisher.
            Camera existing = robot.camera_first;
            var povGO = new GameObject("AutoTrialPovCamera");
            povGO.transform.SetParent(existing.transform, false);
            povGO.transform.localPosition = new Vector3(config.camera.povOffsetX, config.camera.povOffsetY, config.camera.povOffsetZ);
            povGO.transform.localRotation = Quaternion.identity;
            povCam = povGO.AddComponent<Camera>();
            povCam.fieldOfView = existing.fieldOfView;
            povCam.nearClipPlane = existing.nearClipPlane;
            povCam.farClipPlane = existing.farClipPlane;
            povCam.enabled = false; // rendered manually via Camera.Render() at each capture tick

            // Chase: standalone camera, not parented to the robot -- its transform is recomputed
            // every capture tick in TrialController from config.camera.chase* (behind/above/lookAt).
            var chaseGO = new GameObject("AutoTrialChaseCamera");
            chaseCam = chaseGO.AddComponent<Camera>();
            chaseCam.enabled = false;
        }

        private static bool TryPublishNowBestEffort(Tasks.Base task)
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

        private static void LogPublishIntervalBestEffort(Tasks.Base task)
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
