using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Minimal helper referenced by AttachPropToHand.cs:111.
    /// The original definition was never committed; this stub restores compilation.
    /// GetLocomotionAnimator returns the character's locomotion Animator
    /// (searched on the object itself, then its children, then its parents).
    /// </summary>
    public static class AvatarAnimatorUtility
    {
        public static Animator GetLocomotionAnimator(GameObject go)
        {
            if (go == null) return null;
            var animator = go.GetComponent<Animator>();
            if (animator == null) animator = go.GetComponentInChildren<Animator>(true);
            if (animator == null) animator = go.GetComponentInParent<Animator>();
            return animator;
        }
    }
}
