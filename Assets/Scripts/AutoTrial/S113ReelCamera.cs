using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 113 PART 3: a third-person REEL camera for the curious crouch, framed from the
    /// robot's side of the pedestrian so the face (and where it is looking) is on camera.
    ///
    /// The S79 third-person sink latches its azimuth off the pedestrian's forward at first sight
    /// and then trails it, which for the S68 sequence ends up behind the crouching body -- fine
    /// for gait review, useless for judging "is the person looking at the robot". This camera
    /// re-aims every frame: it stands on the pedestrian->robot line, swung sideways by
    /// azimuthDeg, at distanceMeters, and looks at a point between the pedestrian's head and the
    /// robot, so both are in shot while the head turns.
    ///
    /// Recording stream only, no behaviour: active only when AUTOTRIAL_S113_TP_DIR names a
    /// directory. Writes tp_NNNNN.jpg at ~fps and a tp_times.csv (frame, Time.time) so strips can
    /// be cut at an exact Time.time rather than estimated. Same capture mechanics as
    /// S79ThirdPersonCapture (own camera + RenderTexture, no effect on the POV camera or the
    /// file-manifest gate).
    /// </summary>
    public class S113ReelCamera : MonoBehaviour
    {
        public const string OutDirEnv = "AUTOTRIAL_S113_TP_DIR";
        public int width = 1280, height = 720, fps = 15, jpgQuality = 90;
        public float azimuthDeg = 55f;         // swing off the pedestrian->robot line, toward the pedestrian's front-side
        public float distanceMeters = 3.2f;
        public float heightMeters = 1.45f;
        public float lookAtBlend = 0.25f;      // 0 = pedestrian head, 1 = robot
        public float pedHeadHeight = 1.0f;     // mid-height between the standing head (1.7) and the crouched head (0.9)

        private Camera cam;
        private RenderTexture rt;
        private Texture2D readback;
        private Transform subject, robot;
        private int frameIndex;
        private StreamWriter times;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            string dir = System.Environment.GetEnvironmentVariable(OutDirEnv);
            if (string.IsNullOrEmpty(dir)) return;
            var host = new GameObject("S113ReelCameraHost");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<S113ReelCamera>().StartCoroutine(nameof(Run), dir);
        }

        private IEnumerator Run(string dir)
        {
            Directory.CreateDirectory(dir);
            var go = new GameObject("S113ReelCam");
            go.transform.SetParent(transform, false);
            cam = go.AddComponent<Camera>();
            cam.enabled = false;                       // rendered by hand, never a scene camera
            rt = new RenderTexture(width, height, 24);
            cam.targetTexture = rt;
            cam.aspect = (float)width / height;
            cam.fieldOfView = 38f;
            readback = new Texture2D(width, height, TextureFormat.RGB24, false);
            times = new StreamWriter(Path.Combine(dir, "tp_times.csv"));
            times.WriteLine("frame,time");

            while (subject == null || robot == null)
            {
                if (subject == null) { var s = Object.FindObjectOfType<S32AnimatorSpeedScaler>(); if (s != null) subject = s.transform; }
                if (robot == null) { var r = Object.FindObjectOfType<Scenario.Robot>(); if (r != null) robot = (r.base_link != null ? r.base_link.transform : r.transform); }
                if (subject == null || robot == null) yield return new WaitForSeconds(0.25f);
            }
            Debug.Log("[S113Reel] subject='" + subject.name + "' robot='" + robot.name + "' -> " + dir);
            var wait = new WaitForSeconds(1f / Mathf.Max(1, fps));
            while (true)
            {
                yield return wait;
                if (subject == null || robot == null) continue;
                Vector3 toRobot = robot.position - subject.position; toRobot.y = 0f;
                if (toRobot.sqrMagnitude < 1e-4f) toRobot = subject.forward;
                Vector3 dir3 = Quaternion.Euler(0f, azimuthDeg, 0f) * toRobot.normalized;
                Vector3 pos = subject.position + dir3 * distanceMeters + Vector3.up * heightMeters;
                Vector3 headPt = subject.position + Vector3.up * pedHeadHeight;
                Vector3 look = Vector3.Lerp(headPt, robot.position + Vector3.up * 0.3f, lookAtBlend);
                cam.transform.position = pos;
                cam.transform.rotation = Quaternion.LookRotation(look - pos, Vector3.up);
                cam.Render();
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readback.Apply(false);
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(dir, "tp_" + frameIndex.ToString("D5") + ".jpg"), readback.EncodeToJPG(jpgQuality));
                times.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:F3}", frameIndex, Time.time));
                frameIndex++;
            }
        }

        private void OnDestroy()
        {
            if (times != null) { times.Flush(); times.Close(); times = null; }
        }
    }
}
