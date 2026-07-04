using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>
/// Velocity-driven mocap gait player for the Unitree A1 in SEAN (Path C).
///
/// Replaces A1PlaybackController's fixed-cadence, open-loop playback with DISTANCE-DRIVEN
/// playback: the mocap gait cycle advances in proportion to how far the robot base actually
/// travels. Result: the legs "roll" with the ground (minimal foot slip), the dog holds a
/// pose when stationary, and cadence rises with speed -- all while playing the REAL recorded
/// gait, so there are no joint waveforms to hand-tune. Only one knob (framesPerMeter) matters.
///
/// PURELY VISUAL: this does NOT move the robot. The base is moved by cmd_vel via
/// VelocityController; this only articulates the 12 leg joints.
///
/// Reads the same file/format as A1PlaybackController:
///   Assets/Resources/a1mocap.csv, columns = [frameIdx, t, 12 joint angles in RADIANS]
///   joint column order = FR/FL/RR/RL x hip/thigh/calf (see JOINTS below).
/// A header row, if present, is skipped automatically (non-numeric -> ignored).
///
/// >>> IMPORTANT: DISABLE (uncheck) the A1PlaybackController component on this same
///     GameObject before playing. Both write xDrive.target on the same 12 joints and will
///     fight each other otherwise. This script sets the drive stiffness itself, because the
///     URDF import ships the leg drives limp (stiffness = 0).
/// </summary>
[DefaultExecutionOrder(100)] // run after base motion so the root position-delta includes this step's move
public class VelocityDrivenMocapPlayer : MonoBehaviour
{
    // Column order of the 12 joint angles in a1mocap.csv (must match MocapFrame.JOINTS).
    static readonly string[] JOINTS =
    {
        "FR_hip", "FR_thigh", "FR_calf",
        "FL_hip", "FL_thigh", "FL_calf",
        "RR_hip", "RR_thigh", "RR_calf",
        "RL_hip", "RL_thigh", "RL_calf",
    };

    [Header("Root (source of measured travel)")]
    [Tooltip("Robot base transform. Leave empty to use this.transform.")]
    public Transform root;

    [Header("Mocap CSV")]
    [Tooltip("Path relative to Application.dataPath. Matches A1PlaybackController.")]
    public string csvRelativePath = "/Resources/a1mocap.csv";

    [Header("Playback coupling -- the ONE knob to tune")]
    [Tooltip("Mocap frames advanced per METER the base travels. Tune until feet stop " +
             "sliding: raise if feet drag backward, lower if they skate forward.")]
    public float framesPerMeter = 3000f;
    [Tooltip("Extra frames advanced per DEGREE of base yaw, so legs keep cycling during " +
             "in-place turns. Set 0 to ignore turning.")]
    public float framesPerTurnDegree = 3f;
    [Tooltip("Planar travel below this (m) per physics step is treated as zero (noise deadzone).")]
    public float minMoveEpsilon = 0.0005f;

    [Header("Drive gains (leg drives ship limp at stiffness 0)")]
    public float driveStiffness = 1000f;
    public float driveDamping = 50f;
    public float driveForceLimit = 500f;

    [Header("Debug")]
    public bool logBinding = true;

    ArticulationBody[] _joints = new ArticulationBody[12]; // parallel to JOINTS / CSV columns
    float[][] _frames;   // _frames[row][12] joint angles in DEGREES
    int _frameCount;
    float _phaseFrame;   // continuous fractional frame index
    Vector3 _lastPos;
    float _lastYaw;
    bool _ready;

    void Awake()
    {
        if (root == null) root = transform;
        BindJoints();
        LoadCsv();
        ApplyDriveGains();
        _lastPos = root.position;
        _lastYaw = root.eulerAngles.y;
        _ready = _frameCount > 0 && AllJointsBound();
        if (!_ready)
            Debug.LogError("[MocapPlayer] Not ready -- check the CSV-load and joint-binding logs above.");
    }

    void FixedUpdate()
    {
        if (!_ready) return;
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        // --- measure travel since last physics step ---
        Vector3 delta = root.position - _lastPos;
        _lastPos = root.position;
        delta.y = 0f;
        float dist = delta.magnitude;
        if (dist < minMoveEpsilon) dist = 0f;

        float yaw = root.eulerAngles.y;
        float yawDelta = Mathf.Abs(Mathf.DeltaAngle(_lastYaw, yaw));
        _lastYaw = yaw;

        // --- advance gait phase in proportion to distance travelled (+ optional turn) ---
        // NOTE: uses unsigned planar distance, so any base motion cycles the gait forward.
        // Backward/strafe are rare under the nav stack; if you want reversed gait when driving
        // backward, sign 'dist' by Vector3.Dot(delta, root.forward) after confirming the A1's
        // forward axis convention.
        float frameAdvance = dist * framesPerMeter + yawDelta * framesPerTurnDegree;
        _phaseFrame += frameAdvance;
        _phaseFrame %= _frameCount;
        if (_phaseFrame < 0f) _phaseFrame += _frameCount;

        // --- sample the mocap with interpolation (smooth even at very slow advance) ---
        int i0 = Mathf.FloorToInt(_phaseFrame) % _frameCount;
        int i1 = (i0 + 1) % _frameCount;
        float frac = _phaseFrame - Mathf.Floor(_phaseFrame);

        for (int j = 0; j < 12; j++)
        {
            var ab = _joints[j];
            if (ab == null) continue;
            float angleDeg = Mathf.Lerp(_frames[i0][j], _frames[i1][j], frac);
            var drive = ab.xDrive;
            drive.target = angleDeg; // xDrive.target is in DEGREES
            ab.xDrive = drive;
        }
    }

    // ---- setup helpers ------------------------------------------------------
    void BindJoints()
    {
        var all = root.GetComponentsInChildren<ArticulationBody>(true);
        for (int j = 0; j < 12; j++)
        {
            _joints[j] = FindByName(all, JOINTS[j]);
            if (logBinding)
                Debug.Log($"[MocapPlayer] col {j} {JOINTS[j]} -> {(_joints[j] ? _joints[j].name : "<missing>")}");
        }
    }

    static ArticulationBody FindByName(ArticulationBody[] all, string jointName)
    {
        string key = jointName.ToLowerInvariant();
        foreach (var ab in all)                                   // exact match first
            if (ab.name.ToLowerInvariant() == key) return ab;
        foreach (var ab in all)                                   // then substring fallback
            if (ab.name.ToLowerInvariant().Contains(key)) return ab;
        return null;
    }

    bool AllJointsBound()
    {
        for (int j = 0; j < 12; j++) if (_joints[j] == null) return false;
        return true;
    }

    void LoadCsv()
    {
        string path = Application.dataPath + csvRelativePath;
        if (!File.Exists(path))
        {
            Debug.LogError($"[MocapPlayer] CSV not found: {path}");
            _frames = new float[0][]; _frameCount = 0; return;
        }

        var rows = new List<float[]>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 14) continue; // frame, t, + 12 joints
            var deg = new float[12];
            bool ok = true;
            for (int j = 0; j < 12; j++)
            {
                if (!float.TryParse(parts[j + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float rad))
                { ok = false; break; }   // non-numeric (e.g. header) -> skip this row
                deg[j] = rad * Mathf.Rad2Deg;
            }
            if (ok) rows.Add(deg);
        }
        _frames = rows.ToArray();
        _frameCount = _frames.Length;
        if (logBinding) Debug.Log($"[MocapPlayer] loaded {_frameCount} mocap frames from {path}");
    }

    void ApplyDriveGains()
    {
        for (int j = 0; j < 12; j++)
        {
            var ab = _joints[j];
            if (ab == null) continue;
            var d = ab.xDrive;
            d.stiffness = driveStiffness;
            d.damping = driveDamping;
            d.forceLimit = driveForceLimit;
            ab.xDrive = d;
        }
    }
}
