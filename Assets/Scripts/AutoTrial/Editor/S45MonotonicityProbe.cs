using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial.EditorTools
{
    /// <summary>
    /// Session 45 (1.1a): decide, per clip, whether `AnimationClip.averageSpeed` is a valid measure
    /// of its authored walking pace -- and supply a usable number when it is not.
    ///
    /// averageSpeed is NET displacement over duration. That equals the walking pace only if the root
    /// travels monotonically. `Pacing And Talking On A Phone` paces back and forth, so its outbound
    /// and return legs cancel and it measured 0.415 m/s, far below the pace the character actually
    /// walks at. Everything downstream inherited that error: required = target/ref = 0.8/0.415 =
    /// 1.928 (roughly double), the animation played that much too fast, and 19.1% of frames hit the
    /// 3.0 ceiling with worstRequired 7.059 -- a playback rate that jumps rather than holds, which
    /// is what the "accelerating" impression is.
    ///
    /// The discriminator is net displacement over PATH LENGTH. Both are computed from the same
    /// sampled root track:
    ///
    ///   ratio ~ 1.0   monotonic; averageSpeed is the authored pace
    ///   ratio &lt; 0.7   the root reverses; averageSpeed understates the pace and must not be used
    ///
    /// For a non-monotonic clip the replacement is the MEDIAN instantaneous speed over frames where
    /// the character is actually moving. Median rather than mean so that the pauses and turnarounds
    /// a pacing animation contains do not drag the figure down the same way the net cancellation did.
    ///
    ///     Unity -batchmode -quit -executeMethod SEAN.AutoTrial.EditorTools.S45MonotonicityProbe.Dump
    /// </summary>
    public static class S45MonotonicityProbe
    {
        private const float SampleHz = 60f;
        // Below this, the root reverses enough that net displacement is not the pace.
        public const float MonotonicRatioThreshold = 0.7f;
        // Frames slower than this are pauses/turnarounds, excluded from the median.
        private const float MovingThresholdMps = 0.05f;

        [MenuItem("AutoTrial/Session 45/Dump clip monotonicity")]
        public static void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("clip\tasset\tlen_s\tavgSpeed\tpathSpeed\tnet/path\tmedianMoving\tverdict\tsuggestedRef");

            foreach (var guid in AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets/PedestrianAssets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    var clip = obj as AnimationClip;
                    if (clip == null || clip.name.StartsWith("__preview__")) { continue; }

                    List<Vector3> track = SampleRootTrack(clip);
                    if (track.Count < 3)
                    {
                        sb.AppendFormat("{0}\t{1}\tNO ROOT CURVES\n", clip.name, System.IO.Path.GetFileName(path));
                        continue;
                    }

                    float dt = 1f / SampleHz;
                    float net = new Vector2(track[track.Count - 1].x - track[0].x,
                                            track[track.Count - 1].z - track[0].z).magnitude;
                    float pathLen = 0f;
                    var speeds = new List<float>();
                    for (int i = 1; i < track.Count; i++)
                    {
                        float step = new Vector2(track[i].x - track[i - 1].x, track[i].z - track[i - 1].z).magnitude;
                        pathLen += step;
                        speeds.Add(step / dt);
                    }

                    float avgSpeed = clip.length > 0 ? net / clip.length : 0f;
                    float pathSpeed = clip.length > 0 ? pathLen / clip.length : 0f;
                    float ratio = pathLen > 1e-5f ? net / pathLen : 1f;

                    var moving = speeds.FindAll(s => s > MovingThresholdMps);
                    moving.Sort();
                    float medianMoving = moving.Count > 0 ? moving[moving.Count / 2] : 0f;

                    bool inPlace = pathSpeed < MovingThresholdMps;
                    string verdict, suggested;
                    if (inPlace)
                    {
                        verdict = "IN_PLACE";
                        suggested = "n/a";
                    }
                    else if (ratio < MonotonicRatioThreshold)
                    {
                        verdict = "NON_MONOTONIC";
                        suggested = medianMoving.ToString("F4", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        verdict = "monotonic";
                        suggested = avgSpeed.ToString("F4", CultureInfo.InvariantCulture);
                    }

                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "{0}\t{1}\t{2:F3}\t{3:F4}\t{4:F4}\t{5:F3}\t{6:F4}\t{7}\t{8}\n",
                        clip.name, System.IO.Path.GetFileName(path), clip.length,
                        avgSpeed, pathSpeed, ratio, medianMoving, verdict, suggested);
                }
            }
            Debug.Log("[S45Mono]\n" + sb);
        }

        /// <summary>
        /// Root translation over the clip, read from its RootT curves. Humanoid clips store root
        /// motion as RootT.x/y/z rather than on a Transform, so this needs no scene instance -- and
        /// therefore sidesteps the avatar's "Optimize GameObjects" limitation entirely, which is
        /// what defeated the Session 44 pose probe.
        /// </summary>
        private static List<Vector3> SampleRootTrack(AnimationClip clip)
        {
            AnimationCurve cx = null, cy = null, cz = null;
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                if (b.propertyName == "RootT.x") { cx = AnimationUtility.GetEditorCurve(clip, b); }
                else if (b.propertyName == "RootT.y") { cy = AnimationUtility.GetEditorCurve(clip, b); }
                else if (b.propertyName == "RootT.z") { cz = AnimationUtility.GetEditorCurve(clip, b); }
            }
            var track = new List<Vector3>();
            if (cx == null || cz == null) { return track; }
            int n = Mathf.Max(Mathf.CeilToInt(clip.length * SampleHz), 2);
            for (int i = 0; i <= n; i++)
            {
                float t = clip.length * i / n;
                track.Add(new Vector3(cx.Evaluate(t), cy != null ? cy.Evaluate(t) : 0f, cz.Evaluate(t)));
            }
            return track;
        }
    }
}
