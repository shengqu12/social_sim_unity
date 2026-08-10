using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 68. The one hook PedestrianModulator needs so a personality's behaviour can be
    /// replaced from an AutoTrial component without that replacement being written inside
    /// PedestrianModulator itself.
    ///
    /// Deliberately shaped as "answer, or decline to answer" rather than "return a velocity": an
    /// implementation that is not currently driving must be indistinguishable from no implementation
    /// at all, so the personality's own code path stays reachable and unmodified (S68 §4 -- the
    /// existing Curious follow logic is not deleted, only not entered).
    ///
    /// The returned vector is an ABSOLUTE velocity, in the same sense as PedestrianModulator's
    /// Session 47 solution (e): its magnitude must not be a function of the magnitude passed in, or
    /// it re-enters the compounding loop that (e) exists to break. Vector3.zero is a legitimate
    /// answer and is how a stopped state is expressed.
    /// </summary>
    public interface IPedestrianVelocityOverride
    {
        /// <summary>True while this override is driving the agent's destination via InitDest(), so
        /// PedestrianSpawner's random-walk retarget leaves it alone (same contract as
        /// PedestrianModulator.IsControllingDestination).</summary>
        bool IsControllingDestination { get; }

        /// <summary>Return false to leave the personality's own behaviour in charge this frame.</summary>
        bool TryModulate(Vector3 socialForceVelocity, Scenario.Agents.Base self,
                         Scenario.Robot robot, out Vector3 velocity);
    }
}
