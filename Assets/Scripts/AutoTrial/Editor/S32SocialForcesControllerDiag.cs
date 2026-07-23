using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 32 FIX B2: find SocialForcesAnimatorController (the ACTUAL runtime controller for
    /// business_male_01, confirmed via runtime probe -- NOT BaseSFControllerNormalized, which is
    /// what S31's S31GestureAnimationFix.cs edited) and dump its layers/states/params. Read-only.
    /// -executeMethod SEAN.AutoTrial.S32SocialForcesControllerDiag.Run
    /// </summary>
    public static class S32SocialForcesControllerDiag
    {
        public static void Run()
        {
            string[] guids = AssetDatabase.FindAssets("SocialForcesAnimatorController t:AnimatorController");
            if (guids.Length == 0)
            {
                Debug.LogError("[S32SocialForcesControllerDiag] no AnimatorController named SocialForcesAnimatorController found");
                EditorApplication.Exit(1);
                return;
            }
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                Debug.Log("[S32SocialForcesControllerDiag] path=" + path);
                Debug.Log("[S32SocialForcesControllerDiag] parameters=" + string.Join(",", controller.parameters.Select(p => p.name + ":" + p.type)));
                for (int i = 0; i < controller.layers.Length; i++)
                {
                    var layer = controller.layers[i];
                    var stateNames = layer.stateMachine.states.Select(s => s.state.name).ToArray();
                    Debug.Log("[S32SocialForcesControllerDiag] layer[" + i + "] name='" + layer.name + "' states=[" + string.Join(", ", stateNames) + "]");
                    foreach (var cs in layer.stateMachine.states)
                    {
                        if (cs.state.name.ToLower().Contains("surpris") || cs.state.name.ToLower().Contains("assert"))
                        {
                            Debug.Log("[S32SocialForcesControllerDiag]   -> '" + cs.state.name + "' motion="
                                + (cs.state.motion != null ? cs.state.motion.name : "NULL") + " speed=" + cs.state.speed);
                        }
                    }
                }
            }
            EditorApplication.Exit(0);
        }
    }
}
