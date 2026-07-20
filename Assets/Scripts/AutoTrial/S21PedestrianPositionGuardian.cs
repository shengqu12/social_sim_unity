using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 21 STEP 3 fix: runtime-only guard against the white_cane_user origin-reset
    /// defect. Root cause, characterized via S21TransformWatcher (2026-07-20): destPos
    /// transiently becomes Vector3.zero as a BY-DESIGN side effect of the SLATE freeze
    /// (InitDest(spawnPos) -> the navigation coroutine's own CloseEnough()/StopNavigation()
    /// cycle resets destPos to Vector3.zero once "already there", a state every Zone B
    /// character passes through). Base.Move() already guards this ("$$$ FIX: can't move to
    /// 0,0,0", an early-return when destPos==Vector3.zero) but white_cane_user's Animator
    /// lives on a nested child (Male_Adult_12, not the container root) -- Base.LateUpdate()'s
    /// nested-Animator root-motion path applies animator.deltaPosition unconditionally,
    /// un-gated by that same destPos check, and does so a whole frame later than Move()'s own
    /// guard. For every OTHER Zone B container the Animator sits on the root, so Unity's own
    /// OnAnimatorMove() dispatch (which Base.Start() explicitly disables for the nested case)
    /// already goes through the guarded path -- white_cane_user is the only current container
    /// exercising this specific gap. Confirmed shared-family with the brief's suspected
    /// "possibly shared with the dormant patrol case": ANY nested-Animator character combined
    /// with EnablePatrol's own repeated InitDest calls would hit the identical destPos==zero
    /// window on every waypoint arrival, not just at spawn -- flagged, not fixed here (patrol
    /// is dormant/unused, out of this step's scope).
    ///
    /// Fix strategy: root cause lives in Base.cs/INavigable.cs (both off-limits to edit this
    /// session). Runtime-first per the brief -- this component doesn't patch the mechanism,
    /// it guards the OBSERVABLE outcome: watches its own transform every LateUpdate (after
    /// Base.LateUpdate() has had its chance to run, so genuine repositioning is never mistaken
    /// for the bug) and snaps back to the last known-good, intentionally-set position if it
    /// ever diverges past a physically-implausible-per-frame threshold. AutoTrialBootstrap
    /// updates the "known-good" reference every time it deliberately moves this pedestrian
    /// (spawn, SLATE release) via SetIntendedPosition -- anything else this large, this fast,
    /// is the bug, not a real navigation event (Zone B's own top speed cruising is nowhere
    /// near this threshold; see MaxPlausibleDeltaPerFrame).
    /// </summary>
    public class S21PedestrianPositionGuardian : MonoBehaviour
    {
        // Zone B containers cruise well under 2 m/s; at a real 60fps LateUpdate this is a
        // ~30x margin above any plausible single-frame displacement, while comfortably below
        // the ~110m single-frame jump this defect actually produces.
        private const float MaxPlausibleDeltaPerFrame = 2.0f;

        private Vector3 intendedPosition;
        private bool armed;

        public void SetIntendedPosition(Vector3 pos)
        {
            intendedPosition = pos;
            transform.position = pos;
            armed = true;
        }

        void LateUpdate()
        {
            if (!armed) { return; }
            float jump = Vector3.Distance(transform.position, intendedPosition);
            if (jump > MaxPlausibleDeltaPerFrame)
            {
                Debug.LogWarning("[S21PedestrianPositionGuardian] " + name + " jumped " + jump.ToString("F2")
                    + "m from its intended position " + intendedPosition.ToString("F3") + " to "
                    + transform.position.ToString("F3") + " in one frame -- reverting (known "
                    + "destPos==Vector3.zero root-motion defect, see class doc).");
                transform.position = intendedPosition;
            }
            else
            {
                // Legitimate small-scale movement (release-phase navigation, animation sway) --
                // track it as the new known-good baseline instead of fighting real motion.
                intendedPosition = transform.position;
            }
        }
    }
}
