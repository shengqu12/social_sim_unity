using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 41 TASK 3 follow-up (read-only): S41MixamoImport's isHuman gate passed 9/9 but every
    /// row reported height=-1.00m, i.e. no Renderer anywhere in the hierarchy. That distinguishes
    /// two very different kinds of asset -- a full character (skinned mesh + rig + clip) versus a
    /// Mixamo animation-only export (rig + clip, no skin) -- and the difference decides whether
    /// these can be spawned as pedestrians at all or must be retargeted onto an existing avatar.
    /// Dumps every sub-asset and the full transform hierarchy so the answer is evidence, not
    /// inference from a single -1.
    ///
    /// -executeMethod SEAN.AutoTrial.S41MixamoContentProbe.Dump
    /// </summary>
    public static class S41MixamoContentProbe
    {
        private const string Dir = "Assets/PedestrianAssets/Mixamo";

        public static void Dump()
        {
            foreach (string path in Directory.GetFiles(Dir, "*.fbx").OrderBy(p => p))
            {
                string p = path.Replace('\\', '/');
                string name = Path.GetFileNameWithoutExtension(p);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(p);
                if (go == null) { Debug.Log("[S41Content] '" + name + "' FAILED TO LOAD"); continue; }

                var subAssets = AssetDatabase.LoadAllAssetsAtPath(p);
                var meshes = subAssets.OfType<Mesh>().ToArray();
                var mats = subAssets.OfType<Material>().ToArray();
                var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                var meshRenderers = go.GetComponentsInChildren<MeshRenderer>(true);
                var bones = go.GetComponentsInChildren<Transform>(true);

                Debug.Log(string.Format(
                    "[S41Content] '{0}': meshSubAssets={1} materials={2} skinnedRenderers={3} meshRenderers={4} transforms={5}",
                    name, meshes.Length, mats.Length, skinned.Length, meshRenderers.Length, bones.Length));

                foreach (var m in meshes)
                {
                    Debug.Log(string.Format("[S41Content]    mesh '{0}' verts={1} bindposes={2}",
                        m.name, m.vertexCount, m.bindposes != null ? m.bindposes.Length : 0));
                }
                // First few transforms identify whether this is a bare mixamorig skeleton.
                Debug.Log("[S41Content]    roots=[" + string.Join(", ",
                    bones.Take(6).Select(b => b.name)) + (bones.Length > 6 ? ", ..." : "") + "]");
            }
            EditorApplication.Exit(0);
        }
    }
}
