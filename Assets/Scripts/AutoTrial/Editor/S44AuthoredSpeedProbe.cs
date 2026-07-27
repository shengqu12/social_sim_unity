using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial.EditorTools
{
    /// <summary>
    /// Session 44 FIX C, measurement half: read each animation clip's AUTHORED speed -- the ground
    /// speed its root motion actually produces at 1.0x playback -- instead of hand-filling it.
    ///
    /// This is the quantity S32AnimatorSpeedScaler's `referenceSpeedMps` is supposed to hold. Today
    /// that field is the bare literal 1.3f, applied identically to every appearance and every
    /// Mixamo clip, which is why a shuffle authored for ~0.6 m/s slides no matter what the scaler
    /// computes: the scaler normalises correctly against a pace the clip was never authored for.
    ///
    /// Measured rather than typed because a typed table silently rots the moment an asset is
    /// re-exported, and because being wrong here is invisible -- it looks like a scaler bug.
    ///
    /// AnimationClip.averageSpeed is the root-motion displacement over the clip divided by its
    /// length. Only the ground-plane (x, z) magnitude is taken: vertical bob is not travel. An
    /// in-place clip has no root translation and reads ~0; those are reported as inPlace=true so a
    /// per-clip target can be supplied by hand for exactly the clips where measurement cannot work,
    /// and only for those.
    ///
    ///     Unity -batchmode -quit -executeMethod SEAN.AutoTrial.EditorTools.S44AuthoredSpeedProbe.Dump
    /// </summary>
    public static class S44AuthoredSpeedProbe
    {
        private const string OutPath = "Assets/PedestrianAssets/Mixamo/authored_speeds.json";
        private const float InPlaceThresholdMps = 0.05f;

        /// <summary>
        /// The one config BOTH consumers read: S41MixamoClipApplier feeds `authoredSpeedMps` into
        /// S32AnimatorSpeedScaler.referenceSpeedMps, and run_trial.py derives the SFM speed
        /// multiplier from `targetSpeedMps`. Session 44 FIX C's whole point is that these are two
        /// DIFFERENT quantities that were previously conflated into one hard-coded 1.3 -- and that
        /// they must come from one file, because two files drift and the drift shows up as a slide
        /// nobody can attribute.
        /// </summary>
        public const string ClipSpeedsPath = "Assets/PedestrianAssets/Mixamo/clip_speeds.json";

        [MenuItem("AutoTrial/Session 44/Dump authored clip speeds")]
        public static void Dump()
        {
            var searchDirs = new List<string>();
            foreach (var d in new[] { "Assets/PedestrianAssets", "Assets/CustomAnimations", "Assets/Resources/Animation" })
            {
                if (AssetDatabase.IsValidFolder(d)) { searchDirs.Add(d); }
            }

            var seen = new HashSet<string>();
            var lines = new List<string>();
            var report = new StringBuilder();
            report.AppendLine("clip\tsource\tlength_s\tavgSpeed_ground_mps\tavgSpeed_y\tinPlace");

            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", searchDirs.ToArray()))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    var clip = obj as AnimationClip;
                    if (clip == null || clip.name.StartsWith("__preview__")) { continue; }
                    string key = path + "::" + clip.name;
                    if (!seen.Add(key)) { continue; }

                    Vector3 avg = clip.averageSpeed;
                    float ground = new Vector2(avg.x, avg.z).magnitude;
                    bool inPlace = ground < InPlaceThresholdMps;

                    report.AppendFormat(CultureInfo.InvariantCulture,
                        "{0}\t{1}\t{2:F3}\t{3:F4}\t{4:F4}\t{5}\n",
                        clip.name, Path.GetFileName(path), clip.length, ground, avg.y, inPlace);

                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "    {{ \"clip\": \"{0}\", \"asset\": \"{1}\", \"lengthSec\": {2:F3}, " +
                        "\"authoredSpeedMps\": {3:F4}, \"inPlace\": {4} }}",
                        EscapeJson(clip.name), EscapeJson(Path.GetFileName(path)), clip.length,
                        ground, inPlace ? "true" : "false"));
                }
            }

            var json = new StringBuilder();
            json.AppendLine("{");
            json.AppendLine("  \"_comment\": \"Session 44 FIX C. authoredSpeedMps is the ground-plane root-motion speed of the clip at 1.0x playback, measured from AnimationClip.averageSpeed -- NOT hand-entered. inPlace=true means the clip carries no root translation, so its authored speed cannot be measured and a per-clip value must be supplied in the target-speed config instead.\",");
            json.AppendLine("  \"clips\": [");
            json.AppendLine(string.Join(",\n", lines.ToArray()));
            json.AppendLine("  ]");
            json.AppendLine("}");

            File.WriteAllText(OutPath, json.ToString());
            AssetDatabase.ImportAsset(OutPath);
            Debug.Log("[S44AuthoredSpeed] wrote " + OutPath + " (" + lines.Count + " clips)\n" + report);
        }

        /// <summary>
        /// Refresh clip_speeds.json's measured column while PRESERVING every hand-chosen
        /// targetSpeedMps. Re-running after an asset re-export must not silently revert a design
        /// decision, and must not leave a stale authored value behind either.
        /// </summary>
        [MenuItem("AutoTrial/Session 44/Refresh clip_speeds.json (keeps targets)")]
        public static void RefreshClipSpeeds()
        {
            var targets = new Dictionary<string, string>();
            if (File.Exists(ClipSpeedsPath))
            {
                foreach (var raw in File.ReadAllLines(ClipSpeedsPath))
                {
                    string clip = Between(raw, "\"clip\": \"", "\"");
                    string tgt = Between(raw, "\"targetSpeedMps\": ", ",");
                    if (clip != null && tgt != null) { targets[clip] = tgt.Trim(); }
                }
            }

            // controller/--mixamo-clip name -> source FBX, so the measured value can be looked up.
            var byController = new Dictionary<string, string>
            {
                { "carry_and_walk", "carry_and_walk.fbx" },
                { "Drunk_Walk", "Drunk Walk.fbx" },
                { "Old_Man_Walk", "Old Man Walk.fbx" },
                { "Pacing_And_Talking_On_A_Phone", "Pacing And Talking On A Phone.fbx" },
                { "Running", "Running.fbx" },
                { "Sitting", "Sitting.fbx" },
                { "Standing_Arguing", "Standing Arguing.fbx" },
                { "Stroke_Shaking_Head", "Stroke Shaking Head.fbx" },
            };

            var measured = new Dictionary<string, KeyValuePair<float, bool>>();
            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets/PedestrianAssets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    var clip = obj as AnimationClip;
                    if (clip == null || clip.name.StartsWith("__preview__")) { continue; }
                    Vector3 a = clip.averageSpeed;
                    float ground = new Vector2(a.x, a.z).magnitude;
                    measured[Path.GetFileName(path)] =
                        new KeyValuePair<float, bool>(ground, ground < InPlaceThresholdMps);
                }
            }

            var entries = new List<string>();
            foreach (var kv in byController)
            {
                float authored = 0f; bool inPlace = true;
                if (measured.ContainsKey(kv.Value))
                {
                    authored = measured[kv.Value].Key;
                    inPlace = measured[kv.Value].Value;
                }
                string target = targets.ContainsKey(kv.Key) ? targets[kv.Key] : "null";
                entries.Add(string.Format(CultureInfo.InvariantCulture,
                    "    {{ \"clip\": \"{0}\", \"asset\": \"{1}\", \"authoredSpeedMps\": {2:F4}, " +
                    "\"inPlace\": {3}, \"targetSpeedMps\": {4} }}",
                    kv.Key, EscapeJson(kv.Value), authored, inPlace ? "true" : "false", target));
            }

            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"_comment\": \"Session 44 FIX C. THE single source both consumers read: S41MixamoClipApplier sets S32AnimatorSpeedScaler.referenceSpeedMps from authoredSpeedMps, and tools/run_trial.py derives the SFM speed multiplier from targetSpeedMps. Two different quantities -- conflating them into one constant is what caused the slide.\",");
            sb.AppendLine("  \"_authored\": \"MEASURED from AnimationClip.averageSpeed (ground plane), never hand-edited. Regenerate with AutoTrial/Session 44/Refresh clip_speeds.json after any asset re-export; it preserves targetSpeedMps.\",");
            sb.AppendLine("  \"_target\": \"DESIGN CHOICE, hand-set. null means 'no override, use the pipeline default'. 0 means the character should not travel at all.\",");
            sb.AppendLine("  \"_inPlace\": \"true = the clip carries no root translation, so authoredSpeedMps could not be measured and animation scaling is meaningless for it.\",");
            sb.AppendLine("  \"clips\": [");
            sb.AppendLine(string.Join(",\n", entries.ToArray()));
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            File.WriteAllText(ClipSpeedsPath, sb.ToString());
            AssetDatabase.ImportAsset(ClipSpeedsPath);
            Debug.Log("[S44ClipSpeeds] wrote " + ClipSpeedsPath + " (" + entries.Count
                + " clips; preserved " + targets.Count + " existing target value(s))");
        }

        private static string Between(string s, string a, string b)
        {
            int i = s.IndexOf(a);
            if (i < 0) { return null; }
            i += a.Length;
            int j = s.IndexOf(b, i);
            return j < 0 ? null : s.Substring(i, j - i);
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
