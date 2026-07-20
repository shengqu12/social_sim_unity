using System.Text;
using UnityEngine;

namespace SEAN.AutoTrial
{
    /// <summary>
    /// Session 21 STEP 3 diagnostic: per-frame transform watcher, attached to the Zone B
    /// pedestrian instance immediately after spawn (see AutoTrialBootstrap.SpawnPedestrian).
    /// Logs position/frame/elapsed-time every Update AND LateUpdate for a fixed window after
    /// spawn, so a position jump can be bracketed to "between which two lifecycle points" even
    /// though Unity gives no direct "who wrote this Transform" signal. Read-only observation
    /// only -- never writes to the watched transform.
    /// </summary>
    public class S21TransformWatcher : MonoBehaviour
    {
        public float watchDurationSec = 3f;
        private float startTime;
        private int frameCount;
        private Vector3 lastLoggedPos;
        private readonly StringBuilder log = new StringBuilder();

        void Start()
        {
            startTime = Time.time;
            lastLoggedPos = transform.position;
            log.AppendLine("[S21Watcher] spawn position: " + transform.position.ToString("F4"));
        }

        void Update()
        {
            if (Time.time - startTime > watchDurationSec) { return; }
            frameCount++;
            float d = Vector3.Distance(transform.position, lastLoggedPos);
            log.AppendLine(string.Format("[S21Watcher] Update  frame={0} t={1:F4} pos={2} d_since_last={3:F4}",
                frameCount, Time.time - startTime, transform.position.ToString("F4"), d));
        }

        void LateUpdate()
        {
            if (Time.time - startTime > watchDurationSec)
            {
                if (log.Length > 0)
                {
                    Debug.Log(log.ToString());
                    log.Clear();
                }
                return;
            }
            float d = Vector3.Distance(transform.position, lastLoggedPos);
            log.AppendLine(string.Format("[S21Watcher] LateUpd frame={0} t={1:F4} pos={2} d_since_last={3:F4}",
                frameCount, Time.time - startTime, transform.position.ToString("F4"), d));
            lastLoggedPos = transform.position;
        }
    }
}
