using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 46 (1.2): separate WHEN the flee starts from HOW HARD it pushes.
    ///
    /// PedestrianModulator.ModulateScared computes
    ///
    ///     fleeDir   = (self - robot).normalized                      // radius-independent
    ///     closeness = 1 - clamp01(distanceToRobot / scaredRadius)    // radius-DEPENDENT
    ///     result   += fleeDir * scaredStrength * closeness
    ///
    /// so scaredRadius sets both the trigger distance and the force profile. Lowering it from 7.0
    /// to 3.5 in Session 45 fixed the trigger (the encounter finally happened) and broke the shape:
    /// at a given physical distance the flee force is 4x weaker (at 3.0 m, closeness 0.571 vs
    /// 0.143), so the resultant is dominated by the pedestrian's existing walking velocity and
    /// barely bends away -- which is why the pedestrian appeared to turn TOWARD the robot.
    ///
    /// This is not kinematic truncation. Base.ANGULAR_SPEED is 120 deg/s, which turns 90 degrees in
    /// 0.75 s, against roughly 2 s available from 3.5 m at ~1.8 m/s closing.
    ///
    /// The fix keeps the 7.0 force profile -- whose flee shape was accepted on review -- and gates
    /// only its onset: scaredRadius is held at 0 (the `distanceToRobot < scaredRadius` test then
    /// never passes, so no flee force is added at all) until the robot is inside triggerDistance,
    /// at which point it is set to profileRadius. At a 3.5 m trigger against a 7.0 profile the
    /// first frame of flee already carries closeness 0.5, and it strengthens as the robot closes.
    ///
    /// Written as a separate component that drives the modulator's public field, deliberately:
    /// PedestrianModulator is upstream and shared, and this needs no edit to it.
    /// </summary>
    [DefaultExecutionOrder(450)]
    public class S46ScaredTriggerGate : MonoBehaviour
    {
        public float triggerDistanceMeters = 3.5f;
        public float profileRadiusMeters = 7.0f;

        private Scenario.Agents.PedestrianModulator modulator;
        private Scenario.Robot robot;
        private bool armed;
        private bool reported;

        private void Start()
        {
            modulator = GetComponent<Scenario.Agents.PedestrianModulator>();
            robot = SEAN.instance != null ? SEAN.instance.robot : null;
            if (modulator == null || robot == null)
            {
                Debug.LogWarning("[S46ScaredGate] modulator or robot missing -- gate inactive, "
                    + "scaredRadius left as configured.");
                enabled = false;
                return;
            }
            // Suppress the flee entirely until the trigger fires.
            modulator.scaredRadius = 0f;
            Debug.Log("[S46ScaredGate] armed: flee suppressed until " + triggerDistanceMeters.ToString("F2")
                + " m, then profile radius " + profileRadiusMeters.ToString("F2") + " m");
        }

        private void Update()
        {
            if (armed || modulator == null || robot == null) { return; }
            Vector3 d = transform.position - robot.position;
            d.y = 0f;
            if (d.magnitude <= triggerDistanceMeters)
            {
                armed = true;
                modulator.scaredRadius = profileRadiusMeters;
                if (!reported)
                {
                    reported = true;
                    float closeness = 1f - Mathf.Clamp01(d.magnitude / profileRadiusMeters);
                    Debug.Log("[S46ScaredGate] FIRED at " + d.magnitude.ToString("F2")
                        + " m -- scaredRadius 0 -> " + profileRadiusMeters.ToString("F2")
                        + ", initial closeness " + closeness.ToString("F3"));
                }
            }
        }
    }
}
