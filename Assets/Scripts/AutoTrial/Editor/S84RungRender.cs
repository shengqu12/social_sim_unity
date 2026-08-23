using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 84 rungs R1-R3: render and measure one clip per rung at PERFECTLY UNIFORM 1/30 s
    /// steps, in a bare scene with no modulator, no speed scaler and animator.speed pinned to 1.
    ///
    /// Why uniform stepping is the whole point. The shipped trial capture (TrialController.RunLoop)
    /// fires a frame on the first Update at or after each 1/fps boundary and writes the true elapsed
    /// time into frames.csv -- but the encoder lays those frames on a uniform 15 fps grid and throws
    /// the timestamps away. Measured on S83 V1 that is dt = 66.70 +/- 19.69 ms over a nominal 66.67,
    /// spanning 18..97 ms. These rungs remove that variable so anything still visible is the motion.
    ///
    /// The Rocketbox pedestrian rigs are imported with Optimize Game Objects ON: the prefab has two
    /// transforms and Animator.m_HasTransformHierarchy = 0, so there are NO bone Transforms to read.
    /// Everything downstream therefore measures the two things that still exist on an optimized rig:
    ///   * HumanPoseHandler muscle values -- the retarget's own representation, 95 numbers/frame;
    ///   * SkinnedMeshRenderer.BakeMesh on a fixed vertex subsample -- literally what is on screen.
    ///
    /// R2 vs R3 differ ONLY in AnimatorState.iKOnFeet. That flag is not academic here: the shipped
    /// SocialForcesAnimatorController has iKOnFeet = 1 on `Grounded` (the locomotion blend tree that
    /// the Kimodo gait override lands in) and 0 on `SurprisedReaction` (where b2 lands).
    ///
    /// -executeMethod SEAN.AutoTrial.S84RungRender.Run
    /// </summary>
    public static class S84RungRender
    {
        private const string OutEnv = "AUTOTRIAL_S84_OUT";
        private const string RungsEnv = "AUTOTRIAL_S84_RUNGS";   // comma list, empty = all
        private const string WorkDir = "Assets/PedestrianAssets/Kimodo/S84";
        private const string TargetPrefab = "Prefabs/Rocketbox/Business_Male_01";
        private const int W = 960, H = 540, FPS = 30, VertSample = 240;

        private class Rung
        {
            public string tag;
            public string clipPath;      // asset holding the clip
            public bool ik;
            public bool render = true;
        }

        public static void Run()
        {
            string outDir = System.Environment.GetEnvironmentVariable(OutEnv);
            if (string.IsNullOrEmpty(outDir)) { Debug.LogError("[S84] set " + OutEnv); EditorApplication.Exit(1); return; }
            string only = System.Environment.GetEnvironmentVariable(RungsEnv) ?? "";
            var wanted = only.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

            const string walk = "Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk.fbx";
            const string b2 = "Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx";
            const string oldman = "Assets/PedestrianAssets/Mixamo/Old Man Walk.fbx";

            var rungs = new List<Rung>
            {
                new Rung { tag = "walk_R2_noik", clipPath = walk,   ik = false },
                new Rung { tag = "walk_R3_ik",   clipPath = walk,   ik = true  },
                new Rung { tag = "b2_R2_noik",   clipPath = b2,     ik = false },
                new Rung { tag = "b2_R3_ik",     clipPath = b2,     ik = true  },
                // CONTROL: a Mixamo clip through the identical harness. Without this the Kimodo
                // numbers have nothing to be "high" relative to.
                new Rung { tag = "oldman_R2_noik", clipPath = oldman, ik = false },
                new Rung { tag = "oldman_R3_ik",   clipPath = oldman, ik = true  },
                // b2 BEFORE the S84 T-pose fix: an S84-folder copy configured exactly as S83 left
                // the shipped asset (Humanoid, explicit bone map, no elbow pre-bend). Same body,
                // same lighting, same uniform stepping as the AFTER rung above -- only the Avatar's
                // reference pose differs.
                new Rung { tag = "b2_BEFORE_noik",
                           clipPath = "Assets/PedestrianAssets/Kimodo/S84/b2_R2_selfrig.fbx", ik = false },
            };

            if (!AssetDatabase.IsValidFolder(WorkDir))
                AssetDatabase.CreateFolder("Assets/PedestrianAssets/Kimodo", "S84");

            foreach (var r in rungs)
            {
                if (wanted.Count > 0 && !wanted.Contains(r.tag)) continue;
                try { Do(r, outDir); }
                catch (Exception e) { Debug.LogError("[S84] rung '" + r.tag + "' FAILED: " + e); }
            }
            Debug.Log("[S84] rung render complete -> " + outDir);
        }

        private static void Do(Rung r, string outDir)
        {
            var clip = AssetDatabase.LoadAllAssetsAtPath(r.clipPath).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (clip == null) throw new Exception("no clip at " + r.clipPath);

            string ctrlPath = WorkDir + "/" + r.tag + ".controller";
            AssetDatabase.DeleteAsset(ctrlPath);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ctrlPath);
            var st = ctrl.layers[0].stateMachine.AddState("S84");
            st.motion = clip;
            st.iKOnFeet = r.ik;
            st.writeDefaultValues = true;
            ctrl.layers[0].stateMachine.defaultState = st;

            var prefab = Resources.Load<GameObject>(TargetPrefab);
            if (prefab == null) throw new Exception("no prefab " + TargetPrefab);
            var go = UnityEngine.Object.Instantiate(prefab);
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;

            var an = go.GetComponentInChildren<Animator>();
            an.runtimeAnimatorController = ctrl;
            an.applyRootMotion = true;
            an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            an.speed = 1f;                                  // pinned: no S32AnimatorSpeedScaler here
            an.Rebind();
            an.Update(0f);

            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
            var poser = an.avatar != null && an.avatar.isHuman
                ? new HumanPoseHandler(an.avatar, go.transform) : null;
            var pose = new HumanPose();

            // fixed, evenly spaced vertex subsample -- the same indices every frame and every rung
            var bake = new Mesh();
            smr.BakeMesh(bake);
            int nv = bake.vertexCount;
            var idx = Enumerable.Range(0, VertSample).Select(i => (int)((long)i * nv / VertSample)).ToArray();

            string frameDir = Path.Combine(outDir, "frames_" + r.tag);
            if (r.render) { Directory.CreateDirectory(frameDir); }
            var cam = MakeCamera(out var lightGo);
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);

            int n = Mathf.RoundToInt(clip.length * FPS);
            var rows = new List<string>();
            var mrows = new List<string>();
            try
            {
                for (int i = 0; i <= n; i++)
                {
                    if (i > 0) an.Update(1f / FPS);        // UNIFORM step -- the whole point

                    if (poser != null)
                    {
                        poser.GetHumanPose(ref pose);
                        mrows.Add(i.ToString() + "," + string.Join(",",
                            pose.muscles.Select(m => m.ToString("F5", CultureInfo.InvariantCulture))));
                    }

                    smr.BakeMesh(bake);
                    var verts = bake.vertices;
                    var m = smr.transform.localToWorldMatrix;
                    foreach (int k in idx)
                    {
                        var p = m.MultiplyPoint3x4(verts[k]);
                        rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},v{1},{2:F6},{3:F6},{4:F6}",
                            i, k, p.x, p.y, p.z));
                    }

                    if (r.render)
                    {
                        FrameCamera(cam, go.transform.position);
                        cam.targetTexture = rt;
                        cam.Render();
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                        tex.Apply();
                        RenderTexture.active = null;
                        File.WriteAllBytes(Path.Combine(frameDir, "f_" + i.ToString("D4") + ".png"),
                            tex.EncodeToPNG());
                    }
                }
            }
            finally
            {
                if (poser != null) poser.Dispose();
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(bake);
                UnityEngine.Object.DestroyImmediate(cam.gameObject);
                UnityEngine.Object.DestroyImmediate(lightGo);
                UnityEngine.Object.DestroyImmediate(go);
            }

            File.WriteAllText(Path.Combine(outDir, r.tag + "_verts.csv"),
                "frame,bone,x,y,z\n" + string.Join("\n", rows) + "\n");
            if (mrows.Count > 0)
                File.WriteAllText(Path.Combine(outDir, r.tag + "_muscles.csv"),
                    "frame," + string.Join(",", Enumerable.Range(0, 95).Select(i => "m" + i)) + "\n"
                    + string.Join("\n", mrows) + "\n");
            Debug.Log(string.Format("[S84rung] {0}: clip='{1}' len={2:F4} loop={3} ik={4} frames={5} verts={6}/{7}",
                r.tag, clip.name, clip.length, clip.isLooping, r.ik, n + 1, idx.Length, nv));
        }

        private static Camera MakeCamera(out GameObject lightGo)
        {
            var camGo = new GameObject("S84Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.05f;
            lightGo = new GameObject("S84Light");
            var li = lightGo.AddComponent<Light>();
            li.type = LightType.Directional;
            li.intensity = 1.25f;
            lightGo.transform.rotation = Quaternion.Euler(38f, 145f, 0f);
            // Batchmode starts with no skybox and no ambient probe, so without this the character
            // renders as a near-black silhouette (measured: mid-frame luma std 7.4).
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);
            var fill = new GameObject("S84Fill");
            fill.transform.SetParent(lightGo.transform, false);
            var fl = fill.AddComponent<Light>();
            fl.type = LightType.Directional;
            fl.intensity = 0.55f;
            fill.transform.rotation = Quaternion.Euler(20f, -60f, 0f);
            return cam;
        }

        /// Three-quarter follow: the character walks several metres, so a fixed camera would leave
        /// frame. Offset is constant, so apparent size is constant across rungs and across frames.
        private static void FrameCamera(Camera cam, Vector3 root)
        {
            Vector3 focus = root + new Vector3(0f, 0.95f, 0f);
            cam.transform.position = focus + new Vector3(1.85f, 0.35f, 1.85f);
            cam.transform.LookAt(focus);
        }
    }
}
