using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 35 BLOCK 3 FIX 7: cyclist reads as running/leaping instead of pedaling.
    ///
    /// Root cause, confirmed by reading the actual asset chain (same "wrong controller" pattern
    /// as Session 32's Surprised bug, found independently this session): `Cyclist.prefab`'s
    /// Animator references `Bike Controller.controller` (guid 72bc3317cc14c2a41b2ea0cde2adee32)
    /// -- NOT `CyclistController.controller`, a DIFFERENT, unused controller sitting in the same
    /// folder whose own "Idle" state is already correctly wired to `anim_Relaxed_Pedal_Seated_
    /// Loop.FBX` (a genuine seated-pedaling clip) but is never actually referenced by the prefab.
    /// The controller ACTUALLY in use, `Bike Controller.controller`, has two states
    /// ("Armature_sepeda|jalan" and a second, confusingly also-named "Idle") that BOTH point at
    /// the exact same motion: a clip inside `Sepeda Facific Invert.fbx` whose own internal name
    /// is "jalan" (Indonesian for "walk/go") -- a walk-cycle clip, not a pedaling animation. This
    /// is why the cyclist visually reads as walking/running/leaping: it always was, regardless of
    /// the `isIdling` parameter toggling between the two states, because both states share the
    /// identical wrong motion.
    ///
    /// Fix: swap BOTH states' motion in `Bike Controller.controller` (the controller genuinely in
    /// use) to `anim_Relaxed_Pedal_Seated_Loop.FBX`'s own clip -- serialization-API-only, same
    /// established technique as every prior S2x/S3x*Fix.cs in this project, no YAML text edits.
    /// `CyclistController.controller` and its correct-but-unused wiring are left exactly as-is
    /// (harmless, orphaned, not this fix's concern -- may be worth deleting in a future cleanup
    /// pass but that's a separate decision from fixing the actually-live asset).
    ///
    /// -executeMethod SEAN.AutoTrial.S35CyclistPedalFix.Apply
    /// </summary>
    public static class S35CyclistPedalFix
    {
        private const string ControllerPath =
            "Assets/Resources/Prefabs/Community-informed Model/Cyclist/Bike Controller.controller";
        private const string PedalClipFbx =
            "Assets/Resources/Prefabs/Community-informed Model/Cyclist/Cycling Animation/anim_Relaxed_Pedal_Seated_Loop.FBX";

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

        private static bool ApplyInternal()
        {
            if (!System.IO.File.Exists(PedalClipFbx))
            {
                Debug.LogError("[S35CyclistPedalFix] expected pedal clip FBX missing: " + PedalClipFbx);
                return false;
            }
            AnimationClip pedalClip = LoadFirstRealClip(PedalClipFbx);
            if (pedalClip == null)
            {
                Debug.LogError("[S35CyclistPedalFix] could not locate a clip sub-asset in " + PedalClipFbx);
                return false;
            }
            Debug.Log("[S35CyclistPedalFix] loaded pedal clip '" + pedalClip.name + "' ("
                + pedalClip.length.ToString("F2") + "s) from " + PedalClipFbx);

            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[S35CyclistPedalFix] could not load " + ControllerPath);
                return false;
            }

            var sm = controller.layers[0].stateMachine;
            int swapped = 0;
            foreach (var childState in sm.states)
            {
                AnimatorState state = childState.state;
                Debug.Log("[S35CyclistPedalFix] state '" + state.name + "' motion before: "
                    + (state.motion != null ? state.motion.name : "NULL"));
                state.motion = pedalClip;
                swapped++;
                Debug.Log("[S35CyclistPedalFix] state '" + state.name + "' motion after: " + state.motion.name);
            }

            if (swapped == 0)
            {
                Debug.LogError("[S35CyclistPedalFix] no states found in " + ControllerPath + " -- nothing swapped.");
                return false;
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[S35CyclistPedalFix] swapped motion on " + swapped + " state(s) in " + ControllerPath);
            return true;
        }
    }
}
