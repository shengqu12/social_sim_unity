using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 44 TASK 1 diagnostic: per-frame record of everything that determines whether a
    /// pedestrian's feet match its translation. READ-ONLY -- it writes a CSV and changes no value
    /// on any component. Env-var gated (AUTOTRIAL_S44_PROBE=&lt;path&gt;), and it attaches itself
    /// via RuntimeInitializeOnLoadMethod specifically so that enabling it requires editing no
    /// existing file.
    ///
    /// What it is for. TWO components write `animator.speed`, with different semantics and no
    /// execution-order override between them:
    ///
    ///   Scenario.Agents.Base.Move()      animator.speed = velocity.magnitude
    ///                                    -- raw m/s used directly as a playback multiplier
    ///   S32AnimatorSpeedScaler.Update()  animator.speed = clamp(smoothedSpeed / 1.3, 0.3, 1.5)
    ///                                    -- a ratio against a hard-coded reference pace
    ///
    /// Both run in Update(), neither declares an order, so the last writer of the frame wins and
    /// which one that is cannot be read off the source. This probe samples in LateUpdate (after
    /// every Update has run) and records, alongside the final value, what each writer WOULD have
    /// produced -- so the winner is identified from data instead of assumed.
    ///
    /// It also records the quantity the whole question turns on: the ratio between actual ground
    /// speed and the pace the playing clip was authored for. Feet match the ground only when
    /// animator.speed equals that ratio; anything else is a slide, in one direction or the other.
    /// </summary>
    public class S44SlideProbe : MonoBehaviour
    {
        private static string OutPath =>
            System.Environment.GetEnvironmentVariable("AUTOTRIAL_S44_PROBE");

        // Candidate state names, tested by hash compare. AnimatorStateInfo exposes no name at
        // runtime, so a fixed candidate list is the only way to label a state; these are every
        // state in SocialForcesAnimatorController plus the generated single-state Mixamo
        // controllers' own name.
        private static readonly string[] StateNames =
        {
            "Idle", "Walk", "Run", "Blend Tree", "AssertiveGesture", "SurprisedReaction", "Motion",
        };

        // Mirrors S32AnimatorSpeedScaler's own constants so the probe can report what that
        // component would have computed this frame. Kept as literals rather than read off the
        // component: the point is to compare an independent reconstruction against the observed
        // final value, not to trust the component's own view of itself.
        private const float ReferenceSpeedMps = 1.3f;
        private const float MinSpeedScale = 0.3f;
        private const float MaxSpeedScale = 1.5f;
        private const float SmoothingTau = 0.25f;
        private static readonly string[] ReactionStates = { "SurprisedReaction", "AssertiveGesture" };

        private Animator animator;
        private Scenario.Agents.Base baseAgent;
        private Vector3 lastPos;
        private bool havePrev;
        private float smoothed;
        private StreamWriter writer;
        private float t0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (string.IsNullOrEmpty(OutPath)) { return; }
            var host = new GameObject("S44SlideProbeHost");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<Attacher>();
        }

        /// <summary>Polls for pedestrians and attaches one probe each. Pedestrians are spawned well
        /// after scene load, so a one-shot find at startup would attach to nothing.</summary>
        private class Attacher : MonoBehaviour
        {
            private IEnumerator Start()
            {
                var seen = new System.Collections.Generic.HashSet<int>();
                while (true)
                {
                    foreach (var scaler in Object.FindObjectsOfType<S32AnimatorSpeedScaler>())
                    {
                        int id = scaler.gameObject.GetInstanceID();
                        if (seen.Contains(id)) { continue; }
                        seen.Add(id);
                        scaler.gameObject.AddComponent<S44SlideProbe>();
                        Debug.Log("[S44Probe] attached to " + scaler.gameObject.name);
                    }
                    yield return new WaitForSeconds(0.5f);
                }
            }
        }

        private void Awake()
        {
            string path = OutPath;
            if (string.IsNullOrEmpty(path)) { enabled = false; return; }
            animator = IVI.AvatarAnimatorUtility.GetLocomotionAnimator(gameObject);
            baseAgent = GetComponent<Scenario.Agents.Base>();
            if (animator == null) { enabled = false; return; }

            t0 = Time.time;
            bool fresh = !File.Exists(path);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                writer = new StreamWriter(path, append: true);
                if (fresh)
                {
                    writer.WriteLine("t,agent,animator_speed_final,base_would_write,scaler_would_write,"
                        + "ground_speed_mps,base_velocity_mps,state,reaction_hold,"
                        + "clip_length,clip_authored_name,scale_needed_vs_ref");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[S44Probe] could not open " + path + ": " + e.Message);
                enabled = false;
            }
        }

        private string CurrentStateName(AnimatorStateInfo info)
        {
            for (int i = 0; i < StateNames.Length; i++)
            {
                if (info.IsName(StateNames[i])) { return StateNames[i]; }
            }
            return "hash:" + info.shortNameHash;
        }

        private bool ReactionActive()
        {
            var cur = animator.GetCurrentAnimatorStateInfo(0);
            var next = animator.GetNextAnimatorStateInfo(0);
            bool inTransition = animator.IsInTransition(0);
            for (int i = 0; i < ReactionStates.Length; i++)
            {
                if (cur.IsName(ReactionStates[i])) { return true; }
                if (inTransition && next.IsName(ReactionStates[i])) { return true; }
            }
            return false;
        }

        // LateUpdate, deliberately: every Update() has run by now, so animator.speed holds the
        // value that actually survived the frame. Sampling in Update would see whichever writer
        // happened to precede this component.
        private void LateUpdate()
        {
            if (writer == null || animator == null) { return; }

            Vector3 pos = transform.position;
            float ground = 0f;
            if (havePrev && Time.deltaTime > 1e-5f)
            {
                Vector3 d = pos - lastPos;
                d.y = 0f;
                ground = d.magnitude / Time.deltaTime;
            }
            lastPos = pos;
            havePrev = true;
            if (ground <= 6.0f)
            {
                float alpha = 1f - Mathf.Exp(-Time.deltaTime / SmoothingTau);
                smoothed = Mathf.Lerp(smoothed, ground, alpha);
            }

            var info = animator.GetCurrentAnimatorStateInfo(0);
            bool hold = ReactionActive();
            float baseVel = baseAgent != null ? baseAgent.velocity.magnitude : float.NaN;

            float scalerWould = hold
                ? 1.0f
                : Mathf.Clamp(smoothed / ReferenceSpeedMps, MinSpeedScale, MaxSpeedScale);

            var clips = animator.GetCurrentAnimatorClipInfo(0);
            string clipName = clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "";
            float clipLen = clips.Length > 0 && clips[0].clip != null ? clips[0].clip.length : float.NaN;

            var sb = new StringBuilder();
            sb.Append((Time.time - t0).ToString("F3", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(gameObject.name.Replace(',', ' ')).Append(',');
            sb.Append(animator.speed.ToString("F4", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(baseVel.ToString("F4", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(scalerWould.ToString("F4", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(ground.ToString("F4", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(baseVel.ToString("F4", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(CurrentStateName(info)).Append(',');
            sb.Append(hold ? "1" : "0").Append(',');
            sb.Append(clipLen.ToString("F3", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(clipName.Replace(',', ' ')).Append(',');
            // What animator.speed WOULD have to be for the feet to match the ground, if the clip
            // really is authored for ReferenceSpeedMps. Divergence between this and the final
            // animator.speed is the slide, signed: above = feet outrun the ground.
            sb.Append((ground / ReferenceSpeedMps).ToString("F4", CultureInfo.InvariantCulture));
            writer.WriteLine(sb.ToString());
        }

        private void OnDestroy()
        {
            if (writer != null) { writer.Flush(); writer.Dispose(); writer = null; }
        }

        private void OnApplicationQuit()
        {
            if (writer != null) { writer.Flush(); writer.Dispose(); writer = null; }
        }
    }
}
