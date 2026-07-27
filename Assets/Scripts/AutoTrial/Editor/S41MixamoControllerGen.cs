using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 41 TASK 3/6: generates one single-state AnimatorController per Mixamo clip, under
    /// Assets/PedestrianAssets/Mixamo/Controllers/.
    ///
    /// Necessary because S41MixamoContentProbe established that all nine FBXs are Mixamo
    /// ANIMATION-ONLY exports -- 0 meshes, 0 materials, a bare 66-transform `mixamorig4:` skeleton.
    /// They are not characters and cannot be spawned as pedestrians. The established pattern in
    /// this repo for such a file (Session 31, point_backwards.fbx / Pointing_towards.fbx) is to
    /// pull the AnimationClip sub-asset out and let Unity's Humanoid retargeting play it on an
    /// existing Rocketbox avatar. These controllers are the per-clip vehicle for that.
    ///
    /// Deliberately single-state rather than an AnimatorOverrideController over
    /// SocialForcesAnimatorController: that controller's locomotion is a Blend Tree driven by
    /// Forward/Turn, and these are behaviour clips (sitting, arguing, a drunk gait), not
    /// drop-in replacements for a locomotion blend. A single looping state is also exactly the
    /// right instrument for TASK 6.1's screen, which asks whether the clip plays and translates
    /// at all -- no blend-tree parameter plumbing in between to confound the answer.
    ///
    /// Everything is written into the new PedestrianAssets tree; no existing controller, prefab or
    /// scene is touched.
    ///
    /// -executeMethod SEAN.AutoTrial.S41MixamoControllerGen.Generate
    /// </summary>
    public static class S41MixamoControllerGen
    {
        private const string SrcDir = "Assets/PedestrianAssets/Mixamo";
        // Under a "Resources" folder so AutoTrialBootstrap can Resources.Load these by name at
        // runtime -- the same mechanism ZoneBContainers already uses for its prefabs.
        private const string OutDir = "Assets/PedestrianAssets/Mixamo/Resources";

        public static void Generate()
        {
            if (!AssetDatabase.IsValidFolder(OutDir))
            {
                AssetDatabase.CreateFolder(SrcDir, "Resources");
            }

            int made = 0;
            foreach (string path in Directory.GetFiles(SrcDir, "*.fbx").OrderBy(p => p))
            {
                string src = path.Replace('\\', '/');
                string name = Path.GetFileNameWithoutExtension(src);

                AnimationClip clip = AssetDatabase.LoadAllAssetsAtPath(src)
                    .OfType<AnimationClip>()
                    .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
                if (clip == null)
                {
                    Debug.LogError("[S41CtrlGen] no AnimationClip sub-asset in " + src);
                    EditorApplication.Exit(1);
                    return;
                }

                string outPath = OutDir + "/" + Sanitize(name) + ".controller";
                var controller = AnimatorController.CreateAnimatorControllerAtPath(outPath);
                var sm = controller.layers[0].stateMachine;
                var state = sm.AddState(Sanitize(name));
                state.motion = clip;
                state.speed = 1.0f;
                // Session 45 (1.2), attempt 1 of 2 on carry_and_walk's left-leg pose. Foot IK
                // pins the retargeted feet to the ground plane, which is the cheapest thing that
                // could account for an ankle reading wrong. Applied to every generated Mixamo
                // state, not just that one: these are all retargeted humanoid clips on an avatar
                // with different proportions from the source rig, and foot placement error is the
                // generic consequence.
                //
                // Note this is a POSE-layer change. carry_and_walk passes 3.2 at its clamp floor
                // of 1.1%, so its playback rate is already correct and no speed parameter is
                // touched here.
                state.iKOnFeet = true;
                sm.defaultState = state;

                EditorUtility.SetDirty(controller);
                made++;
                Debug.Log(string.Format("[S41CtrlGen] '{0}' -> {1} (clip '{2}' {3:F2}s loop={4})",
                    name, outPath, clip.name, clip.length, clip.isLooping));
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[S41CtrlGen] generated " + made + " controller(s) in " + OutDir);
            EditorApplication.Exit(made > 0 ? 0 : 1);
        }

        // Resources.Load and the CLI both go through this name, so spaces are the one thing that
        // has to go; case is preserved so the generated file still reads like its source clip.
        public static string Sanitize(string n)
        {
            return n.Replace(" ", "_");
        }
    }
}
