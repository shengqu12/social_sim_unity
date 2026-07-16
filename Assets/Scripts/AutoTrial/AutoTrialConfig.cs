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

        // Chase camera follow geometry, recomputed every capture tick in TrialController.
        public float chaseDistance = 3f;
        public float chaseHeight = 2f;
        public float chaseLookHeight = 1f;
    }
}
