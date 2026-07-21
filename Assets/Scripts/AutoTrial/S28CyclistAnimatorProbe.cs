using System.Collections;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 28 PART 1 diagnostic: one-shot, gated by -diagCyclistAnimator / DIAG_CYCLIST_ANIMATOR=1.
    /// Logs every Animator found under the live cyclist container's spawned avatar, its resolved
    /// runtimeAnimatorController, and which one AvatarAnimatorUtility.GetLocomotionAnimator would
    /// pick -- settles empirically whether the correct controller is actually live at runtime.
    /// </summary>
    public class S28CyclistAnimatorProbe : MonoBehaviour
    {
        private const string EnvFlag = "DIAG_CYCLIST_ANIMATOR";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            bool enabled = false;
            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-diagCyclistAnimator") { enabled = true; break; }
            }
            if (!enabled && System.Environment.GetEnvironmentVariable(EnvFlag) == "1")
            {
                enabled = true;
            }
            if (!enabled) { return; }

            var go = new GameObject("S28CyclistAnimatorProbe");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var probe = go.AddComponent<S28CyclistAnimatorProbe>();
            probe.StartCoroutine(probe.Run());
        }

        private IEnumerator Run()
        {
            float waitStart = Time.time;
            Scenario.Agents.AppearanceAvatar container = null;
            while (container == null && Time.time - waitStart < 45f)
            {
                container = FindObjectOfType<Scenario.Agents.AppearanceAvatar>();
                if (container == null) yield return null;
            }
            if (container == null)
            {
                Debug.LogError("[S28CyclistAnimatorProbe] timed out waiting for an AppearanceAvatar in the scene.");
                yield break;
            }

            GameObject avatarRoot = container.avatarObject != null ? container.avatarObject : container.gameObject;
            Debug.Log("[S28CyclistAnimatorProbe] container=" + container.gameObject.name + " avatarObject="
                + (container.avatarObject != null ? container.avatarObject.name : "(null)"));

            var animators = avatarRoot.GetComponentsInChildren<Animator>(true);
            Debug.Log("[S28CyclistAnimatorProbe] Found " + animators.Length + " Animator(s) under " + avatarRoot.name + ":");
            foreach (var a in animators)
            {
                string avatarName = a.avatar != null ? a.avatar.name : "(null)";
                bool isHuman = a.avatar != null && a.avatar.isHuman;
                string controllerName = a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "(null)";
                Debug.Log("[S28CyclistAnimatorProbe]   Animator on '" + a.gameObject.name + "' (path="
                    + GetPath(a.transform) + "): avatar=" + avatarName + " isHuman=" + isHuman
                    + " runtimeAnimatorController=" + controllerName + " applyRootMotion=" + a.applyRootMotion
                    + " enabled=" + a.enabled + " isActiveAndEnabled=" + a.isActiveAndEnabled);
                if (a.isInitialized && a.layerCount > 0)
                {
                    var st = a.GetCurrentAnimatorStateInfo(0);
                    var clipInfo = a.GetCurrentAnimatorClipInfo(0);
                    string clips = "";
                    foreach (var ci in clipInfo) clips += (ci.clip != null ? ci.clip.name : "(null)") + "; ";
                    Debug.Log("[S28CyclistAnimatorProbe]     currentState normalizedTime=" + st.normalizedTime.ToString("F2")
                        + " playing clips: " + clips);
                }
            }

            var picked = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(avatarRoot);
            Debug.Log("[S28CyclistAnimatorProbe] GetLocomotionAnimator(avatarRoot) picked: "
                + (picked != null ? picked.gameObject.name + " (path=" + GetPath(picked.transform) + ")" : "(null)"));
        }

        private static string GetPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
