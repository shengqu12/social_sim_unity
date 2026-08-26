using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 41 TASK 3/4/6. Swaps the spawned pedestrian's Animator onto one of the generated
    /// single-state Mixamo controllers (see S41MixamoControllerGen) and, for carry_and_walk,
    /// attaches the carried-box primitive.
    ///
    /// The nine Mixamo FBXs are animation-only (no mesh -- see S41MixamoContentProbe), so the
    /// character on screen is always the ordinary Rocketbox avatar; Unity's Humanoid retargeting
    /// is what puts the Mixamo motion on it. This is the same mechanism Session 31 used for
    /// point_backwards.fbx, just parameterised per clip.
    ///
    /// Runs after Base.Start() (which is what caches the Animator and installs the root-motion
    /// path), hence DefaultExecutionOrder -- swapping the controller before that would race the
    /// applyRootMotion/RootMotionSink setup.
    /// </summary>
    [DefaultExecutionOrder(500)]
    public class S41MixamoClipApplier : MonoBehaviour
    {
        public string clipControllerName = "";
        public bool attachCarriedBox = false;

        // Session 79: candidate names for the forward-locomotion clip to override in the
        // pedestrian's own controller, tried in order. Defaults cover both controllers in the
        // project -- "HumanoidWalk" (SocialForcesAnimatorController, what trial pedestrians
        // actually load) and "Walk" (BaseSFControllerNormalized). Exposed as a field so a
        // re-authored controller can be pointed at a different node without a code edit;
        // S79GaitOverrideBuilder falls back to a root-motion test if no name matches, and fails
        // loudly rather than guessing if that is ambiguous.
        public string[] forwardClipNames = S79GaitOverrideBuilder.DefaultForwardClipNames;

        // TASK 4's dimensions, straight from the ticket.
        public Vector3 boxSize = new Vector3(0.45f, 0.35f, 0.35f);
        // Cardboard brown #8B6F47.
        public Color boxColor = new Color(0x8B / 255f, 0x6F / 255f, 0x47 / 255f);

        // Body-relative anchor used when the rig exposes no bone Transforms (see AttachBox).
        // ~1.1m is the chest height the ticket cites, and is the number the robot-cannot-see-it
        // argument is built on (the sensor plane is 0.32m).
        public float carryHeightMeters = 1.1f;
        public float carryForwardMeters = 0.28f;

        private bool applied;

        /// <summary>
        /// Session 83. True once the gait install has finished (or been skipped because no clip
        /// was named). S83SurpriseRebind waits on this before wrapping the controller, so the two
        /// overrides compose into ONE AnimatorOverrideController instead of racing -- the applier
        /// defers a frame for the Animator rebind, so "the component exists" is not "it has run".
        /// </summary>
        public bool GaitInstalled { get; private set; }

        void Start()
        {
            if (applied) { return; }
            applied = true;
            StartCoroutine(ApplyNextFrame());
        }

        // Deferred one frame. Assigning runtimeAnimatorController rebinds the Animator, and until
        // that rebind has been processed GetBoneTransform() returns null even on a valid Humanoid
        // avatar -- which is exactly how the first attempt at the carried box failed ("hand bones
        // missing" on an avatar whose isHuman was True).
        private System.Collections.IEnumerator ApplyNextFrame()
        {
            var animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator == null)
            {
                Debug.LogError("[S41Mixamo] no Animator resolved on '" + name + "' -- cannot apply clip.");
                yield break;
            }

            if (!string.IsNullOrEmpty(clipControllerName))
            {
                var rac = Resources.Load<RuntimeAnimatorController>(clipControllerName);
                if (rac == null)
                {
                    Debug.LogError("[S41Mixamo] Resources.Load failed for controller '" + clipControllerName
                        + "' -- is it under a Resources folder? Leaving the original controller in place.");
                }
                // Session 79: MOVED AHEAD of the install below. It resolves inPlaceClip from
                // clip_speeds.json, and inPlaceClip is what decides which install path is correct
                // (see InstallGait). It only reads the json and writes S32AnimatorSpeedScaler's
                // referenceSpeedMps, so it has no dependency on which controller is installed --
                // running it first changes nothing about what it does.
                ApplyAuthoredSpeed();
                if (rac != null)
                {
                    InstallGait(animator, rac);
                }
                // Session 44 TASK 5.2/5.3: per-clip staging (Sitting's stool, Standing_Arguing's
                // second person). Additive and self-selecting on the clip name, so every other clip
                // is untouched.
                var props = gameObject.AddComponent<S44ClipProps>();
                props.clipName = clipControllerName;
                // Session 46 (2): in-place clips are exactly the ones that must not travel.
                props.suppressRootMotion = inPlaceClip;
            }

            GaitInstalled = true;

            if (attachCarriedBox)
            {
                // One frame for the rebind, then attach. AttachBox falls back to a body-relative
                // anchor when the rig exposes no bone Transforms, so this does not need to retry.
                yield return null;
                AttachBox(animator);
            }
        }

        /// <summary>
        /// Session 79. Install the gait, by ONE of two paths.
        ///
        /// OVERRIDE (default, travelling gaits). Build an AnimatorOverrideController on the
        /// pedestrian's existing controller and remap only its forward-locomotion clip. Every
        /// state, parameter and transition survives, so the reaction states (SurprisedReaction,
        /// AssertiveGesture) and the Idle node still exist. This is the S78 fix: the wholesale
        /// replacement below is what made `[S41Latency] T_SIGNAL=-1 T_STATE=-1` and
        /// "Parameter 'Surprised' does not exist." the normal case for every Kimodo trial.
        ///
        /// LEGACY WHOLESALE SWAP, kept reachable three ways:
        ///   * AUTOTRIAL_S79_LEGACY_SWAP=1 -- forces it for everything, so the S73 regression arm
        ///     can be captured on the old path for a like-for-like comparison.
        ///   * in-place clips ALWAYS take it, and that is a correctness requirement, not a
        ///     fallback. Sitting / Standing_Arguing / Stroke_Shaking_Head are not walk cycles;
        ///     they are meant to own the character continuously, and S44ClipProps stages real
        ///     props against them (the stool, the argument partner). Putting one on the blend
        ///     tree's forward node would play it only while walking and blend it with strafes --
        ///     the character would sit down only when moving. Their authored speed is ~0, so they
        ///     also carry no root motion for the locomotion node to use.
        ///   * SESSION 80: every clip that is not a kimodo_* gait takes it BY DEFAULT.
        ///     S79 shipped the override for all travelling gaits and measured a real, explainable
        ///     delta on the Mixamo arm: the gait becomes a member of the FreeformCartesian2D blend
        ///     tree, so at Forward=0.759 roughly 25% HumanoidIdle blends in and 11 of 391 moving
        ///     frames read Idle-dominant. Nothing about that is a bug -- it is what putting a clip
        ///     on a blend node means -- but the Mixamo clips are what planD's frozen pipeline
        ///     generation consumes, and a nonzero regression-arm delta there costs more than the
        ///     architectural uniformity of one install path. Sheng's S80 call: zero delta wins.
        ///     The reaction dead-end this override exists to fix is a Kimodo-only symptom, so the
        ///     scoping loses nothing the fix was for.
        /// </summary>
        private void InstallGait(Animator animator, RuntimeAnimatorController rac)
        {
            string before = animator.runtimeAnimatorController != null
                ? animator.runtimeAnimatorController.name : "NULL";
            bool forced = S79GaitOverrideBuilder.LegacySwapRequested;
            bool outOfScope = !IsKimodoGait(clipControllerName);          // Session 80
            bool legacy = forced || inPlaceClip || outOfScope;

            if (legacy)
            {
                Debug.Log("[S41Mixamo] '" + name + "' controller " + before + " -> " + rac.name
                    + " (LEGACY wholesale swap; reason="
                    + (forced ? S79GaitOverrideBuilder.LegacySwapEnv + " set"
                        : inPlaceClip ? "in-place clip" : "not a kimodo_* gait (S80 scoping)")
                    + "; avatar isHuman=" + (animator.avatar != null && animator.avatar.isHuman) + ")");
                animator.runtimeAnimatorController = rac;
                return;
            }

            AnimationClip gait = S79GaitOverrideBuilder.ExtractGaitClip(rac);
            string detail;
            var aoc = S79GaitOverrideBuilder.Build(
                animator.runtimeAnimatorController, gait, forwardClipNames, out detail);
            if (aoc == null)
            {
                // Deliberately NOT falling back to the wholesale swap. That path is exactly the
                // defect this session removed, and taking it silently on a bad lookup would
                // reintroduce the dead reaction states under a different cause. Loud, and the
                // pedestrian keeps a correct (if un-gaited) controller.
                Debug.LogError("[S41Mixamo] '" + name + "' gait override FAILED (" + detail
                    + ") -- leaving '" + before + "' installed. The pedestrian will walk with its "
                    + "stock gait; reactions still work. Not falling back to the legacy swap.");
                return;
            }

            animator.runtimeAnimatorController = aoc;
            Debug.Log("[S41Mixamo] '" + name + "' controller " + before + " -> override " + detail
                + " (avatar isHuman=" + (animator.avatar != null && animator.avatar.isHuman) + ")");
            // Session 79 GATE 3. Only meaningful once the override has restored the Forward
            // parameter and the idle node -- on the legacy path there is nothing to drive. It
            // self-disables unless this agent's body is externally position-owned (see its class
            // doc for why that scoping is what makes it deadlock-free).
            if (gameObject.GetComponent<S79StalledGaitIdler>() == null)
            {
                gameObject.AddComponent<S79StalledGaitIdler>();
            }
            S79GaitOverrideBuilder.LogVerification(
                animator, gait != null ? gait.name : "(null)", OverriddenClipName(detail));
        }

        /// <summary>
        /// Session 80. Is this clip in scope for the AnimatorOverrideController install?
        ///
        /// Prefix match on the controller name, which is the same string S73 gave the three
        /// generated Kimodo controllers (kimodo_relaxed_walk, kimodo_relaxed_walk_24s,
        /// kimodo_elderly_shuffle) and the same key they carry in clip_speeds.json -- one name,
        /// checked one way, so a future kimodo_* clip is in scope with no code edit. Every Mixamo
        /// controller name is a bare clip name (Old_Man_Walk, Drunk_Walk, carry_and_walk, ...), so
        /// none of them can collide with the prefix.
        ///
        /// Deliberately NOT an env var or an inverted opt-out. The scoping is the decision of
        /// record, not a tuning knob: an opt-out flag would let a Mixamo trial take the override
        /// path by accident, which is exactly the regression-arm delta S80 exists to remove.
        /// AUTOTRIAL_S79_LEGACY_SWAP still forces legacy for everything, including kimodo_*, which
        /// keeps the pre-S79 behaviour reachable in one step for comparison.
        /// </summary>
        public static bool IsKimodoGait(string controllerName)
        {
            return !string.IsNullOrEmpty(controllerName)
                && controllerName.StartsWith("kimodo_", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Pull the clip name the builder reported overriding, so GATE 1's readback
        /// checks the clip that was ACTUALLY replaced rather than re-guessing from the candidate
        /// list (which would silently pass if the fallback had picked a different clip).</summary>
        private static string OverriddenClipName(string detail)
        {
            const string k = "forward='";
            int i = detail.IndexOf(k);
            if (i < 0) { return ""; }
            i += k.Length;
            int j = detail.IndexOf('\'', i);
            return j > i ? detail.Substring(i, j - i) : "";
        }

        /// <summary>
        /// Session 44 FIX C: point S32AnimatorSpeedScaler at the pace THIS clip was actually
        /// authored for, instead of the single hard-coded 1.3 m/s it applied to every clip.
        ///
        /// That constant was measured wrong for every Mixamo asset, in both directions -- Old Man
        /// Walk is authored at 0.392 m/s (3.3x over-estimated) and Running at 4.406 m/s (3.4x
        /// under). The scaler normalises correctly; it was normalising against a pace none of these
        /// clips were authored for, which is why the animation could match travel speed or footfall
        /// cadence but never both.
        ///
        /// Reads Assets/PedestrianAssets/Mixamo/clip_speeds.json -- the SAME file run_trial.py
        /// derives the SFM speed multiplier from. One source, deliberately: two would drift, and
        /// the drift would present as a slide with no obvious cause.
        ///
        /// in-place clips (no root translation) are skipped: animation speed scaling has no meaning
        /// for a clip that does not travel, and their measured authored speed is ~0, which would
        /// divide through to an absurd scale.
        /// </summary>
        // Set by ApplyAuthoredSpeed so the caller can act on it (Session 46: root-motion
        // suppression applies to exactly the in-place clips).
        private bool inPlaceClip;

        private void ApplyAuthoredSpeed()
        {
            var scaler = GetComponent<S32AnimatorSpeedScaler>();
            if (scaler == null) { return; }

            var cfg = Resources.Load<TextAsset>("clip_speeds");
            string json = cfg != null ? cfg.text : ReadFromAssetPath();
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning("[S41Mixamo] clip_speeds.json not found -- S32AnimatorSpeedScaler "
                    + "keeps its default referenceSpeedMps=" + scaler.referenceSpeedMps
                    + ", which is WRONG for every Mixamo clip. Animation scaling will be off.");
                return;
            }

            float authored;
            bool inPlace;
            if (!TryLookup(json, clipControllerName, out authored, out inPlace))
            {
                Debug.LogWarning("[S41Mixamo] '" + clipControllerName + "' has no clip_speeds.json entry -- "
                    + "leaving referenceSpeedMps=" + scaler.referenceSpeedMps);
                return;
            }
            inPlaceClip = inPlace || authored < 0.05f;
            if (inPlaceClip)
            {
                Debug.Log("[S41Mixamo] '" + clipControllerName + "' is in-place (authored "
                    + authored.ToString("F4") + " m/s) -- animation speed scaling left untouched.");
                return;
            }

            float before = scaler.referenceSpeedMps;
            scaler.referenceSpeedMps = authored;
            // Session 55: mark it authoritative so S32AnimatorSpeedScaler divides by THIS rather than
            // by a live AnimationClip.averageSpeed read. clip_speeds.json carries hand-derived values
            // for the clips whose averageSpeed is invalid because the root does not travel
            // monotonically -- Pacing_And_Talking_On_A_Phone paces back and forth, so its outbound
            // and return cancel and averageSpeed reads 0.415 against a real 0.5636.
            scaler.referenceSpeedMpsExplicit = true;
            Debug.Log("[S41Mixamo] '" + clipControllerName + "' referenceSpeedMps "
                + before.ToString("F4") + " -> " + authored.ToString("F4") + " m/s (measured authored pace)");
        }

        private static string ReadFromAssetPath()
        {
            const string p = "Assets/PedestrianAssets/Mixamo/clip_speeds.json";
            try
            {
                return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>Minimal field scan -- JsonUtility cannot parse a top-level array of objects with
        /// mixed types, and pulling in a JSON dependency for four numbers is not worth it.</summary>
        private static bool TryLookup(string json, string clip, out float authored, out bool inPlace)
        {
            authored = 0f;
            inPlace = true;
            int i = json.IndexOf("\"clip\": \"" + clip + "\"");
            if (i < 0) { return false; }
            int end = json.IndexOf('}', i);
            if (end < 0) { end = json.Length; }
            string rec = json.Substring(i, end - i);

            int a = rec.IndexOf("\"authoredSpeedMps\":");
            if (a < 0) { return false; }
            a += "\"authoredSpeedMps\":".Length;
            int aEnd = rec.IndexOf(',', a);
            if (aEnd < 0) { aEnd = rec.Length; }
            if (!float.TryParse(rec.Substring(a, aEnd - a).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out authored))
            {
                return false;
            }
            inPlace = rec.Contains("\"inPlace\": true");
            return true;
        }

        // GetBoneTransform is the correct, rig-naming-agnostic lookup, but it returns null on rigs
        // imported with "Optimize GameObjects" (the bone Transforms are stripped from the
        // hierarchy) -- which is what happened here on Male_Adult_01Avatar despite isHuman=True.
        // Falls back to a name search covering both this project's rig conventions: Rocketbox
        // ("Bip01 L Hand") and Mixamo ("mixamorig:LeftHand").
        private static Transform FindBone(Transform root, params string[] needles)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                foreach (var needle in needles)
                {
                    if (t.name.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return t;
                    }
                }
            }
            return null;
        }

        private bool AttachBox(Animator animator)
        {
            if (animator.avatar == null || !animator.avatar.isHuman)
            {
                Debug.LogError("[S41Mixamo] carried box needs a Humanoid avatar to find the hand bones.");
                return false;
            }

            Transform lHand = animator.GetBoneTransform(HumanBodyBones.LeftHand) ?? FindBone(animator.transform, "L Hand", "LeftHand");
            Transform rHand = animator.GetBoneTransform(HumanBodyBones.RightHand) ?? FindBone(animator.transform, "R Hand", "RightHand");

            var anchor = new GameObject("CarriedBox");
            if (lHand != null && rHand != null)
            {
                // Preferred: parented to one hand, positioned at the midpoint of both, so the box
                // tracks actual hand motion.
                anchor.transform.SetParent(rHand, false);
                anchor.transform.position = (lHand.position + rHand.position) * 0.5f;
                anchor.transform.rotation = animator.transform.rotation;
                anchor.transform.position += animator.transform.forward * (boxSize.z * 0.5f);
            }
            else
            {
                // Fallback, and in practice the path this project takes: the Rocketbox rigs are
                // imported with "Optimize GameObjects", which strips every bone Transform out of
                // the hierarchy (verified -- the only children under the Animator are the root and
                // a single skinned mesh), so BOTH GetBoneTransform and a name search necessarily
                // return null even though avatar.isHuman is true. Exposing the hands would mean
                // editing the shared Microsoft-Rocketbox submodule's import settings, which is out
                // of scope. The ticket anticipates exactly this and offers a body-relative anchor
                // as the alternative; carry_and_walk holds the hands nearly static relative to the
                // chest, so a fixed chest-height offset is visually equivalent.
                anchor.transform.SetParent(animator.transform, false);
                anchor.transform.localPosition = new Vector3(0f, carryHeightMeters, carryForwardMeters);
                anchor.transform.localRotation = Quaternion.identity;
                Debug.Log("[S41Mixamo] carried box: rig exposes no bone Transforms (Optimize "
                    + "GameObjects) -- using body-relative anchor at chest height.");
            }

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "CarriedBoxMesh";
            box.transform.SetParent(anchor.transform, false);
            box.transform.localPosition = Vector3.zero;
            box.transform.localRotation = Quaternion.identity;
            box.transform.localScale = boxSize;

            // No collider: the ticket calls for this explicitly. The box sits at ~1.1m, far above
            // the robot's 0.32m sensor plane, so a collider could not affect navigation anyway --
            // it would only add physics cost. See the README on why that invisibility is a
            // deliberately retained perception case rather than a bug.
            var col = box.GetComponent<Collider>();
            if (col != null) { Destroy(col); }

            var renderer = box.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
            {
                renderer.material.color = boxColor;
                // Matte: specular highlights are a confound for VLM judgement.
                if (renderer.material.HasProperty("_Glossiness")) { renderer.material.SetFloat("_Glossiness", 0f); }
                if (renderer.material.HasProperty("_Metallic")) { renderer.material.SetFloat("_Metallic", 0f); }
            }

            Debug.Log(string.Format("[S41Mixamo] carried box attached at world y={0:F2}m (robot sensor plane is 0.32m) size=({1},{2},{3})",
                anchor.transform.position.y, boxSize.x, boxSize.y, boxSize.z));
            return true;
        }
    }
}
