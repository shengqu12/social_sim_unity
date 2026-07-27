using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial.EditorTools
{
    /// <summary>
    /// Session 44 FIX B: make the winner of the `animator.speed` race deterministic.
    ///
    /// Two components write `animator.speed` every frame, with incompatible semantics:
    ///
    ///   Scenario.Agents.Base.Move()      animator.speed = velocity.magnitude
    ///                                    -- a raw m/s figure used directly as a playback rate
    ///   S32AnimatorSpeedScaler.Update()  animator.speed = clamp(smoothed / referenceSpeedMps, ...)
    ///                                    -- a dimensionless ratio
    ///
    /// Both live in Update() and neither declared an execution order, so the surviving value was
    /// whichever happened to run last. Session 44 TASK 1 measured the scaler winning 97.6% of
    /// frames (55,303 of 56,687) with Base winning 24 -- so today's behaviour is the intended one,
    /// but by accident. If that order ever flips (different build, a component added or removed,
    /// another platform) `animator.speed` silently becomes a speed MAGNITUDE (~1.3) instead of a
    /// normalised ratio, which would be a severe and very hard-to-diagnose regression.
    ///
    /// Setting the scaler to a positive order puts it after every default-order (0) script,
    /// including Base, without touching Base.cs -- which is upstream and off-limits. Base keeps its
    /// default order; only the scaler is moved, so this cannot perturb the relative order of any
    /// other pair of scripts.
    ///
    /// Deliberately NOT solved by moving the scaler to LateUpdate: that would delay its write by a
    /// frame relative to the animation evaluation, ~67ms at the capture cadence.
    ///
    /// Writes ProjectSettings/MonoManager.asset via MonoImporter, the same "use the typed
    /// serialization API, never hand-edit the YAML" discipline this project applies to prefabs and
    /// animator controllers. ProjectSettings/ is shared -- see MERGE_NOTES_ped-behavior-v2.md.
    ///
    ///     Unity -batchmode -quit -executeMethod SEAN.AutoTrial.EditorTools.S44ExecutionOrder.Apply
    /// </summary>
    public static class S44ExecutionOrder
    {
        // Comfortably after default-order (0) scripts, and clear of Unity's own reserved bands.
        private const int ScalerOrder = 100;

        [MenuItem("AutoTrial/Session 44/Apply script execution order")]
        public static void Apply()
        {
            var target = FindScript("S32AnimatorSpeedScaler");
            if (target == null)
            {
                Debug.LogError("[S44ExecutionOrder] S32AnimatorSpeedScaler.cs not found -- nothing applied.");
                EditorApplication.Exit(1);
                return;
            }

            int before = MonoImporter.GetExecutionOrder(target);
            if (before != ScalerOrder)
            {
                MonoImporter.SetExecutionOrder(target, ScalerOrder);
            }
            int after = MonoImporter.GetExecutionOrder(target);

            var baseScript = FindScript("Base");
            int baseOrder = baseScript != null ? MonoImporter.GetExecutionOrder(baseScript) : -9999;

            Debug.Log("[S44ExecutionOrder] S32AnimatorSpeedScaler: " + before + " -> " + after
                + "  (Scenario.Agents.Base left at its default order " + baseOrder + ")");

            AssetDatabase.SaveAssets();
            if (after != ScalerOrder)
            {
                Debug.LogError("[S44ExecutionOrder] execution order did not stick.");
                EditorApplication.Exit(1);
            }
        }

        private static MonoScript FindScript(string className)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:MonoScript " + className))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                if (ms != null && ms.GetClass() != null && ms.GetClass().Name == className)
                {
                    return ms;
                }
            }
            return null;
        }
    }
}
