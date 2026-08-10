using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial.EditorTools
{
    /// <summary>
    /// Session 68-B §1.1: find out WHERE the right foot is reversed, before changing anything.
    ///
    /// Two candidate layers, and they call for different fixes, so the probe separates them:
    ///
    ///   SOURCE      the downloaded clip is itself wrong on its own skeleton
    ///               -> nothing about retargeting can fix it; re-download or a different clip
    ///   RETARGET    the clip is fine on its own skeleton but the FBX's Avatar maps the right
    ///               foot/toes to the wrong source bone
    ///               -> fixable in that FBX's own Avatar configuration
    ///
    /// The measurement is the foot's pointing direction -- Foot -> Toes, expressed in the HIPS'
    /// frame so it is independent of which way the character happens to face. A correctly built
    /// pair has both feet pointing broadly forward (+z in hips space) and roughly mirrored in x.
    /// A reversed right foot shows up as a negative forward component on that side only.
    ///
    /// This runs on the FBX's OWN 66-bone mixamorig4 skeleton, which is the only place the question
    /// can be asked cleanly: the Rocketbox target is imported with Optimize GameObjects and exposes
    /// no bone Transforms at all (see S41MixamoClipApplier / the S68 §0.2 finding).
    ///
    /// Entirely read-only. It writes nothing and changes no import setting.
    ///
    ///     --exec-editor-method SEAN.AutoTrial.EditorTools.S68FootProbe.Dump
    /// </summary>
    public static class S68FootProbe
    {
        private static readonly string[] Clips =
        {
            "Assets/PedestrianAssets/S68Crouch/Crouch To Stand v2.fbx",
            "Assets/PedestrianAssets/S68Crouch/Crouch To Stand.fbx",
        };

        // The IVI round-trip crouch, included read-only as the §1.3 tier-C candidate so its feet are
        // measured by the same instrument BEFORE anything is committed to it.
        private const string IviCrouch =
            "Assets/IVI/Animations/Locomotion Pack/Interacting/Idle2Crouch_Neutral2Crouch2Idle.fbx";

        [MenuItem("AutoTrial/Session 68/Probe crouch clip feet")]
        public static void Dump()
        {
            var sb = new StringBuilder();
            foreach (string path in Clips) { Probe(path, sb); }
            Probe(IviCrouch, sb);
            Debug.Log(sb.ToString());
            EditorApplication.Exit(0);
        }

        private static void Probe(string path, StringBuilder sb)
        {
            sb.AppendLine("========================================================");
            sb.AppendLine("[S68Foot] " + path);

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) { sb.AppendLine("  MISSING"); return; }

            var srcAnim = go.GetComponentInChildren<Animator>(true);
            var avatar = srcAnim != null ? srcAnim.avatar : null;
            if (avatar == null || !avatar.isHuman)
            {
                sb.AppendLine("  not a Humanoid avatar (isHuman=false) -- cannot inspect mapping");
                return;
            }

            // ---- layer 1: what does the Avatar map each human bone to? ----
            var map = new Dictionary<string, string>();
            foreach (var hb in avatar.humanDescription.human) { map[hb.humanName] = hb.boneName; }
            string[] interesting = { "LeftFoot", "RightFoot", "LeftToes", "RightToes",
                                     "LeftLowerLeg", "RightLowerLeg" };
            sb.AppendLine("  -- Avatar human-bone mapping --");
            foreach (string h in interesting)
            {
                sb.AppendFormat("    {0,-14} -> {1}\n", h, map.ContainsKey(h) ? map[h] : "(unmapped)");
            }
            // Asymmetry in the mapping is the single most likely cause of a ONE-SIDED defect: the
            // same human bone should resolve to the same source bone name on both sides, differing
            // only by the Left/Right token.
            sb.AppendLine("  -- mapping symmetry --");
            CheckPair(map, "LeftFoot", "RightFoot", sb);
            CheckPair(map, "LeftToes", "RightToes", sb);

            // ---- layer 2: where do the feet actually point on the source skeleton? ----
            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview__"));
            if (clip == null) { sb.AppendLine("  no AnimationClip sub-asset"); return; }

            var inst = Object.Instantiate(go);
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.identity;

            Transform hips = FindBone(inst.transform, "Hips");
            Transform lFoot = FindBone(inst.transform, "LeftFoot");
            Transform rFoot = FindBone(inst.transform, "RightFoot");
            Transform lToe = FindBone(inst.transform, "LeftToeBase", "LeftToe_End", "LeftToe");
            Transform rToe = FindBone(inst.transform, "RightToeBase", "RightToe_End", "RightToe");

            sb.AppendFormat("  -- source skeleton bones: hips={0} lFoot={1} rFoot={2} lToe={3} rToe={4}\n",
                Named(hips), Named(lFoot), Named(rFoot), Named(lToe), Named(rToe));

            if (hips == null || lFoot == null || rFoot == null || lToe == null || rToe == null)
            {
                sb.AppendLine("  cannot measure -- missing a foot/toe bone on the source rig");
                Object.DestroyImmediate(inst);
                return;
            }

            sb.AppendLine("  -- foot direction on the SOURCE skeleton (hips frame; fwd>0 = toes ahead) --");
            sb.AppendLine("     t_norm | L fwd  L lat | R fwd  R lat | verdict");
            int reversedFrames = 0, measured = 0;
            float[] samples = { 0.00f, 0.25f, 0.50f, 0.75f, 1.00f };
            foreach (float tn in samples)
            {
                clip.SampleAnimation(inst, tn * clip.length);

                Vector3 lDir = (lToe.position - lFoot.position);
                Vector3 rDir = (rToe.position - rFoot.position);
                // Hips frame. Using the hips' own axes rather than world keeps this independent of
                // how the character is oriented at that instant.
                float lF = Vector3.Dot(lDir.normalized, hips.forward);
                float rF = Vector3.Dot(rDir.normalized, hips.forward);
                float lL = Vector3.Dot(lDir.normalized, hips.right);
                float rL = Vector3.Dot(rDir.normalized, hips.right);

                bool oneSided = (lF > 0.15f && rF < -0.15f) || (rF > 0.15f && lF < -0.15f);
                if (oneSided) { reversedFrames++; }
                measured++;
                sb.AppendFormat("     {0,6:F2} | {1,5:F2} {2,5:F2} | {3,5:F2} {4,5:F2} | {5}\n",
                    tn, lF, lL, rF, rL, oneSided ? "ONE FOOT REVERSED" : "consistent");
            }

            sb.AppendFormat("  => SOURCE VERDICT: {0} ({1}/{2} sampled frames one-sided)\n",
                reversedFrames > 0
                    ? "the reversal is IN THE CLIP/RIG ITSELF -- retargeting cannot fix it"
                    : "source feet are consistent -- any reversal is introduced downstream",
                reversedFrames, measured);

            Object.DestroyImmediate(inst);
        }

        private static void CheckPair(Dictionary<string, string> map, string a, string b, StringBuilder sb)
        {
            string va = map.ContainsKey(a) ? map[a] : null;
            string vb = map.ContainsKey(b) ? map[b] : null;
            if (va == null || vb == null)
            {
                sb.AppendFormat("    {0}/{1}: one side UNMAPPED ({2} / {3}) -> ASYMMETRIC\n", a, b, va, vb);
                return;
            }
            // Normalise the side token out; what remains must be identical.
            string na = va.Replace("Left", "#").Replace("Right", "#");
            string nb = vb.Replace("Left", "#").Replace("Right", "#");
            sb.AppendFormat("    {0}/{1}: '{2}' vs '{3}' -> {4}\n", a, b, va, vb,
                na == nb ? "symmetric" : "ASYMMETRIC -- likely the defect");
        }

        private static string Named(Transform t) { return t == null ? "(null)" : t.name; }

        private static Transform FindBone(Transform root, params string[] needles)
        {
            // Exact-suffix match first so "LeftToeBase" never accidentally answers a "LeftToe_End"
            // query, then fall back to a contains match for rigs with a different prefix.
            foreach (var needle in needles)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.EndsWith(needle, System.StringComparison.OrdinalIgnoreCase)) { return t; }
                }
            }
            foreach (var needle in needles)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) >= 0) { return t; }
                }
            }
            return null;
        }
    }
}
