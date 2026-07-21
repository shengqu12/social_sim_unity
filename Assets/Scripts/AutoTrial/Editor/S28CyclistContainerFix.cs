using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 28 PART 1, Fault 1 -- one-off fix, run once via the guarded launcher then left in
    /// the repo for provenance (same convention as S21PhoneUserContainerFix.cs).
    ///
    /// Diagnosis (read-only recon before writing anything): CyclistContainer.prefab's
    /// AppearanceAvatar has avatars[0] correctly wired to Cyclist.prefab (the bike+rider nested
    /// prefab) and directVelocityDrive already true (correctly, per Base.cs's own comment citing
    /// this exact cyclist case -- a seated pedaling loop has no useful forward root motion, and
    /// setting directVelocityDrive also engages Base.cs's own existing double-drive guard, see
    /// that class's LateUpdate()). But animationController is UNSET ({fileID: 0}) -- so
    /// AvatarAnimatorUtility.GetLocomotionAnimator's isHuman preference (see that class's own
    /// doc) picks the RIDER's own Humanoid Animator with whatever RuntimeAnimatorController it
    /// shipped with (a generic walk controller), never the purpose-built CyclistController
    /// (single state, IsBiking param, plays anim_Relaxed_Pedal_Seated_Loop.FBX) -- this is why
    /// the rider walks beside the bike instead of pedaling it. Same class of bug, same fix
    /// mechanism as Session 21's phone_user container.
    ///
    /// Fix: point animationController at CyclistController.controller. Fault 2 (mount transform)
    /// and fault 3 (drift/dual-drive) are NOT touched here -- fault-tree order per the brief;
    /// verify after this fix alone before assuming either is still needed.
    ///
    /// -executeMethod SEAN.AutoTrial.S28CyclistContainerFix.Apply
    /// </summary>
    public static class S28CyclistContainerFix
    {
        private const string ContainerPath = "Assets/Resources/Prefabs/CyclistContainer.prefab";
        private const string ControllerPath = "Assets/Resources/Prefabs/Community-informed Model/Cyclist/Cycling Animation/CyclistController.controller";

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
                Debug.LogError("[S28CyclistContainerFix] Could not load container prefab at " + ContainerPath);
                return false;
            }

            RuntimeAnimatorController controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[S28CyclistContainerFix] Could not load animator controller at " + ControllerPath);
                return false;
            }

            // Edit via PrefabUtility.LoadPrefabContents, per the Session 21 policy unlock --
            // serialization APIs only, never text-editing the .prefab YAML.
            GameObject root = PrefabUtility.LoadPrefabContents(ContainerPath);
            try
            {
                var appearanceAvatar = root.GetComponent<Scenario.Agents.AppearanceAvatar>();
                if (appearanceAvatar == null)
                {
                    Debug.LogError("[S28CyclistContainerFix] No AppearanceAvatar component on " + ContainerPath);
                    return false;
                }

                var so = new SerializedObject(appearanceAvatar);
                so.FindProperty("animationController").objectReferenceValue = controller;
                so.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, ContainerPath);
                Debug.Log("[S28CyclistContainerFix] " + ContainerPath + " -- animationController=" + ControllerPath
                    + ". avatars[0]/directVelocityDrive left untouched (already correct). Saved via PrefabUtility.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return true;
        }
    }
}
