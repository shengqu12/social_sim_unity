using System;
using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Owns the capture loop once AutoTrialBootstrap has finished setup: samples on Time.time at
    /// 1/fps (never Time.captureFramerate/timeScale, which would desync from wall-clock move_base),
    /// renders both cameras to disk as JPGs, appends frames.csv, and on termination writes
    /// meta.json and exits the process.
    /// </summary>
    public class TrialController : MonoBehaviour
    {
        private AutoTrialConfig config;
        private Scenario.Robot robot;
        private Camera povCam;
        private Camera chaseCam;
        private Transform pedestrian;
        private string appearanceZone;
        private string appearanceResourcePath;

        private RenderTexture povRT;
        private RenderTexture chaseRT;
        private Texture2D readBuffer;
        private string povDir;
        private string tpDir;
        private StreamWriter csv;

        private float startTime;
        private float lastSampleTime;
        private Vector3 lastSamplePos;
        private int frameIdx;
        private float minDistSeen = float.PositiveInfinity;
        private string terminationReason = "unknown";

        private const int CaptureWidth = 1280;
        private const int CaptureHeight = 720;
        private const float GoalArrivalDistMeters = 0.5f;

        public void Initialize(AutoTrialConfig config, Scenario.Robot robot, Camera povCam, Camera chaseCam,
            Transform pedestrian, string appearanceZone, string appearanceResourcePath)
        {
            this.config = config;
            this.robot = robot;
            this.povCam = povCam;
            this.chaseCam = chaseCam;
            this.pedestrian = pedestrian;
            this.appearanceZone = appearanceZone;
            this.appearanceResourcePath = appearanceResourcePath;

            Directory.CreateDirectory(config.outDir);
            povDir = Path.Combine(config.outDir, "pov");
            tpDir = Path.Combine(config.outDir, "tp");
            Directory.CreateDirectory(povDir);
            Directory.CreateDirectory(tpDir);

            povRT = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32);
            chaseRT = new RenderTexture(CaptureWidth, CaptureHeight, 24, RenderTextureFormat.ARGB32);
            povCam.targetTexture = povRT;
            chaseCam.targetTexture = chaseRT;
            readBuffer = new Texture2D(CaptureWidth, CaptureHeight, TextureFormat.RGB24, false);

            csv = new StreamWriter(Path.Combine(config.outDir, "frames.csv"), false);
            csv.WriteLine("t,frame_idx,robot_x,robot_y,robot_z,robot_yaw_deg,robot_speed,pedestrian_appearance,pedestrian_personality,pedestrian_x,pedestrian_z,dist_to_pedestrian,min_dist");

            startTime = Time.time;
            lastSampleTime = startTime;
            lastSamplePos = robot.position;

            StartCoroutine(RunLoop());
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
            UpdateChaseCameraTransform();

            RenderAndSave(povCam, povRT, Path.Combine(povDir, "pov_" + frameIdx.ToString("D5", CultureInfo.InvariantCulture) + ".jpg"));
            RenderAndSave(chaseCam, chaseRT, Path.Combine(tpDir, "tp_" + frameIdx.ToString("D5", CultureInfo.InvariantCulture) + ".jpg"));

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
            }));

            lastSampleTime = t;
            lastSamplePos = pos;
            frameIdx++;
        }

        private void UpdateChaseCameraTransform()
        {
            Transform robotT = robot.transform;
            Vector3 back = -robotT.forward;
            back.y = 0;
            if (back.sqrMagnitude < 1e-6f)
            {
                back = Vector3.back;
            }
            back.Normalize();

            Vector3 chasePos = robotT.position + back * config.camera.chaseDistance + Vector3.up * config.camera.chaseHeight;
            chaseCam.transform.position = chasePos;
            chaseCam.transform.LookAt(robotT.position + Vector3.up * config.camera.chaseLookHeight);
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
            chaseRT.Release();

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
