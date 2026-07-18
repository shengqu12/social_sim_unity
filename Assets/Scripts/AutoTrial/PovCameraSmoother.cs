using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Round 3 fix. Session 10's "horizon-locked, zero roll" claim was false in the render path:
    /// human frame-extraction forensics on pov_near_00 found 66-100% of sampled frames statistically
    /// uniform gray, alternating with frames where the ground fills the frame and the scene is a
    /// tilted sliver at the top -- the camera was staring at the floor with visible roll, oscillating
    /// with gait. Root cause: Session 10's smoother read `mount.eulerAngles.x` as "pitch" and forced
    /// its OWN roll to 0, but never accounted for the mount's actual roll. Unity's `eulerAngles` is
    /// not an independent-axis decomposition -- once the mount (`robot.camera_first`, an animated
    /// first-person camera bone) carries any real roll component (gait sway), decomposing its
    /// rotation into eulerAngles.x/.y aliases that roll into wildly wrong pitch/yaw values, including
    /// flips past the file's own [-90,90]->[90,180] boundary. That's the forensics signature exactly:
    /// gray (camera pitched to stare at the near-uniform floor) alternating with a tilted sliver
    /// (partial decomposition artifacts), not the intended small-angle damping.
    ///
    /// Fix: stop decomposing the mount's rotation at all. Position is rigidly snapped to the mount
    /// every frame (the shake was rotational, not translational -- position lag only buys mesh-burial
    /// risk, so smoothing it bought nothing and is deleted). Rotation is built directly in
    /// world-frame terms with zero dependency on the mount's own orientation: roll is always exactly
    /// 0, pitch is always exactly fixedPitchDeg (a constant configurable downtilt, never derived from
    /// any transform), and yaw is a low-pass filter of a separate HEADING SOURCE's own world yaw
    /// (headingSource -- the robot chassis transform, e.g. Scenario.Robot.transform, NOT the camera
    /// mount). Chassis yaw is already independently confirmed reliable: it backs TrialController's
    /// robot_yaw_deg column, which Session 10's own D2 diagnosis found correlates -0.986 with
    /// commanded steering, and a ground-vehicle chassis carries no roll/pitch to alias into its own
    /// eulerAngles.y the way the camera bone did.
    ///
    /// Runs its update in Update(), not the more conventional LateUpdate(): TrialController's
    /// capture coroutine uses `yield return null`, which Unity resumes after all Update() calls but
    /// before LateUpdate() this same frame -- so computing the pose in Update() guarantees
    /// CaptureFrame's Camera.Render() call (later this same frame) sees this frame's value, not a
    /// one-frame-stale one from LateUpdate.
    /// </summary>
    public class PovCameraSmoother : MonoBehaviour
    {
        private Transform mount;
        private Transform headingSource;
        private float yawTau;
        private float fixedPitchDeg;

        private float smoothedYaw;
        private bool initialized;

        public void Initialize(Transform mountTransform, Transform headingTransform, CameraParams camParams)
        {
            mount = mountTransform;
            headingSource = headingTransform;
            bool rigid = camParams.rigidMount;
            yawTau = rigid ? 0f : Mathf.Max(0f, camParams.yawSmoothTau);
            fixedPitchDeg = camParams.fixedPitchDeg;
        }

        private void Update()
        {
            if (mount == null || headingSource == null)
            {
                return;
            }

            float headingYaw = headingSource.eulerAngles.y;

            if (!initialized)
            {
                smoothedYaw = headingYaw;
                initialized = true;
            }
            else
            {
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                smoothedYaw = ExpSmoothAngle(smoothedYaw, headingYaw, yawTau, dt);
            }

            // Position rigid to the mount -- no smoothing (Round 3: deleted entirely, see class doc).
            transform.position = mount.position;
            // World-frame horizon lock: roll and pitch are constants, independent of the mount's own
            // (previously corrupting) orientation. Only yaw is derived from a transform, and it's the
            // robot chassis's, not the camera mount's.
            //
            // fixedPitchDeg follows the conventional camera-pitch sign (positive = tilt up, negative
            // = tilt down, i.e. "downtilt" is negative) -- Unity's own raw eulerAngles.x convention is
            // the opposite (positive X tilts the view DOWN; verified empirically this session, see
            // REPORT.md Round 3 Step 1), hence the negation here so the config value's sign means what
            // its name says.
            transform.rotation = Quaternion.Euler(-fixedPitchDeg, smoothedYaw, 0f);
        }

        private static float ExpSmoothAngle(float current, float target, float tau, float dt)
        {
            if (tau <= 0f)
            {
                return target;
            }
            float alpha = 1f - Mathf.Exp(-dt / tau);
            float delta = Mathf.DeltaAngle(current, target);
            return current + delta * alpha;
        }
    }
}
