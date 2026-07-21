using System.Collections;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 24 diagnostic: logs the robot's camera_first vertical FOV + aspect, so the
    /// subject-in-frame metric's horizontal FOV can be derived (Unity's Camera.fieldOfView is
    /// vertical; hFOV = 2*atan(tan(vFOV/2)*aspect)) from a real, measured value rather than an
    /// assumed datasheet number. One-shot: logs once SEAN.instance/robot are ready, then exits.
    ///
    /// -diagCameraFov on the command line, or DIAG_CAMERA_FOV=1 in the environment.
    /// </summary>
    public class S24CameraFovProbe : MonoBehaviour
    {
        private const string EnvFlag = "DIAG_CAMERA_FOV";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            bool enabled = false;
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-diagCameraFov") { enabled = true; break; }
            }
            if (!enabled && System.Environment.GetEnvironmentVariable(EnvFlag) == "1")
            {
                enabled = true;
            }
            if (!enabled) { return; }

            var go = new GameObject("S24CameraFovProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var probe = go.AddComponent<S24CameraFovProbe>();
            probe.StartCoroutine(probe.Run());
        }

        private IEnumerator Run()
        {
            float waitStart = Time.time;
            while (SEAN.instance == null && Time.time - waitStart < 30f)
            {
                yield return null;
            }
            if (SEAN.instance == null)
            {
                Debug.LogError("[S24CameraFovProbe] timed out waiting for SEAN.instance.");
                Finish(1);
                yield break;
            }
            SEAN sean = SEAN.instance;

            Scenario.Robot robot = null;
            waitStart = Time.time;
            while (robot == null && Time.time - waitStart < 30f)
            {
                try { robot = sean.robot; } catch (System.Exception) { }
                if (robot == null) yield return null;
            }
            if (robot == null || robot.camera_first == null)
            {
                Debug.LogError("[S24CameraFovProbe] timed out waiting for robot.camera_first.");
                Finish(1);
                yield break;
            }

            Camera cam = robot.camera_first;
            float vFovDeg = cam.fieldOfView;
            float aspect = cam.aspect;
            float vFovRad = vFovDeg * Mathf.Deg2Rad;
            float hFovRad = 2f * Mathf.Atan(Mathf.Tan(vFovRad / 2f) * aspect);
            float hFovDeg = hFovRad * Mathf.Rad2Deg;

            Debug.Log(string.Format(
                "[S24CameraFovProbe] camera_first: verticalFOV={0:F4}deg aspect={1:F4} -> derivedHorizontalFOV={2:F4}deg "
                + "(at capture aspect 1.7778, horizontalFOV would be {3:F4}deg)",
                vFovDeg, aspect, hFovDeg,
                2f * Mathf.Rad2Deg * Mathf.Atan(Mathf.Tan(vFovRad / 2f) * 1.777778f)));

            Finish(0);
        }

        private static void Finish(int code)
        {
#if UNITY_EDITOR
            EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }
    }
}
