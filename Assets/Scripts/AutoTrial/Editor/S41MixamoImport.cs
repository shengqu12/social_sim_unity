using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 41 TASK 3: configures the nine Mixamo FBXs under Assets/PedestrianAssets/Mixamo/
    /// as Humanoid rigs, then asserts the result.
    ///
    /// On the ticket's TASK 3.0 concern: FixRocketboxMaxImport already carries a sticky guard
    /// (`if (importer.animationType != ModelImporterAnimationType.Human)`) so it will not stomp a
    /// Humanoid rig back to Generic -- the ticket's "unconditionally forces Generic" premise is
    /// stale. The independent directory is used anyway, per the ticket's own stated preference for
    /// the safer of its two options, so nothing here depends on that shared external-asset file
    /// staying the way it is. That file is NOT modified.
    ///
    /// Root motion policy follows the ticket: MOVING clips keep root motion (they must translate);
    /// STATIONARY clips have it stripped, because a stationary character with live root motion
    /// drifts off its spawn point over a 90s trial (the "idle translation" bug).
    ///
    /// -executeMethod SEAN.AutoTrial.S41MixamoImport.Apply   (configure + verify)
    /// -executeMethod SEAN.AutoTrial.S41MixamoImport.Verify  (assert only, changes nothing)
    /// </summary>
    public static class S41MixamoImport
    {
        private const string Dir = "Assets/PedestrianAssets/Mixamo";

        // Clips that must translate through the world. These need root motion, and Base.cs adds
        // its own RootMotionSink automatically when the resolved Animator sits on a nested child.
        private static readonly HashSet<string> Moving = new HashSet<string>
        {
            "Pacing And Talking On A Phone",
            "carry_and_walk",
            "Old Man Walk",
            "Drunk Walk",
            "Running",
        };

        // Clips that must hold station. Root motion off, or they slide.
        //
        // Session 44 (5.1 / 0): "Talking_standing" and "Stroke Shaking Head" remain listed here so
        // that the FBXs still import correctly if anyone opens them, but neither ships. Both are
        // off the roster:
        //   Talking_standing    -- dropped on request (5.1)
        //   Stroke Shaking Head -- permanently excluded, see
        //                          known_issues/S44_stroke_shaking_head_excluded.md
        private static readonly HashSet<string> Stationary = new HashSet<string>
        {
            "Standing Arguing",
            "Talking_standing",
            "Sitting",
            "Stroke Shaking Head",
        };

        public static void Apply() { Run(true); }
        public static void Verify() { Run(false); }

        private static void Run(bool write)
        {
            string[] paths = Directory.GetFiles(Dir, "*.fbx")
                                      .Select(p => p.Replace('\\', '/'))
                                      .OrderBy(p => p).ToArray();
            if (paths.Length == 0)
            {
                Debug.LogError("[S41Mixamo] no FBX found under " + Dir);
                EditorApplication.Exit(1);
                return;
            }

            if (write)
            {
                foreach (string path in paths)
                {
                    var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (importer == null)
                    {
                        Debug.LogError("[S41Mixamo] no ModelImporter for " + path);
                        continue;
                    }
                    string name = Path.GetFileNameWithoutExtension(path);
                    bool isMoving = Moving.Contains(name);
                    bool isStationary = Stationary.Contains(name);
                    if (!isMoving && !isStationary)
                    {
                        Debug.LogError("[S41Mixamo] '" + name + "' is in neither the moving nor the "
                            + "stationary list -- refusing to guess its root-motion policy.");
                        EditorApplication.Exit(1);
                        return;
                    }

                    importer.animationType = ModelImporterAnimationType.Human;
                    importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                    importer.importAnimation = true;
                    // Mixamo FBXs declare their own unit scale in the file; useFileScale keeps
                    // Unity honouring it rather than layering an extra 0.01 on top. The resulting
                    // character height is asserted below rather than assumed.
                    importer.useFileScale = true;
                    importer.globalScale = 1.0f;

                    // Loop every clip. Locomotion must loop to walk continuously; the stationary
                    // ones loop too so a 90s trial doesn't leave them frozen on a final pose after
                    // a few seconds (the ticket asks for this explicitly).
                    var clips = importer.defaultClipAnimations;
                    if (clips != null && clips.Length > 0)
                    {
                        for (int i = 0; i < clips.Length; i++)
                        {
                            clips[i].loopTime = true;
                            // Session 45 (1.3), the single sanctioned attempt on Running's legs.
                            //
                            // NOT the route the work order suggested. That route -- UpperLeg/
                            // LowerLeg mapping and muscle rotation limits -- lives on the
                            // DESTINATION avatar, which is the shared Rocketbox prefab. Editing it
                            // is a red line and would alter every trial ever run with that avatar.
                            //
                            // In-bounds hypothesis instead, on the clip's own import settings:
                            // Running is a 0.700s single stride played on loop, and loopTime alone
                            // loops the TIME without matching the pose at the seam. A run cycle has
                            // the legs at opposite extremes, so a mismatched seam snaps them past
                            // each other once per 0.7s -- which is what "the legs cross" describes.
                            // loopPose blends the cycle closed.
                            //
                            // Applied ONLY to Running. Old_Man_Walk and Drunk_Walk passed the
                            // Session 44 eyeball pass and must not be disturbed.
                            if (name == "Running") { clips[i].loopPose = true; }
                            // Stationary clips: bake the root's motion into the pose so the
                            // character animates in place instead of translating away.
                            clips[i].lockRootPositionXZ = isStationary;
                            clips[i].lockRootHeightY = isStationary;
                            clips[i].lockRootRotation = isStationary;
                        }
                        importer.clipAnimations = clips;
                    }

                    EditorUtility.SetDirty(importer);
                    importer.SaveAndReimport();
                    Debug.Log(string.Format("[S41Mixamo] configured '{0}' class={1} clips={2}",
                        name, isMoving ? "MOVING" : "STATIONARY", clips != null ? clips.Length : 0));
                }
                AssetDatabase.Refresh();
            }

            // ---- THE HARD GATE: every asset must come back as a real Humanoid rig ----
            Debug.Log("[S41Mixamo] ==== isHuman ASSERTION TABLE (" + paths.Length + " assets) ====");
            int failures = 0;
            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null)
                {
                    Debug.LogError("[S41Mixamo] ROW name='" + name + "' FAILED: asset did not load");
                    failures++;
                    continue;
                }
                var animator = go.GetComponentInChildren<Animator>(true);
                bool hasAnimator = animator != null;
                bool hasAvatar = hasAnimator && animator.avatar != null;
                bool isHuman = hasAvatar && animator.avatar.isHuman;
                bool avatarValid = hasAvatar && animator.avatar.isValid;

                // Height, as the ticket's scale check: a correctly-scaled human should be ~1.7m.
                var renderers = go.GetComponentsInChildren<Renderer>(true);
                float height = -1f;
                if (renderers.Length > 0)
                {
                    Bounds b = renderers[0].bounds;
                    foreach (var r in renderers) { b.Encapsulate(r.bounds); }
                    height = b.size.y;
                }

                var clipAssets = AssetDatabase.LoadAllAssetsAtPath(path)
                                              .OfType<AnimationClip>()
                                              .Where(c => !c.name.StartsWith("__preview__")).ToArray();
                string clipDesc = clipAssets.Length == 0 ? "NONE"
                    : string.Join("; ", clipAssets.Select(c => string.Format(
                        "{0} len={1:F2}s fps={2:F0} loop={3} rootMotion={4}",
                        c.name, c.length, c.frameRate, c.isLooping, c.hasRootCurves || c.hasMotionCurves)));

                bool ok = isHuman && avatarValid;
                if (!ok) { failures++; }
                Debug.Log(string.Format(
                    "[S41Mixamo] ROW name='{0}' class={1} isHuman={2} avatarValid={3} height={4:F2}m clips=[{5}] -> {6}",
                    name, Moving.Contains(name) ? "MOVING" : (Stationary.Contains(name) ? "STATIONARY" : "UNCLASSIFIED"),
                    isHuman, avatarValid, height, clipDesc, ok ? "PASS" : "FAIL"));
            }

            Debug.Log("[S41Mixamo] ==== RESULT: " + (paths.Length - failures) + "/" + paths.Length + " PASS ====");
            if (failures > 0)
            {
                Debug.LogError("[S41Mixamo] HARD GATE FAILED: " + failures
                    + " asset(s) are not valid Humanoid rigs -- downstream tasks must stop here.");
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }
    }
}
