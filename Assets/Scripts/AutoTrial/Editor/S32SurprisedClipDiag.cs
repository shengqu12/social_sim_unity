using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 32 FIX B2 diagnostic (fault-tree branch (a)): static inspection of the
    /// Pointing_towards clip currently wired into SurprisedReaction (S31 FIX 6a) -- does the
    /// imported clip actually carry arm/shoulder muscle curve data, or did the retarget silently
    /// drop it? Read-only, no asset changes. -executeMethod SEAN.AutoTrial.S32SurprisedClipDiag.Run
    /// </summary>
    public static class S32SurprisedClipDiag
    {
        private const string ClipPath = "Assets/CustomAnimations/S31Mixamo/Pointing_towards.fbx";

        public static void Run()
        {
            var clip = AssetDatabase.LoadAllAssetsAtPath(ClipPath)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .OrderByDescending(c => c.length)
                .FirstOrDefault();
            if (clip == null)
            {
                Debug.LogError("[S32SurprisedClipDiag] no clip found at " + ClipPath);
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("[S32SurprisedClipDiag] clip=" + clip.name + " length=" + clip.length
                + " isHumanMotion=" + clip.humanMotion + " legacy=" + clip.legacy);

            var bindings = AnimationUtility.GetCurveBindings(clip);
            Debug.Log("[S32SurprisedClipDiag] total curve bindings: " + bindings.Length);

            int armLike = 0;
            foreach (var b in bindings)
            {
                bool isArmLike = b.propertyName.ToLower().Contains("arm")
                    || b.propertyName.ToLower().Contains("shoulder")
                    || b.propertyName.ToLower().Contains("elbow")
                    || b.propertyName.ToLower().Contains("hand")
                    || b.propertyName.ToLower().Contains("spine")
                    || b.propertyName.ToLower().Contains("head");
                if (isArmLike)
                {
                    armLike++;
                    var curve = AnimationUtility.GetEditorCurve(clip, b);
                    float rangeMin = float.MaxValue, rangeMax = float.MinValue;
                    if (curve != null)
                    {
                        foreach (var key in curve.keys)
                        {
                            if (key.value < rangeMin) rangeMin = key.value;
                            if (key.value > rangeMax) rangeMax = key.value;
                        }
                    }
                    Debug.Log("[S32SurprisedClipDiag] curve '" + b.propertyName + "' range=["
                        + rangeMin.ToString("F4") + ", " + rangeMax.ToString("F4") + "] variation="
                        + (rangeMax - rangeMin).ToString("F4"));
                }
            }
            Debug.Log("[S32SurprisedClipDiag] arm/shoulder/elbow/hand/spine/head-like curves found: " + armLike
                + " / " + bindings.Length + " total");

            // Also list ALL binding property names once, so we don't miss unexpected muscle naming.
            var names = bindings.Select(b => b.propertyName).Distinct().OrderBy(n => n).ToArray();
            Debug.Log("[S32SurprisedClipDiag] all distinct property names: " + string.Join(" | ", names));

            EditorApplication.Exit(0);
        }
    }
}
