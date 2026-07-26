using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 41 TASK 1 (read-only instrumentation): decomposes the "起手慢" / slow-to-start
    /// complaint into the two independent quantities the ticket asks for, by logging three
    /// timestamps per reaction:
    ///
    ///   (1) T_ENTER  -- the sim time the robot first crosses the reaction trigger radius.
    ///   (2) T_SIGNAL -- the sim time the Animator trigger parameter is actually set
    ///                   (S31AssertiveGestureTrigger / PedestrianModulator.ModulateSurprised).
    ///   (3) T_STATE  -- the sim time the Animator first reports the reaction state as current.
    ///
    /// LOGIC latency = (2)-(1); ANIMATION latency = (3)-(2); total "起手慢" = (3)-(1).
    ///
    /// Deliberately does NOT read the trigger-firing component's internals -- it independently
    /// recomputes the radius crossing from the same robot/self positions, and detects the trigger
    /// set by polling Animator.GetBool/IsInTransition rather than by hooking SetTrigger, so it
    /// stays a pure observer and cannot alter the timing it is measuring.
    ///
    /// Env-var gated (AUTOTRIAL_S41_LATENCY_PROBE) and completely inert otherwise -- same pattern
    /// as S39LocomotionStateProbe.
    /// </summary>
    public class S41ReactionLatencyProbe : MonoBehaviour
    {
        // Set by AutoTrialBootstrap to the radius the trigger component itself uses, so
        // T_ENTER is computed against the real gate rather than a guessed constant.
        public float triggerRadius = 5.0f;
        public string reactionStateName = "AssertiveGesture";
        public string triggerParamName = "AssertiveGesture";

        private Animator animator;
        private bool enabledByEnv;

        private float tEnter = -1f;
        private float tSignal = -1f;
        private float tState = -1f;
        private bool wasInRadius;
        private bool reported;

        // Frame counters, so the ticket's "反应触发到实际调速的帧数" is answered in frames as
        // well as seconds (Time.deltaTime varies in batchmode).
        private int fEnter = -1;
        private int fSignal = -1;
        private int fState = -1;

        void Awake()
        {
            enabledByEnv = !string.IsNullOrEmpty(
                System.Environment.GetEnvironmentVariable("AUTOTRIAL_S41_LATENCY_PROBE"));
            if (!enabledByEnv) { return; }
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
        }

        // Deferred to the first Update() rather than logged in Awake(): AutoTrialBootstrap sets
        // triggerRadius/reactionStateName immediately AFTER AddComponent (which has already run
        // Awake), so an Awake-time log reports the field defaults, not the configured values.
        private void LogArmedOnce()
        {
            Debug.Log("[S41Latency] probe armed radius=" + triggerRadius
                + " state=" + reactionStateName + " param=" + triggerParamName
                + " animator=" + (animator != null ? animator.gameObject.name : "NULL")
                + " controller=" + (animator != null && animator.runtimeAnimatorController != null
                    ? animator.runtimeAnimatorController.name : "NULL"));
        }

        private bool armedLogged;

        void Update()
        {
            if (!enabledByEnv || animator == null || reported) { return; }
            if (!armedLogged) { armedLogged = true; LogArmedOnce(); }
            if (SEAN.instance == null) { return; }

            Scenario.Robot robot;
            try { robot = SEAN.instance.robot; }
            catch (System.Exception) { return; }

            Vector3 toRobot = robot.position - transform.position;
            toRobot.y = 0f;
            float dist = toRobot.magnitude;

            // (1) radius crossing -- rising edge only
            bool inRadius = dist <= triggerRadius;
            if (inRadius && !wasInRadius && tEnter < 0f)
            {
                tEnter = Time.time;
                fEnter = Time.frameCount;
                Debug.Log(string.Format("[S41Latency] T_ENTER t={0:F4} frame={1} dist={2:F3}",
                    tEnter, fEnter, dist));
            }
            wasInRadius = inRadius;

            // (2) trigger consumed: a set Trigger is cleared the moment the state machine
            // consumes it, so the observable signal is "an Any State transition into the
            // reaction state has begun". IsInTransition + next-state check catches the frame
            // the crossfade starts, which is exactly when the trigger took effect.
            if (tSignal < 0f)
            {
                var next = animator.GetNextAnimatorStateInfo(0);
                if (animator.IsInTransition(0) && next.IsName(reactionStateName))
                {
                    tSignal = Time.time;
                    fSignal = Time.frameCount;
                    Debug.Log(string.Format("[S41Latency] T_SIGNAL t={0:F4} frame={1} dist={2:F3} animatorSpeed={3:F3} (crossfade into {4} began)",
                        tSignal, fSignal, dist, animator.speed, reactionStateName));
                }
            }

            // (3) reaction state actually current
            if (tState < 0f)
            {
                var cur = animator.GetCurrentAnimatorStateInfo(0);
                if (cur.IsName(reactionStateName))
                {
                    tState = Time.time;
                    fState = Time.frameCount;
                    // cur.length is the state's duration AFTER animator.speed scaling, so an
                    // authored 3.6s clip reporting 12.0s here is direct evidence of a 0.3x
                    // animator.speed -- the exact quantity the "播放慢" complaint is about.
                    Debug.Log(string.Format("[S41Latency] T_STATE t={0:F4} frame={1} dist={2:F3} animatorSpeed={3:F3} stateSpeed={4:F3} effectiveClipLen={5:F3}",
                        tState, fState, dist, animator.speed, cur.speed, cur.length));
                }
            }

            if (tEnter >= 0f && tSignal >= 0f && tState >= 0f)
            {
                reported = true;
                Debug.Log(string.Format(
                    "[S41Latency] RESULT logic_latency_s={0:F4} ({1} frames) anim_latency_s={2:F4} ({3} frames) " +
                    "total_startup_s={4:F4} ({5} frames)",
                    tSignal - tEnter, fSignal - fEnter,
                    tState - tSignal, fState - fSignal,
                    tState - tEnter, fState - fEnter));
            }
        }

        void OnDestroy()
        {
            if (!enabledByEnv || reported) { return; }
            Debug.Log(string.Format(
                "[S41Latency] INCOMPLETE at teardown: T_ENTER={0:F4} T_SIGNAL={1:F4} T_STATE={2:F4} " +
                "(-1 = never observed)", tEnter, tSignal, tState));
        }
    }
}
