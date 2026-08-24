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
        private Transform head, wrist, elbow, midProx, shoulderT, hipsT, neckT, chestT;
        private float rUpper, rFore, rHand, rHead, rNeck, rTorso;   // S91 capsule radii, from the mesh
        private float torsoHalfW, torsoHalfD;                        // S91: the torso is an ELLIPSE
        private Transform rUpperArmT;
        private Mesh bake;
        private int[] headIdx, handIdx;
        private Vector3 mouthLocal, bodyRight, bodyFwd;
        private string outDir, tag;
        private readonly StringBuilder rows = new StringBuilder();
        private readonly StringBuilder hand = new StringBuilder();
        private bool init, wrote;
        private int shot;
        // S91b. cam.Render() called from LateUpdate draws the SkinnedMeshRenderer with the skinning
        // of the PREVIOUS probe sample, not the bone poses this LateUpdate just read -- the CSV row
        // and the PNG written on the same tick depict different frames. This was invisible for four
        // tickets because the render window was [15,115], entirely inside the static hold, where
        // pose(n) == pose(n-1). Widening it to cover the ramps exposed it immediately: the shot
        // taken at frame 17 (weight 1.00, hand measured 14 mm off the mouth) shows the mid-reach
        // pose of frame 10, and the shot at frame 120 shows frame 114's hand-at-chin.
        // The measurement path is NOT affected -- GATE A and GATE B read the CSV, never the PNGs.
        // Until the render is moved to end-of-frame, name each file for the frame it actually
        // depicts, which is the previous sample. -1 = nothing rendered yet this pass.
        private int prevRenderedFrame = -1;

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
            shoulderT = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
            hipsT = animator.GetBoneTransform(HumanBodyBones.Hips);
            neckT = animator.GetBoneTransform(HumanBodyBones.Neck) ?? animator.GetBoneTransform(HumanBodyBones.Head);
            chestT = animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Spine);
            rUpperArmT = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
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
            MeasureRadii(Baked());
            rows.Append("frame,frameF,d_palm_mouth,d_handmesh_mouth,miss_low,miss_lat,miss_fwd,"
                        + "wristL_x,wristL_y,wristL_z,elbowL_x,elbowL_y,elbowL_z,weight,"
                        + "dShoulderDeg,dElbowDeg,dWristDeg,signedClear,penDeepest,gapNearest,"
                        + "shX,shY,shZ,elX,elY,elZ,wrX,wrY,wrZ,tipX,tipY,tipZ,"
                        + "hipX,hipY,hipZ,nkX,nkY,nkZ,hdX,hdY,hdZ,rgX,rgZ,"
                        // S92 G-ROM: the joint angles the solver clamps against, logged from the
                        // component itself so the gate grades exactly the quantity that was clamped
                        + "wFlex,wDev,wTwist,pronation,pronApplied,eFlex,sElev,sTwist,sSwing,rollGivenUp,"
                        + "aFlex,aDev,aTwist\n");
            hand.Append("frame,x,y,z\n");
            MakeCam();
            Debug.Log("[S89probe2] armed tag=" + tag + " body=" + body + " headVerts=" + headIdx.Length
                      + " handVerts=" + handIdx.Length + " ikPresent=" + (ik != null));
        }

        /// Used only when the IK layer is absent (the baseline arm), so the baseline can be graded
        /// on the same signed axis as the corrected run.
        private static Vector3 FallbackNormal(string body)
        {
            S89ContactIK.FaceSpec f;
            return S89ContactIK.Face.TryGetValue(body, out f) ? f.n : Vector3.up;
        }

        /// S91: capsule radii measured from the baked mesh, per segment. For each axis, take the
        /// vertices whose projection lands in the middle 60% of the segment and within 0.20 m, and
        /// use the 90th percentile of perpendicular distance -- the 90th rather than the max so a
        /// stray sleeve or lapel vertex does not set the radius.
        private void MeasureRadii(Vector3[] vs)
        {
            // Each vertex is assigned to its NEAREST segment, then a segment's radius is the 90th
            // percentile of distance within its own set. A plain "everything within 0.20 m" ball
            // does not work: the forearm's ball swallows torso and head vertices and returned an
            // 18.7 cm forearm radius. The 90th percentile rather than the max keeps a stray lapel
            // or sleeve vertex from setting the radius.
            var segs = new[]
            {
                new[] { shoulderT.position, elbow.position },
                new[] { elbow.position, wrist.position },
                new[] { wrist.position, midProx != null ? midProx.position : wrist.position + Vector3.up * 0.05f },
                new[] { neckT.position, head.position },
                new[] { chestT.position, neckT.position },
                new[] { hipsT.position, neckT.position },
            };
            // Per-segment distance cap: limbs are thin, the torso is not. A single 0.12 m cap threw
            // away every torso vertex (chest half-width is ~0.18 m) and left the torso on its 0.05 m
            // fallback -- which would have made the whole arm-vs-torso audit meaningless.
            float[] cap = { 0.12f, 0.12f, 0.12f, 0.30f, 0.30f, 0.30f };
            var sets = new List<float>[segs.Length];
            for (int i = 0; i < segs.Length; i++) sets[i] = new List<float>();
            foreach (var v in vs)
            {
                int bi = -1; float bd = float.MaxValue; float bt = 0f;
                for (int i = 0; i < segs.Length; i++)
                {
                    float tt; float d = PointSeg(v, segs[i][0], segs[i][1], out tt);
                    if (d < bd) { bd = d; bi = i; bt = tt; }
                }
                if (bi >= 0 && bt > 0.25f && bt < 0.75f && bd < cap[bi]) sets[bi].Add(bd);
            }
            float[] r = new float[segs.Length];
            for (int i = 0; i < segs.Length; i++)
            {
                if (sets[i].Count < 12) { r[i] = 0.05f; continue; }
                sets[i].Sort();
                // MEDIAN, not the 90th percentile. These meshes carry only ~4.7k vertices, so a
                // per-segment set is 30-200 points and the upper tail is whatever loose jacket or
                // lapel geometry drifted into the band -- the 90th percentile returned a 13 cm
                // forearm and a 23 cm neck. The median is the limb.
                r[i] = sets[i][sets[i].Count / 2];
            }
            rUpper = r[0]; rFore = r[1]; rHand = r[2]; rHead = r[3]; rNeck = r[4]; rTorso = r[5];

            // A circular capsule of chest HALF-WIDTH is the wrong torso model: it fills the whole
            // space in front of the sternum, so a hand held in front of the chest reads as 5 cm
            // "inside" the body without touching it -- which is why forearm-vs-torso was invariant
            // to the elbow pole across the sweep. Measure half-width and half-depth separately and
            // let the audit use an ellipse.
            Vector3 axis = (neckT.position - hipsT.position).normalized;
            Vector3 right = (shoulderT.position - rUpperArmT.position); right.y = 0f; right.Normalize();
            Vector3 fwd = Vector3.Cross(Vector3.up, right).normalized;
            if (Vector3.Dot(fwd, bodyFwd) < 0f) fwd = -fwd;
            var lat = new List<float>(); var dep = new List<float>();
            foreach (var v in vs)
            {
                // Only vertices the partition assigned to the TORSO. Without this the arms, which
                // hang at the sides in the pose this is measured in, set the half-width -- 22.7 cm,
                // i.e. shoulder span, not chest.
                int own = -1; float od = float.MaxValue;
                for (int i = 0; i < segs.Length; i++)
                {
                    float tt2; float dd = PointSeg(v, segs[i][0], segs[i][1], out tt2);
                    if (dd < od) { od = dd; own = i; }
                }
                if (own != 5) continue;
                Vector3 d0 = v - hipsT.position;
                float tt = Vector3.Dot(d0, axis) / Vector3.Distance(hipsT.position, neckT.position);
                if (tt < 0.25f || tt > 0.75f) continue;
                Vector3 perp = d0 - axis * Vector3.Dot(d0, axis);
                if (perp.magnitude > 0.30f) continue;
                lat.Add(Mathf.Abs(Vector3.Dot(perp, right)));
                dep.Add(Mathf.Abs(Vector3.Dot(perp, fwd)));
            }
            lat.Sort(); dep.Sort();
            torsoHalfW = lat.Count > 12 ? lat[Mathf.RoundToInt(lat.Count * 0.90f)] : rTorso;
            torsoHalfD = dep.Count > 12 ? dep[Mathf.RoundToInt(dep.Count * 0.90f)] : rTorso;
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[S91caps] ELLIPSE halfW={13:F4} halfD={14:F4} | radii (m): upperArm={0:F4} "
                + "forearm={1:F4} hand={2:F4} head={3:F4} neck={4:F4} TORSO={5:F4} (median) "
                + "torso axis Hips->Neck = {6:F4} m  vertex counts {7}/{8}/{9}/{10}/{11}/{12}",
                rUpper, rFore, rHand, rHead, rNeck, rTorso,
                Vector3.Distance(hipsT.position, neckT.position),
                sets[0].Count, sets[1].Count, sets[2].Count, sets[3].Count, sets[4].Count, sets[5].Count,
                torsoHalfW, torsoHalfD));
            File.WriteAllText(Path.Combine(outDir, "radii_" + tag + ".txt"), string.Format(
                CultureInfo.InvariantCulture,
                "upper {0:F5}\nfore {1:F5}\nhand {2:F5}\nhead {3:F5}\nneck {4:F5}\ntorso {5:F5}\n"
                + "torsoHalfW {6:F5}\ntorsoHalfD {7:F5}\n",
                rUpper, rFore, rHand, rHead, rNeck, rTorso, torsoHalfW, torsoHalfD));
        }

        private static float PointSeg(Vector3 p, Vector3 a, Vector3 b, out float t)
        {
            Vector3 ab = b - a; float L2 = ab.sqrMagnitude;
            t = L2 < 1e-8f ? 0f : Mathf.Clamp01(Vector3.Dot(p - a, ab) / L2);
            return Vector3.Distance(p, a + ab * t);
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
            // S90 signed measurements, in the LOCAL half-space at the lip. The face is only locally
            // planar, so the test is restricted to hand vertices within 4 cm of the landmark -- the
            // actual contact patch. Positive = outside the face.
            Vector3 nWorld = (ik != null && ik.LastFaceNormalWorld != Vector3.zero)
                ? ik.LastFaceNormalWorld
                : (head.TransformDirection(FallbackNormal(S89ContactIK.BodyKey(gameObject.name)))).normalized;
            float signedClear = Vector3.Dot(palm - mouth, nWorld);
            float penDeepest = 0f, gapNearest = float.MaxValue;
            foreach (int i in handIdx)
            {
                float r = Vector3.Distance(vs[i], mouth);
                if (r < gapNearest) gapNearest = r;
                if (r > 0.04f) continue;
                float sd = Vector3.Dot(vs[i] - mouth, nWorld);
                if (-sd > penDeepest) penDeepest = -sd;
            }
            if (gapNearest == float.MaxValue) gapNearest = -1f;
            // hand capsule runs wrist -> fingertip-ish; the palm point is its midline
            Vector3 rgNow = shoulderT.position - rUpperArmT.position; rgNow.y = 0f; rgNow.Normalize();
            Vector3 tip = midProx != null ? wrist.position + (midProx.position - wrist.position) * 1.9f : palm;
            Vector3 miss = mouth - palm;
            Vector3 wl = head.InverseTransformPoint(wrist.position);
            Vector3 el = head.InverseTransformPoint(elbow.position);
            rows.Append(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:F4},{2:F5},{3:F5},{4:F5},{5:F5},{6:F5},{7:F5},{8:F5},{9:F5},{10:F5},{11:F5},{12:F5},"
                + "{13:F4},{14:F4},{15:F4},{16:F4},{17:F6},{18:F6},{19:F6},"
                + "{20:F5},{21:F5},{22:F5},{23:F5},{24:F5},{25:F5},{26:F5},{27:F5},{28:F5},"
                + "{29:F5},{30:F5},{31:F5},{32:F5},{33:F5},{34:F5},{35:F5},{36:F5},{37:F5},"
                + "{38:F5},{39:F5},{40:F5},{41:F5},{42:F5},"
                + "{43:F3},{44:F3},{45:F3},{46:F3},{47:F3},{48:F3},{49:F3},{50:F3},{51:F3},{52:F3},"
                + "{53:F3},{54:F3},{55:F3}\n",
                frame, ik != null ? ik.LastFrameF : frame,
                Vector3.Distance(palm, mouth), handIdx.Min(i => Vector3.Distance(vs[i], mouth)),
                Vector3.Dot(miss, Vector3.up), Vector3.Dot(miss, bodyRight), Vector3.Dot(miss, bodyFwd),
                wl.x, wl.y, wl.z, el.x, el.y, el.z, ik != null ? ik.LastWeight : 0f,
                ik != null ? ik.DeltaShoulderDeg : 0f, ik != null ? ik.DeltaElbowDeg : 0f,
                ik != null ? ik.DeltaWristDeg : 0f, signedClear, penDeepest, gapNearest,
                shoulderT.position.x, shoulderT.position.y, shoulderT.position.z,
                elbow.position.x, elbow.position.y, elbow.position.z,
                wrist.position.x, wrist.position.y, wrist.position.z,
                tip.x, tip.y, tip.z,
                hipsT.position.x, hipsT.position.y, hipsT.position.z,
                neckT.position.x, neckT.position.y, neckT.position.z,
                head.position.x, head.position.y, head.position.z, rgNow.x, rgNow.z,
                ik != null ? ik.LastWristFlexDeg : 0f, ik != null ? ik.LastWristDevDeg : 0f,
                ik != null ? ik.LastWristTwistDeg : 0f, ik != null ? ik.LastForearmPronationDeg : 0f,
                ik != null ? ik.AppliedPronationDeg : 0f,
                ik != null ? ik.LastElbowFlexDeg : 0f, ik != null ? ik.LastShoulderElevDeg : 0f,
                ik != null ? ik.LastShoulderTwistDeg : 0f, ik != null ? ik.LastShoulderSwingDeg : 0f,
                ik != null ? ik.LastRollGivenUpDeg : 0f,
                ik != null ? ik.AuthoredWristFlexDeg : 0f, ik != null ? ik.AuthoredWristDevDeg : 0f,
                ik != null ? ik.AuthoredWristTwistDeg : 0f));
            if (frame >= 20 && frame <= 100)
                foreach (int i in handIdx)
                { var q = head.InverseTransformPoint(vs[i]);
                  hand.Append(string.Format(CultureInfo.InvariantCulture, "{0},{1:F5},{2:F5},{3:F5}\n", frame, q.x, q.y, q.z)); }

            // S91b: opened from [15,115] to [4,120]. The old window started at the END of the
            // ramp-in, so only the static hold was ever rendered -- which is why the 0.3x close-up
            // of the REACTION could not be cut from a capture at all. The ramps are the part a
            // slow-motion close-up is for.
            if (cam != null && frame >= 4 && frame <= 120 && shot < 660)
            {
                // S90: a profile shot is now mandatory. Penetration at the mouth is invisible from
                // the front -- that is exactly how S89's frontal verdict still passed while the hand
                // was inside the head.
                // S91: three angles, now the permanent standard. Penetration hid from the frontal
                // view once already (S89's verdict still passed with the hand inside the skull), and
                // the profile alone cannot show lateral placement.
                // Name the shots for the frame they DEPICT (the previous sample), not the frame
                // being measured on this tick -- see prevRenderedFrame. The first sample in the
                // state has no predecessor to name, so it is measured but not rendered.
                if (prevRenderedFrame >= 0)
                {
                    Shoot(mouth, prevRenderedFrame, 0);   // frontal
                    Shoot(mouth, prevRenderedFrame, 1);   // profile
                    Shoot(mouth, prevRenderedFrame, 2);   // three-quarter
                }
                prevRenderedFrame = frame;
            }
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

        private void Shoot(Vector3 mouth, int frame, int view)
        {
            marker.transform.position = mouth;
            Vector3 focus = head.position + Vector3.up * -0.02f;
            Vector3 off;
            if (view == 1) off = -bodyRight * 0.68f + Vector3.up * 0.04f;                       // profile, from the character's right
            else if (view == 2) off = (bodyFwd * 0.72f - bodyRight * 0.60f).normalized * 0.70f + Vector3.up * 0.05f;  // three-quarter
            else off = bodyFwd * 0.70f + Vector3.up * 0.06f;                                    // frontal
            cam.transform.position = focus + off;
            cam.transform.LookAt(focus);
            cam.targetTexture = rt; cam.Render();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, 1000, 720), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(outDir, "frames_" + tag,
                (view == 1 ? "p_" : view == 2 ? "q_" : "f_") + frame.ToString("D4") + ".png"), tex.EncodeToPNG());
            shot++;
        }

        private void OnDisable() { Flush(); }

        private void Flush()
        {
            // The bootstrap host GameObject carries a probe instance too, and it has no Animator --
            // its Init throws, headIdx stays null, and on shutdown it used to Flush a header-only
            // CSV over the pedestrian probe's real one. Only an instance that actually measured may
            // write. (This is why m2 came back with 1 line while m1 had 47.)
            if (wrote || headIdx == null || rows.Length == 0 || string.IsNullOrEmpty(outDir)) return;
            wrote = true;
            File.WriteAllText(Path.Combine(outDir, "contact_" + tag + ".csv"), rows.ToString());
            File.WriteAllText(Path.Combine(outDir, "handcloud_" + tag + ".csv"), hand.ToString());
            Debug.Log("[S89probe2] wrote contact_" + tag + ".csv and handcloud_" + tag + ".csv");
        }
    }
}
