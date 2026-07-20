using System.IO;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 21 STEP 1 -- one-off fix, run once via the guarded launcher then left in the repo
    /// for provenance (matches this project's convention of keeping session-numbered fix history
    /// rather than deleting one-off scripts). Executes the phone_user container-rewiring decision
    /// that RECORDING.md / AutoTrialBootstrap.ZoneBContainers had flagged PENDING VERIFICATION
    /// since pre-flight A (2026-07-15).
    ///
    /// Diagnosis (read-only recon before writing anything): the canonical
    /// PhoneUserContainer.prefab's AppearanceAvatar already had the correct animationController
    /// (PhoneUser_TextingController) but the wrong avatars[0] (the old Community-informed-Model
    /// Phone_User.prefab). A scratch duplicate, "PhoneUserContainer 1.prefab", had the right
    /// avatar (PhoneUser_Ped.prefab) wired up but a different, wrong animationController
    /// (BaseSFControllerNormalized) -- an abandoned in-Editor experiment, not a second real
    /// container. Fix: point the canonical prefab's avatars[0] at PhoneUser_Ped.prefab (the
    /// duplicate's one correct field), re-affirm animationController explicitly rather than
    /// trust it was already right, then move the now-redundant duplicate out of any Resources/
    /// folder so Resources.Load can never resolve it by accident -- kept, not deleted, for
    /// provenance of what the abandoned experiment looked like.
    ///
    /// -executeMethod SEAN.AutoTrial.S21PhoneUserContainerFix.Apply
    /// </summary>
    public static class S21PhoneUserContainerFix
    {
        private const string ContainerPath = "Assets/Resources/Prefabs/PedetrainAvatars/PhoneUserContainer.prefab";
        private const string DuplicatePath = "Assets/Resources/Prefabs/PedetrainAvatars/PhoneUserContainer 1.prefab";
        private const string DuplicateArchiveDir = "Assets/ArchivedPrefabs/PedetrainAvatars";
        private const string DuplicateArchivePath = DuplicateArchiveDir + "/PhoneUserContainer 1.prefab";
        private const string AvatarPath = "Assets/Resources/Prefabs/PedetrainAvatars/PhoneUser_Ped.prefab";
        private const string ControllerPath = "Assets/CustomAnimations/PhoneUser_TextingController.controller";

        public static void Apply()
        {
            bool ok = ApplyInternal();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool ApplyInternal()
        {
            GameObject containerAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ContainerPath);
            if (containerAsset == null)
            {
                Debug.LogError("[S21PhoneUserContainerFix] Could not load container prefab at " + ContainerPath);
                return false;
            }

            GameObject avatarPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPath);
            if (avatarPrefab == null)
            {
                Debug.LogError("[S21PhoneUserContainerFix] Could not load avatar prefab at " + AvatarPath);
                return false;
            }

            RuntimeAnimatorController controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[S21PhoneUserContainerFix] Could not load animator controller at " + ControllerPath);
                return false;
            }

            // Edit via PrefabUtility.LoadPrefabContents, per the Session 21 policy unlock --
            // serialization APIs only, never text-editing the .prefab YAML. Operates on an
            // isolated in-memory root, not a scene instance.
            GameObject root = PrefabUtility.LoadPrefabContents(ContainerPath);
            try
            {
                var appearanceAvatar = root.GetComponent<Scenario.Agents.AppearanceAvatar>();
                if (appearanceAvatar == null)
                {
                    Debug.LogError("[S21PhoneUserContainerFix] No AppearanceAvatar component on " + ContainerPath);
                    return false;
                }

                var so = new SerializedObject(appearanceAvatar);
                var avatarsProp = so.FindProperty("avatars");
                avatarsProp.arraySize = 1;
                avatarsProp.GetArrayElementAtIndex(0).objectReferenceValue = avatarPrefab;
                so.FindProperty("animationController").objectReferenceValue = controller;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, ContainerPath);
                Debug.Log("[S21PhoneUserContainerFix] " + ContainerPath + " -- avatars[0]=" + AvatarPath
                    + ", animationController=" + ControllerPath + ". Saved via PrefabUtility.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            // Move the scratch duplicate out of Resources/ (kept, not deleted).
            if (AssetDatabase.LoadAssetAtPath<GameObject>(DuplicatePath) != null)
            {
                if (!AssetDatabase.IsValidFolder(DuplicateArchiveDir))
                {
                    Directory.CreateDirectory(DuplicateArchiveDir);
                    AssetDatabase.Refresh();
                }
                string moveError = AssetDatabase.MoveAsset(DuplicatePath, DuplicateArchivePath);
                if (!string.IsNullOrEmpty(moveError))
                {
                    Debug.LogError("[S21PhoneUserContainerFix] MoveAsset failed: " + moveError);
                    return false;
                }
                Debug.Log("[S21PhoneUserContainerFix] Moved " + DuplicatePath + " -> " + DuplicateArchivePath
                    + " (out of Resources/, kept for provenance).");
            }
            else
            {
                Debug.LogWarning("[S21PhoneUserContainerFix] Duplicate not found at " + DuplicatePath
                    + " (already moved, or never present this run) -- continuing.");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return true;
        }
    }
}
