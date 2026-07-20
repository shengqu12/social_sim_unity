using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 21 STEP 1 diagnostic ONLY -- temporarily rewires PhoneUserContainer.prefab's
    /// avatars[0] back to the OLD Phone_User.prefab (Community-informed Model), to A/B whether
    /// the ~2x-shorter pre-roll / robotSpeedAtTrigger=0.000 pattern observed with PhoneUser_Ped
    /// wired in is a property of the new avatar's own root pivot, or something else entirely.
    /// Never intended to be the final state -- S21PhoneUserContainerFix.Apply re-applies the
    /// real fix afterward. Left in the repo alongside S21PhoneUserContainerFix.cs for provenance
    /// of the diagnostic, same as this project's convention elsewhere.
    ///
    /// -executeMethod SEAN.AutoTrial.S21PhoneUserAbTest.WireOldAvatar
    /// </summary>
    public static class S21PhoneUserAbTest
    {
        private const string ContainerPath = "Assets/Resources/Prefabs/PedetrainAvatars/PhoneUserContainer.prefab";
        private const string OldAvatarPath = "Assets/Resources/Prefabs/Community-informed Model/Phone User/Phone_User.prefab";

        public static void WireOldAvatar()
        {
            bool ok = Run();
            EditorApplication.Exit(ok ? 0 : 1);
        }

        private static bool Run()
        {
            GameObject oldAvatar = AssetDatabase.LoadAssetAtPath<GameObject>(OldAvatarPath);
            if (oldAvatar == null)
            {
                Debug.LogError("[S21PhoneUserAbTest] Could not load " + OldAvatarPath);
                return false;
            }

            GameObject root = PrefabUtility.LoadPrefabContents(ContainerPath);
            try
            {
                var appearanceAvatar = root.GetComponent<Scenario.Agents.AppearanceAvatar>();
                var so = new SerializedObject(appearanceAvatar);
                var avatarsProp = so.FindProperty("avatars");
                avatarsProp.arraySize = 1;
                avatarsProp.GetArrayElementAtIndex(0).objectReferenceValue = oldAvatar;
                so.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, ContainerPath);
                Debug.Log("[S21PhoneUserAbTest] " + ContainerPath + " avatars[0] temporarily set back to " + OldAvatarPath);
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
