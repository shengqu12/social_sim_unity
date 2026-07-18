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
        public float fixedPitchDeg = -5f;
        public bool rigidMount = false;
    }
}
