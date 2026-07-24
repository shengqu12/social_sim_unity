using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 40 STEP 2: Scared's existing radial flee (PedestrianModulator.ModulateScared(),
    /// untouched, outside writable scope) computes fleeDir as directly AWAY from the robot --
    /// a retreat along the approach line, not a lateral step out of the robot's own forward
    /// path. Diagnosed as the likely reason Scared's ~1-in-5 min_dist floor-breach rate was
    /// UNCHANGED across both of Session 38's robot-side backstop-parameter iterations: the
    /// robot-side lever isn't what matters for this failure mode, the pedestrian's own flee
    /// SHAPE is. This component adds a lateral bias -- perpendicular to the ROBOT's own current
    /// heading (not the instantaneous robot-pedestrian axis), so the push is "out of the
    /// robot's way," not just "backward" -- proportional to closeness once inside scaredRadius,
    /// ADDITIVE on top of PedestrianModulator's own existing radial flee. Same "safety
    /// topping-up from outside the modulator" discipline as S32's assertive lateral-evasion
    /// backstop and S38's robot-side one -- never fights the existing mechanism, only nudges it.
    /// </summary>
    public class S40ScaredLateralEvasion : MonoBehaviour
    {
        public float scaredRadiusMeters = 3.0f;
        public float lateralStepSpeedMps = 1.0f;

        // Decided once per approach (first frame inside the radius), not re-evaluated every
        // frame -- avoids a jittery side-to-side correction if the pedestrian sits close to the
        // robot's own heading line.
        private int lateralSign = 0;

        void LateUpdate()
        {
            if (SEAN.instance == null) { return; }
            Transform robot;
            try { robot = SEAN.instance.robot.transform; }
            catch (System.Exception) { return; }
            if (robot == null) { return; }

            Vector3 toSelf = transform.position - robot.position;
            toSelf.y = 0f;
            float dist = toSelf.magnitude;
            if (dist >= scaredRadiusMeters)
            {
                lateralSign = 0;
                return;
            }
            if (dist < 1e-4f) { return; }

            Vector3 robotFwd = robot.forward;
            robotFwd.y = 0f;
            if (robotFwd.sqrMagnitude < 1e-6f) { return; }
            robotFwd.Normalize();
            Vector3 perp = Vector3.Cross(Vector3.up, robotFwd).normalized;

            if (lateralSign == 0)
            {
                // Pick whichever side the pedestrian is already (mostly) on, so the nudge
                // reinforces the natural flee direction rather than crossing in front of the
                // robot to get to the "wrong" side.
                lateralSign = Vector3.Dot(toSelf, perp) >= 0f ? 1 : -1;
            }

            float closeness = 1f - Mathf.Clamp01(dist / scaredRadiusMeters);
            transform.position += perp * lateralSign * lateralStepSpeedMps * closeness * Time.deltaTime;
        }
    }
}
