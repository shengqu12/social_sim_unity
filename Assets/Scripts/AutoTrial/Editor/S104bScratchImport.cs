using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 104b. Brings the six S104 candidate clips into the project as SCRATCH so they can be
    /// measured in engine, which is where S104b's hard gate now lives (S103 showed the source-side
    /// ordering inverts, so gating the input was aiming the ruler at the wrong end).
    ///
    /// SCRATCH, NOT PROMOTION. Everything lands under Assets/PedestrianAssets/Kimodo/S104b/, which
    /// is gitignored. Nothing is appended to clip_speeds, no shipped controller is regenerated, and
    /// the shipped assets are not touched. Promotion is a separate, later act that needs Sheng's
    /// semantic review first.
    ///
    /// The controllers are named `kimodo_s104b_*` deliberately: S80's scoping predicate is
    /// `name.StartsWith("kimodo_")`, so a candidate named anything else would fall OUT of scope for
    /// the S79 gait override and the S83/S89 layers, and would be measured on a different code path
    /// from the clips it is meant to replace.
    ///
    /// Reference: the canonical file from S104 Phase 0, not a clip donor. That decoupling is what
    /// makes this safe -- importing a new walk clip can no longer perturb anyone else's reference.
    ///
    /// -executeMethod SEAN.AutoTrial.S104bScratchImport.Run
    /// </summary>
    public static class S104bScratchImport
    {
        private const string Src = "/mnt/ssd/Social_Navigation/sandbox_s72_nextgen/01_kimodo_out";
        private const string Dir = "Assets/PedestrianAssets/Kimodo/S104b";
        private const string ResDir = Dir + "/Resources";

        public static readonly string[] Tags =
        {
            "s104_r1_seed42", "s104_r1_seed1042", "s104_r1_seed2042",
            "s104_r2_seed42", "s104_r2_seed1042", "s104_r2_seed2042",
            "s104_r2_seed3042", "s104_r2_seed4042",     // STEP 3 seeds, imported if present
        };

        public static void Run()
        {
            if (!AssetDatabase.IsValidFolder(Dir)) { AssetDatabase.CreateFolder("Assets/PedestrianAssets/Kimodo", "S104b"); }
            if (!AssetDatabase.IsValidFolder(ResDir)) { AssetDatabase.CreateFolder(Dir, "Resources"); }

            var canonical = S104CanonicalReference.TryLoad();
            if (canonical == null) { Debug.LogError("[S104b] canonical reference missing -- refusing to guess a pose."); return; }

            var sb = new StringBuilder();
            foreach (string tag in Tags)
            {
                string src = string.Format("{0}/{1}/{1}.fbx", Src, tag);
                if (!File.Exists(src)) { sb.AppendLine("[S104b] absent, skipped: " + tag); continue; }
                string dst = Dir + "/" + tag + ".fbx";
                File.Copy(src, dst, true);
                AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);

                var imp = AssetImporter.GetAtPath(dst) as ModelImporter;
                if (imp == null) { sb.AppendLine("[S104b] no importer for " + tag); continue; }

                // Pass 1 Generic: the SkeletonBone array must name the bones of the actual rig.
                imp.animationType = ModelImporterAnimationType.Generic;
                imp.SaveAndReimport();
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(dst);
                if (root == null) { sb.AppendLine("[S104b] could not load " + tag); continue; }
                var skel = root.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone
                {
                    name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale,
                }).ToArray();

                int taken = 0;
                for (int i = 0; i < skel.Length; i++)
                {
                    SkeletonBone c;
                    if (!canonical.TryGetValue(skel[i].name, out c)) { continue; }
                    skel[i].position = c.position; skel[i].rotation = c.rotation; skel[i].scale = c.scale;
                    taken++;
                }

                var human = new List<HumanBone>();
                for (int i = 0; i < S86KimodoAvatarRefPose.BoneMapPublic.GetLength(0); i++)
                {
                    var hb = new HumanBone
                    {
                        humanName = S86KimodoAvatarRefPose.BoneMapPublic[i, 0],
                        boneName = S86KimodoAvatarRefPose.BoneMapPublic[i, 1],
                    };
                    hb.limit.useDefaultValues = true;
                    human.Add(hb);
                }

                var hd = imp.humanDescription;
                hd.human = human.ToArray();
                hd.skeleton = skel;
                imp.humanDescription = hd;
                imp.animationType = ModelImporterAnimationType.Human;
                imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

                // These ARE gaits: they must loop. S86's Configure force-clears loopTime because it
                // was written for one-shot reactions -- doing that here would break the walk cycle.
                var clips = imp.defaultClipAnimations;
                if (clips != null && clips.Length > 0)
                {
                    for (int i = 0; i < clips.Length; i++) { clips[i].loopTime = true; clips[i].loop = true; }
                    imp.clipAnimations = clips;
                }
                imp.SaveAndReimport();

                var clip = AssetDatabase.LoadAllAssetsAtPath(dst).OfType<AnimationClip>()
                    .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
                if (clip == null) { sb.AppendLine("[S104b] no clip sub-asset in " + tag); continue; }

                string ctrlName = "kimodo_s104b_" + tag.Replace("s104_", "");
                string ctrlPath = ResDir + "/" + ctrlName + ".controller";
                var controller = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
                var sm = controller.layers[0].stateMachine;
                var st = sm.AddState(ctrlName);
                st.motion = clip; st.speed = 1.0f; st.iKOnFeet = true;   // matches S41MixamoControllerGen
                sm.defaultState = st;
                EditorUtility.SetDirty(controller);

                sb.AppendLine(string.Format(
                    "[S104b] {0}: reference {1}/{2} bones, clip '{3}' {4:F2}s loop={5} -> {6}",
                    tag, taken, skel.Length, clip.name, clip.length, clip.isLooping, ctrlName));
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log(sb.ToString());
        }
    }
}
