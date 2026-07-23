using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 32 FIX B2 diagnostic (fault-tree branch (b)): dump every layer of
    /// BaseSFControllerNormalized.controller -- name, defaultWeight, blendingMode, avatarMask,
    /// and which layer's state machine contains "SurprisedReaction" / "AssertiveGesture".
    /// Read-only. -executeMethod SEAN.AutoTrial.S32AnimLayerDiag.Run
    /// </summary>
    public static class S32AnimLayerDiag
    {
        private const string ControllerPath = "Assets/IVI/Controllers/BaseSFControllerNormalized.controller";

        public static void Run()
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[S32AnimLayerDiag] could not load " + ControllerPath);
                EditorApplication.Exit(1);
                return;
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                var layer = controller.layers[i];
                var stateNames = layer.stateMachine.states.Select(s => s.state.name).ToArray();
                Debug.Log("[S32AnimLayerDiag] layer[" + i + "] name='" + layer.name + "' defaultWeight="
                    + layer.defaultWeight + " blendingMode=" + layer.blendingMode
                    + " avatarMask=" + (layer.avatarMask != null ? layer.avatarMask.name : "null")
                    + " iKPass=" + layer.iKPass
                    + " states=[" + string.Join(", ", stateNames) + "]");

                foreach (var cs in layer.stateMachine.states)
                {
                    if (cs.state.name == "SurprisedReaction" || cs.state.name == "AssertiveGesture")
                    {
                        Debug.Log("[S32AnimLayerDiag]   -> '" + cs.state.name + "' lives on layer[" + i + "] ('"
                            + layer.name + "'), state.speed=" + cs.state.speed
                            + " motion=" + (cs.state.motion != null ? cs.state.motion.name : "null")
                            + " writeDefaultValues=" + cs.state.writeDefaultValues);
                        // dump incoming/outgoing transitions
                        foreach (var t in cs.state.transitions)
                        {
                            Debug.Log("[S32AnimLayerDiag]      out-transition -> " + (t.destinationState != null ? t.destinationState.name : "(exit)")
                                + " hasExitTime=" + t.hasExitTime + " exitTime=" + t.exitTime + " duration=" + t.duration
                                + " conditions=" + string.Join(",", t.conditions.Select(c => c.parameter + c.mode + c.threshold)));
                        }
                    }
                }
                // Any-state transitions into these states
                foreach (var t in layer.stateMachine.anyStateTransitions)
                {
                    if (t.destinationState != null && (t.destinationState.name == "SurprisedReaction" || t.destinationState.name == "AssertiveGesture"))
                    {
                        Debug.Log("[S32AnimLayerDiag]   AnyState -> '" + t.destinationState.name + "' on layer[" + i + "] hasExitTime="
                            + t.hasExitTime + " duration=" + t.duration + " conditions="
                            + string.Join(",", t.conditions.Select(c => c.parameter + c.mode + c.threshold)));
                    }
                }
            }

            Debug.Log("[S32AnimLayerDiag] total layers: " + controller.layers.Length
                + " parameters: " + string.Join(",", controller.parameters.Select(p => p.name + ":" + p.type)));

            EditorApplication.Exit(0);
        }
    }
}
