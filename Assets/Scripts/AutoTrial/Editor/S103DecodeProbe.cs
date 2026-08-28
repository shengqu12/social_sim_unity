using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 103. Three questions about ONE asset, answered from the imported artifact rather
    /// than from the source file, because S100-S102 have already eliminated everything on the
    /// source side.
    ///
    /// STEP 0  is the imported artifact stale? S101's applier was killed at run_trial's hardcoded
    ///         180 s cap (rc=-9) mid-reimport, so the .meta on disk may be ahead of what Unity
    ///         actually baked. Logs the AssetDatabase dependency hash, forces a clean reimport of
    ///         this one asset, and logs the hash again.
    /// STEP 1  the baked MUSCLE curves. A humanoid clip is stored in muscle space, converted at
    ///         import against the source avatar's reference. If the 11x asymmetry is in the bake,
    ///         it is visible as an L/R difference in the leg muscle channels.
    /// STEP 2  the baked IK GOAL curves ("LeftFootT/Q", "RightFootT/Q"). The Grounded state runs
    ///         with m_IKOnFeet: 1, and Unity's automatic foot IK reads exactly these. A goal-curve
    ///         asymmetry sitting on top of clean muscle curves indicts the goal bake specifically.
    ///
    /// Read-only apart from the Step 0 reimport, which rebuilds Library artifacts and touches no
    /// source asset. Writes one CSV per clip to AUTOTRIAL_S103_OUT.
    ///
    /// -executeMethod SEAN.AutoTrial.S103DecodeProbe.Run
    /// </summary>
    public static class S103DecodeProbe
    {
        private static readonly string[] Clips =
        {
            "Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk.fbx",
            "Assets/PedestrianAssets/Kimodo/kimodo_elderly_shuffle.fbx",
            "Assets/PedestrianAssets/Mixamo/Old Man Walk.fbx",
        };

        public static void Run()
        {
            string outDir = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S103_OUT");
            if (string.IsNullOrEmpty(outDir)) { Debug.LogError("[S103] AUTOTRIAL_S103_OUT unset"); return; }
            Directory.CreateDirectory(outDir);
            var sb = new StringBuilder();

            // ---- STEP 0: hash, force reimport, hash again -------------------------------------
            foreach (string p in Clips)
            {
                if (AssetImporter.GetAtPath(p) == null) { sb.AppendLine("[S103] absent: " + p); continue; }
                Hash128 before = AssetDatabase.GetAssetDependencyHash(p);
                sb.AppendLine("[S103] STEP0 " + Path.GetFileName(p) + " dependencyHash BEFORE = " + before);
            }
            string forced = Clips[0];
            var t0 = System.DateTime.UtcNow;
            AssetDatabase.ImportAsset(forced,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "[S103] STEP0 forced clean reimport of {0} took {1:F1} s",
                Path.GetFileName(forced), (System.DateTime.UtcNow - t0).TotalSeconds));
            foreach (string p in Clips)
            {
                if (AssetImporter.GetAtPath(p) == null) { continue; }
                sb.AppendLine("[S103] STEP0 " + Path.GetFileName(p) + " dependencyHash AFTER  = "
                              + AssetDatabase.GetAssetDependencyHash(p));
            }

            // ---- STEPS 1 & 2: dump every curve binding of the imported clip --------------------
            foreach (string p in Clips)
            {
                var clip = AssetDatabase.LoadAllAssetsAtPath(p).OfType<AnimationClip>()
                    .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
                if (clip == null) { sb.AppendLine("[S103] no clip in " + p); continue; }

                string csv = Path.Combine(outDir, Path.GetFileNameWithoutExtension(p) + "_curves.csv");
                using (var w = new StreamWriter(csv, false))
                {
                    w.WriteLine("binding,keys,min,max,mean,ptp");
                    foreach (var b in AnimationUtility.GetCurveBindings(clip))
                    {
                        var c = AnimationUtility.GetEditorCurve(clip, b);
                        if (c == null || c.keys.Length == 0) { continue; }
                        float mn = float.MaxValue, mx = float.MinValue, sum = 0f;
                        foreach (var k in c.keys)
                        {
                            mn = Mathf.Min(mn, k.value); mx = Mathf.Max(mx, k.value); sum += k.value;
                        }
                        w.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "{0},{1},{2:F6},{3:F6},{4:F6},{5:F6}",
                            b.propertyName, c.keys.Length, mn, mx, sum / c.keys.Length, mx - mn));
                    }
                }
                sb.AppendLine("[S103] " + Path.GetFileName(p) + ": "
                              + AnimationUtility.GetCurveBindings(clip).Length + " bindings -> " + csv);
            }
            Debug.Log(sb.ToString());
            File.WriteAllText(Path.Combine(outDir, "s103_log.txt"), sb.ToString());
        }
    }
}
