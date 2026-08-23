using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// S84: dump the 95 Humanoid muscle values per frame for a clip played on its own (NON-optimized)
    /// Kimodo rig. The Rocketbox pedestrian prefabs are imported with Optimize Game Objects on, so a
    /// HumanPoseHandler built against them reads a rig with no Transforms and returns a static pose --
    /// measured, and the reason this probe deliberately uses the Kimodo rig instead.
    ///
    /// Muscle values are normalized to [-1, 1] against the avatar's per-DoF range. A value pinned at
    /// +/-1 is CLAMPED: the source rotation asked for more than the humanoid rig can express.
    ///
    /// -executeMethod SEAN.AutoTrial.S84MuscleProbe.Run
    public static class S84MuscleProbe
    {
        public static void Run()
        {
            string outDir = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S84_OUT");
            foreach (var tag in new[] { "b2_R2_selfrig", "walk_R2_selfrig", "b2_fix_tdof", "b2_fix_stretch", "b2_fix_both", "b2_fix_limits", "walk_fix_limits" })
            {
                string path = "Assets/PedestrianAssets/Kimodo/S84/" + tag + ".fbx";
                var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                    .FirstOrDefault(c => !c.name.StartsWith("__preview"));
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var go = Object.Instantiate(model);
                var an = go.GetComponentInChildren<Animator>();
                var poser = new HumanPoseHandler(an.avatar, go.transform);
                var pose = new HumanPose();
                var names = HumanTrait.MuscleName;
                int n = Mathf.RoundToInt(clip.length * 30f);
                var sb = new System.Text.StringBuilder();
                sb.Append("frame,").Append(string.Join(",", names.Select(s => s.Replace(" ", "_")))).Append('\n');

                AnimationMode.StartAnimationMode();
                for (int i = 0; i <= n; i++)
                {
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(go, clip, Mathf.Min(clip.length, i / 30f));
                    AnimationMode.EndSampling();
                    poser.GetHumanPose(ref pose);
                    sb.Append(i);
                    foreach (var m in pose.muscles)
                        sb.Append(',').Append(m.ToString("F5", CultureInfo.InvariantCulture));
                    sb.Append('\n');
                }
                AnimationMode.StopAnimationMode();
                poser.Dispose();
                Object.DestroyImmediate(go);
                File.WriteAllText(Path.Combine(outDir, tag + "_muscles.csv"), sb.ToString());
                Debug.Log("[S84muscle] " + tag + " frames=" + (n + 1) + " muscles=" + names.Length);
            }
        }
    }
}
