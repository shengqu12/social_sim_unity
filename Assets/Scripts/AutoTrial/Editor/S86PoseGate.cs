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
    /// Session 86 gates G1 (pose) and G2 (twitch), plus the frame sequences the acceptance video is
    /// cut from. Measures POSE, which S84's derivative gates were structurally blind to.
    ///
    /// P1 = the clip on its own imported rig, Generic, no muscle space at all.
    /// P2 = retargeted onto the Rocketbox pedestrian. Those prefabs ship with Optimize Game Objects
    ///      ON (two Transforms, GetBoneTransform returns null), so the instance is passed through
    ///      AnimatorUtility.DeoptimizeTransformHierarchy first -- 2 transforms become 85.
    ///
    /// Scratch assets live under Assets/PedestrianAssets/Kimodo/S86/ and are deleted by the caller.
    ///
    /// -executeMethod SEAN.AutoTrial.S86PoseGate.Run
    /// </summary>
    public static class S86PoseGate
    {
        private const string WorkDir = "Assets/PedestrianAssets/Kimodo/S86";
        private const string TargetPrefab = "Prefabs/Rocketbox/Business_Male_01";
        private const int W = 1000, H = 720, FPS = 30;

        private static readonly (string slot, HumanBodyBones bone, string soma)[] Joints =
        {
            ("Hips", HumanBodyBones.Hips, "Hips"), ("Spine", HumanBodyBones.Spine, "Spine1"),
            ("Chest", HumanBodyBones.Chest, "Spine2"), ("Neck", HumanBodyBones.Neck, "Neck1"),
            ("Head", HumanBodyBones.Head, "Head"),
            ("LeftUpperArm", HumanBodyBones.LeftUpperArm, "LeftArm"),
            ("LeftLowerArm", HumanBodyBones.LeftLowerArm, "LeftForeArm"),
            ("LeftHand", HumanBodyBones.LeftHand, "LeftHand"),
            ("RightUpperArm", HumanBodyBones.RightUpperArm, "RightArm"),
            ("RightLowerArm", HumanBodyBones.RightLowerArm, "RightForeArm"),
            ("RightHand", HumanBodyBones.RightHand, "RightHand"),
            ("LeftUpperLeg", HumanBodyBones.LeftUpperLeg, "LeftLeg"),
            ("LeftLowerLeg", HumanBodyBones.LeftLowerLeg, "LeftShin"),
            ("LeftFoot", HumanBodyBones.LeftFoot, "LeftFoot"),
            ("LeftToes", HumanBodyBones.LeftToes, "LeftToeBase"),
            ("RightUpperLeg", HumanBodyBones.RightUpperLeg, "RightLeg"),
            ("RightLowerLeg", HumanBodyBones.RightLowerLeg, "RightShin"),
            ("RightFoot", HumanBodyBones.RightFoot, "RightFoot"),
            ("RightToes", HumanBodyBones.RightToes, "RightToeBase"),
        };

        public static void Run()
        {
            string outDir = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S86_OUT");
            if (string.IsNullOrEmpty(outDir)) { Debug.LogError("[S86pg] set AUTOTRIAL_S86_OUT"); EditorApplication.Exit(1); return; }
            Directory.CreateDirectory(outDir);
            bool render = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S86_RENDER"));

            foreach (var (tag, fbx) in new[]
            {
                ("b2", "Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx"),
                ("b6", WorkDir + "/kimodo_b6_surprised.fbx"),
            })
            {
                if (!File.Exists(fbx)) { Debug.LogWarning("[S86pg] skip " + tag + ": " + fbx + " absent"); continue; }
                try
                {
                    SampleP1(tag, fbx, outDir, render);
                    Sample(tag + "_P2", fbx, outDir, true, render);
                }
                catch (Exception e) { Debug.LogError("[S86pg] " + tag + " FAILED: " + e); }
            }
            Debug.Log("[S86pg] done -> " + outDir);
        }

        /// A Generic copy: raw transform curves on the clip's own rig, no muscle space, no avatar.
        /// This is the pose ground truth inside Unity -- it matched the raw BVH to four figures in
        /// S85, twice, so it is the row the retarget is graded against.
        private static void SampleP1(string tag, string fbx, string outDir, bool render)
        {
            if (!AssetDatabase.IsValidFolder(WorkDir))
                AssetDatabase.CreateFolder("Assets/PedestrianAssets/Kimodo", "S86");
            string gen = WorkDir + "/" + tag + "_P1_generic.fbx";
            if (!File.Exists(gen)) File.Copy(fbx, gen);
            AssetDatabase.ImportAsset(gen, ImportAssetOptions.ForceSynchronousImport);
            var imp = (ModelImporter)AssetImporter.GetAtPath(gen);
            imp.animationType = ModelImporterAnimationType.Generic;
            imp.animationCompression = ModelImporterAnimationCompression.Off;
            imp.SaveAndReimport();
            Sample(tag + "_P1", gen, outDir, false, render);
        }

        private static void Sample(string label, string fbx, string outDir, bool onTarget, bool render)
        {
            var clip = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (clip == null) throw new Exception("no clip at " + fbx);

            var scaler = new GameObject("S86Scale");
            scaler.transform.localScale = Vector3.one * (onTarget ? 1f : 0.01f);
            GameObject go; Animator an;
            if (onTarget)
            {
                go = UnityEngine.Object.Instantiate(Resources.Load<GameObject>(TargetPrefab));
                an = go.GetComponentInChildren<Animator>();
                int before = go.GetComponentsInChildren<Transform>(true).Length;
                AnimatorUtility.DeoptimizeTransformHierarchy(go);
                Debug.Log("[S86pg] " + label + ": deoptimized " + before + " -> "
                          + go.GetComponentsInChildren<Transform>(true).Length + " transforms");
            }
            else
            {
                go = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(fbx));
                an = go.GetComponentInChildren<Animator>();
                if (render)
                {
                    // The Kimodo FBXs are armature-only (bvh_to_fbx.py exports object_types={"ARMATURE"}),
                    // so there is nothing to shade -- a cube per bone reads the same way as the S76
                    // BVH skeleton previews, and shares this file's camera so the two panels of the
                    // acceptance video are framed identically.
                    foreach (var b in go.GetComponentsInChildren<Transform>(true))
                    {
                        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
                        cube.transform.SetParent(b, false);
                        cube.transform.localScale = Vector3.one * 5.5f;
                    }
                }
            }
            go.transform.SetParent(scaler.transform, false);

            var all = go.GetComponentsInChildren<Transform>(true);
            var find = new Dictionary<string, Transform>();
            foreach (var j in Joints)
            {
                Transform t = null;
                if (onTarget && an != null && an.avatar != null && an.avatar.isHuman)
                    t = an.GetBoneTransform(j.bone);
                if (t == null) t = all.FirstOrDefault(x => x.name == j.soma);
                if (t == null) throw new Exception(label + ": no transform for " + j.slot);
                find[j.slot] = t;
            }

            Camera cam = null; GameObject lightGo = null; RenderTexture rt = null; Texture2D tex = null;
            string frameDir = Path.Combine(outDir, "frames_" + label);
            if (render)
            {
                cam = MakeCamera(out lightGo);
                rt = new RenderTexture(W, H, 24) { antiAliasing = 4 };
                tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                Directory.CreateDirectory(frameDir);
            }

            // On the Rocketbox target, drive the Animator itself rather than
            // AnimationMode.SampleAnimationClip. Measured: on a DEOPTIMIZED instance, SampleAnimationClip
            // poses the rebuilt bone Transforms correctly but the SkinnedMeshRenderer does not follow --
            // the joint CSV showed the left hand 0.20 m from the head while the render showed both arms
            // hanging. Reading transforms and rendering pixels must come from the SAME evaluation, so
            // the target path uses a generated single-state controller and animator.Update, which is the
            // path S84's renders used and which provably tracked. animator.speed stays at 1: no
            // S32AnimatorSpeedScaler here.
            AnimatorController ctrl = null;
            if (onTarget)
            {
                string cp = WorkDir + "/" + label + ".controller";
                AssetDatabase.DeleteAsset(cp);
                ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
                var st = ctrl.layers[0].stateMachine.AddState("S86");
                st.motion = clip;
                st.writeDefaultValues = true;
                ctrl.layers[0].stateMachine.defaultState = st;
                an.runtimeAnimatorController = ctrl;
                an.applyRootMotion = false;
                an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                an.speed = 1f;
                an.Rebind();
                an.Update(0f);
            }

            // Render from a SECOND, UNTOUCHED instance. Measured the hard way: this prefab's
            // SkinnedMeshRenderer ships with m_Bones: [] and m_RootBone: 0 -- the mesh was never
            // bound to bone Transforms, it is driven by the Animator's internal skeleton. So on a
            // DEOPTIMIZED instance the rebuilt Transforms animate correctly (the joint CSV is right)
            // while the mesh stays in bind pose: at frame 35 the CSV put the left hand 0.20 m from
            // the head and the render showed both arms hanging. Measurement therefore reads the
            // deoptimized instance and rendering uses an optimized one -- the configuration the
            // trials themselves run.
            GameObject renderGo = null; Animator renderAn = null; GameObject renderScaler = null;
            var bakeProxies = new List<(SkinnedMeshRenderer smr, MeshFilter mf)>();
            // The measurement instance shares the scene and the origin with the render instance, and
            // its own mesh sits in bind pose -- left visible it draws a second, arms-down body over
            // the posed one. Measure with it, never draw it.
            if (onTarget && render)
                foreach (var r0 in go.GetComponentsInChildren<Renderer>(true)) r0.enabled = false;
            if (onTarget && render)
            {
                renderScaler = new GameObject("S86RenderScale");
                renderGo = UnityEngine.Object.Instantiate(Resources.Load<GameObject>(TargetPrefab));
                renderGo.transform.SetParent(renderScaler.transform, false);
                renderAn = renderGo.GetComponentInChildren<Animator>();
                renderAn.runtimeAnimatorController = ctrl;
                renderAn.applyRootMotion = false;
                renderAn.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                renderAn.speed = 1f;
                renderAn.Rebind();
                renderAn.Update(0f);
                // Outside play mode the Animator writes its result but the skinned mesh is not
                // re-skinned for drawing, so the character renders in bind pose no matter how the
                // clip is stepped. BakeMesh forces that evaluation; S84's renders tracked precisely
                // because they baked every frame. The baked mesh is also what gets DRAWN here -- a
                // plain MeshRenderer alongside the (disabled) SkinnedMeshRenderer -- so what the
                // camera sees is exactly what was measured.
                foreach (var smr0 in renderGo.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var proxy = new GameObject("S86Baked");
                    proxy.transform.SetParent(renderScaler.transform, false);
                    proxy.AddComponent<MeshFilter>();
                    var mr = proxy.AddComponent<MeshRenderer>();
                    mr.sharedMaterials = smr0.sharedMaterials;
                    bakeProxies.Add((smr0, proxy.GetComponent<MeshFilter>()));
                    smr0.enabled = false;
                }
            }

            var rows = new List<string>();
            int n = Mathf.RoundToInt(clip.length * FPS);
            if (!onTarget) AnimationMode.StartAnimationMode();
            try
            {
                for (int i = 0; i <= n; i++)
                {
                    if (onTarget)
                    {
                        if (i > 0) { an.Update(1f / FPS); if (renderAn != null) renderAn.Update(1f / FPS); }
                    }
                    else
                    {
                        AnimationMode.BeginSampling();
                        AnimationMode.SampleAnimationClip(go, clip, Mathf.Min(clip.length, i / (float)FPS));
                        AnimationMode.EndSampling();
                    }
                    foreach (var j in Joints)
                    {
                        var p = find[j.slot].position;
                        rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F6},{3:F6},{4:F6}",
                            i, j.slot, p.x, p.y, p.z));
                    }
                    if (render)
                    {
                        foreach (var (smr0, mf) in bakeProxies)
                        {
                            var baked = new Mesh();
                            smr0.BakeMesh(baked);
                            if (mf.sharedMesh != null) UnityEngine.Object.DestroyImmediate(mf.sharedMesh);
                            mf.sharedMesh = baked;
                            mf.transform.SetPositionAndRotation(smr0.transform.position, smr0.transform.rotation);
                            mf.transform.localScale = Vector3.one;
                        }
                        // Front three-quarter derived from the BODY'S OWN axes, not a world-space
                        // offset: the two rigs face different ways, and a fixed offset put the
                        // covering hand behind the torso. Forward comes from foot->toes, and the
                        // camera swings to the character's LEFT because that is the acting hand in
                        // b2; b6 is symmetric so the same framing serves.
                        Vector3 up = Vector3.up;
                        Vector3 right = find["LeftUpperArm"].position - find["RightUpperArm"].position;
                        right.y = 0f; right.Normalize();
                        Vector3 fwd = find["LeftToes"].position - find["LeftFoot"].position;
                        fwd.y = 0f;
                        fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.Cross(up, right).normalized;
                        Vector3 dir = (fwd * 0.80f + right * 0.60f).normalized;
                        Vector3 focus = find["Head"].position + new Vector3(0f, -0.34f, 0f);
                        cam.transform.position = focus + dir * 2.05f + up * 0.14f;
                        cam.transform.LookAt(focus);
                        cam.targetTexture = rt; cam.Render();
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
                        RenderTexture.active = null;
                        File.WriteAllBytes(Path.Combine(frameDir, "f_" + i.ToString("D4") + ".png"), tex.EncodeToPNG());
                    }
                }
            }
            finally { if (!onTarget) AnimationMode.StopAnimationMode(); }

            File.WriteAllText(Path.Combine(outDir, label + ".csv"),
                "frame,bone,x,y,z\n" + string.Join("\n", rows) + "\n");
            if (render)
            {
                UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(cam.gameObject); UnityEngine.Object.DestroyImmediate(lightGo);
            }
            foreach (var (_, mf) in bakeProxies)
                if (mf != null && mf.sharedMesh != null) UnityEngine.Object.DestroyImmediate(mf.sharedMesh);
            if (renderScaler != null) UnityEngine.Object.DestroyImmediate(renderScaler);
            UnityEngine.Object.DestroyImmediate(scaler);
            Debug.Log("[S86pg] " + label + " frames=" + (n + 1) + " clip='" + clip.name + "' len=" + clip.length.ToString("F4"));
        }

        private static Camera MakeCamera(out GameObject lightGo)
        {
            var camGo = new GameObject("S86Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
            cam.fieldOfView = 38f; cam.nearClipPlane = 0.02f;
            lightGo = new GameObject("S86Light");
            var li = lightGo.AddComponent<Light>();
            li.type = LightType.Directional; li.intensity = 1.15f;
            lightGo.transform.rotation = Quaternion.Euler(30f, 150f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.47f, 0.51f);
            return cam;
        }
    }
}
