using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// S84 rung R1: the Kimodo FBX played on ITS OWN imported rig, Generic, no retarget of any kind.
    /// bvh_to_fbx.py exports object_types={"ARMATURE"} so there is no mesh to render -- a cube per
    /// bone gives the same read as the S76 BVH skeleton previews that serve as R0, which is the
    /// point: R0 and R1 should be indistinguishable if the conversion and import are faithful.
    ///
    /// -executeMethod SEAN.AutoTrial.S84SkeletonRender.Run
    public static class S84SkeletonRender
    {
        private const int W = 960, H = 540, FPS = 30;

        public static void Run()
        {
            string outDir = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S84_OUT");
            foreach (var tag in new[] { "walk_R1_generic", "b2_R1_generic" })
            {
                string path = "Assets/PedestrianAssets/Kimodo/S84/" + tag + ".fbx";
                var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                    .FirstOrDefault(c => !c.name.StartsWith("__preview"));
                var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));

                // The rig imports at 100x (the BVH is authored in centimetres and bvh_to_fbx.py
                // exports at global_scale 1.0) -- scale it for framing only; the pose is unaffected,
                // which is what rung R2's scale test already showed. The scale has to live on a
                // PARENT: the FBX carries baked scale curves on every bone, so sampling the clip
                // overwrites any localScale set on the model root itself.
                var scaler = new GameObject("S84Scale");
                scaler.transform.localScale = Vector3.one * 0.01f;
                go.transform.SetParent(scaler.transform, false);

                var bones = go.GetComponentsInChildren<Transform>(true);
                foreach (var b in bones)
                {
                    var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Object.DestroyImmediate(cube.GetComponent<Collider>());
                    cube.transform.SetParent(b, false);
                    cube.transform.localScale = Vector3.one * 4.5f;   // rig units, i.e. 4.5 cm
                }

                var camGo = new GameObject("cam");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
                cam.fieldOfView = 40f;
                var lightGo = new GameObject("light");
                var li = lightGo.AddComponent<Light>();
                li.type = LightType.Directional; li.intensity = 1.1f;
                lightGo.transform.rotation = Quaternion.Euler(38f, 145f, 0f);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);

                var rt = new RenderTexture(W, H, 24) { antiAliasing = 4 };
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                string dir = Path.Combine(outDir, "frames_" + tag);
                Directory.CreateDirectory(dir);
                var hips = bones.First(b => b.name == "Hips");

                int n = Mathf.RoundToInt(clip.length * FPS);
                AnimationMode.StartAnimationMode();
                for (int i = 0; i <= n; i++)
                {
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(go, clip, Mathf.Min(clip.length, i / (float)FPS));
                    AnimationMode.EndSampling();
                    Vector3 focus = new Vector3(hips.position.x, 0.90f, hips.position.z);
                    cam.transform.position = focus + new Vector3(3.1f, 0.6f, 3.1f);
                    cam.transform.LookAt(focus);
                    cam.targetTexture = rt; cam.Render();
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes(Path.Combine(dir, "f_" + i.ToString("D4") + ".png"), tex.EncodeToPNG());
                }
                AnimationMode.StopAnimationMode();
                Object.DestroyImmediate(rt); Object.DestroyImmediate(tex);
                Object.DestroyImmediate(camGo); Object.DestroyImmediate(lightGo); Object.DestroyImmediate(scaler);
                Debug.Log("[S84skel] " + tag + " frames=" + (n + 1) + " -> " + dir);
            }
        }
    }
}
