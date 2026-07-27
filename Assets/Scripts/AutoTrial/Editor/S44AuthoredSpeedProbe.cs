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

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
