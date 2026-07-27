using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 32 FIX E: locomotion animator playback rate should scale with the agent's ACTUAL
    /// movement speed. Before this fix, `animator.speed` was never touched anywhere in this
    /// codebase (grepped tools/*.py, Assets/Scripts/AutoTrial/**, PedestrianModulator.cs -- no
    /// existing `animator.speed =` or comparable hook) -- every appearance played its locomotion
    /// clip at its authored (1.0x) rate regardless of `walkSpeedMultiplier`/`--ped-speed`, so a
    /// slowed-down actor (white_cane_user, wheelchair_user after Session 31/32's speed
    /// corrections) visually "crawls while its legs/wheels churn at a normal walking cadence" --
    /// the user's own "white_cane looks too fast" complaint, even though its measured m/s speed
    /// was already correctly slow (Session 31 confirmed this: movement speed and animation rate
    /// are two independent things, and only the first had been fixed).
    ///
    /// General fix, not appearance-specific: attached to every Zone A/B pedestrian regardless of
    /// personality (a general locomotion property, orthogonal to personality-driven reaction
    /// behavior). animator.speed = currentSpeed / ReferenceSpeedMps, clamped to [MinSpeedScale,
    /// MaxSpeedScale] so an extreme multiplier (e.g. scooter's ~3.5 m/s) doesn't produce an
    /// absurd, blurring playback rate -- clamped rather than unclamped because this project's
    /// appearances don't all share one "natural" authored clip speed (scooter/cyclist likely use
    /// their own riding animations, not the walk-cycle this reference speed was chosen against);
    /// FIX E's own verification target is white_cane_user and wheelchair_user specifically, where
    /// the reference speed is directly applicable.
    ///
    /// Session 36 FIX 5 correction: originally read `Base.velocity.magnitude` (a pre-existing
    /// public property). Diagnostic instrumentation this session proved this is WRONG for
    /// white_cane_user specifically: `Base.velocity` reported a stable ~2.09 m/s the whole trial
    /// while the pedestrian's actual on-screen displacement (computed independently from
    /// frames.csv position deltas) was ~0.32-0.41 m/s -- `Base.velocity` reflects this
    /// appearance's underlying nav/rigidbody-commanded speed, not the root-motion-driven visible
    /// movement (see AutoTrialBootstrap.cs's own note that white_cane_user is "unlike every other
    /// current Zone B container" in this exact respect -- the same root-motion divergence
    /// Session 21 already characterized for its origin-reset bug). Feeding that inflated 2.09 into
    /// `scale = currentSpeed / referenceSpeedMps` clamped the result to `maxSpeedScale=1.5` --
    /// i.e. this component was SPEEDING UP white_cane's animator, the exact opposite of the
    /// intended slowdown, which is why Session 35's "confirm this applies" check evidently didn't
    /// catch the real complaint (the mechanism WAS running, just computing the wrong number).
    /// Fixed by measuring speed directly from frame-to-frame world-position displacement
    /// (`transform.position` delta / `Time.deltaTime`) instead of trusting `Base.velocity` --
    /// this is by construction exactly "how fast the character visibly moved," correct regardless
    /// of whatever internal representation any given appearance's movement path uses, and general
    /// (not a white_cane-specific branch).
    /// </summary>
    public class S32AnimatorSpeedScaler : MonoBehaviour
    {
        // The shared Zone A/B walk-cycle reference pace this project has used since Session 30R
        // (business_male_01's own measured ~1.29-1.30 m/s, walkSpeedMultiplier=1.0 case) -- the
        // speed at which the underlying Locomotion clip's own root motion/authored cadence reads
        // as natural.
        public float referenceSpeedMps = 1.3f;
        public float minSpeedScale = 0.3f;
        public float maxSpeedScale = 1.5f;

        // Session 41 TASK 2 FIX 0. `animator.speed` is a whole-Animator multiplier, not a
        // per-state one, so the locomotion scaling above also stretches one-shot REACTION clips --
        // which are not locomotion and whose authored timing is the point. This is worst exactly
        // where it matters most: Assertive's gesture is fired by S32AssertiveStraightLineGuardian
        // on the frame proximity first forces a STOP, so speed has just decayed to minSpeedScale
        // when the gesture starts.
        //
        // Measured (Session 41 TASK 1, business_male_01 x assertive, probe logs in
        // trial_outputs/s41_task1/): animatorSpeed=0.300 at gesture entry -> the authored 3.600s
        // AssertiveGesture clip reported an effective length of 12.000s (3.6/0.3) and the authored
        // 0.15s entry crossfade took 0.428-0.465s. The same trial for Surprised, which triggers
        // while still moving (animatorSpeed 0.896), showed effective length 2.473s against a 2.767s
        // authored clip and a 0.125s crossfade -- same code path, same controller, same authored
        // transition, the ONLY difference being animator.speed. That contrast is why the user's
        // two separate complaints ("起手慢" slow to start / "播放慢" slow playback) are one bug.
        //
        // Reaction states hold speed at exactly 1.0 (authored rate) rather than being scaled --
        // matched by name because this project's controllers carry no state tags (verified in the
        // TASK 1 graph dump); AnimatorStateInfo.IsName is a cheap hash compare, not a string op.
        public string[] reactionStateNames = { "SurprisedReaction", "AssertiveGesture" };

        // Session 44 FIX A. `animator.speed` is only meaningful while a LOCOMOTION clip is playing:
        // it is the knob that matches footfall cadence to ground travel, and a character that is
        // not travelling has no cadence to match. Session 41 already established this for one-shot
        // reaction states (hold at 1.0 above); this extends the same rule to standing still.
        //
        // Measured (Session 44 TASK 1, business_male_01, probe CSVs in trial_outputs/s44_diag/):
        // while stopped or creeping at 0.000-0.116 m/s the required scale is 0.00-0.09, but
        // minSpeedScale pinned animator.speed at 0.300 -- the legs cycled 353-447% faster than the
        // ground demanded. Scared's stopped segment measured 217% with 96% of frames on the floor.
        // Sustained walking measured 95-103%, i.e. correct, which is why the defect reads as
        // "slides only after the reaction / only while turning" rather than as a constant fault.
        //
        // Below this ground speed the character is treated as stationary and the Animator is
        // returned to its authored rate. NOT a hard freeze: 1.0 keeps an idle clip breathing at its
        // designed pace, where scaling toward 0 would produce the frozen-statue failure a human
        // reviewer has caught on this project before.
        public float idleSpeedThresholdMps = 0.15f;
        // Hysteresis, so a character hovering either side of the threshold doesn't flicker between
        // scaled and authored playback -- must fall below for this long to count as stopped, and
        // any sample above it resumes scaling immediately.
        public float idleDwellSec = 0.20f;

        // Session 44 FIX A, second half: Base.cs:351 computes `idle` as
        // `animParams.magnitude < idleSpeed && !applyRootMotion`, and Base.cs:32 hardcodes
        // applyRootMotion = true, so that expression is ALWAYS false and the Animator's "Idling"
        // bool is never set true by the upstream code. Base.cs is off-limits, so the parameter is
        // driven from here instead. Ordering makes this safe rather than a second race: this
        // component now runs at execution order 100, after Base's default 0 (see
        // Editor/S44ExecutionOrder.cs), so this write lands after Base's write every frame.
        public bool driveIdlingParameter = true;

        private float belowThresholdSince = -1f;

        private Animator animator;
        private Scenario.Agents.Base baseAgent;
        private Vector3 lastPos;
        private bool havePrevPos = false;
        // Smoothed so a single noisy frame (e.g. a footstep-driven root-motion micro-stutter)
        // doesn't jerk the playback rate -- exponential moving average, not a hard window buffer.
        private float smoothedSpeed = 0f;
        private const float SmoothingTau = 0.25f;

        // Session 36 FIX 5, second finding: an early diagnostic run showed a huge (300+ m/s)
        // spurious FIRST-FRAME position-delta speed reading that then decayed smoothly over
        // ~1-2s -- an implausible single-frame teleport, not real walking (matches
        // S21PedestrianPositionGuardian's own "implausible multi-meter single-frame jump"
        // concept, which exists specifically because this appearance's nested-Animator
        // root-motion path is prone to exactly this). A max plausible human/mobility-aid speed
        // (generous headroom above scooter's own ~3.5 m/s) rejects the teleport frame outright
        // (skip the EMA update, just resync lastPos) instead of smoothing garbage into the average.
        private const float MaxPlausibleSpeedMps = 6.0f;

        // Session 36 FIX 5 diagnostic: env-var gated, logs what Awake() actually found once per
        // agent so a real trial run can confirm/refute the "wrong nested Animator" hypothesis
        // without guessing from prefab YAML (containers reference their avatar via a nested
        // PrefabInstance, so the real Animator hierarchy isn't visible via static text grep).
        private static readonly bool DiagEnabled =
            !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("S36_ANIM_SCALER_DIAG"));

        void Awake()
        {
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            baseAgent = GetComponent<Scenario.Agents.Base>();
            if (DiagEnabled)
            {
                string path = animator == null ? "NULL" : GetPath(animator.transform);
                bool isHuman = animator != null && animator.avatar != null && animator.avatar.isHuman;
                Debug.Log("[S36AnimScalerDiag] host=" + gameObject.name
                    + " foundAnimatorPath=" + path + " isHuman=" + isHuman
                    + " baseAgentNull=" + (baseAgent == null));
            }
        }

        // Checks both current and next state so the hold starts on the frame the entry crossfade
        // BEGINS, not when it completes -- otherwise the crossfade itself still plays stretched,
        // which is the "起手慢" half of the complaint.
        private bool IsReactionActive()
        {
            if (reactionStateNames == null || reactionStateNames.Length == 0) { return false; }
            var cur = animator.GetCurrentAnimatorStateInfo(0);
            var next = animator.GetNextAnimatorStateInfo(0);
            bool inTransition = animator.IsInTransition(0);
            for (int i = 0; i < reactionStateNames.Length; i++)
            {
                string n = reactionStateNames[i];
                if (string.IsNullOrEmpty(n)) { continue; }
                if (cur.IsName(n)) { return true; }
                if (inTransition && next.IsName(n)) { return true; }
            }
            return false;
        }

        // Cached so the parameter list isn't walked every frame.
        private bool idlingParamChecked;
        private bool idlingParamExists;

        private void SetBoolIfPresent(string name, bool value)
        {
            if (!idlingParamChecked)
            {
                idlingParamChecked = true;
                foreach (var p in animator.parameters)
                {
                    if (p.type == AnimatorControllerParameterType.Bool && p.name == name)
                    {
                        idlingParamExists = true;
                        break;
                    }
                }
            }
            if (idlingParamExists) { animator.SetBool(name, value); }
        }

        private static string GetPath(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }

        void Update()
        {
            if (animator == null) { return; }

            Vector3 pos = transform.position;
            float instantaneousSpeed;
            if (havePrevPos && Time.deltaTime > 1e-5f)
            {
                Vector3 delta = pos - lastPos;
                delta.y = 0f; // ground-plane displacement only -- vertical bob isn't walking speed
                instantaneousSpeed = delta.magnitude / Time.deltaTime;
            }
            else
            {
                instantaneousSpeed = 0f;
            }
            bool plausible = instantaneousSpeed <= MaxPlausibleSpeedMps;
            lastPos = pos;
            havePrevPos = true;

            if (plausible)
            {
                float alpha = 1f - Mathf.Exp(-Time.deltaTime / SmoothingTau);
                smoothedSpeed = Mathf.Lerp(smoothedSpeed, instantaneousSpeed, alpha);
            }
            // else: teleport/correction frame -- lastPos is resynced above so the NEXT frame's
            // delta is measured from the corrected position, but this frame's implausible reading
            // is discarded rather than folded into the average.

            // Keep the EMA above running through the reaction (so locomotion resumes at the right
            // rate afterwards) but don't let it drive animator.speed while a reaction clip owns
            // the Animator -- see reactionStateNames' comment for the measured justification.
            if (IsReactionActive())
            {
                animator.speed = 1.0f;
                belowThresholdSince = -1f;
                return;
            }

            // Session 44 FIX A: treat "not travelling" as its own regime rather than as very slow
            // travel. See idleSpeedThresholdMps for the measurements this closes.
            if (smoothedSpeed < idleSpeedThresholdMps)
            {
                if (belowThresholdSince < 0f) { belowThresholdSince = Time.time; }
            }
            else
            {
                belowThresholdSince = -1f;
            }
            bool stationary = belowThresholdSince >= 0f && (Time.time - belowThresholdSince) >= idleDwellSec;

            if (driveIdlingParameter)
            {
                // Best-effort: controllers without an "Idling" bool (the generated single-state
                // Mixamo ones) simply have no such parameter, and Unity logs a warning per call
                // rather than throwing -- so check before setting rather than swallowing exceptions.
                SetBoolIfPresent("Idling", stationary);
            }

            if (stationary)
            {
                // Authored rate, not zero: an idle clip should play at its designed pace. Scaling
                // it toward zero is the frozen-statue failure mode.
                animator.speed = 1.0f;
                return;
            }

            float scale = referenceSpeedMps > 0.01f ? smoothedSpeed / referenceSpeedMps : 1.0f;
            scale = Mathf.Clamp(scale, minSpeedScale, maxSpeedScale);
            animator.speed = scale;
            if (DiagEnabled && Time.frameCount % 60 == 0)
            {
                float legacySpeed = baseAgent != null ? baseAgent.velocity.magnitude : -1f;
                Debug.Log("[S36AnimScalerDiag] host=" + gameObject.name
                    + " positionDeltaSpeed=" + smoothedSpeed + " legacyBaseVelocity=" + legacySpeed
                    + " appliedAnimatorSpeed=" + animator.speed);
            }
        }
    }
}
