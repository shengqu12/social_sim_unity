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

        // Session 10 (D2 treatment): soft-mounted POV camera. The camera stays a child transform
        // of robot.camera_first (unchanged parenting, adjustment #6 still honored) but
        // PovCameraSmoother (new file) overrides its *local* pose every LateUpdate with a
        // low-pass-filtered version of the mount's motion instead of inheriting it rigidly.
        // Time constants in seconds -- larger = smoother/laggier. rigidMount=true forces all taus
        // to ~0 (an every-frame snap to the raw mount pose) so the exact same code path serves as
        // the rigid-mount comparison case the brief asks for, rather than a second implementation.
        public float posSmoothTau = 0.12f;
        public float yawSmoothTau = 0.15f;
        public float pitchSmoothTau = 0.20f;
        public bool rigidMount = false;
    }
}
