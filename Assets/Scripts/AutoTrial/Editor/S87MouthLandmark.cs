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
    /// Session 87 STEP 1 + STEP 2. Derives a MOUTH landmark on the Rocketbox business_male_01 head
    /// from the baked head mesh, then measures where b2's covering hand actually lands relative to it.
    ///
    /// Why a new ruler. S86's G1 gated on |LeftHand - Head JOINT|. The Head joint sits inside the
    /// skull, so that distance cannot distinguish a hand on the mouth from a hand on the jaw or the
    /// cheek -- both are ~0.13 of head height away. Contact gating needs a point ON THE FACE.
    ///
    /// How the landmark is derived (frame 0, hand at the hip, so the hand cannot contaminate the
    /// head vertex selection):
    ///   1. head vertices  = baked vertices within HeadRadius of the Head joint
    ///   2. body axes      = right from the two upper arms, forward from foot->toes, up = world up
    ///   3. nose tip       = the head vertex furthest along +forward, near the lateral midline
    ///   4. chin           = the lowest front-half head vertex near the midline
    ///   5. mouth height   = MouthFraction of the way from the nose tip down to the chin
    ///   6. mouth landmark = the furthest-forward midline vertex at that height
    /// and the result is stored as Head-LOCAL coordinates so it rides the head for every frame.
    ///
    /// Rendering and measurement come from two instances on purpose (S86 note): this prefab ships
    /// with SkinnedMeshRenderer.m_Bones empty, so a deoptimized instance animates its Transforms
    /// correctly but never re-skins the mesh. Joints are read from the deoptimized instance; the mesh
    /// is baked from an optimized one stepped in lockstep.
    ///
    /// -executeMethod SEAN.AutoTrial.S87MouthLandmark.Run
    /// </summary>
    public static class S87MouthLandmark
    {
        private const string TargetPrefab = "Prefabs/Rocketbox/Business_Male_01";
        private const string WorkDir = "Assets/PedestrianAssets/Kimodo/S87";
        private const int W = 1000, H = 720, FPS = 30;

        public const float HeadRadius = 0.17f;      // metres around the Head joint that count as head
        public const float MidlineHalfWidth = 0.022f;
        public const float MouthFraction = 0.42f;   // nose tip -> chin; unused once MouthUp is pinned

        /// Height of the mouth landmark above the Head joint, in metres, PINNED from the head's own
        /// midline surface profile rather than interpolated. The profile (see midline_profile.csv)
        /// shows the nose tip at up=+0.0693 fwd=0.1367, a philtrum recess at up=+0.055 fwd=0.1196,
        /// then the LIP BULGE -- a genuine local forward maximum -- at up=+0.041..+0.045 fwd=0.1232,
        /// falling away again below. The first attempt interpolated nose->chin and landed on the
        /// chin, because the 0.17 m head ball reaches down the neck and the "chin" search found a
        /// throat vertex. Verified by render: the marker sits on the lip.
        public const float MouthUp = 0.0403f;

        /// Import a list of scratch variant FBXs through the S86 pipeline and measure each against
        /// the same landmark, in one editor launch. AUTOTRIAL_S87_BATCH is "tag=path,tag=path,...".
        public static void RunBatch()
        {
            string batch = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S87_BATCH");
            if (string.IsNullOrEmpty(batch)) { Debug.LogError("[S87] set AUTOTRIAL_S87_BATCH"); EditorApplication.Exit(1); return; }
            foreach (var item in batch.Split(','))
            {
                var kv = item.Split('=');
                if (kv.Length != 2) continue;
                string tag = kv[0].Trim(), src = kv[1].Trim();
                string dst = WorkDir + "/" + tag + ".fbx";
                if (!AssetDatabase.IsValidFolder(WorkDir))
                    AssetDatabase.CreateFolder("Assets/PedestrianAssets/Kimodo", "S87");
                if (!File.Exists(dst)) File.Copy(src, dst);
                AssetDatabase.ImportAsset(dst, ImportAssetOptions.ForceSynchronousImport);
                S86KimodoAvatarRefPose.ApplyTo(dst);       // walk-donor reference, no pre-bend
                Measure(dst, tag, !string.IsNullOrEmpty(
                    System.Environment.GetEnvironmentVariable("AUTOTRIAL_S87_RENDER")));
            }
            Debug.Log("[S87] batch complete");
        }

        public static void Run()
        {
            string outDir = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S87_OUT");
            string fbx = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S87_FBX")
                         ?? "Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx";
            string tag = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S87_TAG") ?? "shipped";
            bool render = !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S87_RENDER"));
            if (string.IsNullOrEmpty(outDir)) { Debug.LogError("[S87] set AUTOTRIAL_S87_OUT"); EditorApplication.Exit(1); return; }
            Directory.CreateDirectory(outDir);
            if (!AssetDatabase.IsValidFolder(WorkDir))
                AssetDatabase.CreateFolder("Assets/PedestrianAssets/Kimodo", "S87");
            Measure(fbx, tag, render);
        }

        private static void Measure(string fbx, string tag, bool render)
        {
            string outDir = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S87_OUT");
            var clip = AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));
            if (clip == null) { Debug.LogError("[S87] no clip at " + fbx); EditorApplication.Exit(1); return; }

            string cp = WorkDir + "/s87_" + tag + ".controller";
            AssetDatabase.DeleteAsset(cp);
            var ctrl = AnimatorController.CreateAnimatorControllerAtPath(cp);
            var st = ctrl.layers[0].stateMachine.AddState("S87");
            st.motion = clip; st.writeDefaultValues = true;
            ctrl.layers[0].stateMachine.defaultState = st;

            // measurement instance: deoptimized, joints readable
            var mGo = UnityEngine.Object.Instantiate(Resources.Load<GameObject>(TargetPrefab));
            var mAn = mGo.GetComponentInChildren<Animator>();
            AnimatorUtility.DeoptimizeTransformHierarchy(mGo);
            mAn.runtimeAnimatorController = ctrl; mAn.applyRootMotion = false;
            mAn.cullingMode = AnimatorCullingMode.AlwaysAnimate; mAn.speed = 1f;
            mAn.Rebind(); mAn.Update(0f);
            foreach (var r in mGo.GetComponentsInChildren<Renderer>(true)) r.enabled = false;

            // bake/render instance: left optimized, so the mesh actually skins
            var bGo = UnityEngine.Object.Instantiate(Resources.Load<GameObject>(TargetPrefab));
            var bAn = bGo.GetComponentInChildren<Animator>();
            bAn.runtimeAnimatorController = ctrl; bAn.applyRootMotion = false;
            bAn.cullingMode = AnimatorCullingMode.AlwaysAnimate; bAn.speed = 1f;
            bAn.Rebind(); bAn.Update(0f);
            var smr = bGo.GetComponentInChildren<SkinnedMeshRenderer>();
            // S86's lesson, which this probe initially failed to carry over: this prefab's
            // SkinnedMeshRenderer has an empty m_Bones array, so outside play mode it never
            // re-skins -- rendering it directly draws the BIND pose, arms down, no matter what the
            // clip is doing. The measured mesh must also be the DRAWN mesh: bake to a MeshFilter
            // proxy each frame and disable the SMR.
            MeshFilter bakeProxy = null;
            {
                var proxy = new GameObject("S87Baked");
                bakeProxy = proxy.AddComponent<MeshFilter>();
                var mr = proxy.AddComponent<MeshRenderer>();
                mr.sharedMaterials = smr.sharedMaterials;
                smr.enabled = false;
            }

            Transform Head = mAn.GetBoneTransform(HumanBodyBones.Head);
            Transform LHand = mAn.GetBoneTransform(HumanBodyBones.LeftHand);
            Transform LMid = mAn.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            Transform LIdx = mAn.GetBoneTransform(HumanBodyBones.LeftIndexProximal);
            Transform LElb = mAn.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            Transform LUp = mAn.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            Transform RUp = mAn.GetBoneTransform(HumanBodyBones.RightUpperArm);
            Transform LFoot = mAn.GetBoneTransform(HumanBodyBones.LeftFoot);
            Transform LToe = mAn.GetBoneTransform(HumanBodyBones.LeftToes);
            Debug.Log("[S87] finger bones: LeftMiddleProximal=" + (LMid ? LMid.name : "NULL")
                      + " LeftIndexProximal=" + (LIdx ? LIdx.name : "NULL"));

            var bake = new Mesh();
            Func<Vector3[]> Bake = () =>
            {
                smr.BakeMesh(bake);
                var m = smr.transform.localToWorldMatrix;
                return bake.vertices.Select(v => m.MultiplyPoint3x4(v)).ToArray();
            };

            // ---- STEP 1: the landmark, from frame 0 ----
            Vector3 right = LUp.position - RUp.position; right.y = 0; right.Normalize();
            Vector3 fwd = LToe.position - LFoot.position; fwd.y = 0;
            fwd = fwd.sqrMagnitude > 1e-6f ? fwd.normalized : Vector3.Cross(Vector3.up, right).normalized;
            Vector3 up = Vector3.up;

            var v0 = Bake();
            var head = v0.Where(v => (v - Head.position).sqrMagnitude < HeadRadius * HeadRadius).ToArray();
            Func<Vector3, float> F = v => Vector3.Dot(v - Head.position, fwd);
            Func<Vector3, float> U = v => Vector3.Dot(v - Head.position, up);
            Func<Vector3, float> X = v => Vector3.Dot(v - Head.position, right);
            var mid = head.Where(v => Mathf.Abs(X(v)) < MidlineHalfWidth).ToArray();
            Vector3 nose = mid.OrderByDescending(F).First();
            Vector3 chin = mid.Where(v => F(v) > 0f).OrderBy(U).First();
            // The first pass put the landmark on the chin: the "chin" search had picked a THROAT
            // vertex (the 0.17 m head ball reaches well down the neck), which dragged the
            // interpolation far too low. Dump the midline surface profile so the lip bulge -- a real
            // local forward maximum below the nose -- can be found from geometry instead of a guess.
            {
                var prof = new List<string>();
                for (float u = U(nose) + 0.01f; u > U(nose) - 0.14f; u -= 0.002f)
                {
                    var band0 = mid.Where(v => Mathf.Abs(U(v) - u) < 0.0015f).ToArray();
                    if (band0.Length == 0) continue;
                    prof.Add(string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2}",
                        u, band0.Max(F), band0.Length));
                }
                File.WriteAllText(Path.Combine(outDir, "midline_profile.csv"),
                    "up_from_head_joint,max_forward,n\n" + string.Join("\n", prof) + "\n");
            }

            float mouthU = MouthUp;
            {   // explicit override, so the landmark can be pinned once the profile is read
                string envU = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S87_MOUTH_UP");
                if (!string.IsNullOrEmpty(envU)) mouthU = float.Parse(envU, CultureInfo.InvariantCulture);
            }
            var band = mid.Where(v => Mathf.Abs(U(v) - mouthU) < 0.004f && F(v) > 0f).ToArray();
            // Average the front-most few in the band rather than taking a single extreme vertex: one
            // vertex can sit 1 cm off the midline on this topology, which is not "front-centre".
            Vector3 mouth = band.Length > 0
                ? band.OrderByDescending(F).Take(Mathf.Max(1, band.Length / 4))
                      .Aggregate(Vector3.zero, (acc, v) => acc + v) / Mathf.Max(1, band.Length / 4)
                : mid.OrderByDescending(F).First();
            Vector3 mouthLocal = Head.InverseTransformPoint(mouth);

            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[S87landmark] headVerts={0} midlineVerts={1}  nose(fwd={2:F4},up={3:F4})  chin(fwd={4:F4},up={5:F4})  "
                + "mouth(fwd={6:F4},up={7:F4},lat={8:F4})  MOUTH LOCAL OFFSET FROM Head JOINT = ({9:F5}, {10:F5}, {11:F5}) m  "
                + "|offset|={12:F4} m",
                head.Length, mid.Length, F(nose), U(nose), F(chin), U(chin), F(mouth), U(mouth), X(mouth),
                mouthLocal.x, mouthLocal.y, mouthLocal.z, mouthLocal.magnitude));

            // Vertex sets are captured ONCE, at frame 0, as INDICES. BakeMesh returns a stable
            // index order, so the same indices name the same skin points for the whole clip.
            // Selecting "hand vertices" by proximity to the wrist EVERY frame was wrong: once the
            // hand reaches the face that filter also swallows cheek and neck vertices, which of
            // course sit on or inside the head surface, and reported 0.11 m of penetration -- deeper
            // than the skull is wide. Frame 0 has the hand down at the hip, so the two sets separate
            // cleanly there.
            var headIdx = Enumerable.Range(0, v0.Length)
                .Where(i => (v0[i] - Head.position).sqrMagnitude < HeadRadius * HeadRadius).ToArray();
            var handIdx = Enumerable.Range(0, v0.Length)
                .Where(i => (v0[i] - LHand.position).sqrMagnitude < 0.10f * 0.10f).ToArray();
            // The radial map lives in HEAD-LOCAL space so it rotates with the head.
            var surf = BuildRadialMap(headIdx.Select(i => Head.InverseTransformPoint(v0[i])).ToArray(), Vector3.zero);
            Debug.Log("[S87sets] headVerts=" + headIdx.Length + " handVerts=" + handIdx.Length
                      + " (captured at frame 0, indices reused for every frame)");
            // The radial map treats the head as star-shaped about the Head joint, which it is not
            // near the jaw and neck: a hand beside the jaw sits at a smaller radius than the jaw
            // surface in the same direction and reads as deeply "inside". Dump the two clouds in
            // head-local space instead and do the inside test offline against a convex hull, which
            // is well behaved over the face.
            File.WriteAllLines(Path.Combine(outDir, "headcloud.csv"),
                new[] { "x,y,z" }.Concat(headIdx.Select(i =>
                {
                    var q = Head.InverseTransformPoint(v0[i]);
                    return string.Format(CultureInfo.InvariantCulture, "{0:F5},{1:F5},{2:F5}", q.x, q.y, q.z);
                })));

            // ---- STEP 2: per-frame contact measurement ----
            var rows = new List<string>();
            var handCloud = new List<string>();
            int n = Mathf.RoundToInt(clip.length * FPS);
            Camera cam = null; GameObject lightGo = null; RenderTexture rt = null; Texture2D tex = null;
            GameObject marker = null;
            if (render)
            {
                cam = MakeCamera(out lightGo);
                rt = new RenderTexture(W, H, 24) { antiAliasing = 4 };
                tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                UnityEngine.Object.DestroyImmediate(marker.GetComponent<Collider>());
                marker.transform.localScale = Vector3.one * 0.016f;
                var mat = new Material(Shader.Find("Unlit/Color")); mat.color = new Color(1f, 0.25f, 0.2f);
                marker.GetComponent<MeshRenderer>().sharedMaterial = mat;
                Directory.CreateDirectory(Path.Combine(outDir, "frames_" + tag));
            }

            for (int i = 0; i <= n; i++)
            {
                if (i > 0) { mAn.Update(1f / FPS); bAn.Update(1f / FPS); }
                var vs = Bake();
                if (bakeProxy != null)
                {
                    var drawn = new Mesh();
                    drawn.vertices = bake.vertices; drawn.normals = bake.normals;
                    drawn.uv = bake.uv; drawn.triangles = bake.triangles;
                    drawn.subMeshCount = bake.subMeshCount;
                    for (int sm = 0; sm < bake.subMeshCount; sm++) drawn.SetTriangles(bake.GetTriangles(sm), sm);
                    if (bakeProxy.sharedMesh != null) UnityEngine.Object.DestroyImmediate(bakeProxy.sharedMesh);
                    bakeProxy.sharedMesh = drawn;
                    bakeProxy.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
                    bakeProxy.transform.localScale = Vector3.one;
                }
                Vector3 mouthW = Head.TransformPoint(mouthLocal);

                // palm: wrist -> middle-finger base midpoint if the rig has fingers, else wrist
                Vector3 palm = LMid != null ? Vector3.Lerp(LHand.position, LMid.position, 0.6f) : LHand.position;

                // hand skin points, by the frame-0 index set -- never re-selected by proximity
                float dHandMesh = handIdx.Min(i => Vector3.Distance(vs[i], mouthW));
                float pen = Penetration(handIdx.Select(i => Head.InverseTransformPoint(vs[i])).ToArray(),
                                        Vector3.zero, surf);

                Vector3 miss = mouthW - palm;                       // from palm to the target
                float low = Vector3.Dot(miss, up);                  // +ve: the palm is BELOW the mouth
                float lat = Vector3.Dot(miss, right);               // +ve: the palm is to the character's right of it
                float fw = Vector3.Dot(miss, fwd);                  // +ve: the palm is BEHIND the mouth

                // wrist and elbow in HEAD-LOCAL space, so the forearm can be intersection-tested
                // against the head point cloud offline. Targeting the palm alone is not enough: a
                // solution can put the palm on the mouth while routing the forearm through the skull,
                // which is exactly what iterations 1 and 2 did.
                Vector3 wristL = Head.InverseTransformPoint(LHand.position);
                Vector3 elbowL = Head.InverseTransformPoint(LElb.position);
                rows.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1:F5},{2:F5},{3:F5},{4:F5},{5:F5},{6:F5},{7:F5},{8:F5},{9:F5},{10:F5},{11:F5},{12:F5},"
                    + "{13:F5},{14:F5},{15:F5},{16:F5},{17:F5},{18:F5}",
                    i, Vector3.Distance(palm, mouthW), dHandMesh, pen, low, lat, fw,
                    palm.x, palm.y, palm.z, mouthW.x, mouthW.y, mouthW.z,
                    wristL.x, wristL.y, wristL.z, elbowL.x, elbowL.y, elbowL.z));

                if (i >= 20 && i <= 100)
                    foreach (int hi in handIdx)
                    {
                        var q = Head.InverseTransformPoint(vs[hi]);
                        handCloud.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1:F5},{2:F5},{3:F5}",
                            i, q.x, q.y, q.z));
                    }

                if (render)
                {
                    marker.transform.position = mouthW;
                    // The oblique three-quarter close-up repeatedly hid whether fingers were
                    // through the cheek. AUTOTRIAL_S87_CAM=front gives a straight-on view, which
                    // settles it: a hand resting on the face reads as a silhouette against it, a
                    // hand inside it shows fingertips emerging past the far cheek.
                    bool frontCam = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S87_CAM") == "front";
                    Vector3 focus = Head.position + up * -0.02f;
                    Vector3 dir = frontCam ? fwd : (fwd * 0.80f + right * 0.60f).normalized;
                    cam.transform.position = focus + dir * (frontCam ? 0.70f : 0.62f) + up * 0.06f;
                    cam.transform.LookAt(focus);
                    cam.targetTexture = rt; cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes(Path.Combine(outDir, "frames_" + tag, "f_" + i.ToString("D4") + ".png"),
                        tex.EncodeToPNG());
                }
            }

            File.WriteAllText(Path.Combine(outDir, "handcloud_" + tag + ".csv"),
                "frame,x,y,z\n" + string.Join("\n", handCloud) + "\n");
            File.WriteAllText(Path.Combine(outDir, "contact_" + tag + ".csv"),
                "frame,d_palm_mouth,d_handmesh_mouth,penetration,miss_low,miss_lat,miss_fwd,"
                + "palm_x,palm_y,palm_z,mouth_x,mouth_y,mouth_z,"
                + "wristL_x,wristL_y,wristL_z,elbowL_x,elbowL_y,elbowL_z\n" + string.Join("\n", rows) + "\n");
            File.WriteAllText(Path.Combine(outDir, "mouth_landmark.txt"), string.Format(
                CultureInfo.InvariantCulture, "{0:F6} {1:F6} {2:F6}\n", mouthLocal.x, mouthLocal.y, mouthLocal.z));

            if (render)
            {
                UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(cam.gameObject); UnityEngine.Object.DestroyImmediate(lightGo);
                UnityEngine.Object.DestroyImmediate(marker);
            }
            if (bakeProxy != null)
            {
                if (bakeProxy.sharedMesh != null) UnityEngine.Object.DestroyImmediate(bakeProxy.sharedMesh);
                UnityEngine.Object.DestroyImmediate(bakeProxy.gameObject);
            }
            UnityEngine.Object.DestroyImmediate(bake);
            UnityEngine.Object.DestroyImmediate(mGo); UnityEngine.Object.DestroyImmediate(bGo);
            Debug.Log("[S87] " + tag + ": wrote contact_" + tag + ".csv (" + (n + 1) + " frames)");
        }

        /// Coarse radial map of the head surface, binned by direction from the Head joint. Used to
        /// ask "is this hand vertex inside the head?" without a real signed distance field.
        private const int NAz = 36, NEl = 18;

        private static float[,] BuildRadialMap(Vector3[] head, Vector3 c)
        {
            var map = new float[NAz, NEl];
            foreach (var v in head)
            {
                var d = v - c; float r = d.magnitude;
                if (r < 1e-5f) continue;
                Bin(d, out int a, out int e);
                if (r > map[a, e]) map[a, e] = r;
            }
            return map;
        }

        private static void Bin(Vector3 d, out int a, out int e)
        {
            d.Normalize();
            float az = Mathf.Atan2(d.z, d.x) + Mathf.PI;
            float el = Mathf.Acos(Mathf.Clamp(d.y, -1f, 1f));
            a = Mathf.Clamp((int)(az / (2f * Mathf.PI) * NAz), 0, NAz - 1);
            e = Mathf.Clamp((int)(el / Mathf.PI * NEl), 0, NEl - 1);
        }

        private static float Penetration(Vector3[] handVerts, Vector3 c, float[,] surf)
        {
            float worst = 0f;
            foreach (var v in handVerts)
            {
                var d = v - c; float r = d.magnitude;
                if (r > 0.20f) continue;
                Bin(d, out int a, out int e);
                float s = surf[a, e];
                if (s > 0f && s - r > worst) worst = s - r;
            }
            return worst;
        }

        private static Camera MakeCamera(out GameObject lightGo)
        {
            var camGo = new GameObject("S87Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
            cam.fieldOfView = 34f; cam.nearClipPlane = 0.01f;
            lightGo = new GameObject("S87Light");
            var li = lightGo.AddComponent<Light>();
            li.type = LightType.Directional; li.intensity = 1.15f;
            lightGo.transform.rotation = Quaternion.Euler(28f, 150f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.48f, 0.50f, 0.54f);
            return cam;
        }
    }
}
