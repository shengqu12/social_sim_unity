using System.Text;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial.EditorTools
{
    /// <summary>
    /// Session 44 TASK 5.4: determine what pose a clip is actually authored in, instead of guessing
    /// from its name. Samples humanoid bone heights across the clip on the real Rocketbox avatar and
    /// reports hips / feet / head, from which standing, sitting, crouching and lying are separable:
    ///
    ///   standing  hips ~0.9-1.0 m, head ~1.6-1.8 m, feet ~0
    ///   sitting   hips ~0.4-0.5 m, head ~1.2-1.3 m
    ///   crouching hips ~0.5-0.7 m, head ~1.0-1.3 m
    ///   lying     every bone low and the head-to-hip spread small
    ///
    /// Stroke_Shaking_Head was described as "lying in the sky", which conflates two separable
    /// questions -- what pose, and at what height -- and only measurement tells them apart.
    ///
    ///     Unity -batchmode -quit -executeMethod SEAN.AutoTrial.EditorTools.S44PoseProbe.Dump
    /// </summary>
    public static class S44PoseProbe
    {
        private static readonly string[] Clips =
        {
            "Assets/PedestrianAssets/Mixamo/Stroke Shaking Head.fbx",
            "Assets/PedestrianAssets/Mixamo/Sitting.fbx",
            "Assets/PedestrianAssets/Mixamo/Standing Arguing.fbx",
        };

        [MenuItem("AutoTrial/Session 44/Dump clip poses")]
        public static void Dump()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Resources/Prefabs/Rocketbox/Business_Male_01.prefab");
            if (prefab == null)
            {
                Debug.LogError("[S44Pose] Business_Male_01 prefab not found.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("clip\tt_norm\tbounds");
            foreach (var path in Clips)
            {
                var clip = FirstClip(path);
                if (clip == null)
                {
                    sb.AppendLine(path + "\tNO CLIP");
                    continue;
                }
                var inst = Object.Instantiate(prefab);
                inst.transform.position = Vector3.zero;
                inst.transform.rotation = Quaternion.identity;
                var anim = inst.GetComponentInChildren<Animator>();
                if (anim == null || anim.avatar == null || !anim.avatar.isHuman)
                {
                    sb.AppendLine(path + "\tNOT HUMANOID");
                    Object.DestroyImmediate(inst);
                    continue;
                }

                // Renderer bounds, not GetBoneTransform: this avatar is imported with "Optimize
                // GameObjects", which strips the bone Transforms from the hierarchy and makes
                // GetBoneTransform return null on a rig whose isHuman is True -- the same trap
                // S41MixamoClipApplier documents for the carried box. Bounds need no hierarchy and
                // answer the question directly: a standing figure is ~1.8 m tall and narrow, a
                // lying one is short and long.
                for (int k = 0; k <= 4; k++)
                {
                    float tn = k / 4f;
                    clip.SampleAnimation(inst, tn * clip.length);
                    Bounds b = new Bounds(inst.transform.position, Vector3.zero);
                    bool any = false;
                    foreach (var r in inst.GetComponentsInChildren<Renderer>())
                    {
                        if (!any) { b = r.bounds; any = true; }
                        else { b.Encapsulate(r.bounds); }
                    }
                    if (!any) { sb.AppendLine(path + "\tNO RENDERER"); break; }
                    sb.AppendFormat("{0}\t{1:F2}\tminY={2:F3}\tmaxY={3:F3}\theight={4:F3}\t"
                        + "footprintXZ={5:F2}x{6:F2}\tverdict={7}\n",
                        System.IO.Path.GetFileNameWithoutExtension(path), tn,
                        b.min.y, b.max.y, b.size.y, b.size.x, b.size.z, Verdict(b));
                }
                Object.DestroyImmediate(inst);
            }
            Debug.Log("[S44Pose]\n" + sb);
        }

        /// <summary>Height and footprint separate the four candidate poses without needing bones.</summary>
        private static string Verdict(Bounds b)
        {
            float h = b.size.y;
            float spread = Mathf.Max(b.size.x, b.size.z);
            if (h > 1.5f) { return "STANDING"; }
            if (h > 1.0f) { return "SITTING/CROUCHING"; }
            return spread > h * 1.5f ? "LYING" : "LOW/UNKNOWN";
        }

        private static AnimationClip FirstClip(string path)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var c = o as AnimationClip;
                if (c != null && !c.name.StartsWith("__preview__")) { return c; }
            }
            return null;
        }
    }
}
