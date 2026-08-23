using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 89 measurement harness. Records, during a REAL trial (so the shipping code path is
    /// what gets graded), the same quantities S87/S88 gated on, plus frontal frames of the hold.
    ///
    /// Writes the two files the validated offline rulers consume -- a head point cloud in head-local
    /// space and a per-frame hand cloud -- so `s88_rank.py` grades this exactly as it graded the
    /// edit-mode sweeps. Only the two rulers that survived S87's validation are fed: the forearm
    /// capsule count and the hand-hull enclosure count. The three signed-distance metrics that S87
    /// falsified are not computed here.
    ///
    /// Env: AUTOTRIAL_S89_MEASURE=<output directory>. Absent -> the component never bootstraps.
    /// </summary>
    public class S89ContactProbe : MonoBehaviour
    {
        public const string Env = "AUTOTRIAL_S89_MEASURE";

        private Animator animator;
        private SkinnedMeshRenderer smr;
        private S89ContactIK ik;
        private Transform head, wrist, elbow, midProx;
        private Mesh bake;
        private int[] headIdx, handIdx;
        private Vector3 mouthLocal, bodyRight, bodyFwd;
        private string outDir, tag;
        private readonly StringBuilder rows = new StringBuilder();
        private readonly StringBuilder hand = new StringBuilder();
        private bool init, wrote;
        private int shot;

        private Camera cam; private RenderTexture rt; private Texture2D tex; private GameObject marker, lightGo;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(Env))) return;
            var host = new GameObject("S89ContactProbeHost");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<S89ContactProbe>().StartCoroutine(nameof(Attach));
        }

        private IEnumerator Attach()
        {
            Scenario.Agents.PedestrianModulator mod = null;
            float deadline = Time.time + 30f;
            while (mod == null && Time.time < deadline)
            {
                mod = Object.FindObjectOfType<Scenario.Agents.PedestrianModulator>();
                if (mod == null) yield return new WaitForSeconds(0.25f);
            }
            if (mod == null) yield break;
            if (mod.GetComponent<S89ContactProbe>() == null) mod.gameObject.AddComponent<S89ContactProbe>();
        }

        private void Init()
        {
            init = true;
            outDir = System.Environment.GetEnvironmentVariable(Env);
            tag = System.Environment.GetEnvironmentVariable("AUTOTRIAL_S89_TAG") ?? "run";
            Directory.CreateDirectory(outDir);
            animator = GetComponentInChildren<Animator>();
            smr = GetComponentInChildren<SkinnedMeshRenderer>();
            ik = GetComponent<S89ContactIK>();
            // The probe must be able to read bones even when the IK layer is absent (the baseline
            // arm), so it deoptimises on its own account if nothing else has. Same 1.05 ms,
            // same memory-only clone.
            if (animator != null && !animator.hasTransformHierarchy)
                AnimatorUtility.DeoptimizeTransformHierarchy(gameObject);
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            wrist = animator.GetBoneTransform(HumanBodyBones.LeftHand);
            elbow = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            midProx = animator.GetBoneTransform(HumanBodyBones.LeftMiddleProximal);
            var lu = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            var ru = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (head == null || wrist == null || lu == null) { Debug.LogWarning("[S89probe2] bones missing"); return; }

            string body = S89ContactIK.BodyKey(gameObject.name);
            if (!S89ContactIK.MouthLocal.TryGetValue(body, out mouthLocal))
            { Debug.LogWarning("[S89probe2] no landmark for " + body); return; }

            bodyRight = (lu.position - ru.position); bodyRight.y = 0; bodyRight.Normalize();
            bodyFwd = Vector3.Cross(Vector3.up, bodyRight).normalized;

            bake = new Mesh();
            var vs = Baked();
            headIdx = Enumerable.Range(0, vs.Length).Where(i => (vs[i] - head.position).sqrMagnitude < 0.17f * 0.17f).ToArray();
            handIdx = Enumerable.Range(0, vs.Length).Where(i => (vs[i] - wrist.position).sqrMagnitude < 0.10f * 0.10f).ToArray();
            File.WriteAllLines(Path.Combine(outDir, "headcloud_" + tag + ".csv"),
                new[] { "x,y,z" }.Concat(headIdx.Select(i =>
                { var q = head.InverseTransformPoint(vs[i]);
                  return string.Format(CultureInfo.InvariantCulture, "{0:F5},{1:F5},{2:F5}", q.x, q.y, q.z); })));
            rows.Append("frame,frameF,d_palm_mouth,d_handmesh_mouth,miss_low,miss_lat,miss_fwd,"
                        + "wristL_x,wristL_y,wristL_z,elbowL_x,elbowL_y,elbowL_z,weight,"
                        + "dShoulderDeg,dElbowDeg,dWristDeg\n");
            hand.Append("frame,x,y,z\n");
            MakeCam();
            Debug.Log("[S89probe2] armed tag=" + tag + " body=" + body + " headVerts=" + headIdx.Length
                      + " handVerts=" + handIdx.Length + " ikPresent=" + (ik != null));
        }

        private Vector3[] Baked()
        {
            smr.BakeMesh(bake);
            var m = smr.transform.localToWorldMatrix;
            return bake.vertices.Select(v => m.MultiplyPoint3x4(v)).ToArray();
        }

        private void LateUpdate()
        {
            if (!init) { Init(); return; }
            if (headIdx == null) return;
            var st = animator.GetCurrentAnimatorStateInfo(0);
            if (st.shortNameHash != Animator.StringToHash(S89ContactIK.StateName)) return;
            var ci = animator.GetCurrentAnimatorClipInfo(0);
            if (ci == null || ci.Length == 0) return;
            var clip = ci[0].clip;
            int frame = Mathf.RoundToInt(Mathf.Repeat(st.normalizedTime, 1f) * clip.length * 30f);
            if (frame < 0 || frame > 179) return;

            var vs = Baked();
            Vector3 mouth = head.TransformPoint(mouthLocal);
            Vector3 palm = midProx != null ? Vector3.Lerp(wrist.position, midProx.position, 0.6f) : wrist.position;
            Vector3 miss = mouth - palm;
            Vector3 wl = head.InverseTransformPoint(wrist.position);
            Vector3 el = head.InverseTransformPoint(elbow.position);
            rows.Append(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:F4},{2:F5},{3:F5},{4:F5},{5:F5},{6:F5},{7:F5},{8:F5},{9:F5},{10:F5},{11:F5},{12:F5},"
                + "{13:F4},{14:F4},{15:F4},{16:F4}\n",
                frame, ik != null ? ik.LastFrameF : frame,
                Vector3.Distance(palm, mouth), handIdx.Min(i => Vector3.Distance(vs[i], mouth)),
                Vector3.Dot(miss, Vector3.up), Vector3.Dot(miss, bodyRight), Vector3.Dot(miss, bodyFwd),
                wl.x, wl.y, wl.z, el.x, el.y, el.z, ik != null ? ik.LastWeight : 0f,
                ik != null ? ik.DeltaShoulderDeg : 0f, ik != null ? ik.DeltaElbowDeg : 0f,
                ik != null ? ik.DeltaWristDeg : 0f));
            if (frame >= 20 && frame <= 100)
                foreach (int i in handIdx)
                { var q = head.InverseTransformPoint(vs[i]);
                  hand.Append(string.Format(CultureInfo.InvariantCulture, "{0},{1:F5},{2:F5},{3:F5}\n", frame, q.x, q.y, q.z)); }

            if (cam != null && frame >= 15 && frame <= 115 && shot < 220) Shoot(mouth, frame);
            if (frame >= 175 && !wrote) Flush();
        }

        private void MakeCam()
        {
            var go = new GameObject("S89Cam"); cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.13f, 0.14f, 0.16f);
            cam.fieldOfView = 34f; cam.nearClipPlane = 0.01f; cam.enabled = false;
            cam.cullingMask = ~0;
            lightGo = new GameObject("S89Light"); var li = lightGo.AddComponent<Light>();
            li.type = LightType.Directional; li.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(28f, 150f, 0f);
            rt = new RenderTexture(1000, 720, 24) { antiAliasing = 4 };
            tex = new Texture2D(1000, 720, TextureFormat.RGB24, false);
            marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(marker.GetComponent<Collider>());
            marker.transform.localScale = Vector3.one * 0.014f;
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            var mat = new Material(sh) { color = new Color(1f, 0.25f, 0.2f) };
            marker.GetComponent<MeshRenderer>().sharedMaterial = mat;
            Directory.CreateDirectory(Path.Combine(outDir, "frames_" + tag));
        }

        private void Shoot(Vector3 mouth, int frame)
        {
            marker.transform.position = mouth;
            Vector3 focus = head.position + Vector3.up * -0.02f;
            cam.transform.position = focus + bodyFwd * 0.70f + Vector3.up * 0.06f;
            cam.transform.LookAt(focus);
            cam.targetTexture = rt; cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, 1000, 720), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(outDir, "frames_" + tag, "f_" + frame.ToString("D4") + ".png"), tex.EncodeToPNG());
            shot++;
        }

        private void OnDisable() { Flush(); }

        private void Flush()
        {
            if (wrote || rows.Length == 0 || string.IsNullOrEmpty(outDir)) return;
            wrote = true;
            File.WriteAllText(Path.Combine(outDir, "contact_" + tag + ".csv"), rows.ToString());
            File.WriteAllText(Path.Combine(outDir, "handcloud_" + tag + ".csv"), hand.ToString());
            Debug.Log("[S89probe2] wrote contact_" + tag + ".csv and handcloud_" + tag + ".csv");
        }
    }
}
