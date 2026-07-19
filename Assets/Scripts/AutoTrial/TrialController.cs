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
        private string terminationReason = "unknown";

        // Session 10 (D2 diagnosis): latest cmd_vel seen, for correlating commanded steering
        // against actual camera/body rotation. Subscribed the same way DiagCmdVel.cs already
        // proved works (topic string read live off the scene's live ControlSubscriber, never
        // assumed) -- best-effort: if this fails, the two columns are just blank, nothing else
        // about the trial is affected.
        private double lastCmdLinX;
        private double lastCmdAngZ;
        private bool cmdVelAvailable;

        // Round 4: made public so AutoTrialBootstrap.BuildPovCamera can set povCam.aspect from
        // the same authoritative numbers used to build the RenderTexture -- see that method's
        // comment for the aspect-mismatch bug this closes.
        public const int CaptureWidth = 1280;
        public const int CaptureHeight = 720;
        private const float GoalArrivalDistMeters = 0.5f;

        public void Initialize(AutoTrialConfig config, Scenario.Robot robot, Camera povCam,
            Transform pedestrian, string appearanceZone, string appearanceResourcePath, List<string> agentCensus)
        {
            this.config = config;
            this.robot = robot;
            this.povCam = povCam;
            this.pedestrian = pedestrian;
            this.appearanceZone = appearanceZone;
            this.appearanceResourcePath = appearanceResourcePath;
            this.agentCensus = agentCensus;

            Directory.CreateDirectory(config.outDir);
            povDir = Path.Combine(config.outDir, "pov");
            Directory.CreateDirectory(povDir);

            povRT = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32);
            povCam.targetTexture = povRT;
            readBuffer = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);

            csv = new StreamWriter(Path.Combine(config.outDir, "frames.csv"), false);
            csv.WriteLine("t,frame_idx,robot_x,robot_y,robot_z,robot_yaw_deg,robot_speed,pedestrian_appearance,pedestrian_personality,pedestrian_x,pedestrian_z,dist_to_pedestrian,min_dist,cmd_lin_x,cmd_ang_z,pov_cam_yaw_deg,pov_cam_pitch_deg,pov_cam_roll_deg");

            startTime = Time.time;
            lastSampleTime = startTime;
            lastSamplePos = robot.position;

            TrySubscribeCmdVel();

            StartCoroutine(RunLoop());
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

                if (Time.time >= nextTick)
                {
                    CaptureFrame(elapsed);
                    nextTick += interval;
                }

                yield return null;
            }

            // One final frame at the terminal instant (arrival/duration-end), then finish.
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

            // Instantaneous nearest-pedestrian distance for this frame (== distToPed with a
            // single tracked pedestrian; kept as its own min() so this still generalizes if
            // multiple pedestrians are ever tracked). minDistSeen is the separate whole-trial
            // running minimum used for meta.json's summary stat, not written per-row.
            float distToPed = pedestrian != null ? Util.Geometry.GroundPlaneDist(pos, pedestrian.position) : float.NaN;
            float frameMinDist = distToPed;
            if (!float.IsNaN(distToPed))
            {
                minDistSeen = Mathf.Min(minDistSeen, distToPed);
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

            csv.WriteLine(string.Join(",", new string[]
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
            }));

            lastSampleTime = t;
            lastSamplePos = pos;
            frameIdx++;
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
        }

        private void WriteMetaJson()
        {
            var meta = new TrialMeta
            {
                config = config,
                terminationReason = terminationReason,
                frameCount = frameIdx,
                minDistanceMeters = float.IsInfinity(minDistSeen) ? -1f : minDistSeen,
                startedAtUtc = DateTime.UtcNow.AddSeconds(-(Time.time - startTime)).ToString("o", CultureInfo.InvariantCulture),
                endedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                unityGitSha = TryReadGitSha(),
                resolvedAppearanceZone = appearanceZone,
                resolvedAppearanceResourcePath = appearanceResourcePath,
                resolvedAppearanceNote = config.appearance == "wheelchair_user"
                    ? "wheelchair_user resolves to WheelChairUserContainer -> Rocketbox/Wheelchair_Female (female avatar only; male package has no importable prefab, out of scope for v1)"
                    : (config.appearance == "phone_user"
                        ? "PENDING VERIFICATION -- container rewiring in progress (editor-side); see AutoTrialBootstrap.ZoneBContainers comment"
                        : ""),
                agentCensus = agentCensus ?? new List<string>(),
                povCameraAspect = povCam.aspect,
                targetAspect = CaptureWidth / (float)CaptureHeight,
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
