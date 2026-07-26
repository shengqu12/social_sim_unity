using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 41 TASK 2 items 2 and 3, applied to the controller the Zone A roster actually uses.
    ///
    /// TASK 1's graph dump established that all five personalities resolve to
    /// Assets/Resources/Animation/SocialForcesAnimatorController.controller -- NOT
    /// Assets/IVI/Controllers/BaseSFControllerNormalized.controller (which no roster entry loads
    /// for Zone A, and which is off-limits without Nathan's authorization). Only the former is
    /// touched here. The IVI controller IS still reached by four Zone B specials (dog_walker,
    /// female_child, male_child, white_cane_user) via AppearanceAvatar.animationController, and is
    /// deliberately left alone -- see REPORT.md Session 41 TASK 2.
    ///
    /// Item 1 of the ticket ("Has Exit Time = false on reaction transitions") is NOT implemented
    /// here because TASK 1 measured it as already false on every reaction ENTRY transition in every
    /// controller in the project -- it is a verified no-op, not a skipped step.
    ///
    /// Edits go through UnityEditor.Animations' typed API (AnimatorStateTransition.duration /
    /// AnimatorState.speed) + SetDirty + SaveAssets -- never a hand-edited YAML field or GUID.
    ///
    /// Run via run_trial.py --exec-editor-method with an explicit --allow-dirty for the controller:
    ///   python3 tools/run_trial.py \
    ///     --exec-editor-method SEAN.AutoTrial.S41ReactionTransitionTuning.Apply \
    ///     --allow-dirty Assets/Resources/Animation/SocialForcesAnimatorController.controller
    ///
    /// Revert is the same call against S41ReactionTransitionTuning.Revert, which restores the
    /// authored-baseline values TASK 1 recorded (entry duration 0.15s, state speed 1.0).
    /// </summary>
    public static class S41ReactionTransitionTuning
    {
        private const string ControllerPath = "Assets/Resources/Animation/SocialForcesAnimatorController.controller";

        private static readonly HashSet<string> ReactionStates =
            new HashSet<string> { "SurprisedReaction", "AssertiveGesture" };

        // Ticket TASK 2 item 2: reaction-entry crossfade. Baseline measured by TASK 1 is 0.15s
        // (the ticket assumed 0.25s -- that is the IVI controller's value, not this one's).
        // Locomotion transitions are deliberately NOT touched: the ticket calls this out
        // explicitly (changing them causes foot sliding), and in this controller they are
        // normalized-duration anyway, so a seconds value would not even mean the same thing.
        private const float TunedEntryDuration = 0.08f;
        private const float BaselineEntryDuration = 0.15f;

        // Ticket TASK 2 item 3: reaction state playback multiplier, 1.15 (explicitly "don't jump
        // straight to 1.3").
        private const float TunedStateSpeed = 1.15f;
        private const float BaselineStateSpeed = 1.0f;

        public static void Apply() { Run(TunedEntryDuration, TunedStateSpeed, "APPLY"); }
        public static void Revert() { Run(BaselineEntryDuration, BaselineStateSpeed, "REVERT"); }

        // Item 2 only -- lets the ticket's "one change at a time, measure between" rule be honored
        // instead of landing both edits in a single indivisible step.
        public static void ApplyDurationOnly() { Run(TunedEntryDuration, BaselineStateSpeed, "APPLY-DURATION-ONLY"); }

        private static void Run(float entryDuration, float stateSpeed, string mode)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[S41Tune] could not load " + ControllerPath);
                EditorApplication.Exit(1);
                return;
            }

            int durationEdits = 0;
            int speedEdits = 0;

            foreach (var layer in controller.layers)
            {
                var sm = layer.stateMachine;

                // Reaction ENTRY transitions live on Any State in this controller (verified by the
                // TASK 1 dump) -- these are the ones that gate "how long until the reaction starts".
                foreach (var t in sm.anyStateTransitions)
                {
                    if (t.destinationState == null || !ReactionStates.Contains(t.destinationState.name)) { continue; }
                    // hasFixedDuration must be true for `duration` to mean seconds at all. TASK 1
                    // recorded it already true here; asserted rather than assumed so a future
                    // controller change can't silently reinterpret 0.08 as 8% of a clip.
                    if (!t.hasFixedDuration)
                    {
                        Debug.LogError("[S41Tune] ABORT: AnyState -> " + t.destinationState.name
                            + " has hasFixedDuration=false, so 'duration' is normalized, not seconds. "
                            + "Refusing to write a seconds value into a normalized field.");
                        EditorApplication.Exit(1);
                        return;
                    }
                    Debug.Log(string.Format("[S41Tune] {0} entry AnyState -> {1}: duration {2:F4} -> {3:F4} (hasExitTime={4}, untouched)",
                        mode, t.destinationState.name, t.duration, entryDuration, t.hasExitTime));
                    t.duration = entryDuration;
                    durationEdits++;
                }

                foreach (var cs in sm.states)
                {
                    if (!ReactionStates.Contains(cs.state.name)) { continue; }
                    Debug.Log(string.Format("[S41Tune] {0} state '{1}': speed {2:F4} -> {3:F4}",
                        mode, cs.state.name, cs.state.speed, stateSpeed));
                    cs.state.speed = stateSpeed;
                    speedEdits++;
                    EditorUtility.SetDirty(cs.state);
                }
            }

            if (durationEdits == 0 && speedEdits == 0)
            {
                Debug.LogError("[S41Tune] ABORT: matched nothing -- reaction state names may have changed.");
                EditorApplication.Exit(1);
                return;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log(string.Format("[S41Tune] {0} DONE: {1} entry transition duration(s), {2} state speed(s) written to {3}",
                mode, durationEdits, speedEdits, ControllerPath));
            EditorApplication.Exit(0);
        }

        /// <summary>Read-back verification -- prints current on-disk values, changes nothing.</summary>
        public static void Verify()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null) { EditorApplication.Exit(1); return; }
            foreach (var layer in controller.layers)
            {
                foreach (var t in layer.stateMachine.anyStateTransitions)
                {
                    if (t.destinationState == null || !ReactionStates.Contains(t.destinationState.name)) { continue; }
                    Debug.Log(string.Format("[S41Tune] VERIFY entry -> {0}: hasExitTime={1} hasFixedDuration={2} duration={3:F4}",
                        t.destinationState.name, t.hasExitTime, t.hasFixedDuration, t.duration));
                }
                foreach (var cs in layer.stateMachine.states)
                {
                    if (!ReactionStates.Contains(cs.state.name)) { continue; }
                    Debug.Log(string.Format("[S41Tune] VERIFY state '{0}': speed={1:F4} clip={2} len={3:F3}",
                        cs.state.name, cs.state.speed,
                        cs.state.motion != null ? cs.state.motion.name : "NULL",
                        cs.state.motion != null ? ((AnimationClip)cs.state.motion).length : -1f));
                }
            }
            EditorApplication.Exit(0);
        }
    }
}
