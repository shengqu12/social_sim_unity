using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 34 FIX 1: converts PedestrianModulator's existing binary robot-repulsion
    /// suppression (previously Assertive-only, permanently 0 -- see ModulateAssertive()) into a
    /// genuine DISTANCE GATE, applied to every non-Assertive personality. Reframe this session:
    /// the complaint was never really about the robot detouring -- Session 33's control trial
    /// already showed robot-side "detour-onset distance" was pure TEB path noise (fired at 37.7m
    /// with the pedestrian parked 40m away), and the costmap audit found the clearance chain
    /// already tight. Assertive (min_dist ~0.77-0.79m across S32/S33) is the only personality with
    /// zero robot-repulsion response; every other personality's larger min_dist is created by the
    /// PEDESTRIAN stepping aside, not the robot. This component makes that stepping-aside distance
    /// an explicit, tunable gate instead of an always-on random 0.5-1.0 value
    /// (Parameters.ROBOT_REPULSION_DAMPENING_MIN/MAX, SFAgent.Start()).
    ///
    /// Mechanism (confirmed by reading SFAgent.cs -- forbidden to edit, fine to read):
    /// SFAgent.RobotRepulsion is a plain public property (Assets/IVI/Scripts/SFAgent.cs:29-37),
    /// not part of IVelocityModulator, so no GetComponent&lt;IVelocityModulator&gt;() singular-cache
    /// conflict (see S32AssertiveStraightLineGuardian's own notes on that constraint for a
    /// different problem). SFAgent.CalculateAgentForce() multiplies BOTH the robot-repulsion force
    /// term AND the tangential side-step term by `dampenFactor`, which equals `robotRepulsion`
    /// whenever the robot's own rigidbody speed exceeds 0.1 m/s (else 1.0, full force, regardless
    /// of this component -- an existing SFAgent behavior, not something this component controls).
    /// Setting RobotRepulsion=0 while the robot is farther than reactDistanceMeters, and to a
    /// personality-scaled nonzero value once inside it, reproduces "zero robot-response beyond
    /// the gate, full (or stronger/milder) response inside it" without touching SFAgent.cs or
    /// PedestrianModulator.cs.
    ///
    /// NOT attached to Assertive pedestrians -- ModulateAssertive() already permanently zeroes
    /// RobotRepulsion once (assertiveInitialized-guarded) and that stays exactly as-is, unchanged,
    /// per this session's own brief ("assertive = never").
    /// </summary>
    public class S34PedestrianReactDistGate : MonoBehaviour
    {
        // Zero robot-response beyond this distance; full/scaled response inside it. Swept this
        // session at {1.5, 2.0, 2.5}; see REPORT.md Session 34 FIX 1 for the landed value.
        public float reactDistanceMeters = 2.0f;

        // Set once at wiring time (AutoTrialBootstrap.SpawnPedestrian) from the pedestrian's own
        // PersonalityType. Drives the personality-scaled "inside the gate" repulsion value below
        // -- kept as an internal design decision (not a separate CLI knob per personality) since
        // only the shared gate distance is what this session actually sweeps.
        public Scenario.Agents.PedestrianModulator.PersonalityType personality =
            Scenario.Agents.PedestrianModulator.PersonalityType.Indifferent;

        private IVI.SFAgent sfAgent;

        // Applied to SFAgent.RobotRepulsion once inside the gate. Natural compiled-in range for a
        // pedestrian that never had this component at all is a random 0.5-1.0
        // (Parameters.ROBOT_REPULSION_DAMPENING_MIN/MAX) -- values here are chosen relative to
        // that band: indifferent=mild (near the low/natural end), surprised=medium (its own
        // freeze/flee logic is separately radius-gated -- see PedestrianModulator.ModulateSurprised
        // -- this only governs its pre-freeze approach), scared=strongest (pushed above the
        // natural max for a visibly sharper reaction once inside the gate; scared ALSO has its own
        // separate, already-working scaredRadius flee logic -- see PedestrianModulator.
        // ModulateScared -- this gate is additive/compounding on top of that, not a replacement).
        private float InsideGateRepulsionFor(Scenario.Agents.PedestrianModulator.PersonalityType p)
        {
            switch (p)
            {
                case Scenario.Agents.PedestrianModulator.PersonalityType.Scared:
                    return 1.3f;
                case Scenario.Agents.PedestrianModulator.PersonalityType.Surprised:
                    return 0.85f;
                case Scenario.Agents.PedestrianModulator.PersonalityType.Indifferent:
                default:
                    return 0.6f;
            }
        }

        void Awake()
        {
            sfAgent = GetComponent<IVI.SFAgent>();
        }

        void Update()
        {
            if (sfAgent == null) { return; }
            Vector3? robotPos = TryGetRobotPosition();
            if (!robotPos.HasValue) { return; }

            Vector3 toRobot = robotPos.Value - transform.position;
            toRobot.y = 0f;
            float dist = toRobot.magnitude;

            sfAgent.RobotRepulsion = dist > reactDistanceMeters ? 0f : InsideGateRepulsionFor(personality);
        }

        private Vector3? TryGetRobotPosition()
        {
            if (SEAN.instance == null) { return null; }
            try
            {
                return SEAN.instance.robot.position;
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
