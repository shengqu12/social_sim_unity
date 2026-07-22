using System;
using System.Globalization;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 30R, Howard priority #4 ("surprised" reaction reads as too theatrical) -- one-off
    /// fix, run once via the guarded launcher then left in the repo for provenance (same
    /// convention as S21PhoneUserContainerFix.cs / S28CyclistContainerFix.cs).
    ///
    /// Diagnosis (read-only recon before writing anything): grepped every .cs file under
    /// Assets/Scripts/AutoTrial/** and Assets/Scripts/SEAN/Scenario/Agents/PedestrianModulator.cs
    /// for a literal "1.8"/"clip speed" constant -- none exists in code. The value lives in the
    /// shared Animator Controller asset instead: BaseSFControllerNormalized.controller's
    /// "SurprisedReaction" state has m_Speed: 1.8 baked in (m_SpeedParameterActive: 0, i.e. not
    /// parameter-driven -- a flat per-state playback-speed multiplier). This is the controller
    /// PedestrianModulator.ModulateSurprised()/TriggerAnimation("Surprised") plays for every
    /// Rocketbox-rig pedestrian with the Surprised personality -- so this one value governs the
    /// theatricality Howard flagged across the whole roster, not a per-character setting.
    ///
    /// Fix mechanism: same discipline as the two prior container fixes -- serialization API only
    /// (UnityEditor.Animations.AnimatorController), never hand-editing the .controller YAML.
    /// Extends the "sanctioned pattern" the guardrails call out for .prefab fixes to a .controller
    /// asset for the same reason: no text-editing, no .unity/.prefab touched, fully attributable
    /// via git diff, and (like the container fixes) explicitly declared as `expected_dirty` to
    /// run_trial.py's tracked-file revert guard rather than silently landing.
    ///
    /// Target speed is read from the SURPRISED_REACTION_SPEED env var (float, e.g. "1.3") so the
    /// same script drives both the A/B sweep (Session 30R STEP 4: candidates 1.2 and 1.3 against
    /// the 1.8 baseline) and the final landing call. No default -- an unset/unparseable value
    /// fails loudly rather than silently reusing whatever the asset already has.
    ///
    /// -executeMethod SEAN.AutoTrial.S30RSurprisedReactionSpeedFix.Apply
    /// </summary>
    public static class S30RSurprisedReactionSpeedFix
    {
        private const string ControllerPath = "Assets/IVI/Controllers/BaseSFControllerNormalized.controller";
        private const string StateName = "SurprisedReaction";
        private const string EnvVar = "SURPRISED_REACTION_SPEED";

        public static void Apply()
        {
            bool ok = ApplyInternal();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool ApplyInternal()
        {
            string raw = System.Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrEmpty(raw) || !float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float newSpeed))
            {
                Debug.LogError("[S30RSurprisedReactionSpeedFix] " + EnvVar + " env var missing or not a valid float (got '" + raw + "'). Refusing to guess.");
                return false;
            }
            if (newSpeed <= 0f)
            {
                Debug.LogError("[S30RSurprisedReactionSpeedFix] " + EnvVar + "=" + newSpeed + " is not a positive playback speed.");
                return false;
            }

            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[S30RSurprisedReactionSpeedFix] Could not load AnimatorController at " + ControllerPath);
                return false;
            }

            bool found = false;
            float oldSpeed = 0f;
            foreach (var layer in controller.layers)
            {
                var states = layer.stateMachine.states;
                foreach (var childState in states)
                {
                    if (childState.state.name == StateName)
                    {
                        oldSpeed = childState.state.speed;
                        childState.state.speed = newSpeed;
                        EditorUtility.SetDirty(childState.state);
                        found = true;
                    }
                }
            }

            if (!found)
            {
                Debug.LogError("[S30RSurprisedReactionSpeedFix] No state named '" + StateName + "' found in " + ControllerPath);
                return false;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[S30RSurprisedReactionSpeedFix] " + ControllerPath + " state '" + StateName + "' m_Speed: "
                + oldSpeed.ToString("F3") + " -> " + newSpeed.ToString("F3") + ". Saved via AnimatorController API.");
            return true;
        }
    }
}
