using System.Collections;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 68 §0.2, second attempt: the crouch retarget smoke test, run in PLAY mode.
    ///
    /// The first attempt sampled the clip from the Editor (AnimationClip.SampleAnimation, then
    /// UnityEditor.AnimationMode) and both posed nothing at all -- six samples across a
    /// crouch-to-stand returned an identical 1.848 m height and byte-identical renders. The
    /// Rocketbox avatars are imported with "Optimize GameObjects", so the bone Transforms are
    /// stripped from the hierarchy and the skinning is driven by the Animator's playable graph,
    /// which only exists in play mode. That is the same trap S41MixamoClipApplier already documents
    /// for GetBoneTransform on this rig; it applies to edit-mode sampling too.
    ///
    /// So this runs the real thing: a live Animator, the real controller asset, animator.Play() at
    /// explicit normalized times, and a render per sample. It deliberately does NOT load the shared
    /// Outdoor scene or touch ROS -- it builds its own empty stage, because the question is "does
    /// this clip retarget onto this avatar", and a trial would only add failure modes that have
    /// nothing to do with it.
    ///
    /// Both playback directions are sampled, because §0.1's inventory came back with only ONE crouch
    /// clip and the single-clip plan depends on the reverse being watchable, not just the forward.
    /// </summary>
    public class S68CrouchSmokeRunner : MonoBehaviour
    {
        public string outDir = "";
        public string avatarResource = "Prefabs/Rocketbox/Male_Adult_01";
        public string controllerResource = "S68_CuriousCrouch";
        public string stateName = "Crouch";
        /// <summary>Render the full enter -> hold -> exit sequence as a frame dump for eyeballing
        /// (S68-A §3). Off by default; the import path turns it on.</summary>
        public bool renderSequence = false;
        public float holdSeconds = 1.5f;
        /// <summary>Which clip end is the kneel -- mirrors S68CuriousCrouch.kneelAtClipEnd so the
        /// eyeball video shows the same motion the trial plays.</summary>
        public bool kneelAtClipEnd = true;
        public string enterState = "S68CrouchEnter";
        public string exitState = "S68CrouchExit";
        /// <summary>Asset path of the clip's own source FBX, used only for the S68-B muscle
        /// comparison. Loaded via AssetDatabase because it is not under a Resources folder and this
        /// probe only ever runs inside the editor.</summary>
        public string sourceFbxPath = "";
        /// <summary>A SHIPPED Mixamo clip controller, rendered on the same avatar for comparison
        /// (S68-B §1.1). Tells a crouch-specific retarget defect apart from a Mixamo-wide one.</summary>
        public string compareControllerResource = "Old_Man_Walk";

        private const int W = 720, H = 720;
        private const int EyeballFps = 30;

        void Start()
        {
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            if (string.IsNullOrEmpty(outDir))
            {
                outDir = "/home/sheng/Desktop/research/social_navigation/trial_outputs/demo_s68/smoke_kneel";
            }
            Directory.CreateDirectory(outDir);

            var prefab = Resources.Load<GameObject>(avatarResource);
            if (prefab == null) { Fail("avatar prefab not found in Resources: " + avatarResource); yield break; }

            var rac = Resources.Load<RuntimeAnimatorController>(controllerResource);
            if (rac == null) { Fail("controller not found in Resources: " + controllerResource); yield break; }

            BuildStage();

            var inst = Instantiate(prefab, Vector3.zero, Quaternion.identity);
            inst.name = "SmokeSubject";
            subject = inst;
            var anim = inst.GetComponentInChildren<Animator>(true);
            if (anim == null) { Fail("no Animator on the instantiated avatar"); yield break; }

            // S68-B §1.1, the discriminator that does not need bone access.
            //
            // Render the feet on this avatar BEFORE any Mixamo clip is applied, driven only by the
            // controller the prefab ships with. If the right foot is already rolled sole-up here,
            // the defect belongs to the shared Rocketbox avatar and no crouch clip can be blamed for
            // it (and it is a red-line asset, so it would have to be reported, not fixed). If the
            // feet are correct here and wrong once the crouch clip is on, the defect belongs to this
            // clip's retarget.
            //
            // This is the measurement the muscle comparison was supposed to provide.
            // HumanPoseHandler cannot supply it: on this avatar it returns 0.000 for every muscle in
            // every pose, because Optimize GameObjects leaves it no Transforms to read -- the same
            // limitation that already defeated edit-mode sampling and GetBoneTransform.
            yield return null;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            yield return null;
            Debug.Log("[S68Smoke] baseline controller (prefab's own) = "
                + (anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "NULL"));
            ShootFeet(outDir + "/foot_BASELINE_noMixamoClip");

            // And one SHIPPED Mixamo clip, retargeted onto this same avatar the same way. This
            // separates "Mixamo clips in general do not retarget cleanly onto Rocketbox feet" --
            // which would implicate the already-delivered dataset -- from "the crouch clips
            // specifically do". Those are very different findings and only a measurement tells them
            // apart.
            if (!string.IsNullOrEmpty(compareControllerResource))
            {
                var cmp = Resources.Load<RuntimeAnimatorController>(compareControllerResource);
                if (cmp != null)
                {
                    anim.runtimeAnimatorController = cmp;
                    anim.Rebind();
                    yield return null;
                    anim.speed = 0f;
                    anim.Update(0f);
                    yield return null;
                    Debug.Log("[S68Smoke] comparison controller = " + cmp.name);
                    ShootFeet(outDir + "/foot_SHIPPED_" + compareControllerResource);
                }
                else
                {
                    Debug.LogWarning("[S68Smoke] comparison controller '" + compareControllerResource
                        + "' not loadable -- skipping the shipped-clip comparison.");
                }
            }

            anim.runtimeAnimatorController = rac;
            anim.Rebind();
            yield return null;
            // The clip is in-place by import (lockRootPositionXZ/HeightY/Rotation), and nothing here
            // is meant to travel -- this stage has no navigation to travel with.
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            anim.Rebind();

            // One frame for the rebind to be processed -- the same deferral S41MixamoClipApplier
            // needs before the rig answers questions about itself.
            yield return null;

            int transforms = inst.GetComponentsInChildren<Transform>(true).Length;
            Debug.Log(string.Format("[S68Smoke] subject transforms={0} isHuman={1} controller={2}",
                transforms, anim.avatar != null && anim.avatar.isHuman, anim.runtimeAnimatorController.name));

            int hash = Animator.StringToHash(stateName);
            float[] samples = { 0.00f, 0.08f, 0.20f, 0.40f, 0.65f, 1.00f };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[S68Smoke] POSE TABLE  t_norm | minY | maxY | height | footprintXZ");
            float minH = float.MaxValue, maxH = float.MinValue;

            foreach (float tn in samples)
            {
                anim.speed = 0f;                    // hold exactly where Play() puts it
                anim.Play(hash, 0, tn);
                anim.Update(0f);
                // `yield return null`, NOT WaitForEndOfFrame: in -batchmode that coroutine yield
                // instruction never resumes, and the first run of this probe hung on it until
                // run_trial.py's 180 s guard killed the editor. Nothing here needs end-of-frame
                // anyway -- the cameras are disabled and rendered explicitly by Render().
                yield return null;

                Bounds b;
                if (!SubjectBounds(inst, out b))
                {
                    sb.AppendLine("  t=" + tn.ToString("F2") + "  NO SKINNED RENDERER");
                    continue;
                }
                sb.AppendFormat("  t={0:F2} | {1,7:F3} | {2,7:F3} | {3,6:F3} | {4:F2}x{5:F2}\n",
                    tn, b.min.y, b.max.y, b.size.y, b.size.x, b.size.z);
                minH = Mathf.Min(minH, b.size.y);
                maxH = Mathf.Max(maxH, b.size.y);

                Shoot(string.Format("{0}/crouch_t{1:F2}", outDir, tn));
            }

            float span = maxH - minH;
            bool varies = span > 0.10f;
            sb.AppendFormat("[S68Smoke] POSE VARIATION height span={0:F3}m -> {1}\n",
                span, varies ? "PASS (clip is driving the rig)" : "FAIL (bind pose -- nothing was tested)");
            Debug.Log(sb.ToString());

            // S68-A §3: the optional eyeball sequence. The POV deliverable cannot show this at all
            // -- the S68 run measured the pedestrian at 0% in-frame for the whole of CROUCH_HOLD and
            // CROUCH_EXIT -- so the only way to eyeball the new clip's crouch and stand-up is to
            // render them directly. Not a pipeline output and never fed to one.
            //
            // Frames are stepped MANUALLY at a fixed rate rather than played in real time: batchmode
            // frame pacing is not a clock, and a video assembled from whatever wall-time the editor
            // happened to take would misrepresent the speed of the very motion being judged. Here
            // one output frame is exactly 1/EyeballFps of clip time, by construction.
            // S68-B: the foot check runs before the sequence dump so its verdict is in the log even
            // if the sequence render is skipped.
            GameObject srcInst = null;
            AnimationClip srcClip = null;
            if (!string.IsNullOrEmpty(sourceFbxPath))
            {
                GameObject srcPrefab = null;
#if UNITY_EDITOR
                srcPrefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(sourceFbxPath);
#endif
                if (srcPrefab != null)
                {
                    // Parked well away from the subject so it never enters the foot close-ups.
                    srcInst = Instantiate(srcPrefab, new Vector3(1000f, 0f, 0f), Quaternion.identity);
                    srcInst.name = "SourceSkeleton";
                    var clips = anim.runtimeAnimatorController.animationClips;
                    srcClip = (clips != null && clips.Length > 0) ? clips[0] : null;
                }
                else
                {
                    Debug.LogWarning("[S68Smoke] source FBX not loadable ('" + sourceFbxPath
                        + "') -- the muscle comparison will be skipped.");
                }
            }
            yield return ProbeFeet(anim, srcInst, srcClip);
            if (srcInst != null) { Destroy(srcInst); }

            if (renderSequence)
            {
                yield return RenderSequence(anim, hash);
            }

            yield return null;
            Exit(varies ? 0 : 1);
        }

        /// <summary>
        /// Dumps enter -> hold -> exit as a numbered frame sequence, stepping clip time by hand.
        ///
        /// The three phases mirror what S68CuriousCrouch actually plays, including the reversed
        /// entry -- so what this shows is the motion as it will appear in a trial, not a preview of
        /// the raw clip. Clip length is read from the loaded controller, never assumed: the v1 clip
        /// ran 3.333 s and there is no reason a re-export does.
        /// </summary>
        private IEnumerator RenderSequence(Animator anim, int fallbackHash)
        {
            string seqDir = outDir + "/seq";
            Directory.CreateDirectory(seqDir);

            float clipLen = 0f;
            var clips = anim.runtimeAnimatorController != null
                ? anim.runtimeAnimatorController.animationClips : null;
            if (clips != null && clips.Length > 0 && clips[0] != null) { clipLen = clips[0].length; }
            if (clipLen <= 0f) { clipLen = 3.33f; }

            int exitHash = Animator.StringToHash(exitState);
            int nEnter = Mathf.Max(1, Mathf.RoundToInt(clipLen * EyeballFps));
            int nHold = Mathf.Max(1, Mathf.RoundToInt(holdSeconds * EyeballFps));
            int nExit = nEnter;
            int idx = 0;

            anim.speed = 0f;

            // Every phase below is seeked through the EXIT state, whose speed is +1, and the reversal
            // is done by walking normalizedTime downward instead.
            //
            // NOT through the enter state, even though that is the state the machine really uses.
            // Seeking a NEGATIVE-speed state to an explicit normalizedTime inverts the mapping:
            // rendering the enter state at normalizedTime 0.013 produced a fully upright figure,
            // where the same clip seeked through the exit state at 0.00 gives the kneel. Since this
            // video exists to show what the motion LOOKS like, it has to be built on the mapping that
            // is known to be faithful. Whether the machine's own negative-speed playback runs the
            // right way is a separate question, answered by DiagnoseEntryDirection below rather than
            // by assuming this render answers it.
            // Direction follows the clip family, same as the state machine: for "Kneeling Down"
            // the descent is the clip's FORWARD half. uStand/uKneel mirror kneelAtClipEnd.
            float uStand = kneelAtClipEnd ? 0f : 1f;
            float uKneel = kneelAtClipEnd ? 1f : 0f;
            for (int i = 0; i < nEnter; i++)          // descend: standing -> kneel
            {
                float tn = Mathf.Lerp(uStand, uKneel, (float)i / nEnter);
                anim.Play(exitHash, 0, tn);
                anim.Update(0f);
                yield return null;
                Render(front, string.Format("{0}/f{1:D5}.png", seqDir, idx));
                Render(feet, string.Format("{0}/g{1:D5}.png", seqDir, idx++));
            }
            for (int i = 0; i < nHold; i++)           // hold on frame 0, the kneel
            {
                anim.Play(exitHash, 0, uKneel);
                anim.Update(0f);
                yield return null;
                Render(front, string.Format("{0}/f{1:D5}.png", seqDir, idx));
                Render(feet, string.Format("{0}/g{1:D5}.png", seqDir, idx++));
            }
            for (int i = 0; i <= nExit; i++)          // rise: kneel -> standing
            {
                float tn = Mathf.Lerp(uKneel, uStand, (float)i / nExit);
                anim.Play(exitHash, 0, tn);
                anim.Update(0f);
                yield return null;
                Render(front, string.Format("{0}/f{1:D5}.png", seqDir, idx));
                Render(feet, string.Format("{0}/g{1:D5}.png", seqDir, idx++));
            }

            Debug.Log(string.Format("[S68Smoke] eyeball sequence: {0} frames @ {1} fps -> {2} "
                + "(enter {3} + hold {4} + exit {5}, clipLen={6:F3}s)",
                idx, EyeballFps, seqDir, nEnter, nHold, nExit + 1, clipLen));

            yield return DiagnoseSeekMapping(anim);
        }

        /// <summary>
        /// Asserts the one invariant S68CuriousCrouch's playback rests on: seeking the pose state to
        /// normalizedTime 1 gives a STANDING figure and 0 gives the KNEEL.
        ///
        /// That mapping is the whole basis for driving the descent as a descending parameter, and it
        /// is not free -- the previous implementation played a negative-speed state, where the same
        /// call inverts. So it is measured, on height, which separates the two poses by ~0.39 m.
        /// </summary>
        private IEnumerator DiagnoseSeekMapping(Animator anim)
        {
            int hash = Animator.StringToHash(exitState);
            anim.speed = 0f;

            anim.Play(hash, 0, 1f); anim.Update(0f);
            yield return null;
            Bounds standB; float standH = SubjectBounds(subject, out standB) ? standB.size.y : -1f;

            anim.Play(hash, 0, 0f); anim.Update(0f);
            yield return null;
            Bounds kneelB; float kneelH = SubjectBounds(subject, out kneelB) ? kneelB.size.y : -1f;

            bool ok = standH > kneelH + 0.05f;
            Debug.Log(string.Format(
                "[S68Smoke] SEEK MAPPING  u=1 height={0:F3}  u=0 height={1:F3}  ->  {2}",
                standH, kneelH,
                ok ? "PASS (1 = stand, 0 = kneel; descending u descends)"
                   : "FAIL (mapping is not what the state machine assumes)"));
        }

        private GameObject subject;

        /// <summary>BakeMesh, not Renderer.bounds: a SkinnedMeshRenderer's bounds are the import-time
        /// local bounds transformed by the root and do not track the skinned pose, so they read
        /// constant even on a rig that is posing correctly.</summary>
        private static bool SubjectBounds(GameObject inst, out Bounds bounds)
        {
            bounds = default(Bounds);
            bool any = false;
            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                Bounds lb = baked.bounds;
                Bounds wb = TransformBounds(smr.transform, lb);
                if (!any) { bounds = wb; any = true; } else { bounds.Encapsulate(wb); }
                Destroy(baked);
            }
            return any;
        }

        private static Bounds TransformBounds(Transform t, Bounds local)
        {
            Vector3 c = local.center, e = local.extents;
            Bounds w = new Bounds(t.TransformPoint(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                w.Encapsulate(t.TransformPoint(corner));
            }
            return w;
        }

        private Camera front, side, feet, feetBack;

        /// <summary>
        /// S68-B §1.1/§1.2. Compares the humanoid pose of the RETARGETED avatar against the same
        /// clip on its own source skeleton, muscle by muscle, and renders close-ups of the feet.
        ///
        /// Needed because neither existing instrument can see the defect Sheng reported. The source
        /// probe (S68FootProbe) reads bone transforms, which the Rocketbox target does not expose at
        /// all (Optimize GameObjects). And the wide smoke shots frame the whole body, where a
        /// rotated foot is a few dozen pixels. A reversal introduced BY retargeting is invisible to
        /// both, and that is exactly the case that has to be ruled in or out.
        ///
        /// Muscle space is the right comparison basis: it is normalised and already side-mirrored,
        /// so "the retarget preserved this pose" means small per-muscle deltas, independent of
        /// skeleton proportions. A large delta confined to right-foot muscles is a retarget defect;
        /// matching values mean the retarget is faithful and the pose is simply what the clip says.
        /// </summary>
        private System.Collections.IEnumerator ProbeFeet(Animator target, GameObject sourceInst, AnimationClip clip)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("[S68Smoke] FOOT / DEPTH SCAN");

            // Scan the whole clip for the DEEPEST pose. Required for a round-trip clip
            // (idle -> crouch -> idle), where the crouch is somewhere in the middle rather than at
            // an end -- and the exact midpoint is an assumption, not a measurement. The state
            // machine holds whatever this finds, so it is measured and logged rather than guessed.
            target.speed = 0f;
            int hash = Animator.StringToHash(exitState);

            // Scan at 1/200 resolution and record BOTH how tall the figure is and how far its lowest
            // vertex sits above the floor. The deepest pose is then the shortest one whose feet are
            // still ON the floor.
            //
            // The ground-contact condition is load-bearing, not cosmetic: this clip is 53.7 s of
            // several concatenated motions, and a plain global height minimum selected a pose with
            // the feet ~0.6 m in the air. "Shortest" and "most crouched" are only the same thing
            // while the character is standing on something.
            const int N = 200;
            float uDeep = -1f, hDeep = float.MaxValue, hMax = float.MinValue;
            var prof = new System.Collections.Generic.List<string>();
            for (int i = 0; i <= N; i++)
            {
                float u = (float)i / N;
                target.Play(hash, 0, u);
                target.Update(0f);
                yield return null;
                Bounds b;
                if (!SubjectBounds(subject, out b)) { continue; }
                bool grounded = b.min.y < 0.06f;
                if (b.size.y > hMax) { hMax = b.size.y; }
                if (grounded && b.size.y < hDeep) { hDeep = b.size.y; uDeep = u; }
                if (i % 10 == 0)
                {
                    prof.Add(string.Format("u={0:F3} h={1:F3} minY={2:+0.000;-0.000} {3}",
                        u, b.size.y, b.min.y, grounded ? "" : "AIRBORNE"));
                }
            }
            if (uDeep < 0f)
            {
                sb.AppendLine("  NO GROUNDED POSE FOUND across the clip -- cannot pick a crouch");
                Debug.Log(sb.ToString());
                yield break;
            }
            DeepestU = uDeep;
            sb.AppendLine("  profile (every 10th sample):");
            foreach (var line in prof) { sb.AppendLine("    " + line); }
            sb.AppendFormat("  deepest GROUNDED pose at normalizedTime {0:F3} (height {1:F3} m; tallest {2:F3} m; span {3:F3} m)\n",
                uDeep, hDeep, hMax, hMax - hDeep);
            sb.AppendFormat("  => {0}\n", (hMax - hDeep) > 0.10f
                ? "clip has a real crouch"
                : "NO CROUCH DETECTED -- this clip does not lower the body");

            // Foot close-ups at both ends and at the held pose. These are the S68-B §1.2 checklist
            // shots: the reversal that shipped in S68-A was visible in the wide smoke frames but only
            // a few dozen pixels across, which is how it got past the old three-item checklist.
            float[] us = { 0f, uDeep, 1f };
            string[] labels = { "start", "deepest", "end" };
            for (int k = 0; k < us.Length; k++)
            {
                target.Play(hash, 0, us[k]);
                target.Update(0f);
                yield return null;
                ShootFeet(string.Format("{0}/foot_{1}", outDir, labels[k]));
                sb.AppendFormat("  shot foot_{0} at u={1:F2}\n", labels[k], us[k]);
            }
            Debug.Log(sb.ToString());
        }

        /// <summary>Normalized time of the deepest (most crouched) pose, measured by ProbeFeet.</summary>
        public float DeepestU { get; private set; }

        private void ShootFeet(string stem)
        {
            Render(feet, stem + "_low.png");
            Render(feetBack, stem + "_behind.png");
        }

        private void BuildStage()
        {
            var key = new GameObject("KeyLight").AddComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 1.15f;
            key.transform.rotation = Quaternion.Euler(38f, 150f, 0f);

            var fill = new GameObject("FillLight").AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.45f;
            fill.transform.rotation = Quaternion.Euler(20f, -40f, 0f);

            // A floor, because "are the feet on the ground" is unanswerable against empty space.
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(0.6f, 1f, 0.6f);
            var gr = ground.GetComponent<Renderer>();
            if (gr != null) { gr.material.color = new Color(0.55f, 0.55f, 0.58f); }

            front = MakeCamera("Front", new Vector3(1.9f, 1.05f, 2.4f));
            side = MakeCamera("Side", new Vector3(3.1f, 0.85f, 0.0f));
            // S68-B: close, low, aimed at ankle height -- a rotated foot is only a few dozen pixels
            // in the whole-body shots, which is how the reversal survived the S68-A smoke pass.
            feet = MakeCamera("Feet", new Vector3(0.85f, 0.32f, 1.15f), new Vector3(0f, 0.10f, 0f), 40f);
            feetBack = MakeCamera("FeetBehind", new Vector3(-0.55f, 0.55f, -1.25f), new Vector3(0f, 0.10f, 0f), 40f);
        }

        private static Camera MakeCamera(string name, Vector3 pos)
        {
            // Aimed at hip height: at the crouch pose the whole subject sits below ~1.3 m and a
            // camera pointed at the origin frames the defect out of shot.
            return MakeCamera(name, pos, new Vector3(0f, 0.85f, 0f), 45f);
        }

        private static Camera MakeCamera(string name, Vector3 pos, Vector3 lookAt, float fov)
        {
            var go = new GameObject("Cam" + name);
            go.transform.position = pos;
            go.transform.LookAt(lookAt);
            var cam = go.AddComponent<Camera>();
            cam.enabled = false;              // rendered explicitly, never by the render loop
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 50f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.17f, 0.20f);
            return cam;
        }

        private void Shoot(string stem)
        {
            Render(front, stem + "_front.png");
            Render(side, stem + "_side.png");
        }

        private static void Render(Camera cam, string outPath)
        {
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 4;
            var prev = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Destroy(tex);
            rt.Release();
            Destroy(rt);
        }

        private static void Fail(string msg)
        {
            Debug.LogError("[S68Smoke] " + msg);
            Exit(1);
        }

        private static void Exit(int code)
        {
#if UNITY_EDITOR
            EditorApplication.Exit(code);
#else
            Application.Quit(code);
#endif
        }
    }
}
