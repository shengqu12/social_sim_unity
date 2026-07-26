using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 41 TASK 1 (read-only): resolves, for every roster entry (5 personalities x Zone A
    /// + all 8 Zone B specials), which AnimatorController the runtime would actually pick, then
    /// dumps every state's speed/clip and every transition's hasExitTime/exitTime/duration.
    ///
    /// Two Unity details this converts rather than reporting raw, because the raw numbers are a
    /// common source of wrong conclusions:
    ///   - exitTime is NORMALIZED (fraction of the source state's clip), never seconds. Seconds =
    ///     exitTime * sourceClipLength / max(stateSpeed, epsilon).
    ///   - duration is seconds ONLY when hasFixedDuration is true; otherwise it too is normalized
    ///     against the SOURCE state's clip. Both forms are printed plus the resolved seconds.
    ///
    /// Pure inspection: LoadAssetAtPath + public API reads only, no SetDirty/SaveAssets anywhere.
    /// -executeMethod SEAN.AutoTrial.S41AnimResponsivenessDiag.Dump
    /// </summary>
    public static class S41AnimResponsivenessDiag
    {
        // Mirrors AutoTrialBootstrap.ZoneBContainers exactly -- kept as a literal rather than
        // reflected out of it so this diagnostic can't silently follow a bootstrap refactor.
        private static readonly Dictionary<string, string> ZoneBContainers = new Dictionary<string, string>
        {
            { "cyclist", "Prefabs/CyclistContainer" },
            { "dog_walker", "Prefabs/DogWalkerContainer" },
            { "female_child", "Prefabs/FemaleChildContainer" },
            { "male_child", "Prefabs/MaleChildContainer" },
            { "phone_user", "Prefabs/PedetrainAvatars/PhoneUserContainer" },
            { "scooter_user", "Prefabs/ScooterUserContainer" },
            { "wheelchair_user", "Prefabs/WheelChairUserContainer" },
            { "white_cane_user", "Prefabs/WhiteCaneUserContainer" },
        };

        // Zone A: every personality resolves to the same avatar prefab path + the same controller;
        // personality only changes which MonoBehaviours get attached, never the controller. Dumped
        // once per personality anyway so the table the ticket asks for has a row for each.
        private const string ZoneAAvatarResource = "Prefabs/Rocketbox/Business_Male_01";

        private static readonly string[] Personalities =
            { "indifferent", "scared", "curious", "surprised", "assertive" };

        // Substring markers for "reaction-class" states/transitions, as opposed to locomotion.
        private static readonly string[] ReactionMarkers =
            { "Surprised", "Assertive", "Gesture", "React", "Point" };

        public static void Dump()
        {
            Debug.Log("[S41Diag] ================ TASK 1: ANIMATION RESPONSIVENESS DIAGNOSTIC ================");

            var controllersSeen = new Dictionary<string, RuntimeAnimatorController>();

            Debug.Log("[S41Diag] ---- ROSTER -> CONTROLLER RESOLUTION ----");
            foreach (var p in Personalities)
            {
                var prefab = Resources.Load<GameObject>(ZoneAAvatarResource);
                var rac = ResolveControllerForPrefab(prefab, "zoneA:" + p);
                Record(controllersSeen, rac);
                Debug.Log(string.Format("[S41Diag] ROSTER personality={0,-12} zone=A prefab={1} controller={2}",
                    p, ZoneAAvatarResource, PathOf(rac)));
            }

            foreach (var kv in ZoneBContainers.OrderBy(k => k.Key))
            {
                var container = Resources.Load<GameObject>(kv.Value);
                if (container == null)
                {
                    Debug.Log(string.Format("[S41Diag] ROSTER special={0,-16} zone=B container={1} controller=<CONTAINER NOT FOUND>",
                        kv.Key, kv.Value));
                    continue;
                }
                var rac = ResolveControllerForPrefab(container, "zoneB:" + kv.Key);
                Record(controllersSeen, rac);
                Debug.Log(string.Format("[S41Diag] ROSTER special={0,-16} zone=B container={1} controller={2}",
                    kv.Key, kv.Value, PathOf(rac)));
            }

            // The Zone A runtime controller is assigned by AppearanceAvatar.animationController at
            // spawn, which the AutoTrial path leaves null (keep-prefab-controller). Session 32
            // established via live runtime probe that the effective one is
            // SocialForcesAnimatorController; include both it and the IVI base explicitly so the
            // table can never miss the controller that actually carries the reaction states.
            foreach (var extra in new[]
            {
                "Assets/Resources/Animation/SocialForcesAnimatorController.controller",
                "Assets/IVI/Controllers/BaseSFControllerNormalized.controller",
            })
            {
                var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(extra);
                if (c != null && !controllersSeen.ContainsKey(extra)) { controllersSeen[extra] = c; }
            }

            Debug.Log("[S41Diag] ---- DISTINCT CONTROLLERS TO DUMP: " + controllersSeen.Count + " ----");

            foreach (var kv in controllersSeen.OrderBy(k => k.Key))
            {
                DumpController(kv.Key, kv.Value);
            }

            Debug.Log("[S41Diag] ================ END TASK 1 DIAGNOSTIC ================");
            EditorApplication.Exit(0);
        }

        private static void Record(Dictionary<string, RuntimeAnimatorController> seen, RuntimeAnimatorController rac)
        {
            if (rac == null) { return; }
            string path = AssetDatabase.GetAssetPath(rac);
            if (string.IsNullOrEmpty(path) || seen.ContainsKey(path)) { return; }
            seen[path] = rac;
        }

        private static string PathOf(RuntimeAnimatorController rac)
        {
            if (rac == null) { return "<NONE ON PREFAB - assigned at runtime>"; }
            string p = AssetDatabase.GetAssetPath(rac);
            return string.IsNullOrEmpty(p) ? rac.name : p;
        }

        // Same self/children resolution the runtime uses (AvatarAnimatorUtility.GetLocomotionAnimator),
        // but reported with every Animator found so a prop/animal Animator stealing the pick is visible.
        private static RuntimeAnimatorController ResolveControllerForPrefab(GameObject prefab, string label)
        {
            if (prefab == null)
            {
                Debug.Log("[S41Diag]   " + label + ": prefab null");
                return null;
            }

            // Zone B containers carry no Animator of their own -- AppearanceAvatar.Awake()
            // instantiates one of avatars[] at runtime and (only if animationController is set)
            // overrides its controller. Follow that indirection statically so the table covers
            // the specials instead of reporting a bare "assigned at runtime".
            var appearance = prefab.GetComponentInChildren<Scenario.Agents.AppearanceAvatar>(true);
            if (appearance != null)
            {
                Debug.Log(string.Format("[S41Diag]   {0}: AppearanceAvatar override animationController={1} directVelocityDrive={2} avatars={3}",
                    label,
                    appearance.animationController != null
                        ? AssetDatabase.GetAssetPath(appearance.animationController) : "NULL(keep prefab's own)",
                    appearance.directVelocityDrive,
                    appearance.avatars != null ? appearance.avatars.Length : 0));

                RuntimeAnimatorController resolved = null;
                if (appearance.avatars != null)
                {
                    foreach (var av in appearance.avatars)
                    {
                        if (av == null) { continue; }
                        var inner = ResolveControllerForPrefab(av, label + "/avatar:" + av.name);
                        if (resolved == null) { resolved = inner; }
                    }
                }
                // An explicit override wins over whatever the avatar prefab shipped with.
                return appearance.animationController != null ? appearance.animationController : resolved;
            }

            var all = prefab.GetComponentsInChildren<Animator>(true);
            foreach (var a in all)
            {
                Debug.Log(string.Format("[S41Diag]   {0}: Animator on '{1}' avatar={2} isHuman={3} applyRootMotion={4} controller={5}",
                    label, a.gameObject.name,
                    a.avatar != null ? a.avatar.name : "NULL",
                    a.avatar != null && a.avatar.isHuman,
                    a.applyRootMotion,
                    a.runtimeAnimatorController != null ? AssetDatabase.GetAssetPath(a.runtimeAnimatorController) : "NULL"));
            }
            var picked = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(prefab);
            return picked != null ? picked.runtimeAnimatorController : null;
        }

        private static bool IsReaction(string name)
        {
            if (string.IsNullOrEmpty(name)) { return false; }
            return ReactionMarkers.Any(m => name.Contains(m));
        }

        private static void DumpController(string path, RuntimeAnimatorController rac)
        {
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (controller == null)
            {
                Debug.Log("[S41Diag] CONTROLLER " + path + " -- not an editable AnimatorController asset, skipping");
                return;
            }

            Debug.Log("[S41Diag] ######## CONTROLLER " + path + " layers=" + controller.layers.Length + " ########");

            for (int li = 0; li < controller.layers.Length; li++)
            {
                var layer = controller.layers[li];
                var sm = layer.stateMachine;
                Debug.Log(string.Format("[S41Diag] LAYER[{0}] name='{1}' weight={2} blending={3} mask={4} default='{5}'",
                    li, layer.name, layer.defaultWeight, layer.blendingMode,
                    layer.avatarMask != null ? layer.avatarMask.name : "NULL",
                    sm.defaultState != null ? sm.defaultState.name : "NULL"));

                // ---- STATES: speed multiplier + clip length/fps/loop ----
                foreach (var cs in sm.states)
                {
                    var st = cs.state;
                    var clip = st.motion as AnimationClip;
                    string clipInfo = clip != null
                        ? string.Format("clip='{0}' len={1:F3}s fps={2:F1} frames~{3:F0} loop={4} hasRootMotion={5}",
                            clip.name, clip.length, clip.frameRate, clip.length * clip.frameRate,
                            clip.isLooping, clip.hasRootCurves || clip.hasMotionCurves)
                        : (st.motion != null ? "motion='" + st.motion.name + "' (blendtree)" : "motion=NULL");
                    Debug.Log(string.Format("[S41Diag] STATE layer={0} name='{1}' REACTION={2} speed={3} speedParamActive={4} writeDefaults={5} {6}",
                        li, st.name, IsReaction(st.name), st.speed, st.speedParameterActive, st.writeDefaultValues, clipInfo));
                }

                // ---- TRANSITIONS: per-state outgoing ----
                foreach (var cs in sm.states)
                {
                    var st = cs.state;
                    float srcLen = ClipLen(st);
                    foreach (var t in st.transitions)
                    {
                        LogTransition(li, st.name, srcLen, st.speed, t, "STATE");
                    }
                }

                // ---- TRANSITIONS: Any State (evaluated in order, first match wins) ----
                var anyT = sm.anyStateTransitions;
                for (int i = 0; i < anyT.Length; i++)
                {
                    // Any State has no source clip, so normalized exitTime/duration have no
                    // meaningful seconds conversion -- passed as -1 and printed as n/a.
                    LogTransition(li, "AnyState[" + i + "]", -1f, 1f, anyT[i], "ANY");
                }

                // ---- Entry transitions ----
                foreach (var t in sm.entryTransitions)
                {
                    Debug.Log(string.Format("[S41Diag] TRANSITION layer={0} kind=ENTRY src='Entry' dst='{1}' conditions=[{2}]",
                        li, t.destinationState != null ? t.destinationState.name : "(none)",
                        string.Join(" && ", t.conditions.Select(c => c.parameter + " " + c.mode + " " + c.threshold))));
                }
            }

            Debug.Log("[S41Diag] PARAMS " + path + ": " +
                string.Join(", ", controller.parameters.Select(p => p.name + ":" + p.type)));
        }

        private static float ClipLen(AnimatorState st)
        {
            var clip = st.motion as AnimationClip;
            return clip != null ? clip.length : -1f;
        }

        private static void LogTransition(int layer, string srcName, float srcLen, float srcSpeed,
                                          AnimatorStateTransition t, string kind)
        {
            string dst = t.destinationState != null ? t.destinationState.name
                       : (t.destinationStateMachine != null ? "SM:" + t.destinationStateMachine.name : "(exit)");
            string conds = string.Join(" && ", t.conditions.Select(c => c.parameter + " " + c.mode + " " + c.threshold));

            float effSpeed = Mathf.Abs(srcSpeed) < 0.0001f ? 1f : Mathf.Abs(srcSpeed);

            // exitTime is always normalized against the source clip.
            string exitSec = (t.hasExitTime && srcLen > 0f)
                ? string.Format("{0:F3}s", t.exitTime * srcLen / effSpeed)
                : "n/a";

            // duration is seconds iff hasFixedDuration; otherwise normalized to source clip.
            string durSec;
            if (t.hasFixedDuration) { durSec = string.Format("{0:F3}s", t.duration); }
            else if (srcLen > 0f) { durSec = string.Format("{0:F3}s", t.duration * srcLen / effSpeed); }
            else { durSec = "n/a"; }

            Debug.Log(string.Format(
                "[S41Diag] TRANSITION layer={0} kind={1} src='{2}' dst='{3}' REACTION={4} " +
                "hasExitTime={5} exitTime_norm={6:F4} exitTime_sec={7} " +
                "hasFixedDuration={8} duration_raw={9:F4} duration_sec={10} " +
                "offset={11:F3} interrupt={12} orderedInterrupt={13} solo={14} mute={15} conditions=[{16}]",
                layer, kind, srcName, dst, IsReaction(dst) || IsReaction(srcName),
                t.hasExitTime, t.exitTime, exitSec,
                t.hasFixedDuration, t.duration, durSec,
                t.offset, t.interruptionSource, t.orderedInterruption, t.solo, t.mute, conds));
        }
    }
}
