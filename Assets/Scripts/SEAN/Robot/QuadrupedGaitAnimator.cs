using UnityEngine;

/// <summary>
/// Procedural trot-gait animator for the Unitree A1 (or any 12-DOF quadruped) in SEAN.
///
/// PURELY VISUAL: this script does NOT move the robot. The base is moved by cmd_vel via
/// VelocityController; this script only articulates the 12 leg joints so the dog appears
/// to walk instead of sliding. Step frequency and amplitude are driven by the robot's
/// measured planar velocity (position-delta on the root — the same technique the
/// curious-pedestrian modulator uses, so it works regardless of Rigidbody/ArticulationBody).
///
/// Actuation path (DEFAULT): ArticulationBody.xDrive.target, because the A1 is URDF-imported
/// as an ArticulationBody chain. If your legs turn out to be plain visual Transforms instead,
/// swap the body of DriveJoint() (see the comment there) — that is the ONLY method that changes.
///
/// Non-invasive: attach this as a component on the A1 root. It modifies no shared script.
/// </summary>
[DefaultExecutionOrder(100)] // run after base motion so the root position-delta includes this step's move
public class QuadrupedGaitAnimator : MonoBehaviour
{
    public enum Leg { FR = 0, FL = 1, RR = 2, RL = 3 }

    [System.Serializable]
    public class LegJoints
    {
        public string label = "FR";
        public ArticulationBody hip;   // abduction/adduction (roll)  -> ~constant for straight walk
        public ArticulationBody thigh; // hip flexion (pitch)         -> main fore/aft swing
        public ArticulationBody calf;  // knee                        -> lift during swing
    }

    [Header("Root (source of measured velocity)")]
    [Tooltip("The robot base transform. Leave empty to use this.transform.")]
    public Transform root;

    [Header("Leg joints (assign manually, or leave empty and use Auto-Bind)")]
    public LegJoints FR = new LegJoints { label = "FR" };
    public LegJoints FL = new LegJoints { label = "FL" };
    public LegJoints RR = new LegJoints { label = "RR" };
    public LegJoints RL = new LegJoints { label = "RL" };

    [Header("Auto-Bind by name (fills only empty slots in Awake)")]
    public bool autoBindByName = true;
    [Tooltip("Substrings expected inside joint names, e.g. FR_hip_joint / FR_thigh_joint / FR_calf_joint.")]
    public string hipToken = "hip";
    public string thighToken = "thigh";
    public string calfToken = "calf";

    [Header("Stand pose (degrees) -- CALIBRATE to your prefab first")]
    [Tooltip("Set speed to ~0 and tune these until the dog stands naturally. This is the neutral the gait rides on.")]
    public float hipStand = 0f;
    public float thighStand = 45f;
    public float calfStand = -75f;

    [Header("Gait shape")]
    [Tooltip("Fore/aft thigh swing amplitude at full speed (deg).")]
    public float thighSwingDeg = 20f;
    [Tooltip("Knee flex amplitude during swing (deg). FLIP SIGN if the knee bends the wrong way.")]
    public float calfFlexDeg = 28f;
    [Tooltip("Optional hip abduction to lean into turns (deg). Set 0 to disable turn cue.")]
    public float turnHipDeg = 8f;

    [Header("Gait timing")]
    [Tooltip("Step frequency near zero speed (Hz).")]
    public float minStepHz = 0.8f;
    [Tooltip("Step frequency at reference speed (Hz).")]
    public float maxStepHz = 2.2f;
    [Tooltip("Speed (m/s) that maps to full amplitude and max step frequency. ~MAX_VEL of the dog.")]
    public float refSpeed = 0.6f;
    [Tooltip("Below this speed (m/s) the dog blends to a still stand.")]
    public float moveThreshold = 0.03f;
    [Tooltip("How fast amplitude follows speed changes (higher = snappier).")]
    public float ampSmoothing = 6f;

    [Header("Drive gains (optional -- enable ONLY if legs are limp / don't track)")]
    public bool applyDriveGains = false;
    public float driveStiffness = 10000f;
    public float driveDamping = 100f;
    public float driveForceLimit = 1000f;

    [Header("Debug")]
    public bool logBinding = true;

    // Trot: diagonal pairs move together. FR+RL in phase (0.0), FL+RR in phase (0.5).
    static readonly float[] PhaseOffset = { 0.0f, 0.5f, 0.5f, 0.0f }; // order: FR, FL, RR, RL

    float _phase;      // global gait phase [0,1)
    float _amp;        // smoothed 0..1 gait amplitude
    Vector3 _lastPos;
    float _lastYaw;
    LegJoints[] _legs;

    void Awake()
    {
        if (root == null) root = transform;
        _legs = new[] { FR, FL, RR, RL };

        if (autoBindByName) AutoBind();
        if (applyDriveGains)
            foreach (var leg in _legs) { SetGains(leg.hip); SetGains(leg.thigh); SetGains(leg.calf); }

        _lastPos = root.position;
        _lastYaw = root.eulerAngles.y;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        if (dt <= 0f) return;

        // --- measure planar velocity from root position-delta (AB/Rigidbody-agnostic) ---
        Vector3 delta = root.position - _lastPos;
        _lastPos = root.position;
        delta.y = 0f;
        float speed = delta.magnitude / dt;

        float yaw = root.eulerAngles.y;
        float yawRate = Mathf.DeltaAngle(_lastYaw, yaw) / dt; // deg/s
        _lastYaw = yaw;

        // --- map speed -> amplitude & step frequency ---
        float targetAmp = Mathf.Clamp01(speed / Mathf.Max(0.0001f, refSpeed));
        if (speed < moveThreshold) targetAmp = 0f;
        _amp = Mathf.Lerp(_amp, targetAmp, 1f - Mathf.Exp(-ampSmoothing * dt)); // framerate-independent smoothing

        float stepHz = Mathf.Lerp(minStepHz, maxStepHz, _amp);
        _phase += stepHz * dt;
        _phase -= Mathf.Floor(_phase); // wrap to [0,1)

        // optional turn cue: lean hips based on yaw rate
        float turnLean = Mathf.Clamp(yawRate / 60f, -1f, 1f) * turnHipDeg;

        // --- drive each leg ---
        for (int i = 0; i < 4; i++)
        {
            float p = _phase + PhaseOffset[i];
            p -= Mathf.Floor(p);

            float thighWave = Mathf.Sin(2f * Mathf.PI * p);                       // fore/aft swing
            float swingLift = Mathf.Max(0f, Mathf.Sin(2f * Mathf.PI * (p - 0.5f))); // positive bump during swing half

            float hipDeg   = hipStand + SideSign((Leg)i) * turnLean;
            float thighDeg = thighStand + _amp * thighSwingDeg * thighWave;
            float calfDeg  = calfStand  + _amp * calfFlexDeg  * swingLift;

            DriveJoint(_legs[i].hip,   hipDeg);
            DriveJoint(_legs[i].thigh, thighDeg);
            DriveJoint(_legs[i].calf,  calfDeg);
        }
    }

    // +1 for right legs, -1 for left legs (sets turn-lean direction)
    static float SideSign(Leg leg) => (leg == Leg.FR || leg == Leg.RR) ? 1f : -1f;

    /// <summary>
    /// Sends a target angle (DEGREES) to one joint.
    /// DEFAULT = ArticulationBody drive. For plain visual Transforms, replace the body with:
    ///     joint.transform.localRotation = restLocalRot[joint] * Quaternion.AngleAxis(angleDeg, localAxis[joint]);
    /// (cache restLocalRot in Awake and expose a per-joint localAxis). That's the only change needed.
    /// </summary>
    void DriveJoint(ArticulationBody joint, float angleDeg)
    {
        if (joint == null) return;
        var drive = joint.xDrive;
        drive.target = angleDeg; // xDrive.target is in DEGREES for revolute joints (jointPosition read-back is in radians!)
        joint.xDrive = drive;
    }

    void SetGains(ArticulationBody ab)
    {
        if (ab == null) return;
        var d = ab.xDrive;
        d.stiffness = driveStiffness;
        d.damping = driveDamping;
        d.forceLimit = driveForceLimit;
        ab.xDrive = d;
    }

    // ---- name-based auto binding -------------------------------------------
    void AutoBind()
    {
        var all = root.GetComponentsInChildren<ArticulationBody>(true);
        BindLeg(FR, all, "FR");
        BindLeg(FL, all, "FL");
        BindLeg(RR, all, "RR");
        BindLeg(RL, all, "RL");
    }

    void BindLeg(LegJoints leg, ArticulationBody[] all, string legToken)
    {
        if (leg.hip == null)   leg.hip   = Find(all, legToken, hipToken);
        if (leg.thigh == null) leg.thigh = Find(all, legToken, thighToken);
        if (leg.calf == null)  leg.calf  = Find(all, legToken, calfToken);
        if (logBinding)
            Debug.Log($"[Gait] {legToken}: hip={NameOf(leg.hip)}  thigh={NameOf(leg.thigh)}  calf={NameOf(leg.calf)}");
    }

    static ArticulationBody Find(ArticulationBody[] all, string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        foreach (var ab in all)
        {
            string n = ab.name.ToLowerInvariant();
            if (n.Contains(a) && n.Contains(b)) return ab;
        }
        return null;
    }

    static string NameOf(ArticulationBody ab) => ab ? ab.name : "<missing>";
}
