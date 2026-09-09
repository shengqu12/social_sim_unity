using System.Collections;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 113 PART 2: the gaze layer for the S68 curious crouch. Turns the neck and head toward
    /// the robot while the pedestrian is stationary and watching (S68 Stop / CrouchEnter /
    /// CrouchHold / CrouchExit), so the crouch reads as "crouching to LOOK AT the robot" rather
    /// than a squat exercise with the face pointed at the pavement.
    ///
    /// ================== SCOPE, AND WHY IT IS INERT BY CONSTRUCTION ==================
    /// OPT-IN with S68's own flag (AUTOTRIAL_S68_CROUCH) and keyed on S68's state machine:
    ///   * no flag -> the bootstrap returns before creating a host: no component, no
    ///     DeoptimizeTransformHierarchy, no object-graph change, no log line;
    ///   * flag set but no S68CuriousCrouch on the pedestrian (non-curious personality) -> the
    ///     finder times out and the layer never attaches;
    ///   * attached but S68 outside its four watching states -> weight 0 and NO transform write
    ///     (WriteFrames does not advance; the CSV rows carry w=0 and delta=0).
    /// Regression arms prove each branch categorically by the absence of "[S113Gaze]" lines.
    ///
    /// ================== MECHANISM (the S89 pattern) ==================
    /// The spawned clone runs with an optimised transform hierarchy (S89 STEP 0: no bone
    /// transforms exposed), so the layer deoptimises it once, in memory, and writes the neck and
    /// head world rotations in LateUpdate -- after the Animator (and after S68's own SeekCrouch,
    /// which runs in Update) has posed the body, so the write is what the renderer sees. Direct
    /// transform writes rather than SetBoneLocalRotation because the latter is clamped in muscle
    /// space, which is exactly what the crouch's pitched torso would fight.
    ///
    /// Per frame, with the robot's body as the target (base_link + targetHeight):
    ///   1. desired = direction head -> target, expressed in the TORSO frame (chest bone, with the
    ///      forward/up axes captured at setup) as yaw / pitch relative to neutral;
    ///   2. clamp to the human cervical range of motion -- the limits used, in-script:
    ///        yaw   +/- 70 deg   (axial rotation; textbook adult range ~70-90, the conservative end)
    ///        pitch +/- 40 deg   (extension / flexion from neutral; adult extension ~40-70, flexion
    ///                            ~45-60 -- 40 keeps the pose well inside both)
    ///      -- the crouch itself pitches the torso forward, so "pitch up toward a close robot" is
    ///      measured against the tilted torso, which is where the ROM actually lives;
    ///   3. slerp-smooth the desired direction (time constant gazeTau) so there is never a snap;
    ///   4. split the rotation ~40 % neck / ~60 % head: the neck takes 0.4 of the head->desired
    ///      rotation, the head takes what remains after the neck has moved;
    ///   5. weight ramps in and out over rampSeconds at the state edges (smoothstep).
    /// Measurement per frame goes to a CSV when AUTOTRIAL_S113_MEASURE names a directory:
    /// gaze error (angle between the written head forward and the target direction), the head's
    /// total yaw/pitch relative to the torso (the anatomical neck angles the ROM gate reads), the
    /// per-bone deltas applied, and the S68 state -- so the three S113 gates (error < 15 deg through
    /// the hold, ROM every frame, zero writes outside the active states) are graded off the record
    /// rather than asserted.
    /// </summary>
    [DefaultExecutionOrder(650)]   // after S68CuriousCrouch (600) so the state it reads is this frame's
    public class S113CuriousGaze : MonoBehaviour
    {
        public const string MeasureEnv = "AUTOTRIAL_S113_MEASURE";
        /// <summary>Opt-out for A/B strips only: the S68 crouch runs with the gaze layer absent.</summary>
        public const string OffEnv = "AUTOTRIAL_S113_GAZE_OFF";

        // ---- the ROM limits, cited in the class doc ----
        public const float YawLimitDeg = 70f;
        public const float PitchUpLimitDeg = 40f;     // extension
        public const float PitchDownLimitDeg = 40f;   // flexion

        public float neckShare = 0.4f;                // 40 / 60 neck / head
        public float rampSeconds = 0.4f;
        public float gazeTau = 0.18f;                 // smoothing time constant of the target direction
        public float targetHeight = 0.30f;            // metres above base_link: the dog's head, roughly

        // ---- readbacks, graded by the gates ----
        public float LastWeight { get; private set; }
        public float LastGazeErrorDeg { get; private set; }
        public float LastHeadYawDeg { get; private set; }
        public float LastHeadPitchDeg { get; private set; }
        public float DeltaNeckDeg { get; private set; }
        public float DeltaHeadDeg { get; private set; }
        public int WriteFrames { get; private set; }
        public int Frames { get; private set; }

        private S68CuriousCrouch crouch;
        private Animator animator;
        private Transform neck, head, chest, root;
        private Scenario.Robot robot;
        private Transform robotBody;
        private bool ready;
        private Vector3 headFwdLocal, headUpLocal, chestFwdLocal, chestUpLocal;
        private Vector3 smoothedDir;
        private bool haveSmoothed;
        private float weight;             // current ramp weight
        private bool wasActive;
        private float edgeTime = -1f;
        private StreamWriter csv;
        private bool announced;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // INERT WITHOUT THE S68 FLAG, BY CONSTRUCTION -- see the class doc.
            if (!S68CuriousCrouch.Enabled) return;
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(OffEnv)))
            {
                Debug.Log("[S113Gaze] " + OffEnv + " set -- gaze layer absent (A/B baseline).");
                return;
            }
            var host = new GameObject("S113CuriousGazeHost");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<S113CuriousGaze>().StartCoroutine(nameof(FindTarget));
        }

        private IEnumerator FindTarget()
        {
            S68CuriousCrouch c = null;
            float deadline = Time.time + 30f;
            while (c == null && Time.time < deadline)
            {
                c = Object.FindObjectOfType<S68CuriousCrouch>();
                if (c == null) yield return new WaitForSeconds(0.25f);
            }
            if (c == null) { Debug.Log("[S113Gaze] no S68CuriousCrouch in the scene -- inert (nothing attached, nothing deoptimised)"); yield break; }
            if (c.GetComponent<S113CuriousGaze>() == null) c.gameObject.AddComponent<S113CuriousGaze>().Setup(c);
        }

        public void Setup(S68CuriousCrouch c)
        {
            crouch = c;
            root = c.transform;
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            { Debug.LogWarning("[S113Gaze] no humanoid Animator -- inert"); return; }

            if (!animator.hasTransformHierarchy)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                AnimatorUtility.DeoptimizeTransformHierarchy(animator.gameObject);
                sw.Stop();
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[S113Gaze] deoptimised the spawned clone in {0:F2} ms (memory only, no asset touched)",
                    sw.Elapsed.TotalMilliseconds));
            }
            head = animator.GetBoneTransform(HumanBodyBones.Head);
            neck = animator.GetBoneTransform(HumanBodyBones.Neck);
            chest = animator.GetBoneTransform(HumanBodyBones.Chest) ?? animator.GetBoneTransform(HumanBodyBones.Spine);
            if (neck == null) neck = head != null ? head.parent : null;
            ready = head != null && neck != null && chest != null;
            if (!ready) { Debug.LogWarning("[S113Gaze] neck/head/chest bones unresolved -- inert"); return; }

            // Reference axes, captured once in the standing pose the pedestrian spawns in: the
            // character root's forward/up expressed in each bone's local frame. The Bip01 bones do
            // not share Unity's axis convention, so nothing below assumes which local axis is
            // "forward" -- every direction is (bone.rotation * capturedLocal).
            Quaternion ih = Quaternion.Inverse(head.rotation), ic = Quaternion.Inverse(chest.rotation);
            headFwdLocal = ih * root.forward; headUpLocal = ih * root.up;
            chestFwdLocal = ic * root.forward; chestUpLocal = ic * root.up;

            robot = Object.FindObjectOfType<Scenario.Robot>();
            if (robot != null)
            {
                robotBody = robot.base_link != null ? robot.base_link.transform : robot.transform;
                // prefer the physics root if there is one (same body S68 reads its velocity from)
                if (robot.base_link != null)
                    foreach (ArticulationBody b in robot.base_link.GetComponentsInChildren<ArticulationBody>())
                        if (b.isRoot) { robotBody = b.transform; break; }
            }
            if (robotBody == null) { Debug.LogWarning("[S113Gaze] no robot in the scene -- inert"); ready = false; return; }

            string dir = System.Environment.GetEnvironmentVariable(MeasureEnv);
            if (!string.IsNullOrEmpty(dir))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    csv = new StreamWriter(Path.Combine(dir, "s113_gaze.csv"));
                    csv.WriteLine("t,state,active,weight,gaze_err_deg,head_yaw_deg,head_pitch_deg,delta_neck_deg,delta_head_deg,dist_robot,wrote");
                }
                catch (System.Exception e) { Debug.LogWarning("[S113Gaze] measure CSV not opened: " + e.Message); csv = null; }
            }
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[S113Gaze] armed on '{0}' neck='{1}' head='{2}' chest='{3}' split={4:F2}/{5:F2} ramp={6:F2}s tau={7:F2}s "
                + "ROM yaw+/-{8:F0} pitch +{9:F0}/-{10:F0} target=base_link+{11:F2}m t={12:F2}",
                gameObject.name, neck.name, head.name, chest.name, neckShare, 1f - neckShare, rampSeconds, gazeTau,
                YawLimitDeg, PitchUpLimitDeg, PitchDownLimitDeg, targetHeight, Time.time));
        }

        private static float Smooth(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }

        private void LateUpdate()
        {
            if (!ready || crouch == null) return;
            Frames++;
            bool active = crouch.enabled && crouch.IsWatching;
            if (active != wasActive) { edgeTime = Time.time; wasActive = active; }
            float ramp = edgeTime < 0f ? 1f : Smooth((Time.time - edgeTime) / Mathf.Max(0.01f, rampSeconds));
            weight = active ? ramp : 1f - ramp;
            LastWeight = weight;
            DeltaNeckDeg = DeltaHeadDeg = 0f;

            // The target and the torso frame, this frame.
            Vector3 target = robotBody.position + Vector3.up * targetHeight;
            Vector3 cf = (chest.rotation * chestFwdLocal).normalized;
            Vector3 cu = (chest.rotation * chestUpLocal).normalized;
            Vector3 cr = Vector3.Cross(cu, cf).normalized;
            Vector3 headPos = head.position;
            Vector3 want = (target - headPos).normalized;

            // Smooth the DIRECTION, not the joint -- a snap is a step in the target, so filter it there.
            if (!haveSmoothed || weight <= 0f) { smoothedDir = want; haveSmoothed = true; }
            else
            {
                float a = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, gazeTau));
                smoothedDir = Vector3.Slerp(smoothedDir, want, a).normalized;
            }
            // Clamp AFTER smoothing, in THIS frame's torso frame (the cervical ROM lives there,
            // crouched or not). Iteration 3 clamped before smoothing and the torso turned under the
            // smoothed direction during the stop, so a direction clamped to 70 deg one frame read
            // 73 deg the next; clamping the filtered direction each frame cannot exceed the limit.
            float yaw = Mathf.Atan2(Vector3.Dot(smoothedDir, cr), Vector3.Dot(smoothedDir, cf)) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(smoothedDir, cu), -1f, 1f)) * Mathf.Rad2Deg;
            float yawC = Mathf.Clamp(yaw, -YawLimitDeg, YawLimitDeg);
            float pitchC = Mathf.Clamp(pitch, -PitchDownLimitDeg, PitchUpLimitDeg);
            Vector3 desired = (Quaternion.AngleAxis(yawC, cu) * cf) * Mathf.Cos(pitchC * Mathf.Deg2Rad)
                            + cu * Mathf.Sin(pitchC * Mathf.Deg2Rad);
            desired.Normalize();
            smoothedDir = desired;   // the written direction is the clamped one; the filter continues from it

            bool wrote = false;
            if (weight > 0f)
            {
                // 1. neck takes its share of the head->desired rotation
                Vector3 hf0 = (head.rotation * headFwdLocal).normalized;
                Quaternion full = Quaternion.FromToRotation(hf0, smoothedDir);
                Quaternion rn = Quaternion.Slerp(Quaternion.identity, full, neckShare * weight);
                Quaternion n0 = neck.rotation;
                neck.rotation = rn * neck.rotation;
                DeltaNeckDeg = Quaternion.Angle(n0, neck.rotation);
                // 2. head takes what is left after the neck moved it
                Vector3 hf1 = (head.rotation * headFwdLocal).normalized;
                Quaternion rem = Quaternion.FromToRotation(hf1, smoothedDir);
                Quaternion rh = Quaternion.Slerp(Quaternion.identity, rem, weight);
                Quaternion h0 = head.rotation;
                head.rotation = rh * head.rotation;
                DeltaHeadDeg = Quaternion.Angle(h0, head.rotation);
                wrote = true;
                WriteFrames++;
                if (!announced)
                {
                    announced = true;
                    Debug.Log(string.Format(CultureInfo.InvariantCulture,
                        "[S113Gaze] engaged: state={0} t={1:F2} yaw_req={2:F1} pitch_req={3:F1} (clamped {4:F1}/{5:F1})",
                        crouch.CurrentState, Time.time, yaw, pitch, yawC, pitchC));
                }
            }

            // Readback off the pose that is actually on screen.
            Vector3 hfNow = (head.rotation * headFwdLocal).normalized;
            LastGazeErrorDeg = Vector3.Angle(hfNow, want);
            LastHeadYawDeg = Mathf.Atan2(Vector3.Dot(hfNow, cr), Vector3.Dot(hfNow, cf)) * Mathf.Rad2Deg;
            LastHeadPitchDeg = Mathf.Asin(Mathf.Clamp(Vector3.Dot(hfNow, cu), -1f, 1f)) * Mathf.Rad2Deg;

            if (csv != null)
            {
                Vector3 d = robotBody.position - root.position; d.y = 0f;
                csv.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:F3},{1},{2},{3:F3},{4:F2},{5:F2},{6:F2},{7:F2},{8:F2},{9:F3},{10}",
                    Time.time, crouch.CurrentState, active ? 1 : 0, weight, LastGazeErrorDeg, LastHeadYawDeg, LastHeadPitchDeg,
                    DeltaNeckDeg, DeltaHeadDeg, d.magnitude, wrote ? 1 : 0));
            }
        }

        private void OnDestroy()
        {
            if (crouch != null)
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[S113Gaze] summary: frames={0} writes={1} (writes happen only in the watching states plus the declared {2:F2}s ramp-out tail after them; the CSV proves it)",
                    Frames, WriteFrames, rampSeconds));
            if (csv != null) { csv.Flush(); csv.Close(); csv = null; }
        }
    }
}
