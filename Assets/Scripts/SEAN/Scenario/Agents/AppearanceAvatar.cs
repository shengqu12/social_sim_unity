using UnityEngine;

namespace SEAN.Scenario.Agents
{
    /// <summary>
    /// Minimal appearance instantiator used by PedestrianSpawner for the "Simple" appearance
    /// type. Parallels RandomAvatar.cs's Instantiate -> set Animator -> AddComponent&lt;SFAgent&gt;
    /// skeleton, but is a separate class (RandomAvatar is left untouched, it's still used by
    /// Random.prefab) and picks uniformly at random from a single candidate array every time
    /// (no "no repeat until exhausted" bookkeeping -- not needed for the small group sizes
    /// PedestrianSpawner spawns).
    ///
    /// Full AppearanceType/AppearanceMapping table (Elderly/Child/Distracted/TBD) is out of
    /// scope this pass -- see PEDESTRIAN_SPAWNER_DESIGN.md §2.6.
    /// </summary>
    public class AppearanceAvatar : MonoBehaviour
    {
        public RuntimeAnimatorController animationController;
        public GameObject[] avatars;
        public LowLevelControl controller = LowLevelControl.SF;

        // For avatars with no usable root motion (e.g. a wheelchair looping a static
        // seated-idle clip) -- pushed onto the spawned agent's Base.DirectVelocityDrive so
        // Move() applies social-force velocity straight to the transform instead of relying on
        // Animator deltaPosition. Defaults false; unrelated to animationController above.
        public bool directVelocityDrive = false;

        public GameObject avatarObject { get; private set; }

        void Awake()
        {
            GameObject avatarPrefab = avatars[Random.Range(0, avatars.Length)];
            avatarObject = Instantiate(avatarPrefab, transform.position, transform.rotation);
            // Some character packages (e.g. White_Cane_User) put the Animator on a nested
            // child instead of the root, and may carry extra prop/animal Animators too -- use
            // the shared picker (self/children, preferring Humanoid) so this stays in sync with
            // Base.cs / PedestrianModulator.cs / AttachPropToHand.cs, which resolve the same
            // GameObject's Animator via the same call.
            Animator animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(avatarObject);
            if (animator != null)
            {
                // Leave animationController unset (null) to keep whatever
                // RuntimeAnimatorController the avatar prefab already ships with -- e.g. the
                // wheelchair avatar's own Wheelchair.controller, which must stay untouched.
                if (animationController != null)
                {
                    animator.runtimeAnimatorController = animationController;
                }
            }
            else
            {
                Debug.LogError($"[AppearanceAvatar] No Animator found anywhere under avatar prefab "
                    + $"'{avatarPrefab.name}' -- spawning unanimated so SFAgent/navigation still attaches.", this);
            }
            if (SEAN.instance)
            {
                controller = SEAN.instance.AgentController;
            }
            if (controller == LowLevelControl.SF)
            {
                avatarObject.AddComponent<IVI.SFAgent>();
            }
            else if (controller == LowLevelControl.ORCA)
            {
                avatarObject.AddComponent<ORCA.Agent>();
            }
            if (directVelocityDrive)
            {
                var agentBase = avatarObject.GetComponent<Base>();
                if (agentBase != null)
                {
                    agentBase.DirectVelocityDrive = true;
                }
            }
            avatarObject.transform.parent = transform;
        }
    }
}
