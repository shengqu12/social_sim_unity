using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 31 FIX 6 -- one-off fix, run once via the guarded launcher then left in the repo
    /// for provenance (same convention as S21PhoneUserContainerFix.cs / S28CyclistContainerFix.cs /
    /// S30RSurprisedReactionSpeedFix.cs).
    ///
    /// Imports two new Mixamo FBX clips (one-time copy this session from ~/Downloads/Mixamo/ into
    /// Assets/CustomAnimations/S31Mixamo/, tracked in git from here on -- no runtime copy step,
    /// this is a static asset like every other character/animation file in Assets/) and wires
    /// them in:
    ///   (a) REPLACES SurprisedReaction's motion (Assets/IVI/Controllers/
    ///       BaseSFControllerNormalized.controller) with Pointing_towards.fbx's clip -- Howard's
    ///       standing complaint was that the OLD clip reads as too theatrical even at Session 30R's
    ///       landed speed (1.3); the new clip is the fix, not a further speed tweak. Reuses the
    ///       EXISTING "Surprised" trigger parameter and transition wiring (PedestrianModulator.
    ///       ModulateSurprised() already calls self.TriggerAnimation("Surprised") -- untouched),
    ///       so only the state's motion changes, nothing about how it's triggered.
    ///   (b) ADDS a new "AssertiveGesture" state + Trigger parameter to the same controller, motion
    ///       = point_backwards.fbx's clip, wired Any State -> AssertiveGesture -> back to the
    ///       controller's own existing default state (exit-time based, no new locomotion state
    ///       invented). Assertive personality has no existing TriggerAnimation() call anywhere
    ///       (ModulateAssertive() in PedestrianModulator.cs only suppresses robotRepulsion, no
    ///       animation) -- PedestrianModulator.cs is outside this project's writable scope, so
    ///       firing this new trigger is done by a SEPARATE runtime MonoBehaviour
    ///       (S31AssertiveGestureTrigger.cs, non-Editor, added at bootstrap time only for
    ///       Assertive) rather than editing that file.
    ///
    /// Import settings for both FBX files match the EXISTING retargeted-Mixamo-clip precedent
    /// already in this repo (Assets/CustomAnimations/Texting And Walking.fbx, used by phone_user's
    /// texting layer): animationType=Human, avatarSetup=CreateFromThisModel (NOT copied from a
    /// Rocketbox avatar) -- Mecanim's humanoid muscle-space retargeting handles playback on any
    /// other Humanoid avatar (Rocketbox included) automatically at runtime; no explicit bone
    /// remapping needed, matching how the texting clip already works today.
    ///
    /// -executeMethod SEAN.AutoTrial.S31GestureAnimationFix.Apply
    /// </summary>
    public static class S31GestureAnimationFix
    {
        private const string ControllerPath = "Assets/IVI/Controllers/BaseSFControllerNormalized.controller";
        private const string SurprisedStateName = "SurprisedReaction";
        private const string PointingTowardsFbx = "Assets/CustomAnimations/S31Mixamo/Pointing_towards.fbx";
        private const string PointBackwardsFbx = "Assets/CustomAnimations/S31Mixamo/point_backwards.fbx";
        private const string AssertiveGestureStateName = "AssertiveGesture";
        private const string AssertiveGestureParam = "AssertiveGesture";

        public static void Apply()
        {
            bool ok = ApplyInternal();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool ConfigureHumanoidImport(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError("[S31GestureAnimationFix] No ModelImporter at " + assetPath);
                return false;
            }
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            EditorUtility.SetDirty(importer);
            importer.SaveAndReimport();
            return true;
        }

        private static AnimationClip LoadFirstRealClip(string assetPath)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            AnimationClip best = null;
            foreach (var a in assets)
            {
                var clip = a as AnimationClip;
                if (clip == null) continue;
                // Mixamo FBX imports sometimes carry a "__preview__" clip alongside the real one --
                // skip it, prefer the longest real clip if more than one candidate survives.
                if (clip.name.StartsWith("__preview__")) continue;
                if (best == null || clip.length > best.length) best = clip;
            }
            return best;
        }

        private static bool ApplyInternal()
        {
            if (!System.IO.File.Exists(PointingTowardsFbx) || !System.IO.File.Exists(PointBackwardsFbx))
            {
                Debug.LogError("[S31GestureAnimationFix] Expected FBX assets missing -- " + PointingTowardsFbx
                    + " / " + PointBackwardsFbx + ".");
                return false;
            }

            AssetDatabase.Refresh();

            if (!ConfigureHumanoidImport(PointingTowardsFbx)) return false;
            if (!ConfigureHumanoidImport(PointBackwardsFbx)) return false;
            AssetDatabase.Refresh();

            AnimationClip pointingTowardsClip = LoadFirstRealClip(PointingTowardsFbx);
            AnimationClip pointBackwardsClip = LoadFirstRealClip(PointBackwardsFbx);
            if (pointingTowardsClip == null || pointBackwardsClip == null)
            {
                Debug.LogError("[S31GestureAnimationFix] Could not locate an AnimationClip sub-asset in "
                    + "one or both imported FBX files (pointingTowardsClip=" + pointingTowardsClip
                    + ", pointBackwardsClip=" + pointBackwardsClip + ").");
                return false;
            }
            Debug.Log("[S31GestureAnimationFix] Loaded clips: Pointing_towards='" + pointingTowardsClip.name
                + "' (" + pointingTowardsClip.length.ToString("F2") + "s), point_backwards='"
                + pointBackwardsClip.name + "' (" + pointBackwardsClip.length.ToString("F2") + "s)");

            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[S31GestureAnimationFix] Could not load AnimatorController at " + ControllerPath);
                return false;
            }

            // ---- FIX 6(a): replace SurprisedReaction's motion ----
            bool foundSurprised = false;
            AnimatorState surprisedState = null;
            foreach (var layer in controller.layers)
            {
                foreach (var childState in layer.stateMachine.states)
                {
                    if (childState.state.name == SurprisedStateName)
                    {
                        surprisedState = childState.state;
                        Motion oldMotion = surprisedState.motion;
                        surprisedState.motion = pointingTowardsClip;
                        // New clip, new natural feel -- reset to 1.0 (authored speed) rather than
                        // carrying over Session 30R's 1.3 (tuned for the OLD clip's own timing).
                        surprisedState.speed = 1.0f;
                        EditorUtility.SetDirty(surprisedState);
                        Debug.Log("[S31GestureAnimationFix] " + SurprisedStateName + ".motion: "
                            + (oldMotion != null ? oldMotion.name : "null") + " -> " + pointingTowardsClip.name
                            + " (speed reset to 1.0)");
                        foundSurprised = true;
                    }
                }
            }
            if (!foundSurprised)
            {
                Debug.LogError("[S31GestureAnimationFix] No state named '" + SurprisedStateName + "' found in " + ControllerPath);
                return false;
            }

            // ---- FIX 6(b): add AssertiveGesture state + trigger ----
            var baseLayer = controller.layers[0];
            var sm = baseLayer.stateMachine;

            bool alreadyHasParam = controller.parameters.Any(p => p.name == AssertiveGestureParam);
            if (!alreadyHasParam)
            {
                controller.AddParameter(AssertiveGestureParam, AnimatorControllerParameterType.Trigger);
            }

            bool alreadyHasState = sm.states.Any(s => s.state.name == AssertiveGestureStateName);
            if (!alreadyHasState)
            {
                AnimatorState gestureState = sm.AddState(AssertiveGestureStateName);
                gestureState.motion = pointBackwardsClip;
                gestureState.speed = 1.0f;

                AnimatorStateTransition inTransition = sm.AddAnyStateTransition(gestureState);
                inTransition.hasExitTime = false;
                inTransition.duration = 0.15f;
                inTransition.AddCondition(AnimatorConditionMode.If, 0, AssertiveGestureParam);

                AnimatorState defaultState = sm.defaultState;
                if (defaultState != null && defaultState != gestureState)
                {
                    AnimatorStateTransition outTransition = gestureState.AddTransition(defaultState);
                    outTransition.hasExitTime = true;
                    outTransition.exitTime = 0.9f;
                    outTransition.duration = 0.25f;
                }
                else
                {
                    Debug.LogWarning("[S31GestureAnimationFix] Controller's own defaultState was null/self -- "
                        + "AssertiveGesture has no return transition wired (will hold on its own last frame "
                        + "until Any-State-retriggered elsewhere). Flagging, not failing outright.");
                }

                EditorUtility.SetDirty(controller);
                Debug.Log("[S31GestureAnimationFix] Added state '" + AssertiveGestureStateName + "' (motion="
                    + pointBackwardsClip.name + ") + trigger parameter '" + AssertiveGestureParam + "' to "
                    + ControllerPath + ", Any State -> gesture -> back to '"
                    + (defaultState != null ? defaultState.name : "?") + "'.");
            }
            else
            {
                Debug.Log("[S31GestureAnimationFix] State '" + AssertiveGestureStateName + "' already present -- not re-added.");
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return true;
        }
    }
}
