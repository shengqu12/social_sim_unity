using System.Collections;
using System.IO;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 79. A SECOND, independent video stream that frames the PEDESTRIAN, written as JPGs
    /// for an out-of-band ffmpeg assembly.
    ///
    /// Entirely additive and env-gated (AUTOTRIAL_S79_TP_DIR). It never touches the POV pipeline:
    /// it owns its own Camera and its own RenderTexture, renders with an explicit Camera.Render()
    /// on its own timer, and writes to its own directory. TrialController's capture loop, its
    /// frames.csv, and the file-manifest gate are untouched and do not know this exists.
    ///
    /// WHY NOT robot.camera_third. Robot.cs does instantiate SEAN/Sensors/ThirdPersonCameraParent
    /// into robot.camera_third, and pointing a parallel capture at it is one line cheaper. But it
    /// is mounted on the ROBOT, so it inherits precisely the failure S78 measured and quantified:
    /// the camera's yaw follows the robot's course, the navigation stack's job is to steer AROUND
    /// the pedestrian, and the subject therefore leaves frame exactly during the reaction. S78's
    /// numbers for the POV camera were 0-19% full-body coverage in the reaction window (0% for the
    /// surprised cell), and a robot-mounted chase camera does not change that geometry -- it only
    /// moves the viewpoint backwards along the same axis. Since the stated purpose of this stream
    /// is to make the reaction judgeable, the camera is slaved to the PEDESTRIAN instead.
    ///
    /// FRAMING. The offset direction is latched ONCE, from the pedestrian's heading on the first
    /// frame it is resolved, and then never rotated. The camera translates with the pedestrian but
    /// holds a fixed world orientation, i.e. a dolly running alongside the corridor. That is
    /// deliberate: a camera that re-aims off the live heading would swing through 180 degrees
    /// during a scared flee -- the one moment the footage most needs to be readable. A fixed
    /// three-quarter view keeps the whole body in frame through turns, flees and crouches alike.
    /// </summary>
    public class S79ThirdPersonCapture : MonoBehaviour
    {
        public const string OutDirEnv = "AUTOTRIAL_S79_TP_DIR";
        /// <summary>Degrees to swing the camera off the pedestrian's initial heading. ~50 gives a
        /// three-quarter front view: the face and the leading leg are both visible, which is what
        /// foot-slide and reaction-pose judgements actually need.</summary>
        public float azimuthDeg = 50f;
        public float distanceMeters = 4.2f;
        public float heightMeters = 1.75f;
        public float lookAtHeightMeters = 0.95f;
        public int width = 1280;
        public int height = 720;
        public int fps = 15;
        public int jpgQuality = 85;

        private static string OutDir
        {
            get { return System.Environment.GetEnvironmentVariable(OutDirEnv); }
        }

        private Camera cam;
        private RenderTexture rt;
        private Texture2D readback;
        private Transform subject;
        private Vector3 offset;          // world-space, latched once
        private bool latched;
        private int frameIndex;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (string.IsNullOrEmpty(OutDir)) { return; }
            var host = new GameObject("S79ThirdPersonCaptureHost");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<S79ThirdPersonCapture>();
        }

        private IEnumerator Start()
        {
            string dir = OutDir;
            if (string.IsNullOrEmpty(dir)) { enabled = false; yield break; }
            Directory.CreateDirectory(dir);

            cam = new GameObject("S79ThirdPersonCamera").AddComponent<Camera>();
            Object.DontDestroyOnLoad(cam.gameObject);
            cam.enabled = false;                       // rendered manually, never in the camera stack
            rt = new RenderTexture(width, height, 24);
            cam.targetTexture = rt;
            cam.aspect = (float)width / height;
            cam.fieldOfView = 40f;                     // vertical; tight enough that the body fills frame
            readback = new Texture2D(width, height, TextureFormat.RGB24, false);

            // The pedestrian is spawned well after scene load, so poll rather than one-shot find.
            // S32AnimatorSpeedScaler is on every Zone A/B pedestrian and nothing else, which is the
            // same handle S44SlideProbe uses to find them.
            while (subject == null)
            {
                var scaler = Object.FindObjectOfType<S32AnimatorSpeedScaler>();
                if (scaler != null) { subject = scaler.transform; break; }
                yield return new WaitForSeconds(0.25f);
            }
            Debug.Log("[S79TP] subject='" + subject.name + "' -> " + dir);

            var wait = new WaitForSeconds(1f / Mathf.Max(1, fps));
            while (true)
            {
                yield return wait;
                if (subject == null) { continue; }
                if (!latched)
                {
                    Vector3 fwd = subject.forward; fwd.y = 0f;
                    if (fwd.sqrMagnitude < 1e-6f) { fwd = Vector3.forward; }
                    Vector3 dir3 = Quaternion.Euler(0f, azimuthDeg, 0f) * fwd.normalized;
                    offset = dir3 * distanceMeters + Vector3.up * heightMeters;
                    latched = true;
                    Debug.Log("[S79TP] framing latched: azimuth=" + azimuthDeg + "deg dist="
                        + distanceMeters + "m height=" + heightMeters + "m");
                }
                Vector3 target = subject.position + Vector3.up * lookAtHeightMeters;
                cam.transform.position = subject.position + offset;
                cam.transform.rotation = Quaternion.LookRotation(target - cam.transform.position, Vector3.up);
                Capture(dir);
            }
        }

        private void Capture(string dir)
        {
            cam.Render();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(dir, "tp_" + frameIndex.ToString("D5") + ".jpg"),
                readback.EncodeToJPG(jpgQuality));
            frameIndex++;
        }

        private void OnDestroy()
        {
            if (rt != null) { rt.Release(); }
        }
    }
}
