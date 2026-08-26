using System.Collections.Generic;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 79. Builds a runtime <see cref="AnimatorOverrideController"/> that swaps ONLY the
    /// forward-locomotion clip of the pedestrian's existing controller for a generated gait clip
    /// (Kimodo or Mixamo), leaving every state, parameter and transition in place.
    ///
    /// This class is scope-agnostic -- it builds an override for whatever it is handed. WHICH
    /// clips get one is the caller's decision, and since S80 that is kimodo_* only; see
    /// S41MixamoClipApplier.InstallGait for the rationale.
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
        /// regression arm can still be captured for comparison -- including for kimodo_* clips,
        /// which is the only way to reach the pre-S79 behaviour for those. Unset => the caller
        /// decides; since S80 that means kimodo_* gaits take the override path and everything
        /// else takes the wholesale swap (S41MixamoClipApplier.IsKimodoGait).</summary>
        public const string LegacySwapEnv = "AUTOTRIAL_S79_LEGACY_SWAP";

        public static bool LegacySwapRequested
        {
            get { return !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(LegacySwapEnv)); }
        }

        // ------------------------------------------------------------------------------------
        // Session 83: the surprise-reaction rebind (b2).
        // ------------------------------------------------------------------------------------

        /// <summary>Arms the b2 surprise rebind. Opt-IN: unset means the stock pointing gesture,
        /// so nothing outside a run that explicitly asks for it can change behaviour.</summary>
        public const string B2Env = "AUTOTRIAL_S83_B2";

        /// <summary>Resources path of the imported Kimodo b2 reaction (see
        /// S83KimodoReactionImport). Under a Resources/ folder so it is loadable at runtime
        /// without an asset reference or a generated controller.</summary>
        public const string B2ResourcePath = "kimodo_b2_surprised";

        /// <summary>
        /// Identity of the clip bound to SurprisedReaction, as a (name, length) pair.
        ///
        /// WHY NOT NAME ALONE. Both reaction states bind a clip literally named "mixamo.com" --
        /// the default take name every Mixamo FBX ships with. Measured in-project (S83):
        ///
        ///   state 'SurprisedReaction' motion='mixamo.com' length=2.7667 asset=Pointing_towards.fbx
        ///   state 'AssertiveGesture'  motion='mixamo.com' length=3.6000 asset=point_backwards.fbx
        ///
        /// They are DIFFERENT assets (guid 17119d7c vs 6f88966a, and 17119d7c occurs exactly once
        /// in the controller) so there is no shared-clip leak -- but a name-only lookup would match
        /// both and could rebind the assertive gesture into a surprise flinch. Runtime has no guid
        /// access, so the discriminator is the length, which is unique across all 20 clips the
        /// controller references (verified by dumping every name|length key: 20 distinct keys for
        /// 20 clips). Ambiguity is a hard failure, never a guess.
        /// </summary>
        public const string SurprisedClipName = "mixamo.com";
        public const float SurprisedClipLength = 2.7667f;
        public const float ClipLengthTolerance = 0.01f;

        /// S97: the opt-out. Setting AUTOTRIAL_S83_B2_OFF=1 restores the stock pointing gesture.
        public const string B2OffEnv = "AUTOTRIAL_S83_B2_OFF";

        /// DEFAULT ON since S97, and SCOPED TO KIMODO PEDESTRIANS. A Kimodo pedestrian is correct
        /// with zero environment variables set; suppressing it is what you now have to ask for.
        /// AUTOTRIAL_S83_B2 is retired as a switch and reading it is deliberately not restored -- a
        /// stale caller that still exports it gets the b2 clip, which is what it was asking for.
        ///
        /// THIS IS THE BOOTSTRAP-LEVEL GATE ONLY. It answers "may anything arm at all", which is all
        /// that is knowable before pedestrians spawn. Whether b2 actually applies to a given
        /// pedestrian is B2InScope below, and that is the one that decides.
        public static bool B2Requested
        {
            get { return string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(B2OffEnv)); }
        }

        /// WHY THE DEFAULT IS SCOPED RATHER THAN GLOBAL. planD's A1 arm runs the `surprised`
        /// personality across 6 appearances x 3 repeats, and the stock SocialForcesAnimatorController
        /// carries a SurprisedReaction state -- so an unscoped default-on would have succeeded on
        /// those 18 trials and put a Kimodo clip into the frozen paper dataset, against the S73 rule
        /// that these clips never enter it. Scoping to the pedestrians the clip was made for keeps
        /// planD untouched and is the same predicate S80 already applies to the gait override:
        /// one name, checked one way, so a future kimodo_* clip is in scope with no code edit.
        public static bool B2InScope(S41MixamoClipApplier applier)
        {
            if (!B2Requested) return false;
            if (applier == null) return false;                       // no gait install at all
            return S41MixamoClipApplier.IsKimodoGait(applier.clipControllerName);
        }

        /// <summary>Load the b2 reaction clip. The FBX exposes its take as a sub-asset, so this
        /// takes the first non-preview AnimationClip under the Resources path.</summary>
        public static AnimationClip LoadB2Clip()
        {
            var all = Resources.LoadAll<AnimationClip>(B2ResourcePath);
            if (all == null || all.Length == 0)
            {
                Debug.LogError("[S83] no AnimationClip under Resources/" + B2ResourcePath
                    + " -- was S83KimodoReactionImport.Apply run?");
                return null;
            }
            foreach (var c in all)
            {
                if (c != null && !c.name.StartsWith("__preview")) { return c; }
            }
            return all[0];
        }

        /// <summary>
        /// Find a clip in an override table by exact name AND length. Must match exactly once;
        /// zero or several is a hard failure so a mis-key can never silently rebind the wrong
        /// state.
        /// </summary>
        public static AnimationClip FindClipByNameAndLength(
            List<KeyValuePair<AnimationClip, AnimationClip>> overrides,
            string wantName, float wantLength, out string how)
        {
            how = "none";
            var hits = new List<AnimationClip>();
            foreach (var kv in overrides)
            {
                if (kv.Key == null) { continue; }
                if (kv.Key.name != wantName) { continue; }
                if (Mathf.Abs(kv.Key.length - wantLength) > ClipLengthTolerance) { continue; }
                hits.Add(kv.Key);
            }
            if (hits.Count == 1)
            {
                how = "name '" + wantName + "' + length " + wantLength.ToString("F4");
                return hits[0];
            }
            Debug.LogError("[S83] " + hits.Count + " clips match name='" + wantName + "' length="
                + wantLength.ToString("F4") + " (+/-" + ClipLengthTolerance + ") among "
                + overrides.Count + " -- expected exactly 1. Not rebinding.");
            return null;
        }

        /// <summary>
        /// Compose the surprise rebind onto whatever controller is already installed.
        ///
        /// COMPOSITION, not replacement. If an AnimatorOverrideController is already installed
        /// (the kimodo gait path), this MUTATES that same instance -- one controller carrying both
        /// remaps -- rather than wrapping it, which would nest override on override and make the
        /// effective clip table impossible to reason about. If a plain controller is installed
        /// (stock pedestrian, or a legacy wholesale swap), a fresh override is built on it.
        ///
        /// Returns the controller to install, or null if nothing could be rebound (which is the
        /// normal, logged outcome on a legacy-swapped single-state controller: it carries no
        /// SurprisedReaction clip to override, because the swap deleted the whole state machine).
        /// </summary>
        public static RuntimeAnimatorController ApplySurpriseOverride(
            RuntimeAnimatorController installed, AnimationClip b2, out string detail)
        {
            detail = "";
            if (installed == null) { detail = "no controller installed"; return null; }
            if (b2 == null) { detail = "b2 clip not loaded"; return null; }

            var aoc = installed as AnimatorOverrideController;
            bool mutatingExisting = aoc != null;
            if (aoc == null) { aoc = new AnimatorOverrideController(installed); }

            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(aoc.overridesCount);
            aoc.GetOverrides(pairs);

            string how;
            AnimationClip surprised = FindClipByNameAndLength(
                pairs, SurprisedClipName, SurprisedClipLength, out how);
            if (surprised == null) { detail = "surprised clip unresolved"; return null; }

            aoc[surprised] = b2;
            if (!mutatingExisting) { aoc.name = installed.name + "+S83b2"; }
            detail = (mutatingExisting ? "composed onto existing override '" : "new override on '")
                + aoc.name + "' surprised clip (" + how + ") -> '" + b2.name + "' len "
                + b2.length.ToString("F3") + "s; " + pairs.Count + " clips in table";
            return aoc;
        }

        /// <summary>GATE II evidence, printed from the live Animator.</summary>
        public static void LogSurpriseVerification(Animator animator, AnimationClip b2)
        {
            var aoc = animator != null
                ? animator.runtimeAnimatorController as AnimatorOverrideController : null;
            if (aoc == null) { Debug.LogError("[S83Gate2] no override controller installed"); return; }

            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(aoc.overridesCount);
            aoc.GetOverrides(pairs);
            int overridden = 0;
            string surprisedTo = "(not overridden)", forwardTo = "(not overridden)";
            foreach (var kv in pairs)
            {
                if (kv.Value != null && kv.Value != kv.Key) { overridden++; }
                if (kv.Key == null) { continue; }
                if (kv.Key.name == SurprisedClipName
                    && Mathf.Abs(kv.Key.length - SurprisedClipLength) <= ClipLengthTolerance)
                {
                    surprisedTo = kv.Value != null ? kv.Value.name + " len "
                        + kv.Value.length.ToString("F3") : "(null)";
                }
                if (kv.Key.name == "HumanoidWalk")
                {
                    forwardTo = kv.Value != null ? kv.Value.name : "(null)";
                }
            }
            Debug.Log("[S83Gate2] controller='" + aoc.name + "' overridden=" + overridden
                + " of " + pairs.Count);
            Debug.Log("[S83Gate2] SurprisedReaction clip -> '" + surprisedTo + "' (expected '"
                + b2.name + " len " + b2.length.ToString("F3") + "') match="
                + surprisedTo.StartsWith(b2.name));
            Debug.Log("[S83Gate2] gait clip HumanoidWalk -> '" + forwardTo
                + "' (composition check: both remaps live in one controller)");

            bool hasSurprisedParam = false;
            foreach (var pm in animator.parameters)
            {
                if (pm.name == "Surprised") { hasSurprisedParam = true; }
            }
            Debug.Log("[S83Gate2] param Surprised=" + hasSurprisedParam
                + " (total " + animator.parameters.Length + ")");
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
