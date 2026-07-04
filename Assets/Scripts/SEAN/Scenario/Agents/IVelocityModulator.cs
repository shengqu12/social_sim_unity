using UnityEngine;

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// Implemented by independent MonoBehaviours (e.g. PedestrianModulator) that adjust an
    /// agent's social-force velocity for one frame. See Agents.Base.ModulateVelocity().
    /// </summary>
    public interface IVelocityModulator
    {
        Vector3 Modulate(Vector3 socialForceVelocity, Base self);

        /// <summary>
        /// Optional per-frame facing override, read by Base.Move() after Modulate() has run
        /// (e.g. so a frozen Surprised pedestrian can face the robot instead of destPos). See
        /// SURPRISED_FACING_DESIGN.md. Return false and Move() uses its normal destPos/velocity
        /// goalDir logic unchanged.
        /// </summary>
        bool TryGetFacingOverride(out Vector3 facingDirection);

        /// <summary>
        /// True if Move() should skip its own goalDir/RotateAround turning logic entirely this
        /// frame, leaving transform.rotation untouched for something else (e.g.
        /// PedestrianModulator.OnAnimatorMove()) to own exclusively. See
        /// SURPRISED_TURN_DIAGNOSIS.md -- without this, a frozen Surprised pedestrian's rotation
        /// gets fought over every frame by Move()'s destPos-facing turn and OnAnimatorMove()'s
        /// robot-facing Slerp. Return false and Move() turns exactly as it does today.
        /// </summary>
        bool IsRotationSuppressed();
    }
}
