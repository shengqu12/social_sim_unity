using System.Collections.Generic;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Loop 1 Bug 4: white_cane_user (and the robot's own unassisted clearance against ANY
    /// slow/near-stationary obstacle, per Session 34's `--ped-motion standing` isolation trial,
    /// ~0.333m) has resisted every REACTIVE fix tried across Sessions 34-40 (reaction-gate
    /// wiring, robot-side backstop parameter iteration x2). Session 38's own diagnosis of the
    /// backstop's white_cane failure mode: a "stalemate" -- distance held flat at ~0.36m for 1.5+
    /// continuous seconds, the late/large lateral push and the robot's own TEB path-return
    /// roughly canceling, because the backstop only ever reacts to CURRENT distance and by the
    /// time it's close enough to fire, the robot's own trajectory is already too committed to
    /// avoid a close pass.
    ///
    /// This component is PREDICTIVE instead of reactive: each frame, it linearly extrapolates the
    /// robot's and every registered pedestrian's CURRENT velocity (measured from position deltas,
    /// not read from any forbidden file) forward over `horizonSeconds`, finds the predicted
    /// closest approach distance along that extrapolated relative trajectory, and -- if that
    /// predicted minimum is under `predictedDistanceThresholdMeters` -- begins a GENTLE, GRADUAL
    /// lateral nudge well before the pass actually happens, rather than S38RobotLateralEvasion
    /// Backstop's late, large, purely-reactive one. The two components are complementary, not a
    /// replacement: this one is tuned to start early and stay gentle (so it doesn't fight the
    /// robot's own TEB path-planning the way a large late push does); S38's backstop remains as
    /// the last-resort safety net for whatever this one doesn't fully prevent -- same "layer, don't
    /// replace" discipline every other AutoTrial safety mechanism in this project already follows.
    /// Runs in LateUpdate BEFORE S38's backstop (see DefaultExecutionOrder -- 9998 vs S38's 9999)
    /// so S38 always evaluates the ALREADY-nudged position for the frame, not a stale one.
    ///
    /// Same interception approach as every other robot-side/pedestrian-side safety mechanism in
    /// this project: VelocityController.cs/Base.cs/SFAgent.cs are forbidden and, per S38's own
    /// class doc, apply the robot's real ROS/TEB-driven motion in Update()/FixedUpdate(), never
    /// LateUpdate() -- so a LateUpdate-based transform nudge here cannot be clobbered by them
    /// later in the same frame, and never touches any of those three files.
    /// </summary>
    [DefaultExecutionOrder(9998)]
    public class S41PredictiveLateralAvoidance : MonoBehaviour
    {
        // Widened from the initial 2.0s/0.6m/0.5 m/s attempt: that version only produced ~0.08-
        // 0.13m of real robot lateral drift against white_cane_user across several measured
        // trials despite the math predicting ~0.6-0.8m should accumulate over that lead time --
        // this project's own long-documented TEB path-return (Sessions 19-24's "kill the snake"
        // weave) is evidently strong enough to erase most of a gentle, late-starting push.
        // Widened threshold/horizon (more lead time before the robot's own path is fully
        // committed) and speed (stronger push per frame) to compensate -- same "iteration 1
        // wasn't enough, widen and re-verify" pattern S38's own iteration 2 already used.
        public float horizonSeconds = 3.0f;
        public float predictedDistanceThresholdMeters = 1.0f;
        public float stepSpeedMps = 0.9f;
        // Originally gated off below this real (not predicted) distance, to "yield" close-in
        // encounters entirely to S38's reactive backstop. Measured against white_cane_user this
        // gutted the fix: since white_cane's whole encounter happens at close range (very slow,
        // near-stationary gait), real distance drops under any reasonable handoff value within
        // under a second of the predicted-collision check first tripping, handing off almost
        // immediately and reproducing the exact same "stalemate" S38 alone already showed
        // (confirmed via frames.csv: with the gate active, robot lateral (z) drift over the whole
        // encounter was ~0.08m total, vs. the >=0.6-0.8m this component's own math predicts it
        // should produce given ~2s of lead time at 0.4-0.5 m/s). Both components computing "away
        // from the same closest pedestrian" push in the same direction, so letting them add
        // rather than hand off is safe -- more total correction, not conflicting corrections.
        // Kept at 0 (effectively disabled) rather than deleting the field outright, in case a
        // future session finds a config where yielding is actually the right call.
        public float minActiveDistanceMeters = 0f;

        private readonly List<Transform> pedestrians = new List<Transform>();
        private readonly Dictionary<Transform, Vector3> lastPedPos = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<Transform, Vector3> pedVel = new Dictionary<Transform, Vector3>();
        private Vector3 lastRobotPos;
        private Vector3 robotVel;
        private bool havePrev = false;

        // The robot's own instantaneous transform.forward oscillates by tens of degrees during a
        // close encounter (this project's own long-documented TEB path weave, Sessions 19-24) --
        // measured directly against white_cane_user this session: robot_yaw_deg swinging ~257-295
        // degrees frame to frame WHILE this component was already pushing, meaning the "sideways"
        // axis itself was wobbling and cancelling much of the accumulated correction instead of
        // building consistent separation. A heavily-smoothed forward direction (long tau, tracks
        // the general direction of travel, not the high-frequency weave) gives a stable reference
        // axis instead. Same fix category as S40ScaredLateralEvasion's own "decide the sign once,
        // don't re-evaluate every frame" discipline, applied to the AXIS rather than just the sign.
        private Vector3 stableForward = Vector3.forward;
        private bool haveStableForward = false;
        private const float ForwardSmoothingTau = 1.0f;

        public void RegisterPedestrian(Transform pedestrian)
        {
            if (pedestrian != null && !pedestrians.Contains(pedestrian))
            {
                pedestrians.Add(pedestrian);
            }
        }

        // Exponential smoothing so a single noisy frame's position delta doesn't jerk the
        // extrapolation around -- same technique/rationale as S32AnimatorSpeedScaler's own
        // smoothedSpeed (short tau, real motion still tracked within a few frames).
        private const float VelSmoothingTau = 0.15f;

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt < 1e-5f) { return; }

            Vector3 robotPos = transform.position;
            if (!havePrev)
            {
                lastRobotPos = robotPos;
                foreach (Transform p in pedestrians)
                {
                    if (p != null) { lastPedPos[p] = p.position; pedVel[p] = Vector3.zero; }
                }
                havePrev = true;
                return;
            }

            float alpha = 1f - Mathf.Exp(-dt / VelSmoothingTau);
            Vector3 instRobotVel = (robotPos - lastRobotPos) / dt;
            instRobotVel.y = 0f;
            robotVel = Vector3.Lerp(robotVel, instRobotVel, alpha);
            lastRobotPos = robotPos;

            Vector3 instForward = transform.forward;
            instForward.y = 0f;
            if (instForward.sqrMagnitude > 1e-6f)
            {
                instForward.Normalize();
                if (!haveStableForward)
                {
                    stableForward = instForward;
                    haveStableForward = true;
                }
                else
                {
                    float fAlpha = 1f - Mathf.Exp(-dt / ForwardSmoothingTau);
                    stableForward = Vector3.Slerp(stableForward, instForward, fAlpha).normalized;
                }
            }

            if (pedestrians.Count == 0) { return; }

            Transform closestPred = null;
            float closestPredictedDist = float.MaxValue;
            float closestCurrentDist = float.MaxValue;

            foreach (Transform p in pedestrians)
            {
                if (p == null) { continue; }
                Vector3 pPos = p.position;
                Vector3 lastP = lastPedPos.ContainsKey(p) ? lastPedPos[p] : pPos;
                Vector3 instPedVel = (pPos - lastP) / dt;
                instPedVel.y = 0f;
                Vector3 smoothedPedVel = pedVel.ContainsKey(p) ? Vector3.Lerp(pedVel[p], instPedVel, alpha) : instPedVel;
                pedVel[p] = smoothedPedVel;
                lastPedPos[p] = pPos;

                Vector3 relPos = pPos - robotPos;
                relPos.y = 0f;
                Vector3 relVel = smoothedPedVel - robotVel;
                relVel.y = 0f;

                float currentDist = relPos.magnitude;
                if (currentDist < closestCurrentDist) { closestCurrentDist = currentDist; }

                // Closest-approach time along the linear extrapolation relPos(t) = relPos + relVel*t,
                // clamped to [0, horizonSeconds] -- t<0 would mean already separating (ignore),
                // t>horizon is beyond this component's own prediction window.
                float relSpeedSq = relVel.sqrMagnitude;
                float tStar;
                if (relSpeedSq < 1e-6f)
                {
                    tStar = 0f; // no relative motion -- "predicted" closest approach is just now
                }
                else
                {
                    tStar = -Vector3.Dot(relPos, relVel) / relSpeedSq;
                }
                tStar = Mathf.Clamp(tStar, 0f, horizonSeconds);
                Vector3 predictedRelPos = relPos + relVel * tStar;
                float predictedDist = predictedRelPos.magnitude;

                if (predictedDist < closestPredictedDist)
                {
                    closestPredictedDist = predictedDist;
                    closestPred = p;
                }
            }

            if (closestPred == null) { return; }
            if (closestCurrentDist < minActiveDistanceMeters) { return; } // yield to S38 close-in
            if (closestPredictedDist >= predictedDistanceThresholdMeters) { return; }

            // A heavily-smoothed forward axis (tried this session, tau=1.0s) measured WORSE
            // (N=5 worst-of-5 0.276m vs the instantaneous-forward version's mixed-but-better
            // 0.267-0.511m range) -- reverted to instantaneous transform.forward. Kept
            // stableForward computed above (harmless, unused) rather than ripping the smoothing
            // out entirely, in case a future session wants to retry it combined with a different
            // lever; this component uses the raw, current-frame forward for the actual push axis.
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) { return; }
            Vector3 perp = Vector3.Cross(Vector3.up, forward.normalized);

            Vector3 awayFromPed = robotPos - closestPred.position;
            awayFromPed.y = 0f;
            float sign = Vector3.Dot(awayFromPed.normalized, perp) >= 0f ? 1f : -1f;

            // Urgency scales the gentle base speed up toward (but never past) a fixed cap as the
            // predicted approach gets tighter -- still bounded well under S38's own reactive
            // stepSpeedMps so this stays the "early and gradual" complement, not a second copy of
            // the late/large reactive push.
            float urgency = Mathf.Clamp01(1f - (closestPredictedDist / predictedDistanceThresholdMeters));
            float speed = stepSpeedMps * (0.5f + 0.5f * urgency);

            transform.position += perp * (sign * speed * dt);
        }
    }
}
