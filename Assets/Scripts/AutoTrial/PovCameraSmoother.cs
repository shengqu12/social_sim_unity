using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 10 (D2 treatment b, "soft-mounted POV camera"). Overrides this GameObject's world
    /// position/rotation every frame with a low-pass-filtered version of a tracked mount
    /// transform's motion, instead of relying on rigid parent-child inheritance. Position follows
    /// the mount with light exponential smoothing; rotation is horizon-locked (roll always zero
    /// regardless of the mount's roll), pitch is damped, yaw is low-pass filtered. rigidMount=true
    /// (all taus effectively 0) makes this snap exactly to the mount's raw pose every frame --
    /// the rigid-mount comparison case, sharing this exact code path rather than a second one.
    ///
    /// Runs its update in Update(), not the more conventional LateUpdate(): TrialController's
    /// capture coroutine uses `yield return null`, which Unity resumes after all Update() calls
    /// but before LateUpdate() this same frame -- so computing the smoothed pose in Update()
    /// guarantees CaptureFrame's Camera.Render() call (later this same frame) sees this frame's
    /// value, not a one-frame-stale one from LateUpdate.
    /// </summary>
    public class PovCameraSmoother : MonoBehaviour
    {
        private Transform mount;
        private float posTau;
        private float yawTau;
        private float pitchTau;

        private Vector3 smoothedPos;
        private float smoothedYaw;
        private float smoothedPitch;
        private bool initialized;

        public void Initialize(Transform mountTransform, CameraParams camParams)
        {
            mount = mountTransform;
            bool rigid = camParams.rigidMount;
            posTau = rigid ? 0f : Mathf.Max(0f, camParams.posSmoothTau);
            yawTau = rigid ? 0f : Mathf.Max(0f, camParams.yawSmoothTau);
            pitchTau = rigid ? 0f : Mathf.Max(0f, camParams.pitchSmoothTau);
        }

        private void Update()
        {
            if (mount == null)
            {
                return;
            }

            Vector3 mountPos = mount.position;
            Vector3 mountEuler = mount.eulerAngles;
            float mountYaw = mountEuler.y;
            float mountPitch = NormalizeAngle(mountEuler.x);

            if (!initialized)
            {
                smoothedPos = mountPos;
                smoothedYaw = mountYaw;
                smoothedPitch = mountPitch;
                initialized = true;
            }
            else
            {
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                smoothedPos = ExpSmoothVector(smoothedPos, mountPos, posTau, dt);
                smoothedYaw = ExpSmoothAngle(smoothedYaw, mountYaw, yawTau, dt);
                smoothedPitch = ExpSmoothAngle(smoothedPitch, mountPitch, pitchTau, dt);
            }

            transform.position = smoothedPos;
            // Horizon-locked: roll is always exactly zero, independent of the mount's own roll.
            transform.rotation = Quaternion.Euler(smoothedPitch, smoothedYaw, 0f);
        }

        private static float NormalizeAngle(float deg)
        {
            deg %= 360f;
            if (deg > 180f)
            {
                deg -= 360f;
            }
            return deg;
        }

        private static Vector3 ExpSmoothVector(Vector3 current, Vector3 target, float tau, float dt)
        {
            if (tau <= 0f)
            {
                return target;
            }
            float alpha = 1f - Mathf.Exp(-dt / tau);
            return Vector3.Lerp(current, target, alpha);
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
