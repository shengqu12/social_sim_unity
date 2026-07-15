using UnityEngine;

namespace IVI
{
    /// <summary>
    /// Draws a leash that connects the hand-held grip to the dog. The hand end follows
    /// this transform (attach it to the hand via <see cref="AttachPropToHand"/>), while the
    /// dog is kept in place. The rope is rebuilt every frame so it extends and sags/bends
    /// based on where the hand is relative to the dog.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class DynamicLeash : MonoBehaviour
    {
        [Header("Endpoints")]
        [Tooltip("Hand end of the leash. Defaults to this transform (attach this object to the hand).")]
        public Transform handEnd;

        [Tooltip("The dog. The leash connects to a point on it (see Dog Attach Offset).")]
        public Transform dog;

        [Tooltip("Local offset on the dog where the leash clips on, e.g. the collar/neck.")]
        public Vector3 dogAttachOffset = new Vector3(0f, 0.3f, 0f);

        [Header("Keep dog in place")]
        [Tooltip("On Start, re-parent the dog out of the hand so it does not get carried up to the hand.")]
        public bool detachDogOnStart = true;

        [Tooltip("Parent the dog under this transform so it stays grounded (and walks with the owner). Empty = world root.")]
        public Transform dogStaysUnder;

        [Header("Rope shape")]
        [Min(2)] public int segments = 16;

        [Tooltip("Maximum downward sag/bend (meters) when the leash is slack.")]
        public float sag = 0.25f;

        [Tooltip("Distance at/above which the rope is treated as taut (no sag).")]
        public float tautDistance = 1.5f;

        [Header("Rope look")]
        public float ropeWidth = 0.03f;

        [Tooltip("Material for the rope. If empty, a simple default material is created at runtime.")]
        public Material ropeMaterial;

        LineRenderer line;

        void Awake()
        {
            if (handEnd == null)
                handEnd = transform;

            line = GetComponent<LineRenderer>();
            if (line == null)
                line = gameObject.AddComponent<LineRenderer>();

            line.useWorldSpace = true;
            line.numCornerVertices = 4;
            line.numCapVertices = 4;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.widthMultiplier = ropeWidth;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            if (ropeMaterial != null)
                line.sharedMaterial = ropeMaterial;
            else if (line.sharedMaterial == null)
                line.sharedMaterial = CreateFallbackMaterial();

            // Detach in Awake (before any Start) so the dog is grounded before
            // AttachPropToHand snaps this leash onto the hand and would otherwise
            // carry the dog up with it.
            if (detachDogOnStart && dog != null)
            {
                Transform owner = dogStaysUnder;
                if (owner == null)
                {
                    var attach = GetComponentInParent<AttachPropToHand>();
                    owner = attach != null ? attach.transform : transform.root;
                }
                dog.SetParent(owner, true);
            }
        }

        void LateUpdate()
        {
            if (line == null || dog == null)
                return;

            Vector3 a = handEnd.position;
            Vector3 b = dog.TransformPoint(dogAttachOffset);

            int count = Mathf.Max(2, segments);
            if (line.positionCount != count)
                line.positionCount = count;

            float dist = Vector3.Distance(a, b);
            // More sag when the hand is close to the dog (slack), less when stretched taut.
            float slack = Mathf.Clamp01(1f - dist / Mathf.Max(0.01f, tautDistance));
            float drop = sag * slack;

            for (int i = 0; i < count; i++)
            {
                float t = i / (float)(count - 1);
                Vector3 p = Vector3.Lerp(a, b, t);
                // Parabolic droop: zero at the ends, maximum in the middle.
                p.y -= drop * 4f * t * (1f - t);
                line.SetPosition(i, p);
            }
        }

        static Material CreateFallbackMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            var mat = new Material(shader);
            mat.color = new Color(0.15f, 0.12f, 0.1f, 1f);
            return mat;
        }
    }
}
