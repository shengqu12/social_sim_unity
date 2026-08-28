using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 104 Phase 0. The Kimodo humanoid reference pose, as a standalone data file instead
    /// of something donated from a clip's FBX.
    ///
    /// WHY DECOUPLE IT. Until now the reference lived in `kimodo_relaxed_walk.fbx`'s own
    /// humanDescription, and S86 copied it from there onto every other Kimodo asset. That made the
    /// reference a property of one animation clip, which is how S100 found the whole lower body
    /// storing that clip's FRAME 0. It also means replacing the walk clip -- exactly what S104
    /// Phase 1 does -- would silently replace the reference for every asset that borrows it. The
    /// reference is rig data, not clip data, so it is now stored as rig data.
    ///
    /// WHAT IS IN IT. The S101 state, verbatim: S72's hand-corrected ARMS (b2's contact solution
    /// depends on them and they are carried across byte-for-byte) plus the symmetric legs derived
    /// from the BVH rest pose. Exported from the donor as it stands, so promoting it changes
    /// nothing on the first pass -- which is the gate.
    ///
    /// -executeMethod SEAN.AutoTrial.S104CanonicalReference.Export
    /// </summary>
    public static class S104CanonicalReference
    {
        public const string Path_ = "Assets/PedestrianAssets/Kimodo/kimodo_reference_skeleton.json";
        private const string LegacyDonor = "Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk.fbx";

        [System.Serializable]
        private class Bone
        {
            public string name;
            public float px, py, pz;
            public float rx, ry, rz, rw;
            public float sx, sy, sz;
        }

        [System.Serializable]
        private class Doc
        {
            public string note;
            public string provenance;
            public Bone[] bones;
        }

        [MenuItem("AutoTrial/Session 104/Export canonical Kimodo reference")]
        public static void Export()
        {
            var imp = AssetImporter.GetAtPath(LegacyDonor) as ModelImporter;
            if (imp == null) { Debug.LogError("[S104] legacy donor missing: " + LegacyDonor); return; }
            var skel = imp.humanDescription.skeleton;
            if (skel == null || skel.Length == 0)
            {
                Debug.LogError("[S104] legacy donor has an empty skeleton -- nothing to promote.");
                return;
            }

            var doc = new Doc
            {
                note = "Canonical Kimodo (SOMA 79-bone) humanoid reference pose. Rig data, not clip data.",
                provenance = "S72 hand-configured arms + S101 BVH-rest-derived symmetric legs, "
                             + "exported from kimodo_relaxed_walk.fbx's humanDescription at S104 Phase 0.",
                bones = skel.Select(s => new Bone
                {
                    name = s.name,
                    px = s.position.x, py = s.position.y, pz = s.position.z,
                    rx = s.rotation.x, ry = s.rotation.y, rz = s.rotation.z, rw = s.rotation.w,
                    sx = s.scale.x, sy = s.scale.y, sz = s.scale.z,
                }).ToArray(),
            };
            File.WriteAllText(Path_, JsonUtility.ToJson(doc, true));
            AssetDatabase.ImportAsset(Path_);
            Debug.Log("[S104] exported " + doc.bones.Length + " bones -> " + Path_);
        }

        /// <summary>The canonical reference, or null if the file is absent. S86 falls back to the
        /// legacy donor in that case and says so, rather than silently importing a wrong pose.</summary>
        public static Dictionary<string, SkeletonBone> TryLoad()
        {
            if (!File.Exists(Path_)) { return null; }
            var doc = JsonUtility.FromJson<Doc>(File.ReadAllText(Path_));
            if (doc == null || doc.bones == null || doc.bones.Length == 0) { return null; }
            var outp = new Dictionary<string, SkeletonBone>(doc.bones.Length);
            foreach (var b in doc.bones)
            {
                outp[b.name] = new SkeletonBone
                {
                    name = b.name,
                    position = new Vector3(b.px, b.py, b.pz),
                    rotation = new Quaternion(b.rx, b.ry, b.rz, b.rw),
                    scale = new Vector3(b.sx, b.sy, b.sz),
                };
            }
            return outp;
        }
    }
}
