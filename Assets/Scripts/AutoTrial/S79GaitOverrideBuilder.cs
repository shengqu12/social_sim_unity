using System.Collections.Generic;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 79. Builds a runtime <see cref="AnimatorOverrideController"/> that swaps ONLY the
    /// forward-locomotion clip of the pedestrian's existing controller for a generated gait clip
    /// (Kimodo or Mixamo), leaving every state, parameter and transition in place.
    ///
    /// WHY THIS EXISTS (S78 root cause). S41MixamoClipApplier used to do
    ///
    ///     animator.runtimeAnimatorController = rac;
    ///
    /// where `rac` is one of the generated SINGLE-STATE, ZERO-PARAMETER controllers
    /// (S41MixamoControllerGen / KIMODO_UNITY_STEPS). That is a wholesale replacement, so the
    /// moment a gait was applied the pedestrian lost:
    ///   * every reaction STATE   -- SurprisedReaction, AssertiveGesture
    ///   * every PARAMETER        -- Surprised, AssertiveGesture, Forward, Strafe, Idling
    ///   * the Idle node          -- so a stopped pedestrian kept cycling its walk clip
    /// S78 measured the consequence: `[S41Latency] T_SIGNAL=-1 T_STATE=-1` (the trigger fired,
    /// the state was never entered) and "Parameter 'Surprised' does not exist." in every log.
    ///
    /// WHAT THIS DOES INSTEAD. An AnimatorOverrideController is a thin remap of clip->clip on top
    /// of a base controller: the state machine, its parameters and its transitions are the base
    /// controller's, unchanged. Overriding the single forward-walk clip therefore puts the
    /// generated gait on the locomotion blend tree's forward node and leaves everything else --
    /// idle, strafes, and both reaction states -- exactly as authored.
    ///
    /// THE CLIP-SHARING HAZARD, AND WHY IT IS SAFE HERE. Overrides key on CLIP IDENTITY, so if a
    /// clip were shared between locomotion and a reaction state, overriding it would leak the gait
    /// into the reaction. Verified read-only against the controller the pedestrians ACTUALLY run,
    /// Assets/Resources/Animation/SocialForcesAnimatorController.controller -- confirmed live by
    /// `[S41Latency] ... controller=SocialForcesAnimatorController` in a stock trial. (Note this
    /// is NOT BaseSFControllerNormalized, which S77 cites and which no trial pedestrian loads; the
    /// two have different clip names, which is why the lookup below is a candidate list.)
    ///
    ///   states: Grounded (blend tree), Airborne, Crouching, AssertiveGesture, SurprisedReaction
    ///   params: Forward, Turn, Crouch, OnGround, Jump, JumpLeg, Surprised, AssertiveGesture
    ///           -- note there is NO Strafe and NO Idling parameter, so the
    ///           "Parameter 'Strafe'/'Idling' does not exist." warnings Base.cs produces are
    ///           PRE-EXISTING on stock pedestrians and are not caused by any gait install.
    ///
    ///   Grounded blend tree (FreeformCartesian2D, x=Turn, y=Forward):
    ///     HumanoidIdle              HumanoidIdle.fbx           <- idle; NOT overridden
    ///     HumanoidWalk              HumanoidWalk.fbx           <- override target
    ///     HumanoidWalkRight/Left    HumanoidWalkTurn.fbx
    ///     HumanoidWalk*Sharp        HumanoidWalkTurnSharp.fbx
    ///     Stand*TurnRight/Left      HumanoidStandTurn.fbx
    ///   Reaction states:
    ///     SurprisedReaction  Pointing_towards.fbx  (guid 17119d7c..)  NOT in any blend tree
    ///     AssertiveGesture   point_backwards.fbx   (guid 6f88966a..)  NOT in any blend tree
    ///
    /// So neither reaction clip is shared with locomotion -- overriding HumanoidWalk cannot leak
    /// into them. Idle is a distinct clip and is deliberately left alone: overriding it would
    /// replace the standing pose with a walk cycle, which is the very defect being fixed.
    ///
    /// Only the straight-ahead walk node is overridden, not the turn clips, which also carry a
    /// forward component. At Turn=0 the blend is the override clip alone (the pure gait); while
    /// turning, the stock turn clips blend in, which is the correct reading -- a pedestrian
    /// pivoting on the spot is not performing the authored forward gait.
    /// </summary>
    public static class S79GaitOverrideBuilder
    {
        /// <summary>
        /// Candidate names for the forward-locomotion clip, tried in order; the first that matches
        /// EXACTLY ONE clip wins. A list rather than a constant because this project carries two
        /// pedestrian controllers with different vocabularies:
        ///   "HumanoidWalk" -- SocialForcesAnimatorController, the one every trial pedestrian
        ///                     actually loads (verified live, S79).
        ///   "Walk"         -- BaseSFControllerNormalized, present in the project and cited by
        ///                     S77, but not loaded by any trial appearance observed so far.
        /// Both are unique within their own controller: the near-misses are "HumanoidWalkRight"
        /// and "Walk_Back_Left", neither of which matches exactly.
        /// </summary>
        public static readonly string[] DefaultForwardClipNames = { "HumanoidWalk", "Walk" };

        /// <summary>Env var that forces the pre-S79 wholesale controller replacement, so the S73
        /// regression arm can still be captured for comparison. Unset => the override path.</summary>
        public const string LegacySwapEnv = "AUTOTRIAL_S79_LEGACY_SWAP";

        public static bool LegacySwapRequested
        {
            get { return !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(LegacySwapEnv)); }
        }

        /// <summary>
        /// The single AnimationClip carried by a generated single-state gait controller.
        ///
        /// Read off the controller rather than loaded by asset path on purpose: the generated
        /// controllers are the only Resources-visible handle on these clips, and going through
        /// them keeps this code working for both the Kimodo and the Mixamo sets without a second
        /// lookup table to drift.
        /// </summary>
        public static AnimationClip ExtractGaitClip(RuntimeAnimatorController generated)
        {
            if (generated == null) { return null; }
            AnimationClip[] clips = generated.animationClips;
            if (clips == null || clips.Length == 0) { return null; }
            if (clips.Length > 1)
            {
                Debug.LogWarning("[S79] generated controller '" + generated.name + "' carries "
                    + clips.Length + " clips; expected exactly 1. Using '" + clips[0].name + "'.");
            }
            return clips[0];
        }

        /// <summary>
        /// Resolve the base controller to build an override on. If an override controller is
        /// already installed (a second apply, or a restore from S68CuriousCrouch), unwrap to its
        /// base rather than nesting override on override -- nesting compounds remaps and makes the
        /// resulting clip table impossible to reason about.
        /// </summary>
        public static RuntimeAnimatorController ResolveBase(RuntimeAnimatorController installed)
        {
            var existing = installed as AnimatorOverrideController;
            return existing != null ? existing.runtimeAnimatorController : installed;
        }

        /// <summary>
        /// Pick the forward-locomotion clip out of a controller's override table.
        ///
        /// Primary rule is an EXACT name match, because that is what the asset actually declares
        /// and it is checkable by a human reading the controller. The root-motion fallback exists
        /// so a renamed or re-imported forward clip degrades to a data-driven answer instead of
        /// silently overriding nothing; it selects the clip whose planar root motion is most
        /// forward-dominant (+Z), which is the definition of "the forward walk" independent of
        /// naming. Ambiguity is a hard failure, never a guess -- overriding the wrong clip would
        /// put the gait on a strafe node and be very hard to diagnose from the video.
        /// </summary>
        public static AnimationClip FindForwardClip(
            List<KeyValuePair<AnimationClip, AnimationClip>> overrides,
            string[] forwardClipNames, out string how)
        {
            how = "none";
            foreach (string want in forwardClipNames)
            {
                if (string.IsNullOrEmpty(want)) { continue; }
                var named = new List<AnimationClip>();
                foreach (var kv in overrides)
                {
                    if (kv.Key != null && kv.Key.name == want) { named.Add(kv.Key); }
                }
                if (named.Count == 1) { how = "exact name '" + want + "'"; return named[0]; }
                if (named.Count > 1)
                {
                    Debug.LogError("[S79] " + named.Count + " clips are named '" + want
                        + "' -- cannot decide which is the forward-locomotion node. Not overriding.");
                    return null;
                }
            }

            AnimationClip best = null;
            float bestZ = 0f;
            int ties = 0;
            foreach (var kv in overrides)
            {
                if (kv.Key == null) { continue; }
                Vector3 s = kv.Key.averageSpeed;
                if (s.z <= 0.05f || s.z <= Mathf.Abs(s.x)) { continue; }  // not forward-dominant
                if (best == null || s.z > bestZ) { best = kv.Key; bestZ = s.z; ties = 0; }
                else if (Mathf.Approximately(s.z, bestZ)) { ties++; }
            }
            if (best == null)
            {
                Debug.LogError("[S79] none of [" + string.Join(", ", forwardClipNames)
                    + "] matched, and no forward-dominant clip found among " + overrides.Count
                    + " -- not overriding.");
                return null;
            }
            if (ties > 0)
            {
                Debug.LogError("[S79] forward-clip fallback is ambiguous (" + (ties + 1)
                    + " clips tie at averageSpeed.z=" + bestZ.ToString("F4") + ") -- not overriding.");
                return null;
            }
            how = "root-motion fallback (averageSpeed.z=" + bestZ.ToString("F4") + ")";
            return best;
        }

        /// <summary>
        /// Build the override controller. Returns null (and logs why) if the forward clip cannot be
        /// identified unambiguously -- the caller then leaves the base controller installed, which
        /// loses the gait but keeps a correct, fully-featured pedestrian.
        /// </summary>
        public static AnimatorOverrideController Build(
            RuntimeAnimatorController installed, AnimationClip gaitClip,
            string[] forwardClipNames, out string detail)
        {
            detail = "";
            RuntimeAnimatorController baseCtl = ResolveBase(installed);
            if (baseCtl == null) { detail = "no base controller installed"; return null; }
            if (gaitClip == null) { detail = "no gait clip"; return null; }

            var aoc = new AnimatorOverrideController(baseCtl);
            aoc.name = baseCtl.name + "+" + gaitClip.name;

            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(aoc.overridesCount);
            aoc.GetOverrides(pairs);

            string how;
            AnimationClip forward = FindForwardClip(pairs, forwardClipNames, out how);
            if (forward == null) { detail = "forward clip unresolved"; return null; }

            aoc[forward] = gaitClip;
            detail = "base=" + baseCtl.name + " forward='" + forward.name + "' (" + how
                + ") -> '" + gaitClip.name + "'; " + pairs.Count + " clips in table, 1 overridden";
            return aoc;
        }

        /// <summary>
        /// GATE 1 evidence, printed from the live Animator rather than asserted. Kept in the
        /// shipping class (not a test-only file) because the same three facts are what any future
        /// failure of this feature will turn on.
        /// </summary>
        public static void LogVerification(Animator animator, string expectedGaitClipName,
            string overriddenClipName)
        {
            if (animator == null) { Debug.LogError("[S79Gate1] no animator"); return; }
            var aoc = animator.runtimeAnimatorController as AnimatorOverrideController;
            Debug.Log("[S79Gate1] isOverrideController=" + (aoc != null)
                + " controller='" + (animator.runtimeAnimatorController != null
                    ? animator.runtimeAnimatorController.name : "NULL") + "'");

            bool hasSurprised = false, hasAssertive = false, hasForward = false;
            foreach (var p in animator.parameters)
            {
                if (p.name == "Surprised") { hasSurprised = true; }
                if (p.name == "AssertiveGesture") { hasAssertive = true; }
                if (p.name == "Forward") { hasForward = true; }
            }
            Debug.Log("[S79Gate1] params Surprised=" + hasSurprised
                + " AssertiveGesture=" + hasAssertive + " Forward=" + hasForward
                + " (total " + animator.parameters.Length + ")");

            string resolved = "(no override controller)";
            if (aoc != null)
            {
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(aoc.overridesCount);
                aoc.GetOverrides(pairs);
                foreach (var kv in pairs)
                {
                    if (kv.Key != null && kv.Key.name == overriddenClipName)
                    {
                        resolved = kv.Value != null ? kv.Value.name : "(not overridden)";
                    }
                }
            }
            Debug.Log("[S79Gate1] forward clip '" + overriddenClipName + "' resolves to '" + resolved
                + "' (expected '" + expectedGaitClipName + "') match="
                + (resolved == expectedGaitClipName));
        }
    }
}
