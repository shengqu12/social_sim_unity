using System.IO;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 32 FIX B2 diagnostic: runtime probe logging Animator state + right-arm bone
    /// rotation for a Surprised pedestrian, gated by env var AUTOTRIAL_S32_PROBE_PATH (only
    /// attached by AutoTrialBootstrap when that env var is set and personality==Surprised).
    /// Appends one CSV line per frame: t, stateName, normalizedTime, layerWeight,
    /// rightUpperArmLocalEuler(x,y,z), rightForearmLocalEuler(x,y,z). Diagnostic-only, no
    /// behavior change -- read-only observation of an existing Animator.
    /// </summary>
    public class S32SurprisedRuntimeProbe : MonoBehaviour
    {
        private Animator animator;
        private StreamWriter writer;

        void Awake()
        {
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
        }

        void Start()
        {
            string path = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S32_PROBE_PATH");
            if (string.IsNullOrEmpty(path) || animator == null) { enabled = false; return; }
            writer = new StreamWriter(path, false);
            // Loop 1 Bug 5: added distToRobot (computed in the SAME Time.time clock as every
            // other column here) so the actual gap between "distance crosses surpriseRadius" and
            // "clip visibly starts" can be measured directly, without needing to reconcile this
            // probe's raw Time.time against frames.csv's separate capture-relative clock.
            writer.WriteLine("t,state,normalizedTime,layerWeight,rArmX,rArmY,rArmZ,rForeX,rForeY,rForeZ,cullingMode,isVisible,distToRobot");
            Debug.LogWarning("[S32SurprisedRuntimeProbe] animator on gameObject='" + animator.gameObject.name
                + "' controller=" + (animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "NULL")
                + " layerCount=" + animator.layerCount + " isHuman=" + (animator.avatar != null ? animator.avatar.isHuman.ToString() : "no-avatar"));
        }

        void LateUpdate()
        {
            if (writer == null || animator == null) return;
            var info = animator.GetCurrentAnimatorStateInfo(0);
            string stateName = info.IsName("SurprisedReaction") ? "SurprisedReaction"
                : info.IsName("Locomotion") ? "Locomotion"
                : info.IsName("Idling") ? "Idling" : "Other:" + info.shortNameHash;
            var clipInfos = animator.GetCurrentAnimatorClipInfo(0);
            if (clipInfos.Length > 0 && clipInfos[0].clip != null && clipInfos[0].clip.name == "mixamo.com")
            {
                stateName = "PLAYING_MIXAMO_CLIP";
            }
            var rArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var rFore = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            Vector3 rArmE = rArm != null ? rArm.localEulerAngles : Vector3.zero;
            Vector3 rForeE = rFore != null ? rFore.localEulerAngles : Vector3.zero;
            // Session 34 FIX 4: directly log cullingMode + whether any renderer under this
            // GameObject is currently visible to a camera -- settles whether the S33-flagged
            // multi-second play delay correlates with the pedestrian being out of frame.
            bool anyVisible = false;
            foreach (var r in GetComponentsInChildren<Renderer>())
            {
                if (r.isVisible) { anyVisible = true; break; }
            }
            float distToRobot = -1f;
            if (SEAN.instance != null)
            {
                try
                {
                    Vector3 d = SEAN.instance.robot.position - transform.position;
                    d.y = 0f;
                    distToRobot = d.magnitude;
                }
                catch (System.Exception) { }
            }

            writer.WriteLine(string.Format("{0:F3},{1},{2:F3},{3:F3},{4:F2},{5:F2},{6:F2},{7:F2},{8:F2},{9:F2},{10},{11},{12:F3}",
                Time.time, stateName, info.normalizedTime, animator.GetLayerWeight(0),
                rArmE.x, rArmE.y, rArmE.z, rForeE.x, rForeE.y, rForeE.z,
                animator.cullingMode, anyVisible, distToRobot));
            writer.Flush();
        }

        void OnDestroy()
        {
            if (writer != null) { writer.Close(); writer = null; }
        }
    }
}
