using System.Collections.Generic;
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
    ///
    /// Session 26 addendum: yaw's TARGET is now selectable (camYawMode). "chassis" is exactly the
    /// mechanism described above, unchanged. "course" (the new default) replaces the chassis-heading
    /// target with an estimated direction of travel -- see ComputeCourseYawTarget below. Position,
    /// pitch, and roll are untouched by this switch in either mode.
    /// </summary>
    public class PovCameraSmoother : MonoBehaviour
    {
        private Transform mount;
        private Transform headingSource;
        private float yawTau;
        private float fixedPitchDeg;
        // Session 17 (Step 3, real-A1 camera pose): absolute world-space Y, resolved once at rig
        // build time by AutoTrialBootstrap.ResolveCameraGroundHeight (a downward raycast against
        // the actual ground, not a blind offset) -- replaces following mount.position.y verbatim.
        private float worldHeightY;

        private float smoothedYaw;
        private bool initialized;

        // Session 26 (course-locked camera, standing spec): yaw target = direction of travel over
        // a trailing window, not chassis heading, when camYawMode == "course" (the default).
        // rigidMount does not apply in course mode (its purpose -- an unfiltered A/B snap to raw
        // chassis yaw -- is chassis-mode-specific); course mode always applies its own tau.
        private bool useCourseMode;
        private float courseWindowSec;
        private float courseYawTau;
        private float courseHoldSpeedThreshold;
        private float courseLookAheadMeters;

        private struct PosSample
        {
            public float t;
            public Vector3 pos;
        }
        private readonly List<PosSample> courseHistory = new List<PosSample>();
        private float lastValidCourseYaw;
        private bool haveValidCourseYaw;

        public void Initialize(Transform mountTransform, Transform headingTransform, CameraParams camParams, float resolvedWorldHeightY)
        {
            mount = mountTransform;
            headingSource = headingTransform;
            bool rigid = camParams.rigidMount;
            yawTau = rigid ? 0f : Mathf.Max(0f, camParams.yawSmoothTau);
            fixedPitchDeg = camParams.fixedPitchDeg;
            worldHeightY = resolvedWorldHeightY;

            useCourseMode = camParams.camYawMode == "course";
            courseWindowSec = Mathf.Max(0.05f, camParams.camCourseWindowSec);
            courseYawTau = Mathf.Max(0f, camParams.camYawTauCourse);
            courseHoldSpeedThreshold = Mathf.Max(0f, camParams.camCourseHoldSpeedThreshold);
            courseLookAheadMeters = camParams.camLookAheadMeters;
        }

        private void Update()
        {
            if (mount == null || headingSource == null)
            {
                return;
            }

            float targetYaw = useCourseMode ? ComputeCourseYawTarget() : headingSource.eulerAngles.y;
            float tau = useCourseMode ? courseYawTau : yawTau;

            if (!initialized)
            {
                smoothedYaw = targetYaw;
                initialized = true;
            }
            else
            {
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                smoothedYaw = ExpSmoothAngle(smoothedYaw, targetYaw, tau, dt);
            }

            // Position rigid to the mount in X/Z -- no smoothing (Round 3: deleted entirely, see
            // class doc). Y is Session 17's absolute world height (real-A1 camera pose), NOT the
            // mount's own Y -- the existing first-person camera bone's height was never verified
            // against the real robot's sensor height, only ever a blind rig artifact.
            transform.position = new Vector3(mount.position.x, worldHeightY, mount.position.z);
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

        /// <summary>
        /// Session 26: course-locked camera. Yaw target is direction of travel over a trailing
        /// window (courseWindowSec), estimated from headingSource.position history -- NOT the
        /// mount's position (which carries gait-animation noise the chassis root doesn't; the
        /// mount is only ever used for the camera's own X/Z position snap, never for yaw). This is
        /// a second, independent smoothing stage ahead of the ExpSmoothAngle low-pass already
        /// applied to the result in Update() -- the window damps direction noise at the source,
        /// the low-pass damps the resulting target's own frame-to-frame jitter.
        ///
        /// Below courseHoldSpeedThreshold (m/s), displacement over the window is small enough that
        /// its direction is dominated by noise, not real travel -- the target HOLDS at the last
        /// valid course yaw instead of chasing it (an undefined direction snapping wildly is worse
        /// than a stale-but-stable one). At trial start, before any valid course has ever been
        /// computed, falls back to chassis heading rather than an arbitrary 0deg default.
        ///
        /// Per the design spec, the target is computed by aiming at an explicit look-ahead point
        /// (currentPos + courseDir * courseLookAheadMeters), not a raw bearing angle -- for a unit
        /// courseDir this is mathematically identical to atan2 of courseDir itself (atan2 is scale-
        /// invariant), but frames the computation the way a real gimbal-style tracker would: aim AT
        /// a point ahead on the course, not at an abstract angle.
        /// </summary>
        private float ComputeCourseYawTarget()
        {
            Vector3 pos = headingSource.position;
            float now = Time.time;
            courseHistory.Add(new PosSample { t = now, pos = pos });
            while (courseHistory.Count > 1 && now - courseHistory[0].t > courseWindowSec)
            {
                courseHistory.RemoveAt(0);
            }

            Vector3 oldest = courseHistory[0].pos;
            Vector3 disp = pos - oldest;
            disp.y = 0f;
            float windowDt = Mathf.Max(now - courseHistory[0].t, 1e-4f);
            float speed = disp.magnitude / windowDt;

            if (speed >= courseHoldSpeedThreshold && disp.sqrMagnitude > 1e-6f)
            {
                Vector3 courseDir = disp.normalized;
                Vector3 lookAheadPoint = pos + courseDir * courseLookAheadMeters;
                Vector3 toLookAhead = lookAheadPoint - pos;
                lastValidCourseYaw = Mathf.Atan2(toLookAhead.x, toLookAhead.z) * Mathf.Rad2Deg;
                haveValidCourseYaw = true;
            }
            else if (!haveValidCourseYaw)
            {
                lastValidCourseYaw = headingSource.eulerAngles.y;
            }
            // else: hold -- lastValidCourseYaw unchanged, below-threshold noise is not chased.

            return lastValidCourseYaw;
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
