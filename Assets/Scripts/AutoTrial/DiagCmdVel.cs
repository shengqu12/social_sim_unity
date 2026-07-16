using System;
using System.Collections;
using UnityEngine;
using Unity.Robotics.ROSTCPConnector;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Diagnostic-only, inert without its own flag (same pattern as AutoTrialBootstrap's
    /// -trialConfig gate): -diagCmdVel on the command line or DIAG_CMDVEL=1 in the environment.
    /// Subscribes to the *exact* topic string the scene's live Control.ControlSubscriber instance
    /// is actually using (read from the component, not assumed), and logs once per second:
    /// message receipt count (+ delta), latest linear/angular values, robot transform, and the
    /// root ArticulationBody's velocity -- splits "Unity never receives cmd_vel" (H2) from
    /// "Unity receives it but the robot doesn't move" (H3) with a single run.
    ///
    /// Does not touch SEAN.Control.VelocityController or any other shared file -- this is a
    /// second, independent subscriber alongside whatever the real controller already does.
    /// </summary>
    public class DiagCmdVel : MonoBehaviour
    {
        private const string EnvFlag = "DIAG_CMDVEL";
        private const float RunSeconds = 30f;

        private int totalReceived = 0;
        private double lastLinX, lastLinY, lastAngZ;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            bool enabled = false;
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-diagCmdVel") { enabled = true; break; }
            }
            if (!enabled && System.Environment.GetEnvironmentVariable(EnvFlag) == "1")
            {
                enabled = true;
            }
            if (!enabled)
            {
                return;
            }

            var go = new GameObject("DiagCmdVel");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var diag = go.AddComponent<DiagCmdVel>();
            diag.StartCoroutine(diag.Run());
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
                Debug.LogError("[Diag] timed out waiting for SEAN.instance.");
                Finish();
                yield break;
            }
            SEAN sean = SEAN.instance;

            Scenario.Robot robot = null;
            waitStart = Time.time;
            while (robot == null && Time.time - waitStart < 30f)
            {
                try { robot = sean.robot; } catch (Exception) { /* not resolved yet */ }
                if (robot == null) yield return null;
            }
            if (robot == null)
            {
                Debug.LogError("[Diag] timed out waiting for sean.robot.");
                Finish();
                yield break;
            }

            Control.ControlSubscriber controller = null;
            waitStart = Time.time;
            while (controller == null && Time.time - waitStart < 30f)
            {
                try { controller = sean.controller; } catch (Exception) { /* not resolved yet */ }
                if (controller == null) yield return null;
            }
            if (controller == null)
            {
                Debug.LogError("[Diag] timed out waiting for sean.controller.");
                Finish();
                yield break;
            }

            // The exact topic string the live component uses -- not assumed, read from the
            // instance, in case it's been overridden away from the class default in the scene.
            string topic = controller.Topic;
            Debug.Log("[Diag] controller type=" + controller.GetType().Name + " topic='" + topic + "'");
            ROSConnection.instance.Subscribe<RosMessageTypes.Geometry.MTwist>(topic, OnCmdVel);

            ArticulationBody artRoot = FindArticulationRoot(robot.base_link);
            Debug.Log("[Diag] articulation root found: " + (artRoot != null));

            Vector3 startPos = robot.transform.position;
            float startTime = Time.time;
            float nextLog = startTime;
            int lastLoggedCount = 0;

            while (Time.time - startTime < RunSeconds)
            {
                if (Time.time >= nextLog)
                {
                    Vector3 vel = artRoot != null ? artRoot.velocity : Vector3.zero;
                    Vector3 angVel = artRoot != null ? artRoot.angularVelocity : Vector3.zero;
                    Debug.Log(string.Format(
                        "[Diag] t={0:F1} totalReceived={1} (+{2}) lastLin=({3:F3},{4:F3}) lastAngZ={5:F3} robotPos=({6:F3},{7:F3},{8:F3}) artVel=({9:F3},{10:F3},{11:F3}) artAngVel=({12:F3},{13:F3},{14:F3})",
                        Time.time - startTime, totalReceived, totalReceived - lastLoggedCount,
                        lastLinX, lastLinY, lastAngZ,
                        robot.transform.position.x, robot.transform.position.y, robot.transform.position.z,
                        vel.x, vel.y, vel.z, angVel.x, angVel.y, angVel.z));
                    lastLoggedCount = totalReceived;
                    nextLog += 1f;
                }
                yield return null;
            }

            Debug.Log("[Diag] done. totalReceived=" + totalReceived
                + " total displacement=" + Vector3.Distance(startPos, robot.transform.position));
            Finish();
        }

        private void OnCmdVel(RosMessageTypes.Geometry.MTwist msg)
        {
            totalReceived++;
            lastLinX = msg.linear.x;
            lastLinY = msg.linear.y;
            lastAngZ = msg.angular.z;
        }

        private static ArticulationBody FindArticulationRoot(GameObject robotBase)
        {
            foreach (ArticulationBody body in robotBase.GetComponentsInChildren<ArticulationBody>())
            {
                if (body.isRoot)
                {
                    return body;
                }
            }
            return null;
        }

        private static void Finish()
        {
#if UNITY_EDITOR
            EditorApplication.Exit(0);
#else
            Application.Quit(0);
#endif
        }
    }
}
