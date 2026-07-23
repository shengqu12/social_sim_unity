using System;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Plain-old-data mirror of the JSON file written by tools/run_trial.py (JsonUtility.FromJson
    /// deserializes it directly -- field names/shapes here must match the Python side exactly).
    /// No behavior lives here; see AutoTrialBootstrap for how each field is consumed.
    /// </summary>
    [Serializable]
    public class AutoTrialConfig
    {
        // --appearance value -- resolved against Zone A (Rocketbox convention) or Zone B
        // (hardcoded container map) by AutoTrialBootstrap. Unity is authoritative on validity.
        public string appearance;

        // --personality value, parsed against PedestrianModulator.PersonalityType. Ignored
        // (with a warning) for Zone B appearances, which lock their own preset behavior.
        public string personality = "Indifferent";

        // Required: where the pedestrian spawns.
        public PoseXYZYaw spawnPose;

        // Optional: if length >= 2, PedestrianModulator.EnablePatrol(waypoints[0], waypoints[1])
        // is used (ping-pong only -- the modulator API doesn't support more than 2 points).
        // Ignored for Zone B (no modulator is ever attached to a preset).
        public Vec3[] patrolWaypoints;

        public int fps = 15;
        public float durationSec = 90f;
        public string outDir;

        // Optional robot goal override. JsonUtility can't represent "no value" for a struct,
        // so hasGoalPose is the explicit on/off switch -- goalPose itself is ignored unless true.
        public bool hasGoalPose = false;
        public PoseXYZYaw goalPose;

        // Optional pedestrian destination override (Session 10, D4). Same on/off convention as
        // goalPose above. Ignored (dest falls back to spawnPos, the pre-Session-10 behavior) when
        // false -- see AutoTrialBootstrap.SpawnPedestrian(). Drives INavigable.InitDest() on both
        // Zone A and Zone B; orthogonal to personality/patrol, exactly like the existing
        // patrolWaypoints[0] destination Zone A already uses when patrol is enabled.
        public bool hasPedGoalPose = false;
        public PoseXYZYaw pedGoalPose;

        // Session 14 (SLATE v2): the pedestrian spawns frozen at --ped-distance + --slate-margin
        // (further out than this), and TrialController.PollForTrigger releases it + starts
        // capture the instant the live robot<->pedestrian ground-plane distance first drops to
        // this value or below -- i.e. this is both the trial's dist0 target AND the live trigger
        // threshold, by construction the same number. Set from --ped-distance in run_trial.py
        // (independent of --spawn/--slate-margin, which only affect where the freeze happens).
        public float triggerDistanceMeters = 8.0f;

        // Session 28 PART 2: provenance only (Python already resolves spawnPose/pedGoalPose
        // geometry per-scenario before this JSON is written) -- logged into meta.json so a trial
        // is self-describing without cross-referencing the launch command.
        public string scenario = "headon";

        // Session 28 PART 3b: reuses PedestrianModulator.walkSpeedMultiplier (previously only
        // reachable via child-appearance-specific presets). Zone A only -- see
        // AutoTrialBootstrap.SpawnPedestrian.
        public float pedSpeedMultiplier = 1.0f;
        // Session 28 PART 3a: "standing" keeps the pedestrian's own destination at its spawn
        // pose permanently (SLATE's own capture-start trigger still fires normally) -- see
        // AutoTrialBootstrap.SpawnPedestrian. Zone A only.
        public string pedMotion = "normal";
        // Session 28 PART 3c: phone/texting distraction layer + reaction-delay modulator flag.
        // Zone A only -- see AutoTrialBootstrap.SpawnPedestrian.
        public bool pedDistracted = false;

        // Session 15: root-caused why goal_reached almost never fires -- terminationReason was
        // "duration" on 100% of trials checked across Sessions 12/13/14, because the configured
        // far corridor goal (44m from robot start) needs ~73s of pure driving at max_vel_x=0.6
        // m/s, more than any trial's own duration budget even before pre-roll. Not a 0.5m-
        // tolerance bug (final robot-to-goal distances measured 5-33m, nowhere near the
        // tolerance) -- goal_reached was structurally unreachable, not mistuned. This is the
        // actual fix: end capture postEncounterGraceSec after the live robot<->pedestrian
        // distance first re-exceeds triggerDistanceMeters (i.e. the pedestrian has been passed
        // and is moving away again) rather than filming an unreachable goal for the full
        // --duration. hasPostEncounterGrace follows the same on/off convention as hasGoalPose
        // above; --duration remains the hard cap either way.
        public bool hasPostEncounterGrace = false;
        public float postEncounterGraceSec = 8.0f;

        // Session 31 FIX 5(b): raises Scared's/Surprised's own ACTION-trigger radius
        // (PedestrianModulator.scaredRadius/surpriseRadius -- the distance at which the flee/
        // freeze reaction itself starts) so the reaction begins while the robot is still
        // approaching, giving the camera time to actually capture it -- distinct from the
        // general SLATE release/avoidance-onset distance (triggerDistanceMeters above / TEB's own
        // costmap params). Same hasX/X on-off convention as goalPose etc.; false leaves
        // PedestrianModulator's own compiled-in defaults (3.0/4.0) untouched. Zone A only -- see
        // AutoTrialBootstrap.SpawnPedestrian.
        public bool hasScaredRadiusOverride = false;
        public float scaredRadiusOverride = 3.0f;
        public bool hasSurpriseRadiusOverride = false;
        public float surpriseRadiusOverride = 4.0f;
        // Session 33 FIX 3: PedestrianModulator.ModulateSurprised()'s cooldownDuration (compiled-in
        // default 4.0s, counted from the trigger instant) was found too short this session -- a
        // real trial showed a SECOND, spurious rising-edge trigger firing ~5.7s after the true
        // closest approach while distance was 7.5-9.5m and growing (i.e. during the post-pass
        // separation phase, not a real second encounter), consistent with dist_to_pedestrian
        // fluctuating near the surpriseRadius threshold during separation (this project's own
        // documented TEB path-weave noise) re-arming a rising edge once the short cooldown expired.
        // Overriding to a much longer value for the rest of a normal trial's duration prevents this
        // without touching PedestrianModulator.cs (outside writable scope) -- same non-edit,
        // plain-field-override pattern as scaredRadiusOverride/surpriseRadiusOverride above.
        public bool hasSurpriseCooldownOverride = false;
        public float surpriseCooldownOverride = 4.0f;

        public CameraParams camera = new CameraParams();
        public int jpgQuality = 85;
    }

    [Serializable]
    public struct Vec3
    {
        public float x;
        public float y;
        public float z;

        public Vector3 ToVector3()
        {
            return new Vector3(x, y, z);
        }
    }

    [Serializable]
    public struct PoseXYZYaw
    {
        public float x;
        public float y;
        public float z;
        public float yawDeg;

        public Vector3 Position
        {
            get { return new Vector3(x, y, z); }
        }

        public Quaternion Rotation
        {
            get { return Quaternion.Euler(0, yawDeg, 0); }
        }
    }

    [Serializable]
    public class CameraParams
    {
        // POV mount offset relative to the robot's existing first-person camera transform.
        // Adjustment #6 (2026-07-15): keep this at (0,0,0) -- the POV camera is a new child at
        // zero local offset copying the existing camera's FOV/near/far, not a retarget of it.
        public float povOffsetX = 0f;
        public float povOffsetY = 0f;
        public float povOffsetZ = 0f;

        // Session 10 (D5): the chase/third-person camera is removed from the rig entirely --
        // POV only, per the output-format spec. No chase fields remain; see REPORT.md Session 10.

        // Round 3 fix (Session 10's decomposition of the mount's own rotation into eulerAngles.x/.z
        // for "pitch"/"roll" was the bug -- see PovCameraSmoother.cs class doc): position is now
        // always rigidly snapped to the mount (no tau, no field for it). Rotation is a world-frame
        // horizon lock with zero dependency on the mount's own orientation: roll is hardcoded to 0,
        // pitch is the constant below (sign convention: positive = tilt up, negative = tilt down),
        // and only yaw is derived from a transform (the robot chassis, not the camera mount) with
        // this low-pass time constant. rigidMount=true forces yawSmoothTau to ~0 (an every-frame
        // snap to the raw chassis yaw, no filtering) for direct before/after comparison.
        // yawSmoothTau default (0.5s) is empirically chosen, not the brief's Session 10 value (0.15,
        // which was tuned against the buggy pitch/roll-corrupted implementation and re-verified
        // Round 3 to be insufficient on its own -- see REPORT.md Round 3 Step 2 for the tau sweep).
        public float yawSmoothTau = 0.5f;
        // Session 17 (Step 3, real-A1 camera pose): default retired from Round 3's arbitrary -5
        // (downtilt) to 0 (LEVEL) -- the cited real A1's RealSense D435i mount faces level, not
        // downtilted. Sign convention unchanged (positive = tilt up, negative = tilt down).
        public float fixedPitchDeg = 0f;
        // Session 17 (Step 3): ABSOLUTE camera height in meters above the ground directly under
        // the robot, resolved once at rig build time via a downward raycast (never a blind local
        // offset from the existing first-person camera mount) -- see
        // AutoTrialBootstrap.BuildPovCamera and PovCameraSmoother. Default 0.32: cited, the A1
        // stands ~0.40m tall, RealSense D435i in the front head puts the lens at ~0.30-0.32m.
        public float camHeightMeters = 0.32f;
        public bool rigidMount = false;

        // Session 26 (course-locked camera, standing spec): yaw target source. "course" = direction
        // of travel over a trailing window (default); "chassis" = the pre-S26 behavior (robot body
        // heading, PovCameraSmoother's own headingSource). Position/pitch/roll are unaffected by
        // this switch -- only where the smoothed yaw's TARGET comes from changes. See
        // PovCameraSmoother.ComputeCourseYawTarget for the full mechanism.
        public string camYawMode = "course";
        // Trailing window (seconds) used to estimate direction of travel from position history.
        // Session 27: promoted from S26's spec default (1.5s) to 8.0s, period-matched to TEB's own
        // measured ~9.6s residual weave (S23/S24) -- 1.5s could not meaningfully damp a 9.6s-period
        // oscillation (a low-pass needs tau on the order of the period it's damping, not a fraction
        // of it). S26 confirmed 8s/8s clears far-field SIF and gets landmark swing near its bar;
        // see REPORT.md Session 26/27.
        public float camCourseWindowSec = 8.0f;
        // Low-pass time constant applied to the course-direction yaw target (separate from
        // yawSmoothTau above, which remains chassis-mode's own tau -- the two modes are tuned
        // independently since they smooth different, differently-noisy source signals). Session 27:
        // promoted to 8.0s alongside camCourseWindowSec, same rationale.
        public float camYawTauCourse = 8.0f;
        // Below this speed (m/s), direction of travel is undefined/noise-dominated -- the course
        // target HOLDS at its last valid value instead of chasing near-zero-displacement noise.
        public float camCourseHoldSpeedThreshold = 0.15f;
        // In-design escalation: the course target is computed by aiming at an explicit look-ahead
        // point (currentPos + courseDir * this distance), not a raw bearing -- see
        // PovCameraSmoother.ComputeCourseYawTarget's doc comment for why (mathematically equivalent
        // to the raw course angle for a unit courseDir, but frames the computation the way a real
        // gimbal-style tracker would: aim AT a point).
        public float camLookAheadMeters = 5.0f;

        // Session 27 (FOV truth): horizontal FOV in degrees, the camera property this project had
        // never audited against the real robot's sensor. Prior sessions (S12-S26) inherited
        // whatever vertical FOV the legacy first-person camera happened to carry (22.0deg, ->
        // 38.1267deg horizontal at the 16:9 capture aspect, per S24CameraFovProbe) -- narrower than
        // the real A1's RealSense D435i (RGB 69x42deg, depth 87x58deg H x V). Default here is the
        // D435i RGB horizontal FOV (69deg); pass 87 for the depth FOV. AutoTrialBootstrap.
        // BuildPovCamera converts this to Unity's own vertical Camera.fieldOfView using the actual
        // capture aspect (2*atan(tan(hFov/2)/aspect)) -- NOT copied from the legacy camera anymore.
        public float camHfovDeg = 69.0f;
    }
}
