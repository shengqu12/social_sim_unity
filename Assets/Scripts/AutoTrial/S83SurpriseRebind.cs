using System.Collections;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 83 PHASE 3. Rebinds the SurprisedReaction state's clip to the Kimodo b2 reaction at
    /// runtime, composing onto whatever controller the gait install left behind.
    ///
    /// WHY A SELF-BOOTSTRAPPING COMPONENT RATHER THAN A BOOTSTRAP LINE. AutoTrialBootstrap.cs is
    /// outside this ticket's write boundary, so nothing may add an attach call there. This
    /// discovers pedestrians itself, which also makes the feature work on the STOCK pipeline --
    /// a trial with no --mixamo-clip has no S41MixamoClipApplier at all, so a rebind hung off the
    /// applier could never reach it. V4 in the matrix is exactly that case.
    ///
    /// THE HANDLE. PedestrianModulator is attached to every non-Indifferent pedestrian
    /// (AutoTrialBootstrap.cs:1030) and Surprised is by definition non-Indifferent, so it is
    /// present whenever this feature is meaningful. Indifferent pedestrians never fire the
    /// Surprised trigger, so not reaching them costs nothing.
    ///
    /// THE RACE, AND HOW IT IS CLOSED. S41MixamoClipApplier defers its install one frame (the
    /// Animator rebind invalidates bone lookups), so "the applier component exists" is NOT "the
    /// gait is installed". Composing too early would build the surprise override on the STOCK
    /// controller and then have the gait install replace it wholesale, silently losing the
    /// rebind. This waits on the applier's GaitInstalled flag, with a timeout so a failed install
    /// degrades to "no rebind, loudly" rather than a hang.
    ///
    /// COMPOSITION. S79GaitOverrideBuilder.ApplySurpriseOverride mutates an already-installed
    /// AnimatorOverrideController in place, so the gait remap and the surprise remap end up in ONE
    /// controller. It does not nest override-on-override.
    ///
    /// SCOPE. Opt-in on AUTOTRIAL_S83_B2. Unset -- which is every run outside this ticket, planD
    /// included -- and this class does nothing at all: the bootstrap returns before creating
    /// anything.
    /// </summary>
    public class S83SurpriseRebind : MonoBehaviour
    {
        /// <summary>How long to wait for the gait install before giving up and reporting.</summary>
        public float installTimeoutSeconds = 10f;

        private static bool armed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (!S79GaitOverrideBuilder.B2Requested) { return; }
            if (armed) { return; }
            armed = true;
            var host = new GameObject("S83SurpriseRebindHost");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<S83SurpriseRebind>();
        }

        private IEnumerator Start()
        {
            AnimationClip b2 = S79GaitOverrideBuilder.LoadB2Clip();
            if (b2 == null)
            {
                Debug.LogError("[S83] rebind armed but the b2 clip could not be loaded -- "
                    + "leaving the stock pointing gesture in place.");
                yield break;
            }
            Debug.Log("[S83] armed; b2 clip '" + b2.name + "' length="
                + b2.length.ToString("F3") + "s");

            // Pedestrians spawn well after scene load, so poll rather than one-shot find.
            Scenario.Agents.PedestrianModulator ped = null;
            while (ped == null)
            {
                ped = Object.FindObjectOfType<Scenario.Agents.PedestrianModulator>();
                if (ped == null) { yield return new WaitForSeconds(0.25f); }
            }

            var animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(ped.gameObject);
            if (animator == null)
            {
                Debug.LogError("[S83] no Animator on '" + ped.name + "' -- cannot rebind.");
                yield break;
            }

            // Wait out the gait install if there is one (see class doc).
            var applier = ped.GetComponent<S41MixamoClipApplier>();
            if (applier != null)
            {
                float deadline = Time.time + installTimeoutSeconds;
                while (!applier.GaitInstalled && Time.time < deadline) { yield return null; }
                if (!applier.GaitInstalled)
                {
                    Debug.LogError("[S83] gait install did not complete within "
                        + installTimeoutSeconds + "s -- rebinding anyway; if the gait installs "
                        + "later it will overwrite this and the reaction will read as the stock "
                        + "pointing gesture.");
                }
            }
            yield return null;   // let the applier's own rebind settle

            string detail;
            var composed = S79GaitOverrideBuilder.ApplySurpriseOverride(
                animator.runtimeAnimatorController, b2, out detail);
            if (composed == null)
            {
                // The normal path here is a legacy wholesale swap: the generated single-state
                // controller carries no SurprisedReaction clip, because the swap deleted the state
                // machine. Loud, not silent -- the video would otherwise just look wrong.
                Debug.LogError("[S83] surprise rebind NOT applied on '" + ped.name + "' ("
                    + detail + "). Controller is '"
                    + (animator.runtimeAnimatorController != null
                        ? animator.runtimeAnimatorController.name : "NULL")
                    + "'. The stock pointing gesture stays in place.");
                yield break;
            }

            animator.runtimeAnimatorController = composed;
            Debug.Log("[S83] surprise rebind applied on '" + ped.name + "': " + detail);
            S79GaitOverrideBuilder.LogSurpriseVerification(animator, b2);
        }
    }
}
