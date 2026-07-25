using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Loop 1 Bug 5 (bounded-effort diagnostic): dumps the FULL structure of
    /// SocialForcesAnimatorController -- every state, every transition (source, destination,
    /// hasExitTime/exitTime/duration, conditions), and critically the ANY STATE transition list
    /// IN ORDER (Unity evaluates Any State transitions top-to-bottom, first match wins) -- to
    /// check the one hypothesis Session 34 flagged but couldn't check without live Editor access:
    /// a competing Any State transition (e.g. for Jump/Crouch/OnGround) listed BEFORE the
    /// "Surprised" entry that might intermittently win priority and delay entry into
    /// SurprisedReaction. Read-only (AssetDatabase.LoadAssetAtPath + reflection over the public
    /// API), no SetDirty/SaveAssets -- pure inspection, not a fix attempt.
    ///
    /// -executeMethod SEAN.AutoTrial.S41SurprisedControllerGraphDump.Dump
    /// </summary>
    public static class S41SurprisedControllerGraphDump
    {
        private const string ControllerPath = "Assets/Resources/Animation/SocialForcesAnimatorController.controller";

        public static void Dump()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError("[S41GraphDump] could not load " + ControllerPath);
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log("[S41GraphDump] layerCount=" + controller.layers.Length);
            for (int li = 0; li < controller.layers.Length; li++)
            {
                var layer = controller.layers[li];
                Debug.Log("[S41GraphDump] === layer " + li + ": name='" + layer.name
                    + "' defaultWeight=" + layer.defaultWeight + " blendingMode=" + layer.blendingMode
                    + " avatarMask=" + (layer.avatarMask != null ? layer.avatarMask.name : "NULL") + " ===");
                var sm = layer.stateMachine;
                Debug.Log("[S41GraphDump] defaultState=" + (sm.defaultState != null ? sm.defaultState.name : "NULL"));

                Debug.Log("[S41GraphDump] --- ANY STATE transitions (evaluated in this order, first match wins) ---");
                var anyTransitions = sm.anyStateTransitions;
                for (int i = 0; i < anyTransitions.Length; i++)
                {
                    var t = anyTransitions[i];
                    string conds = string.Join(" && ", t.conditions.Select(c => c.parameter + " " + c.mode + " " + c.threshold));
                    Debug.Log(string.Format("[S41GraphDump]   [{0}] -> {1} | hasExitTime={2} exitTime={3} duration={4} " +
                        "canTransitionToSelf={5} interruptionSource={6} conditions=[{7}]",
                        i, t.destinationState != null ? t.destinationState.name : "(exit)",
                        t.hasExitTime, t.exitTime, t.duration, t.canTransitionToSelf, t.interruptionSource, conds));
                }

                Debug.Log("[S41GraphDump] --- Per-state outgoing transitions ---");
                foreach (var cs in sm.states)
                {
                    var state = cs.state;
                    Debug.Log(string.Format("[S41GraphDump] state '{0}': motion={1} speed={2} tag={3}",
                        state.name, state.motion != null ? state.motion.name : "NULL", state.speed, state.tag));
                    foreach (var t in state.transitions)
                    {
                        string conds = string.Join(" && ", t.conditions.Select(c => c.parameter + " " + c.mode + " " + c.threshold));
                        Debug.Log(string.Format("[S41GraphDump]     -> {0} | hasExitTime={1} exitTime={2} duration={3} " +
                            "offset={4} interruptionSource={5} conditions=[{6}]",
                            t.destinationState != null ? t.destinationState.name : "(exit)",
                            t.hasExitTime, t.exitTime, t.duration, t.offset, t.interruptionSource, conds));
                    }
                }

                Debug.Log("[S41GraphDump] --- Parameters ---");
                foreach (var p in controller.parameters)
                {
                    Debug.Log("[S41GraphDump] param '" + p.name + "' type=" + p.type + " defaultBool=" + p.defaultBool
                        + " defaultFloat=" + p.defaultFloat + " defaultInt=" + p.defaultInt);
                }
            }

            EditorApplication.Exit(0);
        }
    }
}
