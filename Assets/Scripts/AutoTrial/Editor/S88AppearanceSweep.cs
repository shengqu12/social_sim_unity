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
    /// Session 88 PHASE A. Retargets one clip onto many Rocketbox appearances and measures, per body,
    /// how close the covering hand gets to THAT body's own mouth.
    ///
    /// The mouth landmark is derived per body by S87's midline-profile method, automated here rather
    /// than pinned: walk the head's midline surface from the nose tip downwards, find the philtrum
    /// recess (first local minimum in forward protrusion), then the LIP BULGE (the next local
    /// maximum). S87 pinned business_male_01 at up=+0.0403 with the nose at +0.0693; this code has to
    /// find that on its own, and logs which body fell back to the nose-minus-29 mm default.
    ///
    /// Only two rulers are used for contact, both validated in S87 against the uncalibrated baseline
    /// (which reads zero on each): head vertices inside a capsule around the wrist->elbow segment,
    /// and head vertices enclosed by the convex hull of the hand. Three signed-distance metrics were
    /// falsified in S87 and are not repeated -- see that session's notes.
    ///
    /// Read-only: instantiates prefabs, writes no asset. The generated controller is scratch.
    ///
    /// -executeMethod SEAN.AutoTrial.S88AppearanceSweep.Run
    /// </summary>
    public static class S88AppearanceSweep
    {
        private const string WorkDir = "Assets/PedestrianAssets/Kimodo/S88";
        private const int W = 1000, H = 720, FPS = 30;
        public const float HeadRadius = 0.17f;
        public const float MidlineHalfWidth = 0.022f;
        public const float FallbackBelowNose = 0.029f;

        public static void Run()
        {
            string outDir = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S88_OUT");
            string fbx = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S88_FBX")
                         ?? "Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx";
            string clipTag = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S88_CLIPTAG") ?? "b2";
            string bodies = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S88_BODIES");
            bool render = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S88_RENDER"));
            bool renderAll = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S88_RENDER") == "all";
            if (string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(bodies))
            { Debug.LogError("[S88] set AUTOTRIAL_S88_OUT and AUTOTRIAL_S88_BODIES"); EditorApplication.Exit(1); return; }
            Directory.CreateDirectory(outDir);
            if (!AssetDatabase.IsValidFolder(WorkDir))
                AssetDatabase.CreateFolder("Assets/PedestrianAssets/Kimodo", "S88");

            // AUTOTRIAL_S88_IMPORT copies a sandbox FBX in and configures it through the S86
            // importer (walk-donor reference pose, no pre-bend) before measuring.
            string import = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S88_IMPORT");
            if (!string.IsNullOrEmpty(import))
            {
                string dst = WorkDir + "/" + clipTag + ".fbx";
                if (!AssetDatabase.IsValidFolder(WorkDir))
                    AssetDatabase.CreateFolder("Assets/PedestrianAssets/Kimodo", "S88");
                if (!File.Exists(dst)) File.Copy(import, dst);
                AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);
                S86KimodoAvatarRefPose.ApplyTo(dst);
                fbx = dst;
            }

            var clip = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (clip == null) { Debug.LogError("[S88] no clip at " + fbx); EditorApplication.Exit(1); return; }

            string cp = WorkDir + "/s88_" + clipTag + ".controller";
            AssetDatabase.DeleteAsset(cp);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
            var st = ctrl.layers[0].stateMachine.AddState("S88");
            st.motion = clip; st.writeDefaultValues = true;
            ctrl.layers[0].stateMachine.defaultState = st;

            foreach (var body in bodies.Split(',').Select(b => b.Trim()).Where(b => b.Length > 0))
            {
                try { One(body, clip, ctrl, clipTag, outDir, render, renderAll); }
                catch (Exception e) { Debug.LogError("[S88] " + body + " FAILED: " + e.Message); }
            }
            Debug.Log("[S88] sweep complete -> " + outDir);
        }

        private static void One(string body, AnimationClip clip, AnimatorController ctrl,
                                string clipTag, string outDir, bool render, bool renderAll)
        {
            var prefab = Resources.Load<GameObject>("Prefabs/Rocketbox/" + body);
            if (prefab == null) { Debug.LogError("[S88] no prefab for " + body); return; }
            string key = clipTag + "__" + body;

            var mGo = UnityEngine.Object.Instantiate(prefab);
            var mAn = mGo.GetComponentInChildren<Animator>();
            AnimatorUtility.DeoptimizeTransformHierarchy(mGo);
            mAn.runtimeAnimatorController = ctrl; mAn.applyRootMotion = false;
            mAn.cullingMode = AnimatorCullingMode.AlwaysAnimate; mAn.speed = 1f;
            mAn.Rebind(); mAn.Update(0f);
            foreach (var r in mGo.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            var bGo = UnityEngine.Object.Instantiate(prefab);
            var bAn = bGo.GetComponentInChildren<Animator>();
            bAn.runtimeAnimatorController = ctrl; bAn.applyRootMotion = false;
            bAn.cullingMode = AnimatorCullingMode.AlwaysAnimate; bAn.speed = 1f;
            bAn.Rebind(); bAn.Update(0f);
            var smr = bGo.GetComponentInChildren<SkinnedMeshRenderer>();
            // S86: this prefab family never re-skins outside play mode. Bake to a proxy and draw that,
            // so the pixels and the numbers come from one evaluation.
            MeshFilter proxy = null;
            if (render)
            {
                var pg = new GameObject("S88Baked");
                proxy = pg.AddComponent<MeshFilter>();
                pg.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials;
                smr.enabled = false;
            }

            Transform Head = mAn.GetBoneTransform(HumanBodyBones.Head);
            Transform LHand = mAn.GetBoneTransform(HumanBodyBones.LeftHand);
            Transform LElb = mAn.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            Transform LMid = mAn.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            Transform LUp = mAn.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            Transform RUp = mAn.GetBoneTransform(HumanBodyBones.RightUpperArm);
            Transform LFoot = mAn.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform LToe = mAn.GetBoneTransform(HumanBodyBones.LeftToes);
            if (Head == null || LHand == null) { Debug.LogError("[S88] " + body + ": rig lacks Head/LeftHand"); return; }

            var bake = new Mesh();
            Func<Vector3[]> Bake = () =>
            {
                smr.BakeMesh(bake);
                var m = smr.transform.localToWorldMatrix;
                return bake.vertices.Select(v => m.MultiplyPoint3x4(v)).ToArray();
            };

            Vector3 right = LUp.position - RUp.position; right.y = 0; right.Normalize();
            Vector3 fwd = LToe != null ? LToe.position - LFoot.position : Vector3.zero; fwd.y = 0;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.Cross(Vector3.up, right).normalized;
            Vector3 up = Vector3.up;

            var v0 = Bake();
            var headIdx = Enumerable.Range(0, v0.Length)
                .Where(i => (v0[i] - Head.position).sqrMagnitude < HeadRadius * HeadRadius).ToArray();
            var handIdx = Enumerable.Range(0, v0.Length)
                .Where(i => (v0[i] - LHand.position).sqrMagnitude < 0.10f * 0.10f).ToArray();
            Func<Vector3, float> F = v => Vector3.Dot(v - Head.position, fwd);
            Func<Vector3, float> U = v => Vector3.Dot(v - Head.position, up);
            Func<Vector3, float> X = v => Vector3.Dot(v - Head.position, right);
            var mid = headIdx.Select(i => v0[i]).Where(v => Mathf.Abs(X(v)) < MidlineHalfWidth && F(v) > 0f).ToArray();
            if (mid.Length < 30) { Debug.LogError("[S88] " + body + ": only " + mid.Length + " midline verts"); return; }

            float noseU = U(mid.OrderByDescending(F).First());
            // profile, 2 mm bins, smoothed
            var us = new List<float>(); var fs = new List<float>();
            for (float u = noseU + 0.004f; u > noseU - 0.13f; u -= 0.002f)
            {
                var band = mid.Where(v => Mathf.Abs(U(v) - u) < 0.0018f).ToArray();
                if (band.Length == 0) continue;
                us.Add(u); fs.Add(band.Max(F));
            }
            var sm = new float[fs.Count];
            for (int i = 0; i < fs.Count; i++)
                sm[i] = (fs[Mathf.Max(0, i - 1)] + fs[i] + fs[Mathf.Min(fs.Count - 1, i + 1)]) / 3f;
            int iNose = 0; for (int i = 0; i < sm.Length; i++) if (sm[i] > sm[iNose]) iNose = i;
            int iRecess = -1, iLip = -1;
            for (int i = iNose + 1; i < sm.Length - 1; i++)
                if (sm[i] <= sm[i - 1] && sm[i] <= sm[i + 1]) { iRecess = i; break; }
            if (iRecess > 0)
                for (int i = iRecess + 1; i < sm.Length - 1; i++)
                    if (sm[i] >= sm[i - 1] && sm[i] >= sm[i + 1]) { iLip = i; break; }
            float mouthU; string how;
            if (iLip > 0 && us[iNose] - us[iLip] > 0.015f && us[iNose] - us[iLip] < 0.075f)
            { mouthU = us[iLip]; how = "lip-bulge"; }
            else { mouthU = us[iNose] - FallbackBelowNose; how = "FALLBACK nose-29mm"; }

            var bandM = mid.Where(v => Mathf.Abs(U(v) - mouthU) < 0.004f).ToArray();
            int take = Mathf.Max(1, bandM.Length / 4);
            Vector3 mouth = bandM.Length > 0
                ? bandM.OrderByDescending(F).Take(take).Aggregate(Vector3.zero, (a, v) => a + v) / take
                : mid.OrderByDescending(F).First();
            Vector3 mouthLocal = Head.InverseTransformPoint(mouth);

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[S88mouth] {0,-22} nose(up={1:F4} fwd={2:F4})  mouth(up={3:F4} fwd={4:F4} lat={5:F4}) via {6}  "
                + "headLocal=({7:F5},{8:F5},{9:F5})  headVerts={10} handVerts={11}",
                body, noseU, sm[iNose], U(mouth), F(mouth), X(mouth), how,
                mouthLocal.x, mouthLocal.y, mouthLocal.z, headIdx.Length, handIdx.Length));

            File.WriteAllLines(Path.Combine(outDir, "headcloud_" + key + ".csv"),
                new[] { "x,y,z" }.Concat(headIdx.Select(i =>
                { var q = Head.InverseTransformPoint(v0[i]);
                  return string.Format(CultureInfo.InvariantCulture, "{0:F5},{1:F5},{2:F5}", q.x, q.y, q.z); })));

            Camera cam = null; GameObject lightGo = null; RenderTexture rt = null; Texture2D tex = null; GameObject marker = null;
            if (render)
            {
                cam = MakeCamera(out lightGo);
                rt = new RenderTexture(W, H, 24) { antiAliasing = 4 };
                tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                UnityEngine.Object.DestroyImmediate(marker.GetComponent<Collider>());
                marker.transform.localScale = Vector3.one * 0.014f;
                var mat = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Standard"));
                mat.color = new Color(1f, 0.25f, 0.2f);
                marker.GetComponent<MeshRenderer>().sharedMaterial = mat;
                Directory.CreateDirectory(Path.Combine(outDir, "frames_" + key));
            }

            var rows = new List<string>(); var handCloud = new List<string>();
            int n = Mathf.RoundToInt(clip.length * FPS);
            for (int i = 0; i <= n; i++)
            {
                if (i > 0) { mAn.Update(1f / FPS); bAn.Update(1f / FPS); }
                var vs = Bake();
                if (proxy != null)
                {
                    var drawn = new Mesh { vertices = bake.vertices, normals = bake.normals, uv = bake.uv };
                    drawn.subMeshCount = bake.subMeshCount;
                    for (int sIdx = 0; sIdx < bake.subMeshCount; sIdx++) drawn.SetTriangles(bake.GetTriangles(sIdx), sIdx);
                    if (proxy.sharedMesh != null) UnityEngine.Object.DestroyImmediate(proxy.sharedMesh);
                    proxy.sharedMesh = drawn;
                    proxy.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
                }
                Vector3 mouthW = Head.TransformPoint(mouthLocal);
                Vector3 palm = LMid != null ? Vector3.Lerp(LHand.position, LMid.position, 0.6f) : LHand.position;
                float dMesh = handIdx.Min(k => Vector3.Distance(vs[k], mouthW));
                Vector3 miss = mouthW - palm;
                Vector3 wristL = Head.InverseTransformPoint(LHand.position);
                Vector3 elbowL = Head.InverseTransformPoint(LElb.position);
                rows.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:F5},{2:F5},{3:F5},{4:F5},{5:F5},{6:F5},{7:F5},{8:F5},{9:F5},{10:F5},{11:F5}",
                    i, Vector3.Distance(palm, mouthW), dMesh,
                    Vector3.Dot(miss, up), Vector3.Dot(miss, right), Vector3.Dot(miss, fwd),
                    wristL.x, wristL.y, wristL.z, elbowL.x, elbowL.y, elbowL.z));
                if (i >= 20 && i <= 100)
                    foreach (int hi in handIdx)
                    { var q = Head.InverseTransformPoint(vs[hi]);
                      handCloud.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1:F5},{2:F5},{3:F5}", i, q.x, q.y, q.z)); }

                bool shoot = render && (renderAll || i == 0 || (i >= 20 && i <= 100 && i % 5 == 0));
                if (shoot)
                {
                    marker.transform.position = mouthW;
                    Vector3 focus = Head.position + up * -0.02f;
                    cam.transform.position = focus + fwd * 0.70f + up * 0.06f;
                    cam.transform.LookAt(focus);
                    cam.targetTexture = rt; cam.Render();
                    RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes(Path.Combine(outDir, "frames_" + key, "f_" + i.ToString("D4") + ".png"), tex.EncodeToPNG());
                }
            }

            File.WriteAllText(Path.Combine(outDir, "contact_" + key + ".csv"),
                "frame,d_palm_mouth,d_handmesh_mouth,miss_low,miss_lat,miss_fwd,"
                + "wristL_x,wristL_y,wristL_z,elbowL_x,elbowL_y,elbowL_z\n" + string.Join("\n", rows) + "\n");
            File.WriteAllText(Path.Combine(outDir, "handcloud_" + key + ".csv"),
                "frame,x,y,z\n" + string.Join("\n", handCloud) + "\n");

            if (render)
            {
                if (proxy != null && proxy.sharedMesh != null) UnityEngine.Object.DestroyImmediate(proxy.sharedMesh);
                if (proxy != null) UnityEngine.Object.DestroyImmediate(proxy.gameObject);
                UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(cam.gameObject); UnityEngine.Object.DestroyImmediate(lightGo);
                UnityEngine.Object.DestroyImmediate(marker);
            }
            UnityEngine.Object.DestroyImmediate(bake);
            UnityEngine.Object.DestroyImmediate(mGo); UnityEngine.Object.DestroyImmediate(bGo);
            Debug.Log("[S88] " + key + " measured");
        }

        private static Camera MakeCamera(out GameObject lightGo)
        {
            var camGo = new GameObject("S88Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
            cam.fieldOfView = 34f; cam.nearClipPlane = 0.01f;
            lightGo = new GameObject("S88Light");
            var li = lightGo.AddComponent<Light>();
            li.type = LightType.Directional; li.intensity = 1.15f;
            lightGo.transform.rotation = Quaternion.Euler(28f, 150f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.48f, 0.50f, 0.54f);
            return cam;
        }
    }
}
