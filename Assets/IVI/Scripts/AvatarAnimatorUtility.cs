using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Minimal helper referenced by AttachPropToHand.cs:111.
    /// The original definition was never committed; this stub restores compilation.
    /// GetLocomotionAnimator returns the character's locomotion Animator: searched among the
    /// object itself and its children, preferring a Humanoid-avatar Animator (the actual
    /// character body) over a prop/animal's Animator (e.g. a nested fbx accessory that got its
    /// own Animator at import but isn't Humanoid-rigged). Falls back to the first Animator found
    /// if none is Humanoid, and to a parent's Animator if none exist among self/children at all.
    /// </summary>
    public static class AvatarAnimatorUtility
    {
        public static Animator GetLocomotionAnimator(GameObject go)
        {
            if (go == null) return null;
            Animator[] candidates = go.GetComponentsInChildren<Animator>(true);
            Animator animator = null;
            foreach (var candidate in candidates)
            {
                if (candidate.avatar != null && candidate.avatar.isHuman)
                {
                    animator = candidate;
                    break;
                }
            }
            if (animator == null && candidates.Length > 0)
            {
                animator = candidates[0];
                Debug.LogWarning($"[AvatarAnimatorUtility] No Humanoid Animator found under "
                    + $"'{go.name}' -- falling back to first Animator found ('{animator.name}'), "
                    + $"which may be a prop/animal, not the character body.");
            }
            if (animator == null) animator = go.GetComponentInParent<Animator>();
            return animator;
        }
    }
}
