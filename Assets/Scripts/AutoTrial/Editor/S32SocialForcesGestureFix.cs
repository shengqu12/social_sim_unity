using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 32 FIX B2 -- THE REAL FIX. Runtime probe (S32SurprisedRuntimeProbe) proved
    /// business_male_01 (and every other Zone A/generic-Rocketbox pick, per AutoTrialBootstrap's
    /// AppearanceAvatar path) actually runs `Assets/Resources/Animation/
    /// SocialForcesAnimatorController.controller` -- NOT `BaseSFControllerNormalized.controller`,
    /// which is what S31's S31GestureAnimationFix.cs edited. SocialForcesAnimatorController has
    /// only Grounded/Crouching/Airborne states and Forward/Turn/Crouch/OnGround/Jump/JumpLeg
    /// params -- no Surprised trigger, no SurprisedReaction state, ever. This is why
    /// PedestrianModulator.ModulateSurprised()'s `self.TriggerAnimation("Surprised")` call
    /// silently no-ops for business_male_01 (Unity logs "Parameter 'X' does not exist" for a
    /// missing trigger, confirmed in a prior trial's unity.log for the sibling "AssertiveGesture"
    /// case) -- NOT a retarget failure, NOT a layer-weight issue (both ruled out this session by
    /// checking the wrong controller first). This predates Session 32 and arguably predates
    /// Session 31 too -- S31's own gesture work was real and correctly wired, just onto an asset
    /// that isn't in this project's actual default-roster demo appearance's live control path.
    ///
    /// Fix: same technique as S31GestureAnimationFix.cs (serialization-API-only, no YAML edits),
    /// applied to the RIGHT controller this time -- adds "Surprised"/"AssertiveGesture" Trigger
    /// parameters + corresponding states (motion = the SAME already-imported Pointing_towards/
    /// point_backwards clips from Assets/CustomAnimations/S31Mixamo/) to
    /// SocialForcesAnimatorController, Any State -> gesture -> back to Grounded (this
    /// controller's own default/locomotion state). SocialForcesAnimatorController is a single
    /// shared asset (not per-rig duplicated, confirmed via AssetDatabase.FindAssets returning
    /// exactly one match) -- fixing it once covers every Zone A appearance that references it,
    /// a bounded, in-scope fix (NOT the same as Session 28's excluded "generalize phone-
    /// distraction to ~140 rigs" animation-engineering task -- this is one shared controller
    /// asset, not per-rig customization).
    ///
    /// -executeMethod SEAN.AutoTrial.S32SocialForcesGestureFix.Apply
    /// </summary>
    public static class S32SocialForcesGestureFix
    {
        private const string ControllerPath = "Assets/Resources/Animation/SocialForcesAnimatorController.controller";
        private const string PointingTowardsFbx = "Assets/CustomAnimations/S31Mixamo/Pointing_towards.fbx";
        private const string PointBackwardsFbx = "Assets/CustomAnimations/S31Mixamo/point_backwards.fbx";

        public static void Apply()
        {
            bool ok = ApplyInternal();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static AnimationClip LoadFirstRealClip(string assetPath)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            AnimationClip best = null;
            foreach (var a in assets)
            {
                var clip = a as AnimationClip;
                if (clip == null) continue;
                if (clip.name.StartsWith("__preview__")) continue;
                if (best == null || clip.length > best.length) best = clip;
            }
            return best;
        }

        private static void AddGestureState(AnimatorController controller, AnimatorStateMachine sm,
            string stateName, string paramName, AnimationClip motion, AnimatorState defaultState)
        {
            bool alreadyHasParam = controller.parameters.Any(p => p.name == paramName);
            if (!alreadyHasParam)
            {
                controller.AddParameter(paramName, AnimatorControllerParameterType.Trigger);
            }
            bool alreadyHasState = sm.states.Any(s => s.state.name == stateName);
            if (alreadyHasState)
            {
                Debug.Log("[S32SocialForcesGestureFix] state '" + stateName + "' already present -- not re-added.");
                return;
            }
            AnimatorState gestureState = sm.AddState(stateName);
            gestureState.motion = motion;
            gestureState.speed = 1.0f;

            AnimatorStateTransition inTransition = sm.AddAnyStateTransition(gestureState);
            inTransition.hasExitTime = false;
            inTransition.duration = 0.15f;
            inTransition.AddCondition(AnimatorConditionMode.If, 0, paramName);

            if (defaultState != null && defaultState != gestureState)
            {
                AnimatorStateTransition outTransition = gestureState.AddTransition(defaultState);
                outTransition.hasExitTime = true;
                outTransition.exitTime = 0.9f;
                outTransition.duration = 0.25f;
            }
            Debug.Log("[S32SocialForcesGestureFix] added state '" + stateName + "' (motion=" + motion.name
                + ", " + motion.length.ToString("F2") + "s) + trigger '" + paramName + "' to " + ControllerPath);
        }

        private static bool ApplyInternal()
        {
            if (!System.IO.File.Exists(PointingTowardsFbx) || !System.IO.File.Exists(PointBackwardsFbx))
            {
                Debug.LogError("[S32SocialForcesGestureFix] expected FBX assets missing.");
                return false;
            }
            AnimationClip pointingTowardsClip = LoadFirstRealClip(PointingTowardsFbx);
            AnimationClip pointBackwardsClip = LoadFirstRealClip(PointBackwardsFbx);
            if (pointingTowardsClip == null || pointBackwardsClip == null)
            {
                Debug.LogError("[S32SocialForcesGestureFix] could not locate clip sub-assets.");
                return false;
            }

            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[S32SocialForcesGestureFix] could not load " + ControllerPath);
                return false;
            }
            var sm = controller.layers[0].stateMachine;
            AnimatorState defaultState = sm.defaultState;
            Debug.Log("[S32SocialForcesGestureFix] default state: " + (defaultState != null ? defaultState.name : "NULL"));

            AddGestureState(controller, sm, "SurprisedReaction", "Surprised", pointingTowardsClip, defaultState);
            AddGestureState(controller, sm, "AssertiveGesture", "AssertiveGesture", pointBackwardsClip, defaultState);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return true;
        }
    }
}
