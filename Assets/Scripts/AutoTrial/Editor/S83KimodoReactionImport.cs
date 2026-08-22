using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 83 PHASE 2. Configures the Kimodo b2 surprise reaction FBX as a Humanoid rig,
    /// headlessly, with an EXPLICIT bone map -- then asserts the result (GATE I).
    ///
    /// WHY AN EXPLICIT MAP RATHER THAN UNITY'S AUTOMAP. The SOMA skeleton names its thigh
    /// `LeftLeg` and its shin `LeftShin`. Unity's name-based automap reads `LeftLeg` and binds it
    /// to LeftLOWERLeg, because in every rig convention it knows, "Leg" is the shin. S72 proved
    /// this by diffing a name matcher against a structure walk over the actual FBX:
    ///
    ///   TRAP LeftUpperLeg     structure='LeftLeg'   naive_name_match=None
    ///   TRAP RightUpperLeg    structure='RightLeg'  naive_name_match=None
    ///   TRAP LeftLowerLeg     structure='LeftShin'  naive_name_match='LeftLeg'
    ///   TRAP RightLowerLeg    structure='RightShin' naive_name_match='RightLeg'
    ///
    /// The chain from Hips is LeftLeg -> LeftShin -> LeftFoot -> LeftToeBase, so `LeftLeg` is
    /// unambiguously the thigh. Left to the automap the legs come in one segment short and the
    /// character retargets with broken knees. `Chest` is the fifth trap and the subtlest: SOMA's
    /// spine is Hips->Spine1->Spine2->Chest->Neck1->Neck2->Head, four segments where Unity has at
    /// most three, so Unity's Chest slot takes `Spine2` and SOMA's literal `Chest` bone stays
    /// unmapped. Mapping the bone that shares the NAME would bunch all three mapped segments into
    /// the upper torso.
    ///
    /// Bone table is VERBATIM from Assets/PedestrianAssets/Kimodo/README.md section 4, which is
    /// itself verbatim from S72 UNITY_STEPS.md section 4. One source; this file does not restate
    /// it from memory.
    ///
    /// LOOP TIME IS OFF, deliberately. b2 is a one-shot reaction (3 s surprise + 3 s return to
    /// neutral), not a gait. Looping it would restart the flinch every 6 s for as long as the
    /// state is active.
    ///
    /// FixRocketboxMaxImport is a project-wide AssetPostprocessor with no path filter that forces
    /// animationType=Generic. These clips escape it only because the SOMA root is `Root`/`Hips`
    /// and never `Bip01` -- it early-returns when no Bip01 child exists. GATE I re-reads the
    /// importer AFTER the reimport precisely to catch a demotion if that naming luck ever changes.
    ///
    /// -executeMethod SEAN.AutoTrial.S83KimodoReactionImport.Apply
    /// -executeMethod SEAN.AutoTrial.S83KimodoReactionImport.Verify
    /// </summary>
    public static class S83KimodoReactionImport
    {
        public const string FbxPath = "Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx";

        // Unity Humanoid slot -> SOMA bone. The five traps are marked; every other row is a
        // straight rename. UpperChest and SOMA's `Root` are deliberately left unmapped (Unity
        // treats Hips as the root; Root is a reference/motion node).
        private static readonly string[,] BoneMap =
        {
            // --- the 15 required slots ---
            { "Hips",          "Hips"        },
            { "Spine",         "Spine1"      },
            { "Head",          "Head"        },
            { "LeftUpperArm",  "LeftArm"     },
            { "RightUpperArm", "RightArm"    },
            { "LeftLowerArm",  "LeftForeArm" },
            { "RightLowerArm", "RightForeArm"},
            { "LeftHand",      "LeftHand"    },
            { "RightHand",     "RightHand"   },
            { "LeftUpperLeg",  "LeftLeg"     },   // TRAP
            { "RightUpperLeg", "RightLeg"    },   // TRAP
            { "LeftLowerLeg",  "LeftShin"    },   // TRAP
            { "RightLowerLeg", "RightShin"   },   // TRAP
            { "LeftFoot",      "LeftFoot"    },
            { "RightFoot",     "RightFoot"   },
            // --- optional, mapped because they measurably improve retarget quality ---
            { "Chest",         "Spine2"      },   // TRAP (not SOMA's bone literally named Chest)
            { "Neck",          "Neck1"       },
            { "LeftShoulder",  "LeftShoulder"},
            { "RightShoulder", "RightShoulder"},
            { "LeftToes",      "LeftToeBase" },
            { "RightToes",     "RightToeBase"},
        };

        // The four leg slots plus Chest. GATE I asserts these five by name against the built
        // Avatar, because they are the ones a silent automap regression would get wrong.
        private static readonly string[,] TrapSlots =
        {
            { "LeftUpperLeg",  "LeftLeg"   },
            { "RightUpperLeg", "RightLeg"  },
            { "LeftLowerLeg",  "LeftShin"  },
            { "RightLowerLeg", "RightShin" },
            { "Chest",         "Spine2"    },
        };

        public static void Apply() { Run(true); }
        public static void Verify() { Run(false); }

        private static void Run(bool write)
        {
            var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (importer == null)
            {
                Fail("no ModelImporter at " + FbxPath + " -- is the FBX present?");
                return;
            }

            if (write)
            {
                // Pass 1: import Generic so the transform hierarchy is readable. The SkeletonBone
                // array must describe the ACTUAL imported rig (names + bind pose); building it
                // from the live hierarchy is the only way to get the T-pose right headlessly.
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.SaveAndReimport();

                var root = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
                if (root == null) { Fail("could not load the model prefab at " + FbxPath); return; }

                var skeleton = new List<SkeletonBone>();
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    skeleton.Add(new SkeletonBone
                    {
                        name = t.name,
                        position = t.localPosition,
                        rotation = t.localRotation,
                        scale = t.localScale,
                    });
                }

                var present = new HashSet<string>(skeleton.Select(s => s.name));
                var human = new List<HumanBone>();
                for (int i = 0; i < BoneMap.GetLength(0); i++)
                {
                    string slot = BoneMap[i, 0], bone = BoneMap[i, 1];
                    if (!present.Contains(bone))
                    {
                        Fail("rig has no bone '" + bone + "' for Humanoid slot '" + slot
                             + "' -- the SOMA skeleton is not what README section 4 describes.");
                        return;
                    }
                    var hb = new HumanBone { humanName = slot, boneName = bone };
                    hb.limit.useDefaultValues = true;
                    human.Add(hb);
                }

                var hd = importer.humanDescription;
                hd.human = human.ToArray();
                hd.skeleton = skeleton.ToArray();
                importer.humanDescription = hd;

                // Pass 2: promote to Humanoid using the map above rather than the automap.
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

                // One-shot reaction, not a gait.
                var clips = importer.defaultClipAnimations;
                if (clips != null && clips.Length > 0)
                {
                    for (int i = 0; i < clips.Length; i++)
                    {
                        clips[i].loopTime = false;
                        clips[i].loop = false;
                    }
                    importer.clipAnimations = clips;
                }
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
            }

            Assert();
        }

        /// <summary>GATE I, read back from the imported asset -- never from what we just set.</summary>
        private static void Assert()
        {
            var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            bool ok = true;

            Debug.Log("[S83Import] animationType=" + importer.animationType
                      + " (Human required; Generic here means FixRocketboxMaxImport demoted the rig)");
            if (importer.animationType != ModelImporterAnimationType.Human) { ok = false; }

            var assets = AssetDatabase.LoadAllAssetsAtPath(FbxPath);
            var avatar = assets.OfType<Avatar>().FirstOrDefault();
            if (avatar == null) { Debug.LogError("[S83Import] no Avatar sub-asset"); ok = false; }
            else
            {
                Debug.Log("[S83Import] avatar isValid=" + avatar.isValid + " isHuman=" + avatar.isHuman);
                if (!avatar.isValid || !avatar.isHuman) { ok = false; }
            }

            // The five trap slots, read back off the importer's stored description.
            var map = new Dictionary<string, string>();
            foreach (var hb in importer.humanDescription.human) { map[hb.humanName] = hb.boneName; }
            for (int i = 0; i < TrapSlots.GetLength(0); i++)
            {
                string slot = TrapSlots[i, 0], want = TrapSlots[i, 1];
                string got = map.ContainsKey(slot) ? map[slot] : "(unmapped)";
                bool hit = got == want;
                Debug.Log("[S83Import] trap slot " + slot + " -> '" + got + "' (expected '" + want
                          + "') " + (hit ? "OK" : "WRONG"));
                if (!hit) { ok = false; }
            }
            Debug.Log("[S83Import] mapped slots: " + importer.humanDescription.human.Length
                      + " of 21 expected");

            var clip = assets.OfType<AnimationClip>().FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (clip == null) { Debug.LogError("[S83Import] no AnimationClip sub-asset"); ok = false; }
            else
            {
                Debug.Log("[S83Import] clip '" + clip.name + "' length=" + clip.length.ToString("F3")
                          + "s frameRate=" + clip.frameRate + " isLooping=" + clip.isLooping
                          + " (want 5.5-6.5s, isLooping False)");
                if (clip.length < 5.5f || clip.length > 6.5f) { ok = false; }
                if (clip.isLooping) { ok = false; }
            }

            Debug.Log(ok ? "[S83Import] GATE I PASS" : "[S83Import] GATE I FAIL");
            if (!ok) { EditorApplication.Exit(1); }
        }

        /// <summary>
        /// Session 83 recon. Both reaction states bind a clip literally NAMED "mixamo.com" (the
        /// default take name every Mixamo FBX ships with), so a name lookup cannot tell
        /// SurprisedReaction's clip from AssertiveGesture's. They are DIFFERENT assets -- guid
        /// 17119d7c (Pointing_towards) vs 6f88966a (point_backwards), and 17119d7c occurs exactly
        /// once in the controller, so there is no shared-clip leak -- but the runtime override
        /// still needs a discriminator that survives having no guid access. This prints the
        /// candidates and their lengths so one can be chosen from measurement, not assumption.
        ///
        /// -executeMethod SEAN.AutoTrial.S83KimodoReactionImport.DumpReactionClips
        /// </summary>
        public static void DumpReactionClips()
        {
            var ctl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                "Assets/Resources/Animation/SocialForcesAnimatorController.controller");
            if (ctl == null) { Debug.LogError("[S83Dump] controller not found"); return; }
            var seen = new Dictionary<string, int>();
            foreach (var c in ctl.animationClips)
            {
                string key = c.name + "|" + c.length.ToString("F4");
                seen[key] = seen.ContainsKey(key) ? seen[key] + 1 : 1;
            }
            Debug.Log("[S83Dump] " + ctl.animationClips.Length + " clips referenced by "
                      + ctl.name + "; distinct name|length keys: " + seen.Count);
            foreach (var kv in seen.OrderBy(k => k.Key))
            {
                Debug.Log("[S83Dump]   " + kv.Key + "  x" + kv.Value
                          + (kv.Value > 1 ? "   <-- AMBIGUOUS on this key" : ""));
            }
            // And the authoritative editor-side answer, via the state machine.
            var ac = ctl as UnityEditor.Animations.AnimatorController;
            if (ac == null) { Debug.Log("[S83Dump] not an AnimatorController asset"); return; }
            foreach (var layer in ac.layers)
            {
                foreach (var st in layer.stateMachine.states)
                {
                    if (st.state.name != "SurprisedReaction" && st.state.name != "AssertiveGesture") { continue; }
                    var clip = st.state.motion as AnimationClip;
                    string path = clip != null ? AssetDatabase.GetAssetPath(clip) : "(none)";
                    Debug.Log("[S83Dump] state '" + st.state.name + "' motion='"
                              + (clip != null ? clip.name : "null") + "' length="
                              + (clip != null ? clip.length.ToString("F4") : "-")
                              + " speed=" + st.state.speed + " asset=" + path);
                }
            }
        }

        private static void Fail(string msg)
        {
            Debug.LogError("[S83Import] " + msg);
            Debug.Log("[S83Import] GATE I FAIL");
            EditorApplication.Exit(1);
        }
    }
}
