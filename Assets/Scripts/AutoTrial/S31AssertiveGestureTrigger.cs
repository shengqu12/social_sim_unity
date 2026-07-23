using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 31 FIX 6(b): fires the "AssertiveGesture" Animator trigger (added to
    /// BaseSFControllerNormalized.controller by S31GestureAnimationFix, motion = the retargeted
    /// point_backwards.fbx clip) when the robot closes within gestureRadius -- a "back off, I'm
    /// not yielding" shooing gesture for Assertive pedestrians. Rising-edge + cooldown, same shape
    /// as PedestrianModulator.ModulateSurprised()'s own trigger logic, but implemented as an
    /// entirely separate component: ModulateAssertive() in PedestrianModulator.cs (outside this
    /// project's writable scope) only suppresses robotRepulsion today and stays untouched -- this
    /// component is added ALONGSIDE it (AutoTrialBootstrap.SpawnPedestrian, Zone A, Assertive
    /// personality only) purely to drive the new animation trigger, with no dependency on
    /// PedestrianModulator's own internals beyond reading its `personality` field to no-op
    /// gracefully if attached to a non-Assertive agent by mistake.
    /// </summary>
    public class S31AssertiveGestureTrigger : MonoBehaviour
    {
        public float gestureRadius = 5.0f;
        public float cooldownDuration = 6.0f;

        private Animator animator;
        private bool wasInRadius = false;
        private float cooldownUntil = -1f;

        void Awake()
        {
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
        }

        void Update()
        {
            if (animator == null) return;
            if (SEAN.instance == null) return;

            Scenario.Robot robot;
            try
            {
                robot = SEAN.instance.robot;
            }
            catch (System.Exception)
            {
                return;
            }

            Vector3 toRobot = robot.position - transform.position;
            toRobot.y = 0f;
            float distance = toRobot.magnitude;
            bool inRadius = distance <= gestureRadius;
            float now = Time.time;

            if (inRadius && !wasInRadius && now >= cooldownUntil)
            {
                animator.SetTrigger("AssertiveGesture");
                cooldownUntil = now + cooldownDuration;
            }
            wasInRadius = inRadius;
        }
    }
}
