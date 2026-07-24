using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 39 diagnostic: confirms (or refutes) the hypothesis that DirectVelocityDrive
    /// appearances never get their Animator's "Idling"/"Forward" parameters updated, because
    /// Base.cs's else-branch (which owns those SetBool/SetFloat calls) is skipped entirely when
    /// directVelocityDrive=true. Logs once per second: current Animator state name, Idling bool,
    /// Forward float, animator.speed, and real position-delta speed, so a live trial can show
    /// whether the Animator is stuck in Idle regardless of translation speed. Env-var gated
    /// (AUTOTRIAL_S39_PROBE), no-op otherwise -- same discipline as S32AnimatorSpeedScaler's own
    /// S36_ANIM_SCALER_DIAG.
    /// </summary>
    public class S39LocomotionStateProbe : MonoBehaviour
    {
        private static readonly bool Enabled =
            !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S39_PROBE"));

        private Animator animator;
        private Vector3 lastPos;
        private bool havePrev = false;
        private float lastLogTime = -999f;

        void Awake()
        {
            if (!Enabled) { enabled = false; return; }
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
        }

        void Update()
        {
            if (!Enabled || animator == null) { return; }
            Vector3 pos = transform.position;
            float speed = 0f;
            if (havePrev && Time.deltaTime > 1e-5f)
            {
                Vector3 d = pos - lastPos; d.y = 0f;
                speed = d.magnitude / Time.deltaTime;
            }
            lastPos = pos; havePrev = true;

            if (Time.time - lastLogTime >= 1.0f)
            {
                lastLogTime = Time.time;
                var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                bool idling = false; float fwd = 0f;
                try { idling = animator.GetBool("Idling"); } catch (System.Exception) { }
                try { fwd = animator.GetFloat("Forward"); } catch (System.Exception) { }
                Debug.Log("[S39Probe] t=" + Time.time.ToString("F2")
                    + " stateHash=" + stateInfo.shortNameHash
                    + " isName_Idle=" + stateInfo.IsName("Idle")
                    + " isName_Locomotion=" + stateInfo.IsName("Locomotion")
                    + " Idling=" + idling + " Forward=" + fwd
                    + " animatorSpeed=" + animator.speed
                    + " posDeltaSpeed=" + speed.ToString("F3"));
            }
        }
    }
}
