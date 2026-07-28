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
    /// Session 36 FIX 5 (SUPERSEDED, Session 54 -- kept because the reasoning is the instructive
    /// part): it replaced `Base.velocity.magnitude` with frame-to-frame `transform.position`
    /// differencing, on the argument that displacement is "by construction exactly how fast the
    /// character visibly moved." That argument is true and it is beside the point. For a
    /// root-motion agent the displacement IS this component's own output fed back in -- Base.Move()
    /// never translates the transform, PedestrianModulator.ApplyAnimatorRootMotion() does, and
    /// `animator.deltaPosition` scales with `animator.speed`. Choosing the more accurate instrument
    /// closed the loop; the previous, "wrong" one had not.
    ///
    /// Session 54 FIX. The law is now
    ///
    ///     animator.speed = |Base.velocity| / (authored ground speed of the playing clip)
    ///
    /// which makes realised ground speed track the SFM's commanded speed exactly, with no feedback
    /// path. The standing rule this encodes, and the one to apply to any future version of this
    /// component:
    ///
    ///     A control loop's feedback signal is not required to be ACCURATE.
    ///     It is required not to be a function of the loop's own output.
    ///
    /// See trial_outputs/S53_ROOT_CAUSE.md for the measurement and trial_outputs/S54_REPORT.md for
    /// the diagnostics that fixed the design (in particular: no stationary criterion is needed
    /// here, because StopNavigation() -> StopAnimator() already zeroes Forward upstream).
    /// </summary>
    public class S32AnimatorSpeedScaler : MonoBehaviour
    {
        // Session 54: SUPERSEDED as an input to the control law -- kept only because
        // S41MixamoClipApplier writes it and S44SlideProbe reads it back for its own reconstruction.
        // The law now divides by the CURRENTLY PLAYING clip's own authored ground speed, read live
        // (see AuthoredClipSpeedMps). A static reference is what made the loop diverge: Zone A's
        // 1.3 against a HumanoidWalk that actually travels 1.556 m/s at animator.speed=1.0 gave a
        // loop gain of 1.20, and anything above 1.0 runs away until maxSpeedScale catches it.
        public float referenceSpeedMps = 1.3f;

        // Session 55: set true by S41MixamoClipApplier when it supplies a per-clip authored speed
        // from clip_speeds.json. When true, that value WINS over the live AnimationClip.averageSpeed
        // read below.
        //
        // Why an override is needed at all: averageSpeed is NET root displacement over the clip's
        // duration, so it only measures pace when the root travels monotonically.
        // Pacing_And_Talking_On_A_Phone paces back and forth -- outbound and return cancel -- and its
        // entry records `refSource: NON_MONOTONIC: net/path = 0.144, averageSpeed invalid`, with a
        // hand-derived 0.5636 (median instantaneous speed over moving frames) in its place. Session
        // 54's live read silently bypassed that correction and divided by the invalid 0.415, giving
        // animator.speed 1.928 where FIX C intended 1.419 -- 36% fast.
        //
        // Measured, the other three Mixamo clips agree exactly between the two sources
        // (Old_Man_Walk 0.3915, carry_and_walk 0.8969, Drunk_Walk 0.7160), so this changes nothing
        // for them. The live read stays the DEFAULT because Zone A runs a blend tree, where authored
        // speed is a per-frame mix and no single stored constant can be right.
        [System.NonSerialized] public bool referenceSpeedMpsExplicit;

        // Session 44. A clamp here is a FUSE against bad data, not a constraint meant to bind in
        // normal operation -- if it engages on a healthy sample, it is set wrong.
        //
        // minSpeedScale 0.3 -> 0.05. A fixed scale floor stopped being meaningful once
        // referenceSpeedMps became per-clip (FIX C): the floor's equivalent GROUND speed is
        // minSpeedScale * referenceSpeedMps, and authored speeds now span an 11x range.
        //   Old_Man_Walk (ref 0.392):  0.3 floor == 0.118 m/s ground
        //   Running      (ref 4.406):  0.3 floor == 1.32  m/s ground  <- binds on ordinary walking
        // Running would have sat on the floor for any pace below 1.32 m/s. Standing still is
        // handled by FIX A's idle regime (animator.speed = 1.0), not by this floor, so lowering it
        // cannot reintroduce the frozen-statue case.
        //
        // maxSpeedScale 1.5 -> 3.0. Measured requirement after FIX C: Old_Man_Walk needs 1.788 and
        // Pacing_And_Talking_On_A_Phone 1.928, so 1.5 was clamping healthy samples and leaving 19%
        // and 29% residual mismatch. Neither is the "absurd, blurring playback" the original
        // comment guards against. 3.0 still catches genuine garbage -- e.g. an in-place clip whose
        // authored speed measures 0.01 would demand a scale near 100.
        public float minSpeedScale = 0.05f;
        public float maxSpeedScale = 3.0f;

        // Session 44 (1.3): a clamp that engages silently degrades a sample without telling anyone.
        // Counted per agent and reported once at teardown, so INDEX.md can show which clip hit which
        // clamp and how often. This is the clamp's real job: signalling that the data is wrong.
        private int clampLoHits;
        private int clampHiHits;
        private int scaleFrames;
        private float worstRequiredScale;

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
        // Session 54: below this authored ground speed, the playing clip is not locomotion and
        // animator.speed is held at its authored rate. Replaces idleSpeedThresholdMps/idleDwellSec
        // (both removed): those thresholded a position-differenced EMA, which is the very signal
        // that closed the loop, and their hysteresis existed only to stop that EMA flickering.
        private const float MinAuthoredSpeedMps = 0.05f;

        // Session 54-F: the control law's DOMAIN, which is a different question from the numerical
        // division guard above.
        //
        //     realised ground speed = authored * animator.speed
        //
        // only holds for clips that TRANSLATE. A turn-in-place clip rotates without travelling, so
        // `commanded / authored` is not a large number, it is a meaningless one. Session 54 measured
        // exactly that: scared, backing away from the robot, spent 3.4 s in StandQuarterTurnRight
        // (clip weight 1.000 on 59 of 62 frames -- settled, not a blend transition) with a commanded
        // 1.425 m/s over an authored 0.0848, and sat on maxSpeedScale the whole time.
        //
        // The boundary is read off the GAP in the measured distribution, not chosen to make a check
        // pass (dump it with AutoTrial/Session 54/Dump locomotion clip translation):
        //
        //   non-translating   Idle 0.0000 | StandTurnR90 0.0000 | StandTurns90 0.0392
        //                     Sitting 0.0 | Standing_Arguing 0.0 | StandQuarterTurn 0.0848
        //   ---------------------------------- 4.6x gap ----------------------------------
        //   translating       Old_Man_Walk 0.3915 | Pacing_Phone 0.5636 | Drunk_Walk 0.716
        //                     WalkBack 1.334 | StrafeLeft 1.442 | HumanoidWalk 1.558
        //                     strafe_45 2.260 | HumanoidRun 5.662
        //
        // 0.20 is the geometric midpoint of that gap (sqrt(0.0848 * 0.3915) = 0.182), leaving a
        // factor of ~2 of margin on both sides. Anything in 0.1-0.35 classifies identically today;
        // the margin is what keeps that true after an asset re-export.
        //
        // This generalises Session 41's rule for reaction states -- hold authored rate whenever the
        // clip is not locomotion -- from "named reaction states" to "any clip that does not travel".
        private const float LocomotionAuthoredSpeedMps = 0.20f;

        // Session 54 REMOVED: driveIdlingParameter (Session 44 FIX A, second half).
        //
        // It wrote the Animator's "Idling" bool from the position-differenced stationary latch.
        // Measured consequences:
        //   Zone A       -- the shared controller has NO "Idling" parameter at all, so the write
        //                   was a no-op. Clip selection there runs off Forward alone, correctly.
        //   white_cane   -- its controller DOES have "Idling", gating Idle <-> Locomotion, so the
        //                   write landed and self-latched:
        //                     not moving -> smoothedSpeed ~ 0 -> stationary -> Idling = true
        //                       -> held in Idle -> Idle clip has no root motion -> not moving
        //                   Session 54 measured Forward at 4.767 (correctly computed, heading
        //                   aligned to velocity within 0.00 deg) while the agent sat in Idle for
        //                   98.7% of the trial and never moved. This is the same defect class as
        //                   the animator.speed loop, one layer over: an output used as an input.
        // Removing it is a loop removal, not a criterion change, and it is a no-op on every
        // controller that lacks the parameter.

        private Animator animator;
        private Scenario.Agents.Base baseAgent;
        // Smoothed so a single noisy frame (e.g. a footstep-driven root-motion micro-stutter)
        // doesn't jerk the playback rate -- exponential moving average, not a hard window buffer.
        private float smoothedSpeed = 0f;
        /// <summary>Session 44: the EMA this component actually thresholds and divides by, exposed
        /// read-only for the self-test probe. Checks 3.1/3.2 initially measured against the
        /// per-Update instantaneous position delta instead, which is zero on ~86% of frames because
        /// the transform advances in discrete animation steps -- so they selected the wrong frames
        /// entirely and reported failures that were artefacts of the measurement, not the code.</summary>
        public float SmoothedSpeedMps { get { return smoothedSpeed; } }
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

        /// <summary>
        /// Session 44 (1.3). Emitted once per agent so a clamp engagement is never silent. Parsed
        /// out of unity.log into INDEX.md -- a clamp firing does not necessarily mean the trial is
        /// bad, but it must be visible, because it means a sample was played at a rate other than
        /// the one the geometry demanded.
        /// </summary>
        private void OnDisable()
        {
            if (scaleFrames == 0 || (clampLoHits == 0 && clampHiHits == 0)) { return; }
            Debug.Log(string.Format(
                "[S44Clamp] agent={0} ref={1:F4} frames={2} loHits={3} ({4:P1}) hiHits={5} ({6:P1}) "
                + "worstRequired={7:F3} range=[{8:F2},{9:F2}]",
                gameObject.name, referenceSpeedMps, scaleFrames,
                clampLoHits, (float)clampLoHits / scaleFrames,
                clampHiHits, (float)clampHiHits / scaleFrames,
                worstRequiredScale, minSpeedScale, maxSpeedScale));
        }

        /// <summary>
        /// Session 54. The ground speed the CURRENTLY PLAYING animation is authored to travel at,
        /// blended by the weights the Animator is actually mixing this frame.
        ///
        /// Read live rather than from a serialized field, for two reasons. A static reference is
        /// what set the old loop gain above 1.0 (Zone A's 1.3 against HumanoidWalk's real 1.556).
        /// And these agents run a blend tree, not a single clip -- Forward moves the mix between an
        /// idle clip and a walk clip, so the authored speed is a per-frame quantity, not a per-agent
        /// one. Weighting by clip weight is what makes the quotient below the correct scale during
        /// the blend rather than only at its endpoints.
        ///
        /// AnimationClip.averageSpeed is the clip's own baked root motion, a property of the asset.
        /// Nothing this component writes can change it, which is why it is safe to divide by.
        /// </summary>
        private float AuthoredClipSpeedMps()
        {
            // An explicitly supplied per-clip value wins: it exists precisely for the clips whose
            // averageSpeed is known to be wrong, which are exactly the clips where reading it live
            // would be confidently incorrect.
            if (referenceSpeedMpsExplicit) { return referenceSpeedMps; }
            var clips = animator.GetCurrentAnimatorClipInfo(0);
            float weighted = 0f;
            float totalWeight = 0f;
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i].clip == null) { continue; }
                Vector3 a = clips[i].clip.averageSpeed;
                a.y = 0f; // ground travel only -- a bob or a stair clip's rise is not walking speed
                weighted += a.magnitude * clips[i].weight;
                totalWeight += clips[i].weight;
            }
            return totalWeight > 1e-4f ? weighted / totalWeight : 0f;
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

            // Session 54: the input is Base.velocity -- the SFM's COMMANDED velocity -- and the
            // reason is not accuracy. It is not a measurement at all. It is that Base.velocity is
            // not a function of animator.speed, which is the necessary and sufficient condition
            // for this component to stop being a closed loop.
            //
            // What was wrong before (Session 53, trial_outputs/S53_ROOT_CAUSE.md). For a
            // root-motion agent, Base.Move() does NOT translate the transform; every metre comes
            // from PedestrianModulator.ApplyAnimatorRootMotion()'s `transform.position +=
            // animator.deltaPosition`, and deltaPosition scales with animator.speed. So
            // differencing transform.position fed this component's own output straight back into
            // its input:
            //
            //     animator.speed -> ground speed -> smoothedSpeed -> animator.speed
            //
            // Loop gain = (ground speed at animator.speed 1.0) / reference. Measured on corridor:
            // k = 1.556 m/s for HumanoidWalk against reference 1.3, so gain 1.20 -- divergent. It
            // ran away until maxSpeedScale=3.0 caught it, giving a predicted terminal ground speed
            // of 3.0 x 1.556 = 4.67 m/s; windowed endpoint displacement, an independent source,
            // measured 4.6-4.7. That is the user's "walks faster and faster / top speed is far too
            // high", and it is why raising maxSpeedScale 1.5 -> 3.0 doubled the symptom.
            //
            // Session 36 removed Base.velocity because white_cane_user reported ~2.09 m/s against
            // ~0.35 m/s of visible movement. That observation was real; the diagnosis was not. It
            // was never a severed drive chain -- Session 54 measured white_cane sitting in its
            // controller's Idle STATE with Forward at 4.767, held there by this component's own
            // Idling write (see driveIdlingParameter's removal note below). A commanded velocity
            // that nothing consumes is not an inaccurate velocity.
            //
            // Precondition, and it is load-bearing: Session 47's absolute-target modulation (e)
            // pins |Base.velocity| to baseWalkSpeedMps * walkSpeedMultiplier. Without (e), Base.cs
            // :122 writes the modulated result back into the field SFAgent.cs:71 integrates from,
            // so Base.velocity would be a function of its own history and this would be a longer
            // path around the same loop. Do not revert (e).
            Vector3 v = baseAgent != null ? baseAgent.velocity : Vector3.zero;
            v.y = 0f;
            float instantaneousSpeed = v.magnitude;
            bool plausible = instantaneousSpeed <= MaxPlausibleSpeedMps;

            if (plausible)
            {
                float alpha = 1f - Mathf.Exp(-Time.deltaTime / SmoothingTau);
                smoothedSpeed = Mathf.Lerp(smoothedSpeed, instantaneousSpeed, alpha);
            }
            // else: an implausible command. Discarded rather than folded into the average. With
            // (e) pinning the magnitude this should never fire; if it does, the fuse is telling
            // you the commanded speed is wrong, which is a different bug from this component's.

            // Keep the EMA above running through the reaction (so locomotion resumes at the right
            // rate afterwards) but don't let it drive animator.speed while a reaction clip owns
            // the Animator -- see reactionStateNames' comment for the measured justification.
            if (IsReactionActive())
            {
                animator.speed = 1.0f;
                return;
            }

            // Session 54 FIX A, restated on the clip instead of on measured motion.
            //
            // "Not travelling" is still its own regime -- a character with no ground travel has no
            // cadence to match -- but it is now decided by WHICH CLIP IS PLAYING, not by how fast
            // the body was observed to move. An idle clip has no authored root motion; that is a
            // property of the animation asset, so it cannot be influenced by animator.speed and
            // closes no loop. It also removes the need for the threshold/dwell hysteresis entirely:
            // that machinery existed to stop a position-differenced EMA from flickering, and there
            // is no longer a position-differenced EMA.
            //
            // Both stationary regimes are covered by this one test, because upstream already drives
            // them: StopNavigation() (Base.cs:195) calls StopAnimator() (Base.cs:214), which sets
            // Forward and Strafe to 0, and the blend tree follows Forward to its idle clip. That
            // happens at the SLATE frozen spawn (InitDest(spawnPos) -> already there -> arrive) and
            // again on real arrival. Session 54 measured Forward going to exactly 0.000 at t=16.44
            // on corridor, with the clip returning to HumanoidIdle, entirely without this
            // component's help.
            // Domain test first (is this clip locomotion at all?), which subsumes the numerical
            // division guard since LocomotionAuthoredSpeedMps > MinAuthoredSpeedMps. Both are kept:
            // the guard states what is numerically unsafe, the domain states what is semantically
            // meaningless, and they would come apart if either constant were ever retuned.
            float authoredSpeed = AuthoredClipSpeedMps();
            if (authoredSpeed < LocomotionAuthoredSpeedMps || authoredSpeed < MinAuthoredSpeedMps)
            {
                // Authored rate, not zero: an idle clip should breathe at its designed pace.
                // Scaling it toward zero is the frozen-statue failure mode.
                animator.speed = 1.0f;
                return;
            }

            // The law. Ground speed for a root-motion agent is authoredSpeed * animator.speed, so
            // setting animator.speed to commanded/authored makes realised ground speed track the
            // SFM command exactly. Open loop: the numerator does not depend on the output.
            float required = smoothedSpeed / authoredSpeed;
            float scale = Mathf.Clamp(required, minSpeedScale, maxSpeedScale);

            // Session 44 (1.3): count clamp engagements so they are visible rather than silent.
            scaleFrames++;
            if (required < minSpeedScale) { clampLoHits++; }
            else if (required > maxSpeedScale) { clampHiHits++; }
            if (required > worstRequiredScale) { worstRequiredScale = required; }

            animator.speed = scale;
            if (DiagEnabled && Time.frameCount % 60 == 0)
            {
                float legacySpeed = baseAgent != null ? baseAgent.velocity.magnitude : -1f;
                Debug.Log("[S36AnimScalerDiag] host=" + gameObject.name
                    + " commandedSpeed=" + smoothedSpeed + " baseVelocity=" + legacySpeed
                    + " authoredClipSpeed=" + AuthoredClipSpeedMps()
                    + " appliedAnimatorSpeed=" + animator.speed);
            }
        }
    }
}
