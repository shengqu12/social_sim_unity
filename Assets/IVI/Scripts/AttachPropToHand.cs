using System.Collections;
using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Parents a prop to a humanoid body bone (hand, head, etc.) and applies a local pose.
    /// </summary>
    public class AttachPropToHand : MonoBehaviour
    {
        public enum AlignMode
        {
            Pivot = 0,
            BoundsTopToBone = 1,
            BoundsCenterToBone = 2,
            BoundsCenterToEyes = 3,
        }

        public Transform prop;
        public HumanBodyBones handBone = HumanBodyBones.RightHand;

        [Tooltip("Fallback bone name if GetBoneTransform fails (e.g. Bip01 R Hand, Bip01 Head).")]
        public string autoTagBoneName = "Bip01 R Hand";

        [Header("Local pose relative to the body bone")]
        public Vector3 localPosition = new Vector3(0.006f, 0.002f, 0.004f);
        public Vector3 localEulerAngles = new Vector3(-68f, 8f, 88f);
        public Vector3 localScale = new Vector3(4f, 4f, 4f);

        [Tooltip("On attach, derive local offsets from the prop's current scene pose.")]
        public bool captureOffsetFromCurrentPoseOnStart = false;

        [Tooltip("Apply Local Scale after parenting.")]
        public bool applyLocalScale = true;

        [Header("Snap a point of the prop onto the body")]
        [Tooltip("After parenting, slide the prop so a point of its mesh bounds lands on the bone (or eyes). " +
                 "Pivot = no adjustment. BoundsTopToBone = top of the prop sits at the bone (e.g. cane grip at hand). " +
                 "BoundsCenterToEyes = prop centered on the eyes (e.g. sunglasses).")]
        public AlignMode align = AlignMode.Pivot;

        [Tooltip("Extra nudge applied after aligning, in the body's own axes (x = right, y = up, " +
                 "z = forward). Negative x moves the prop to the person's left. Useful for fitting " +
                 "glasses higher or sideways on the face.")]
        public Vector3 alignOffset = Vector3.zero;

        [Header("Keep the prop level / upright (e.g. glasses)")]
        [Tooltip("Face the prop along the body's forward with world-up, so it stays horizontal " +
                 "regardless of the bone's or character's roll. Applied before alignment.")]
        public bool levelToBody = false;

        [Tooltip("Rotation correction (degrees) applied after leveling, to match how the mesh " +
                 "was authored (e.g. flip 180 on Y if it faces backwards).")]
        public Vector3 levelRotationOffset = Vector3.zero;

        [Header("Cane-style orientation (overrides Align when enabled)")]
        [Tooltip("Rotate the prop so its longest axis points forward and downward from the body, " +
                 "with the grip end sitting on the bone (e.g. a white cane held in the hand).")]
        public bool orientLongAxisForwardDown = false;

        [Tooltip("Angle of the far end below horizontal: 0 = straight ahead, 90 = straight down.")]
        [Range(0f, 90f)]
        public float forwardDownAngle = 50f;

        [Tooltip("Swap which end of the long axis is treated as the grip (attached to the bone). " +
                 "Enable this if the cane ends up pointing the wrong way.")]
        public bool flipGripEnd = false;

        [Header("Debug (read-only)")]
        public bool debugAttached;
        public string debugAnchorTarget;

        Animator animator;

        void Start()
        {
            StartCoroutine(AttachWhenReady());
        }

        IEnumerator AttachWhenReady()
        {
            for (int i = 0; i < 90; i++)
            {
                if (TryAttach())
                    yield break;

                yield return null;
            }

            yield return new WaitForEndOfFrame();
            if (!TryAttach())
            {
                Debug.LogWarning(
                    $"[AttachPropToHand] Could not parent prop on '{name}'. " +
                    $"No animated {handBone} and no child named '{autoTagBoneName}'.",
                    this);
            }
        }

        bool TryAttach()
        {
            if (prop == null)
            {
                Debug.LogWarning("[AttachPropToHand] Prop is not assigned.", this);
                return false;
            }

            if (debugAttached)
                return true;

            animator = AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator != null && !animator.isInitialized && animator.runtimeAnimatorController != null)
                animator.Rebind();

            Transform anchor = ResolveAnchorTransform();
            if (anchor == null)
                return false;

            if (captureOffsetFromCurrentPoseOnStart)
                CaptureOffsetFromCurrentPose(anchor);

            prop.SetParent(anchor, false);
            prop.localPosition = localPosition;
            prop.localRotation = Quaternion.Euler(localEulerAngles);

            if (applyLocalScale)
                prop.localScale = localScale;

            if (levelToBody)
            {
                Vector3 fwd = transform.forward;
                fwd.y = 0f;
                fwd = fwd.sqrMagnitude < 1e-4f ? Vector3.forward : fwd.normalized;
                prop.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(levelRotationOffset);
            }

            if (orientLongAxisForwardDown)
                OrientLongAxisForwardDown(anchor);
            else if (align != AlignMode.Pivot)
                AlignPropToBody(anchor);

            if (alignOffset != Vector3.zero)
                prop.position += transform.right * alignOffset.x
                               + transform.up * alignOffset.y
                               + transform.forward * alignOffset.z;

            debugAnchorTarget = anchor.name;
            debugAttached = true;
            return true;
        }

        // Slides the (already parented) prop so a chosen point of its mesh bounds
        // coincides with the bone (or the eyes). Because the prop is rigidly parented,
        // baking this offset once keeps it aligned as the body animates.
        void AlignPropToBody(Transform anchor)
        {
            Renderer r = prop.GetComponentInChildren<Renderer>();
            if (r == null)
                return;

            Bounds b = r.bounds;

            Vector3 source;
            Vector3 target;
            switch (align)
            {
                case AlignMode.BoundsTopToBone:
                    source = new Vector3(b.center.x, b.max.y, b.center.z);
                    target = anchor.position;
                    break;
                case AlignMode.BoundsCenterToEyes:
                    source = b.center;
                    target = ResolveEyeTarget() ?? anchor.position;
                    break;
                default: // BoundsCenterToBone
                    source = b.center;
                    target = anchor.position;
                    break;
            }

            prop.position += target - source;
        }

        // Rotates the prop so its longest axis points forward and downward relative to the body,
        // then slides it so the grip end rests on the bone. Works off the mesh's longest dimension,
        // so it is independent of how the rig bone and the cane mesh were authored. Because the prop
        // is rigidly parented, this baked pose follows the hand as the body animates.
        void OrientLongAxisForwardDown(Transform anchor)
        {
            MeshFilter meshFilter = prop.GetComponentInChildren<MeshFilter>();
            Transform meshTransform = meshFilter != null ? meshFilter.transform : prop;

            Bounds localBounds;
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                localBounds = meshFilter.sharedMesh.bounds;
            }
            else
            {
                Renderer r = prop.GetComponentInChildren<Renderer>();
                if (r == null)
                    return;

                Vector3 c = meshTransform.InverseTransformPoint(r.bounds.center);
                Vector3 s = meshTransform.InverseTransformVector(r.bounds.size);
                localBounds = new Bounds(c, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
            }

            Vector3 size = localBounds.size;
            int axis = 0;
            if (size.y > size.x) axis = 1;
            if (size.z > size[axis]) axis = 2;

            Vector3 axisDir = Vector3.zero;
            axisDir[axis] = 1f;
            float half = size[axis] * 0.5f;
            Vector3 endALocal = localBounds.center - axisDir * half;
            Vector3 endBLocal = localBounds.center + axisDir * half;

            // Default: treat the higher end as the grip (canes are authored roughly upright).
            Vector3 endAWorld = meshTransform.TransformPoint(endALocal);
            Vector3 endBWorld = meshTransform.TransformPoint(endBLocal);
            bool aIsGrip = endAWorld.y >= endBWorld.y;
            if (flipGripEnd)
                aIsGrip = !aIsGrip;

            Vector3 gripLocal = aIsGrip ? endALocal : endBLocal;
            Vector3 tipLocal = aIsGrip ? endBLocal : endALocal;

            Vector3 currentDir = meshTransform.TransformPoint(tipLocal) - meshTransform.TransformPoint(gripLocal);
            if (currentDir.sqrMagnitude < 1e-6f)
                return;
            currentDir.Normalize();

            Vector3 forward = transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized;

            float a = forwardDownAngle * Mathf.Deg2Rad;
            Vector3 desiredDir = (forward * Mathf.Cos(a) + Vector3.down * Mathf.Sin(a)).normalized;

            prop.rotation = Quaternion.FromToRotation(currentDir, desiredDir) * prop.rotation;

            // Snap the grip end onto the bone after rotating.
            prop.position += anchor.position - meshTransform.TransformPoint(gripLocal);
        }

        Vector3? ResolveEyeTarget()
        {
            if (animator == null)
                return null;

            Transform leftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            Transform rightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);

            if (leftEye != null && rightEye != null)
                return (leftEye.position + rightEye.position) * 0.5f;
            if (leftEye != null)
                return leftEye.position;
            if (rightEye != null)
                return rightEye.position;

            return null;
        }

        void CaptureOffsetFromCurrentPose(Transform anchor)
        {
            localPosition = anchor.InverseTransformPoint(prop.position);
            localEulerAngles = (Quaternion.Inverse(anchor.rotation) * prop.rotation).eulerAngles;

            if (applyLocalScale)
                localScale = prop.localScale;
        }

        Transform ResolveAnchorTransform()
        {
            if (animator != null)
            {
                Transform bone = animator.GetBoneTransform(handBone);
                if (bone != null)
                    return bone;
            }

            if (string.IsNullOrEmpty(autoTagBoneName))
                return null;

            foreach (Transform t in transform.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == autoTagBoneName)
                    return t;
            }

            return null;
        }
    }
}
