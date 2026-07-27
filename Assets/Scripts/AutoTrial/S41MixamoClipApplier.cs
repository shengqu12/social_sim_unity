using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 41 TASK 3/4/6. Swaps the spawned pedestrian's Animator onto one of the generated
    /// single-state Mixamo controllers (see S41MixamoControllerGen) and, for carry_and_walk,
    /// attaches the carried-box primitive.
    ///
    /// The nine Mixamo FBXs are animation-only (no mesh -- see S41MixamoContentProbe), so the
    /// character on screen is always the ordinary Rocketbox avatar; Unity's Humanoid retargeting
    /// is what puts the Mixamo motion on it. This is the same mechanism Session 31 used for
    /// point_backwards.fbx, just parameterised per clip.
    ///
    /// Runs after Base.Start() (which is what caches the Animator and installs the root-motion
    /// path), hence DefaultExecutionOrder -- swapping the controller before that would race the
    /// applyRootMotion/RootMotionSink setup.
    /// </summary>
    [DefaultExecutionOrder(500)]
    public class S41MixamoClipApplier : MonoBehaviour
    {
        public string clipControllerName = "";
        public bool attachCarriedBox = false;

        // TASK 4's dimensions, straight from the ticket.
        public Vector3 boxSize = new Vector3(0.45f, 0.35f, 0.35f);
        // Cardboard brown #8B6F47.
        public Color boxColor = new Color(0x8B / 255f, 0x6F / 255f, 0x47 / 255f);

        // Body-relative anchor used when the rig exposes no bone Transforms (see AttachBox).
        // ~1.1m is the chest height the ticket cites, and is the number the robot-cannot-see-it
        // argument is built on (the sensor plane is 0.32m).
        public float carryHeightMeters = 1.1f;
        public float carryForwardMeters = 0.28f;

        private bool applied;

        void Start()
        {
            if (applied) { return; }
            applied = true;
            StartCoroutine(ApplyNextFrame());
        }

        // Deferred one frame. Assigning runtimeAnimatorController rebinds the Animator, and until
        // that rebind has been processed GetBoneTransform() returns null even on a valid Humanoid
        // avatar -- which is exactly how the first attempt at the carried box failed ("hand bones
        // missing" on an avatar whose isHuman was True).
        private System.Collections.IEnumerator ApplyNextFrame()
        {
            var animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator == null)
            {
                Debug.LogError("[S41Mixamo] no Animator resolved on '" + name + "' -- cannot apply clip.");
                yield break;
            }

            if (!string.IsNullOrEmpty(clipControllerName))
            {
                var rac = Resources.Load<RuntimeAnimatorController>(clipControllerName);
                if (rac == null)
                {
                    Debug.LogError("[S41Mixamo] Resources.Load failed for controller '" + clipControllerName
                        + "' -- is it under a Resources folder? Leaving the original controller in place.");
                }
                else
                {
                    Debug.Log("[S41Mixamo] '" + name + "' controller "
                        + (animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "NULL")
                        + " -> " + rac.name + " (avatar isHuman="
                        + (animator.avatar != null && animator.avatar.isHuman) + ")");
                    animator.runtimeAnimatorController = rac;
                }
                ApplyAuthoredSpeed();
                // Session 44 TASK 5.2/5.3: per-clip staging (Sitting's stool, Standing_Arguing's
                // second person). Additive and self-selecting on the clip name, so every other clip
                // is untouched.
                var props = gameObject.AddComponent<S44ClipProps>();
                props.clipName = clipControllerName;
                // Session 46 (2): in-place clips are exactly the ones that must not travel.
                props.suppressRootMotion = inPlaceClip;
            }

            if (attachCarriedBox)
            {
                // One frame for the rebind, then attach. AttachBox falls back to a body-relative
                // anchor when the rig exposes no bone Transforms, so this does not need to retry.
                yield return null;
                AttachBox(animator);
            }
        }

        /// <summary>
        /// Session 44 FIX C: point S32AnimatorSpeedScaler at the pace THIS clip was actually
        /// authored for, instead of the single hard-coded 1.3 m/s it applied to every clip.
        ///
        /// That constant was measured wrong for every Mixamo asset, in both directions -- Old Man
        /// Walk is authored at 0.392 m/s (3.3x over-estimated) and Running at 4.406 m/s (3.4x
        /// under). The scaler normalises correctly; it was normalising against a pace none of these
        /// clips were authored for, which is why the animation could match travel speed or footfall
        /// cadence but never both.
        ///
        /// Reads Assets/PedestrianAssets/Mixamo/clip_speeds.json -- the SAME file run_trial.py
        /// derives the SFM speed multiplier from. One source, deliberately: two would drift, and
        /// the drift would present as a slide with no obvious cause.
        ///
        /// in-place clips (no root translation) are skipped: animation speed scaling has no meaning
        /// for a clip that does not travel, and their measured authored speed is ~0, which would
        /// divide through to an absurd scale.
        /// </summary>
        // Set by ApplyAuthoredSpeed so the caller can act on it (Session 46: root-motion
        // suppression applies to exactly the in-place clips).
        private bool inPlaceClip;

        private void ApplyAuthoredSpeed()
        {
            var scaler = GetComponent<S32AnimatorSpeedScaler>();
            if (scaler == null) { return; }

            var cfg = Resources.Load<TextAsset>("clip_speeds");
            string json = cfg != null ? cfg.text : ReadFromAssetPath();
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogWarning("[S41Mixamo] clip_speeds.json not found -- S32AnimatorSpeedScaler "
                    + "keeps its default referenceSpeedMps=" + scaler.referenceSpeedMps
                    + ", which is WRONG for every Mixamo clip. Animation scaling will be off.");
                return;
            }

            float authored;
            bool inPlace;
            if (!TryLookup(json, clipControllerName, out authored, out inPlace))
            {
                Debug.LogWarning("[S41Mixamo] '" + clipControllerName + "' has no clip_speeds.json entry -- "
                    + "leaving referenceSpeedMps=" + scaler.referenceSpeedMps);
                return;
            }
            inPlaceClip = inPlace || authored < 0.05f;
            if (inPlaceClip)
            {
                Debug.Log("[S41Mixamo] '" + clipControllerName + "' is in-place (authored "
                    + authored.ToString("F4") + " m/s) -- animation speed scaling left untouched.");
                return;
            }

            float before = scaler.referenceSpeedMps;
            scaler.referenceSpeedMps = authored;
            Debug.Log("[S41Mixamo] '" + clipControllerName + "' referenceSpeedMps "
                + before.ToString("F4") + " -> " + authored.ToString("F4") + " m/s (measured authored pace)");
        }

        private static string ReadFromAssetPath()
        {
            const string p = "Assets/PedestrianAssets/Mixamo/clip_speeds.json";
            try
            {
                return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p) : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>Minimal field scan -- JsonUtility cannot parse a top-level array of objects with
        /// mixed types, and pulling in a JSON dependency for four numbers is not worth it.</summary>
        private static bool TryLookup(string json, string clip, out float authored, out bool inPlace)
        {
            authored = 0f;
            inPlace = true;
            int i = json.IndexOf("\"clip\": \"" + clip + "\"");
            if (i < 0) { return false; }
            int end = json.IndexOf('}', i);
            if (end < 0) { end = json.Length; }
            string rec = json.Substring(i, end - i);

            int a = rec.IndexOf("\"authoredSpeedMps\":");
            if (a < 0) { return false; }
            a += "\"authoredSpeedMps\":".Length;
            int aEnd = rec.IndexOf(',', a);
            if (aEnd < 0) { aEnd = rec.Length; }
            if (!float.TryParse(rec.Substring(a, aEnd - a).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out authored))
            {
                return false;
            }
            inPlace = rec.Contains("\"inPlace\": true");
            return true;
        }

        // GetBoneTransform is the correct, rig-naming-agnostic lookup, but it returns null on rigs
        // imported with "Optimize GameObjects" (the bone Transforms are stripped from the
        // hierarchy) -- which is what happened here on Male_Adult_01Avatar despite isHuman=True.
        // Falls back to a name search covering both this project's rig conventions: Rocketbox
        // ("Bip01 L Hand") and Mixamo ("mixamorig:LeftHand").
        private static Transform FindBone(Transform root, params string[] needles)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                foreach (var needle in needles)
                {
                    if (t.name.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return t;
                    }
                }
            }
            return null;
        }

        private bool AttachBox(Animator animator)
        {
            if (animator.avatar == null || !animator.avatar.isHuman)
            {
                Debug.LogError("[S41Mixamo] carried box needs a Humanoid avatar to find the hand bones.");
                return false;
            }

            Transform lHand = animator.GetBoneTransform(HumanBodyBones.LeftHand) ?? FindBone(animator.transform, "L Hand", "LeftHand");
            Transform rHand = animator.GetBoneTransform(HumanBodyBones.RightHand) ?? FindBone(animator.transform, "R Hand", "RightHand");

            var anchor = new GameObject("CarriedBox");
            if (lHand != null && rHand != null)
            {
                // Preferred: parented to one hand, positioned at the midpoint of both, so the box
                // tracks actual hand motion.
                anchor.transform.SetParent(rHand, false);
                anchor.transform.position = (lHand.position + rHand.position) * 0.5f;
                anchor.transform.rotation = animator.transform.rotation;
                anchor.transform.position += animator.transform.forward * (boxSize.z * 0.5f);
            }
            else
            {
                // Fallback, and in practice the path this project takes: the Rocketbox rigs are
                // imported with "Optimize GameObjects", which strips every bone Transform out of
                // the hierarchy (verified -- the only children under the Animator are the root and
                // a single skinned mesh), so BOTH GetBoneTransform and a name search necessarily
                // return null even though avatar.isHuman is true. Exposing the hands would mean
                // editing the shared Microsoft-Rocketbox submodule's import settings, which is out
                // of scope. The ticket anticipates exactly this and offers a body-relative anchor
                // as the alternative; carry_and_walk holds the hands nearly static relative to the
                // chest, so a fixed chest-height offset is visually equivalent.
                anchor.transform.SetParent(animator.transform, false);
                anchor.transform.localPosition = new Vector3(0f, carryHeightMeters, carryForwardMeters);
                anchor.transform.localRotation = Quaternion.identity;
                Debug.Log("[S41Mixamo] carried box: rig exposes no bone Transforms (Optimize "
                    + "GameObjects) -- using body-relative anchor at chest height.");
            }

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "CarriedBoxMesh";
            box.transform.SetParent(anchor.transform, false);
            box.transform.localPosition = Vector3.zero;
            box.transform.localRotation = Quaternion.identity;
            box.transform.localScale = boxSize;

            // No collider: the ticket calls for this explicitly. The box sits at ~1.1m, far above
            // the robot's 0.32m sensor plane, so a collider could not affect navigation anyway --
            // it would only add physics cost. See the README on why that invisibility is a
            // deliberately retained perception case rather than a bug.
            var col = box.GetComponent<Collider>();
            if (col != null) { Destroy(col); }

            var renderer = box.GetComponent<Renderer>();
            if (renderer != null && renderer.material != null)
            {
                renderer.material.color = boxColor;
                // Matte: specular highlights are a confound for VLM judgement.
                if (renderer.material.HasProperty("_Glossiness")) { renderer.material.SetFloat("_Glossiness", 0f); }
                if (renderer.material.HasProperty("_Metallic")) { renderer.material.SetFloat("_Metallic", 0f); }
            }

            Debug.Log(string.Format("[S41Mixamo] carried box attached at world y={0:F2}m (robot sensor plane is 0.32m) size=({1},{2},{3})",
                anchor.transform.position.y, boxSize.x, boxSize.y, boxSize.z));
            return true;
        }
    }
}
