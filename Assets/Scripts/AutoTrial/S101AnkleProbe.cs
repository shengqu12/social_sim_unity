using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 101, A2-probe. Per-frame ankle world positions for a live pedestrian, so the stride
    /// each leg actually takes IN ENGINE -- with foot IK running, which is the shipping config --
    /// can be measured instead of inferred.
    ///
    /// WHY THIS HAS TO EXIST. S100 could not measure in-engine stride at all: the Rocketbox target
    /// imports with Optimize GameObjects, so its bone Transforms do not exist in the hierarchy and
    /// no probe can read an ankle. The sanctioned way round that is already in the tree --
    /// S89ContactIK.Setup() calls AnimatorUtility.DeoptimizeTransformHierarchy on the spawned CLONE
    /// (memory only, no asset touched) precisely so it can read and write bones. This does the same
    /// thing, for reading only, and it is careful to be a no-op when S89 has already done it.
    ///
    /// READ-ONLY with respect to the pose: it writes a CSV and sets no value on any component. The
    /// one side effect is the deoptimisation itself, which S89 already performs on every in-scope
    /// pedestrian, and which this skips if the hierarchy is already deoptimised.
    ///
    /// Env: AUTOTRIAL_S101_ANKLE=&lt;output csv&gt;. Absent -> never bootstraps.
    /// Optional AUTOTRIAL_S101_ANKLE_TAG labels the rows.
    /// </summary>
    public class S101AnkleProbe : MonoBehaviour
    {
        public const string Env = "AUTOTRIAL_S101_ANKLE";

        private static string OutPath { get { return System.Environment.GetEnvironmentVariable(Env); } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (string.IsNullOrEmpty(OutPath)) { return; }
            var host = new GameObject("S101AnkleProbeHost");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<Attacher>();
        }

        private class Attacher : MonoBehaviour
        {
            private IEnumerator Start()
            {
                while (true)
                {
                    var scaler = Object.FindObjectOfType<S32AnimatorSpeedScaler>();
                    if (scaler != null && scaler.GetComponentInChildren<Animator>() != null)
                    {
                        scaler.gameObject.AddComponent<S101AnkleProbe>();
                        yield break;
                    }
                    yield return new WaitForSeconds(0.25f);
                }
            }
        }

        private Animator animator;
        private Transform lAnkle, rAnkle, hips;
        private StreamWriter writer;
        private string tag;
        private float t0;
        private int frame;

        private IEnumerator Start()
        {
            tag = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S101_ANKLE_TAG") ?? "run";
            animator = GetComponentInChildren<Animator>();
            if (animator == null || animator.avatar == null || !animator.isHuman)
            {
                Debug.LogWarning("[S101Ankle] no humanoid Animator on '" + name + "' -- probe inert.");
                yield break;
            }

            // S89 deoptimises in-scope pedestrians for its own IK. Doing it twice is not harmful,
            // but checking first keeps the log honest about who owned the change.
            if (!animator.hasTransformHierarchy)
            {
                AnimatorUtility.DeoptimizeTransformHierarchy(animator.gameObject);
                Debug.Log("[S101Ankle] deoptimised '" + animator.gameObject.name + "' (memory only, no asset touched)");
            }
            else
            {
                Debug.Log("[S101Ankle] hierarchy already deoptimised (S89 owns it) -- reusing");
            }

            // One frame for the deoptimised hierarchy to be populated before binding bones.
            yield return null;
            lAnkle = animator.GetBoneTransform(HumanBodyBones.LeftFoot);
            rAnkle = animator.GetBoneTransform(HumanBodyBones.RightFoot);
            hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (lAnkle == null || rAnkle == null || hips == null)
            {
                Debug.LogError("[S101Ankle] could not resolve ankle/hips bones -- probe inert.");
                yield break;
            }

            string path = OutPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            writer = new StreamWriter(path, false);
            writer.WriteLine("t,tag,agent,hips_x,hips_y,hips_z,hips_yaw_deg,"
                + "lank_x,lank_y,lank_z,rank_x,rank_y,rank_z,"
                + "lank_localz,rank_localz,body_yaw_deg,speed_mps");
            t0 = Time.time;
            Debug.Log("[S101Ankle] recording '" + tag + "' -> " + path);
        }

        private Vector3 prevPos;
        private bool havePrev;

        private void LateUpdate()
        {
            if (writer == null) { return; }
            // Ankle expressed in the BODY's yaw frame: removes the body's travel and facing, so
            // what is left is the foot's excursion relative to the body -- the same quantity S100
            // measured on the source curves, so the two are directly comparable.
            //
            // The body's yaw, NOT the hips bone's. The SOMA hips bone carries a bind orientation of
            // its own, so hips.rotation.eulerAngles.y is not the direction the pedestrian faces;
            // using it puts the anterior axis on z where it belongs on x and makes the L-R
            // separation look like a constant 20-50 cm offset (that is stance width) instead of an
            // alternating stride. Raw world positions are written too, so any frame choice can be
            // re-derived from the CSV without re-running a trial.
            float yaw = transform.rotation.eulerAngles.y;
            Quaternion inv = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f));
            Vector3 l = inv * (lAnkle.position - hips.position);
            Vector3 r = inv * (rAnkle.position - hips.position);

            Vector3 p = transform.position;
            float speed = 0f;
            if (havePrev && Time.deltaTime > 1e-5f) { speed = (p - prevPos).magnitude / Time.deltaTime; }
            prevPos = p; havePrev = true;

            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0:F4},{1},{2},{3:F5},{4:F5},{5:F5},{6:F3},{7:F5},{8:F5},{9:F5},{10:F5},{11:F5},{12:F5},"
                + "{13:F5},{14:F5},{15:F3},{16:F4}",
                Time.time - t0, tag, name, hips.position.x, hips.position.y, hips.position.z, yaw,
                lAnkle.position.x, lAnkle.position.y, lAnkle.position.z,
                rAnkle.position.x, rAnkle.position.y, rAnkle.position.z,
                l.z, r.z, transform.rotation.eulerAngles.y, speed));
            frame++;
        }

        private void OnDestroy()
        {
            if (writer != null)
            {
                writer.Flush(); writer.Close(); writer = null;
                Debug.Log("[S101Ankle] wrote " + frame + " frames for '" + tag + "'");
            }
        }
    }
}
