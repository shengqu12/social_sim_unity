using System;
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
    /// Session 106. Two entry points, one import path -- the S104b path, unchanged:
    /// Humanoid configured against the CANONICAL reference (S104 Phase 0 rig data, never a clip
    /// donor), the 21-slot S86 bone map, loopTime kept true because these are gaits, and one
    /// single-state controller per clip with iKOnFeet = true exactly as S41MixamoControllerGen
    /// writes it. Controllers are named with the clip so S80's `StartsWith("kimodo_")` predicate
    /// keeps them on the in-scope code path.
    ///
    /// PROMOTE: the shipping act. Copies ONE source FBX to Assets/PedestrianAssets/Kimodo/NAME.fbx
    /// and writes Kimodo/Resources/NAME.controller. Driven by two env vars so the same code
    /// promotes the relaxed winner now and an elderly winner later without an edit:
    ///     S106_PROMOTE_SRC=/abs/path/candidate.fbx   S106_PROMOTE_NAME=kimodo_relaxed_walk
    /// Refuses to run if the destination FBX already exists -- retiring the previous asset
    /// (git mv to a provenance name) is a deliberate, separate step, never a silent overwrite.
    ///
    /// SCRATCH: measurement only, gitignored, mirrors S104bScratchImport for the S106 elderly
    /// candidates.  S106_SCRATCH_TAGS="s106_e1_seed42 s106_e1_seed1042 ..." -> Kimodo/S106/ with
    /// kimodo_s106_* controllers.
    ///
    /// Both exit the editor with 0 on success and 1 on any refusal, so run_trial.py's
    /// --exec-editor-method reports the outcome honestly.
    ///
    /// -executeMethod SEAN.AutoTrial.S106KimodoImport.Promote
    /// -executeMethod SEAN.AutoTrial.S106KimodoImport.Scratch
    /// </summary>
    public static class S106KimodoImport
    {
        private const string KimodoDir = "Assets/PedestrianAssets/Kimodo";
        private const string ScratchSrc = "/mnt/ssd/Social_Navigation/sandbox_s72_nextgen/01_kimodo_out";
        private const string ScratchDir = KimodoDir + "/S106";

        public static void Promote()
        {
            string src = System.Environment.GetEnvironmentVariable("S106_PROMOTE_SRC");
            string name = System.Environment.GetEnvironmentVariable("S106_PROMOTE_NAME");
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(name) || !File.Exists(src))
            {
                Debug.LogError("[S106] Promote needs S106_PROMOTE_SRC (existing file) and S106_PROMOTE_NAME; got src='"
                    + src + "' name='" + name + "'");
                EditorApplication.Exit(1); return;
            }
            if (!name.StartsWith("kimodo_"))
            {
                Debug.LogError("[S106] refusing: '" + name + "' is not a kimodo_* name -- it would fall out of S80's scope.");
                EditorApplication.Exit(1); return;
            }
            string dst = KimodoDir + "/" + name + ".fbx";
            string ctrl = KimodoDir + "/Resources/" + name + ".controller";
            if (File.Exists(dst) || File.Exists(ctrl))
            {
                Debug.LogError("[S106] refusing: " + dst + " or " + ctrl + " already exists. Retire the previous asset first (git mv), then re-run.");
                EditorApplication.Exit(1); return;
            }
            var canonical = S104CanonicalReference.TryLoad();
            if (canonical == null) { Debug.LogError("[S106] canonical reference missing -- refusing to guess a pose."); EditorApplication.Exit(1); return; }
            if (!AssetDatabase.IsValidFolder(KimodoDir + "/Resources")) { AssetDatabase.CreateFolder(KimodoDir, "Resources"); }

            var sb = new StringBuilder();
            bool ok = ImportOne(src, dst, ctrl, name, canonical, sb);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[S106] PROMOTE " + (ok ? "OK" : "FAILED") + "\n" + sb);
            EditorApplication.Exit(ok ? 0 : 1);
        }

        public static void Scratch()
        {
            string tags = System.Environment.GetEnvironmentVariable("S106_SCRATCH_TAGS") ?? "";
            var list = tags.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (list.Length == 0) { Debug.LogError("[S106] Scratch needs S106_SCRATCH_TAGS"); EditorApplication.Exit(1); return; }
            var canonical = S104CanonicalReference.TryLoad();
            if (canonical == null) { Debug.LogError("[S106] canonical reference missing -- refusing to guess a pose."); EditorApplication.Exit(1); return; }
            if (!AssetDatabase.IsValidFolder(ScratchDir)) { AssetDatabase.CreateFolder(KimodoDir, "S106"); }
            if (!AssetDatabase.IsValidFolder(ScratchDir + "/Resources")) { AssetDatabase.CreateFolder(ScratchDir, "Resources"); }

            var sb = new StringBuilder();
            bool all = true;
            foreach (string tag in list)
            {
                string src = string.Format("{0}/{1}/{1}.fbx", ScratchSrc, tag);
                if (!File.Exists(src)) { sb.AppendLine("[S106] absent, skipped: " + tag); all = false; continue; }
                string ctrlName = "kimodo_s106_" + tag.Replace("s106_", "");
                all &= ImportOne(src, ScratchDir + "/" + tag + ".fbx", ScratchDir + "/Resources/" + ctrlName + ".controller",
                                 ctrlName, canonical, sb);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[S106] SCRATCH " + (all ? "OK" : "INCOMPLETE") + "\n" + sb);
            EditorApplication.Exit(all ? 0 : 1);
        }

        /// The S104b import path, verbatim in behaviour: Generic pass to read the rig's own
        /// SkeletonBone array, canonical reference substituted bone-for-bone, S86's explicit human
        /// map, Humanoid + CreateFromThisModel, loopTime kept, then the S41-style controller.
        private static bool ImportOne(string src, string dst, string ctrlPath, string ctrlName,
                                      Dictionary<string, SkeletonBone> canonical, StringBuilder sb)
        {
            File.Copy(src, dst, true);
            AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceUpdate);
            var imp = AssetImporter.GetAtPath(dst) as ModelImporter;
            if (imp == null) { sb.AppendLine("[S106] no importer for " + dst); return false; }

            imp.animationType = ModelImporterAnimationType.Generic;
            imp.SaveAndReimport();
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(dst);
            if (root == null) { sb.AppendLine("[S106] could not load " + dst); return false; }
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
            var clips = imp.defaultClipAnimations;
            if (clips != null && clips.Length > 0)
            {
                for (int i = 0; i < clips.Length; i++) { clips[i].loopTime = true; clips[i].loop = true; }
                imp.clipAnimations = clips;
            }
            imp.SaveAndReimport();

            // The five trap slots, asserted against the built avatar (S83's check).
            var avatar = AssetDatabase.LoadAllAssetsAtPath(dst).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                sb.AppendLine("[S106] avatar invalid/non-human for " + dst); return false;
            }
            var hb2 = avatar.humanDescription.human.ToDictionary(h => h.humanName, h => h.boneName);
            string[,] traps = { { "LeftUpperLeg", "LeftLeg" }, { "RightUpperLeg", "RightLeg" },
                                { "LeftLowerLeg", "LeftShin" }, { "RightLowerLeg", "RightShin" }, { "Chest", "Spine2" } };
            for (int i = 0; i < traps.GetLength(0); i++)
            {
                string got; hb2.TryGetValue(traps[i, 0], out got);
                if (got != traps[i, 1]) { sb.AppendLine("[S106] TRAP slot " + traps[i, 0] + " = '" + got + "', expected " + traps[i, 1]); return false; }
            }

            var clip = AssetDatabase.LoadAllAssetsAtPath(dst).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (clip == null) { sb.AppendLine("[S106] no clip sub-asset in " + dst); return false; }

            var controller = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            var sm = controller.layers[0].stateMachine;
            var st = sm.AddState(ctrlName);
            st.motion = clip; st.speed = 1.0f; st.iKOnFeet = true;   // as S41MixamoControllerGen
            sm.defaultState = st;
            EditorUtility.SetDirty(controller);

            sb.AppendLine(string.Format(
                "[S106] {0}: reference {1}/{2} bones, traps ok, clip '{3}' {4:F4}s loop={5} avgSpeed={6:F4} -> {7}",
                Path.GetFileName(dst), taken, skel.Length, clip.name, clip.length, clip.isLooping,
                clip.averageSpeed.magnitude, ctrlPath));
            return true;
        }
    }
}
