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

        public GameObject avatarObject { get; private set; }

        void Awake()
        {
            GameObject avatarPrefab = avatars[Random.Range(0, avatars.Length)];
            avatarObject = Instantiate(avatarPrefab, transform.position, transform.rotation);
            Animator animator = avatarObject.GetComponent<Animator>();
            animator.runtimeAnimatorController = animationController;
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
            avatarObject.transform.parent = transform;
        }
    }
}
