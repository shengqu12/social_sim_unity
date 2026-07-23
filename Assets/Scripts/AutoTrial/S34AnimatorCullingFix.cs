using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 34 FIX 4: root cause of Surprised's reproducible ~12-13s clip-playback delay
    /// (Session 33 found the freeze/trigger fires correctly on schedule, but the visible gesture
    /// clip doesn't start playing until much later -- reproduced identically across three trials,
    /// not explained by transition wiring, which S32SocialForcesGestureFix already confirmed is
    /// instant/no-exit-time on the Any State -> gesture transition). Checked the actual Rocketbox
    /// prefab (Assets/Resources/Prefabs/Rocketbox/Business_Male_01.prefab): its Animator component
    /// is serialized with `m_CullingMode: 2` (AnimatorCullingMode.CullCompletely) -- Unity
    /// completely stops evaluating an Animator's state machine (no transitions, no clip playback
    /// advancement, nothing) whenever its Renderer isn't visible to any camera, resuming only once
    /// visible again. This project has an independent, already-flagged camera-framing defect
    /// (Session 31/33: the POV camera's "course" yaw mode locks onto the ROBOT's own heading, not
    /// the pedestrian's bearing) that can plausibly leave a reacting pedestrian out of frame for a
    /// multi-second stretch -- exactly the kind of window that would silently pause a
    /// CullCompletely Animator's own trigger-driven transition until the camera re-centers,
    /// producing precisely this symptom (game-logic freeze on schedule, visible clip delayed by
    /// however long the pedestrian was out of frame).
    ///
    /// Fix: force AlwaysAnimate on every spawned pedestrian's locomotion Animator at bootstrap --
    /// a one-line runtime property set (Animator.cullingMode is a plain public property, not part
    /// of any forbidden file's own logic), general and safe (a state machine that keeps evaluating
    /// while off-screen costs a little more CPU, never changes visible behavior for anything that
    /// WAS already visible the whole time -- it only prevents behavior state from silently
    /// stalling while off-screen). Verify: S32SurprisedRuntimeProbe's logged gap between trigger
    /// fire and visible clip start should collapse from ~12-13s to near-zero.
    /// </summary>
    public class S34AnimatorCullingFix : MonoBehaviour
    {
        void Awake()
        {
            Animator animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator != null)
            {
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                Debug.LogWarning("[S34AnimatorCullingFix] set cullingMode=" + animator.cullingMode
                    + " on '" + animator.gameObject.name + "'");
            }
        }
    }
}
