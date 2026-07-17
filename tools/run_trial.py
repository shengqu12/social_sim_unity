#!/usr/bin/env python3
"""
CLI-driven trial runner for the SEAN 2.0 AutoTrial pipeline.

    python tools/run_trial.py --appearance wheelchair_user --personality indifferent --duration 90

produces, with zero manual Unity interaction: robot-POV video, third-person chase video,
near-pedestrian clips cut from both, frames.csv of per-frame robot data, and meta.json.

Pipeline:
  1. Validate args against a friendly hardcoded appearance list (Unity's own resolution in
     AutoTrialBootstrap.cs is authoritative -- this is just an early, cheap UX error).
  2. Refuse to start if the Unity Editor already has this project open (Temp/UnityLockfile held
     by a live process). A stale lockfile with no live holder is removed.
  3. Health-check ROS inside the `ros` container (move_base alive, /map present, goal topic has a
     subscriber). Reused by default; --fresh-ros tears down and relaunches cleanly.
  4. Write a per-trial JSON config, launch Unity in batchmode (or windowed, see --windowed) via
     -executeMethod SEAN.AutoTrial.AutoTrialEditorRunner.EnterPlay -trialConfig <path>. No -quit,
     no -nographics -- AutoTrialBootstrap/TrialController own the exit once the trial finishes.
  5. If a goal pose was requested, verify it actually reached move_base
     (rostopic echo -n1 on the goal topic) before trusting the run.
  6. Wait for Unity to exit (hard timeout = duration + 60s), then assemble full-length mp4s from
     the captured JPGs and cut near-pedestrian clips from frames.csv (min_dist < --near-dist, ±2s
     padding). Raw JPGs are deleted afterward unless --keep-full.

Session 6 instrumentation: after a successful trial, meta.json (written by Unity's
TrialController.WriteMetaJson) is read back and augmented with host-known, ROS-side facts this
script observes but never sets: the live /move_base/oscillation_timeout value, the ROS run_id and
bringup mode (fresh/reused) for this trial, how old the current roscore process is in wall-clock
seconds, and this trial's position (1-based) within its sequential run. This script contains no
code that calls `rosparam set` / `dynparam set` on oscillation_timeout or any other move_base
param -- every value recorded here is read live via `rosparam get`, never assigned.

Canonical ROS bringup (read-only extracted from social_sim_ros's sean_navstack.launch and
map_server.launch -- never edited by this script):

    roslaunch social_sim_ros map_server.launch scene:=outdoor
    roslaunch social_sim_ros sean_navstack.launch scene:=outdoor prefix:=<run-name>

sean_navstack.launch itself includes (in this order): sim_tcp_bridge.launch (the Unity<->ROS TCP
bridge, `tcp_server` node), kuri_move_base.launch (move_base + costmap params -- reused as-is for
the Unitree A1; there is no A1-specific move_base config in social_sim_ros, confirmed by recon),
kuri_description.launch, map_publisher.launch, trial_info.launch, depth_to_laserscan.launch.
map_server.launch is launched as a separate process alongside it (not included by
sean_navstack.launch), publishing /map from social_sim_ros/maps/<scene>/map.yaml.
"""
import argparse
import csv
import glob
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

PROJECT_DIR = Path(__file__).resolve().parent.parent  # .../social_sim_unity
DOCKER_CONTAINER = "ros"
DEFAULT_OUT_ROOT = Path.home() / "Desktop" / "research" / "social_navigation" / "trial_outputs"

ZONE_B_APPEARANCES = [
    "cyclist", "dog_walker", "female_child", "male_child",
    "phone_user", "scooter_user", "wheelchair_user", "white_cane_user",
]
PERSONALITIES = ["scared", "curious", "surprised", "indifferent", "assertive"]
# Zone A is validated by convention (snake_case -> Rocketbox PascalCase), not enumerated here --
# Unity's Resources.Load is authoritative. This regex just catches obvious typos early.
ZONE_A_PATTERN = re.compile(r"^[a-z]+(_[a-z0-9]+)*$")


def eprint(*a, **kw):
    print(*a, file=sys.stderr, **kw)
    sys.stderr.flush()


def find_unity_binary():
    candidates = sorted(glob.glob(str(Path.home() / "Unity/Hub/Editor/*/Editor/Unity")))
    if not candidates:
        raise RuntimeError("No Unity editor binary found under ~/Unity/Hub/Editor/*/Editor/Unity")
    return candidates[-1]


def validate_appearance_friendly(appearance):
    if appearance in ZONE_B_APPEARANCES:
        return
    if ZONE_A_PATTERN.match(appearance):
        return
    raise SystemExit(
        "--appearance '{}' doesn't look valid.\n"
        "Zone B (preset) options: {}\n"
        "Zone A (generic Rocketbox pedestrian): snake_case name, e.g. 'business_male_01' "
        "(Unity resolves this against Resources/Prefabs/Rocketbox/* and is authoritative -- "
        "this check is just an early typo catcher).".format(appearance, ", ".join(ZONE_B_APPEARANCES))
    )


def check_editor_lock():
    lockfile = PROJECT_DIR / "Temp" / "UnityLockfile"
    if not lockfile.exists():
        return
    try:
        out = subprocess.run(["lsof", "--", str(lockfile)], capture_output=True, text=True)
        held = out.returncode == 0 and out.stdout.strip() != ""
    except FileNotFoundError:
        # lsof not installed -- fall back to fuser
        out = subprocess.run(["fuser", str(lockfile)], capture_output=True, text=True)
        held = out.returncode == 0
    if held:
        raise SystemExit(
            "Refusing to start: the Unity Editor already has this project open "
            "(Temp/UnityLockfile is held by a live process). Close it first."
        )
    eprint("[run_trial] stale Temp/UnityLockfile found (no live holder) -- removing it.")
    lockfile.unlink()


def docker_exec(cmd, timeout=30):
    full = ["docker", "exec", DOCKER_CONTAINER, "bash", "-lc", cmd]
    return subprocess.run(full, capture_output=True, text=True, timeout=timeout)


def ros_health_check():
    """Returns (healthy: bool, warnings: list[str])."""
    warnings = []
    try:
        nodes = docker_exec("rosnode list").stdout
    except (subprocess.TimeoutExpired, FileNotFoundError) as e:
        return False, ["could not reach ROS container: {}".format(e)]

    if "/move_base" not in nodes:
        return False, ["move_base node not running"]

    topics = docker_exec("rostopic list").stdout
    if "/map" not in topics.split():
        return False, ["/map topic not present"]

    goal_info = docker_exec("rostopic info /move_base_simple/goal").stdout
    if "/move_base" not in goal_info:
        return False, ["/move_base is not subscribed to /move_base_simple/goal"]

    # Known benign inconsistency from recon: sean_navstack.launch and map_server.launch can be
    # running with different `scene` args (labstudy vs outdoor). Detect and warn, don't fail --
    # ROS doesn't care about the visual scene, only about odom/goal/map topics being live.
    procs = subprocess.run(["docker", "exec", DOCKER_CONTAINER, "bash", "-lc", "ps aux | grep roslaunch | grep -v grep"],
                            capture_output=True, text=True).stdout
    scenes = set(re.findall(r"scene:=(\S+)", procs))
    if len(scenes) > 1:
        warnings.append("running roslaunch processes have inconsistent scene args: {} (reusing anyway)".format(sorted(scenes)))

    return True, warnings


def ros_fresh_bringup(scene="outdoor", prefix="autotrial"):
    eprint("[run_trial] --fresh-ros: tearing down existing roslaunch processes in the container...")
    docker_exec("pkill -f roslaunch || true", timeout=15)
    time.sleep(2)
    docker_exec("pkill -9 -f roslaunch || true", timeout=15)
    time.sleep(1)

    eprint("[run_trial] launching canonical bringup: map_server.launch scene:={} + sean_navstack.launch scene:={} prefix:={}".format(scene, scene, prefix))
    subprocess.run(["docker", "exec", "-d", DOCKER_CONTAINER, "bash", "-lc",
                     "source /opt/ros/noetic/setup.bash; source ~/sim_ws/devel/setup.bash 2>/dev/null; "
                     "roslaunch social_sim_ros map_server.launch scene:={}".format(scene)])
    time.sleep(3)
    subprocess.run(["docker", "exec", "-d", DOCKER_CONTAINER, "bash", "-lc",
                     "source /opt/ros/noetic/setup.bash; source ~/sim_ws/devel/setup.bash 2>/dev/null; "
                     "roslaunch social_sim_ros sean_navstack.launch scene:={} prefix:={}".format(scene, prefix)])

    for attempt in range(30):
        time.sleep(2)
        healthy, warnings = ros_health_check()
        if healthy:
            eprint("[run_trial] fresh ROS bringup healthy after {}s.".format((attempt + 1) * 2))
            return
    raise SystemExit("Fresh ROS bringup did not become healthy within 60s.")


def ensure_ros_healthy(fresh):
    if fresh:
        ros_fresh_bringup()
        return
    healthy, warnings = ros_health_check()
    for w in warnings:
        eprint("[run_trial] WARNING: " + w)
    if not healthy:
        eprint("[run_trial] ROS health check failed ({}) -- attempting a fresh bringup.".format(warnings))
        ros_fresh_bringup()
    else:
        eprint("[run_trial] ROS health check passed (reusing running session).")


def snapshot_modified_tracked_files():
    """Tracked (non-untracked) files git already considers modified, before launching Unity.
    Observed in practice (2026-07-16): repeated batchmode Play-mode runs can leave Unity-internal
    state (e.g. a physics/IK-settled camera transform, or the ROS-TCP-Connector package
    re-registering components) written back into tracked scene/prefab files on domain reload or
    scene (re)open, even though AutoTrial's own code never touches them. Comparing before/after
    lets us revert only what *this run* dirtied, never anything already dirty beforehand."""
    out = subprocess.run(["git", "-C", str(PROJECT_DIR), "status", "--porcelain"],
                         capture_output=True, text=True).stdout
    modified = set()
    for line in out.splitlines():
        status, path = line[:2], line[3:]
        if status.strip() and status.strip() != "??":
            modified.add(path)
    return modified


def revert_newly_dirtied_tracked_files(before, after):
    """Restore any tracked file that became modified during this run, via `git show` (a read
    operation) piped to a plain file write -- never git add/commit/checkout/restore/stash, per
    this session's git-is-read-only rule. Never touches files that were already dirty in `before`
    (e.g. the pre-existing Microsoft-Rocketbox submodule / UserSettings churn)."""
    newly_dirty = after - before
    if not newly_dirty:
        return
    for rel_path in sorted(newly_dirty):
        abs_path = PROJECT_DIR / rel_path
        eprint("[run_trial] WARNING: Unity modified tracked file '{}' as a side effect of this run "
               "-- reverting via `git show HEAD` (not a git write command).".format(rel_path))
        result = subprocess.run(["git", "-C", str(PROJECT_DIR), "show", "HEAD:" + rel_path],
                                 capture_output=True)
        if result.returncode != 0:
            eprint("[run_trial] WARNING: could not read HEAD:{} to revert it -- leaving as-is, "
                   "check manually.".format(rel_path))
            continue
        abs_path.write_bytes(result.stdout)


def build_config(args, out_dir):
    def pose(x, y, z, yaw_deg):
        return {"x": x, "y": y, "z": z, "yawDeg": yaw_deg}

    config = {
        "appearance": args.appearance,
        "personality": args.personality.capitalize(),
        "spawnPose": pose(*args.spawn),
        "patrolWaypoints": [{"x": p[0], "y": p[1], "z": p[2]} for p in (args.patrol or [])],
        "fps": args.fps,
        "durationSec": args.duration,
        "outDir": str(out_dir),
        "hasGoalPose": args.goal is not None,
        "goalPose": pose(*args.goal) if args.goal is not None else pose(0, 0, 0, 0),
        "camera": {
            "povOffsetX": 0.0, "povOffsetY": 0.0, "povOffsetZ": 0.0,
            "chaseDistance": args.chase_distance,
            "chaseHeight": args.chase_height,
            "chaseLookHeight": args.chase_look_height,
        },
        "jpgQuality": args.jpg_quality,
    }
    if args.appearance == "phone_user":
        eprint("[run_trial] WARNING: phone_user's container is mid-rewiring (editor-side, pending "
               "verification) -- see AutoTrialBootstrap.ZoneBContainers comment. Results may not "
               "reflect the intended texting-avatar behavior yet.")
    return config


def read_publish_interval(log_path, timeout=45):
    pattern = re.compile(r"active task publishInterval=(\d+(?:\.\d+)?)s")
    deadline = time.time() + timeout
    while time.time() < deadline:
        if log_path.exists():
            text = log_path.read_text(errors="replace")
            m = pattern.search(text)
            if m:
                return float(m.group(1))
            if "[AutoTrial]" in text and "error" in text.lower():
                break
        time.sleep(1)
    return None


def start_goal_listener(timeout):
    """Starts listening for the next /move_base_simple/goal message *now*, concurrently with the
    Unity launch, rather than after some other wait -- AutoTrialBootstrap publishes the goal via
    an immediate reflection-based Publish() call within the first few seconds of Unity starting,
    well before the Task's own (often 60s+) publishInterval loop would fire again. A listener
    started only after that point would miss it entirely (PoseStamped isn't latched)."""
    return subprocess.Popen(
        ["docker", "exec", DOCKER_CONTAINER, "bash", "-lc",
         "timeout {}s rostopic echo -n1 /move_base_simple/goal".format(timeout)],
        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)


def finish_goal_listener(proc):
    try:
        out, _ = proc.communicate(timeout=10)
    except subprocess.TimeoutExpired:
        proc.kill()
        out = ""
    return bool(out and out.strip())


def tail_has_error(log_path):
    if not log_path.exists():
        return None
    text = log_path.read_text(errors="replace")
    m = re.search(r"\[AutoTrial\] .*", text)
    errors = re.findall(r"error CS\d+.*", text)
    return errors


def build_unity_cmd(log_path, windowed=False, extra_args=None, quit_after=False):
    unity_bin = find_unity_binary()
    cmd = [unity_bin, "-projectPath", str(PROJECT_DIR), "-logFile", str(log_path)]
    if not windowed:
        cmd.append("-batchmode")
    if quit_after:
        cmd.append("-quit")
    if extra_args:
        cmd.extend(extra_args)
    return cmd


def guarded_unity_run(cmd, timeout, extra_env=None):
    """The ONLY sanctioned way to launch Unity from this toolset, for a trial OR any ad hoc
    diagnostic. Wraps every launch in the dirty-tracked-file snapshot/revert guard (Session 1/3:
    raw launches that bypassed this left ROSConnectionPrefab.prefab / Outdoor.unity modified as a
    side effect, twice, precisely because they went around this check). Returns
    (returncode_or_None, timed_out: bool)."""
    dirty_before = snapshot_modified_tracked_files()
    env = dict(os.environ)
    if extra_env:
        env.update(extra_env)
    eprint("[run_trial] launching Unity (guarded): {}".format(" ".join(cmd)))
    proc = subprocess.Popen(cmd, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    timed_out = False
    try:
        proc.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        eprint("[run_trial] timeout ({}s) exceeded -- killing Unity.".format(timeout))
        proc.kill()
        proc.wait(timeout=10)
        timed_out = True
    finally:
        revert_newly_dirtied_tracked_files(dirty_before, snapshot_modified_tracked_files())
    return proc.returncode, timed_out


def run_diag_cmdvel(seconds, windowed=False):
    """Guarded launch of DiagCmdVel for `seconds`, then exit. Returns (returncode, timed_out, log_text)."""
    log_path = Path("/tmp/run_trial_diag_cmdvel.log")
    cmd = build_unity_cmd(log_path, windowed=windowed, extra_args=[
        "-executeMethod", "SEAN.AutoTrial.AutoTrialEditorRunner.EnterPlay",
    ])
    returncode, timed_out = guarded_unity_run(cmd, timeout=seconds + 60, extra_env={"DIAG_CMDVEL": "1"})
    text = log_path.read_text(errors="replace") if log_path.exists() else ""
    return returncode, timed_out, text


def warmup_ros_session():
    """Operational recipe; mechanism under investigation (REPORT.md Session 8).

    Session 8 found that priming a fresh bringup with one real move_base navigation cycle before
    the first batch trial -- independent of oscillation_timeout's value -- is what actually
    prevents oscillation-aborts (Cell 3: primed, timeout left at file's 1.0 -> 0/6 aborts; Cell 4:
    primed + dynparam 3.0 -> 0/6, reproducing Session 6's Cell 2). Unprimed bringups aborted 5-6/6
    regardless of whether oscillation_timeout was 1.0 or 3.0 (Session 6 Cell 1, Session 7 N=6).
    Landing oscillation_timeout=3.0 in sim_ws is therefore NOT required; this function is the real
    fix, encoded here so the recording pipeline doesn't depend on remembering the dance by hand.
    Also happens to set oscillation_timeout=3.0 live (harmless per Cell 3/4; kept for continuity
    with Session 4-6's evidence trail, not because it's been shown necessary).

    Idempotent: skipped if the session is already warm (dynamic_reconfigure server registered),
    so a batch of trials sharing one reused ROS session only pays this cost once, on the first
    (fresh-ros) call.
    """
    services = docker_exec("rosservice list", timeout=15).stdout
    if "/move_base/set_parameters" in services:
        eprint("[run_trial] warmup: /move_base/set_parameters already registered -- session already warm, skipping.")
        return
    eprint("[run_trial] warmup: priming move_base with a guarded DiagCmdVel nav cycle (15s)...")
    run_diag_cmdvel(15.0)
    eprint("[run_trial] warmup: setting oscillation_timeout=3.0 live via dynparam...")
    docker_exec("rosrun dynamic_reconfigure dynparam set /move_base oscillation_timeout 3.0", timeout=30)
    val = docker_exec("rosparam get /move_base/oscillation_timeout", timeout=10).stdout.strip()
    eprint("[run_trial] warmup: verified live oscillation_timeout={}".format(val))


def prepare_reused_ros_for_new_trial():
    """Inter-trial hygiene when reusing a ROS session across multiple Unity launches (Session 3):
    cancel any goal left over from the previous trial and clear both costmaps, so this trial's
    readiness gates (AutoTrialBootstrap's WaitForReadinessGates) start from a clean slate rather
    than racing stale state from whatever the last Unity instance left behind."""
    docker_exec("rostopic pub -1 /move_base/cancel actionlib_msgs/GoalID \"{}\"", timeout=10)
    docker_exec("rosservice call /move_base/clear_costmaps", timeout=10)


def read_rosparam(name):
    """Read-only `rosparam get` -- never used to set anything. Returns the trimmed stdout string,
    or None if the param doesn't exist / the container isn't reachable."""
    result = docker_exec("rosparam get {}".format(name), timeout=10)
    if result.returncode != 0:
        return None
    value = result.stdout.strip()
    return value if value else None


def read_ros_session_age_sec():
    """Elapsed wall-clock seconds since the current roscore (rosmaster) process started, via
    `ps -o etimes=` inside the container -- a live, read-only measurement, not a value this
    script tracks or persists itself. Returns None if rosmaster isn't found (e.g. container down)
    or if multiple rosmaster processes are somehow running (ambiguous, not guessed at)."""
    result = docker_exec("pgrep -f 'rosmaster --core'", timeout=10)
    pids = [p for p in result.stdout.split() if p]
    if result.returncode != 0 or len(pids) != 1:
        return None
    etimes = docker_exec("ps -o etimes= -p {}".format(pids[0]), timeout=10)
    if etimes.returncode != 0:
        return None
    try:
        return int(etimes.stdout.strip())
    except ValueError:
        return None


def augment_trial_meta(out_dir, bringup_mode, trial_position):
    """Reads meta.json (written by Unity's TrialController before it exits) and records
    host-observed ROS instrumentation into it. Every value here is read live -- this function
    never calls rosparam/dynparam *set* on anything."""
    meta_path = out_dir / "meta.json"
    if not meta_path.exists():
        eprint("[run_trial] WARNING: meta.json missing, cannot record instrumentation.")
        return

    data = json.loads(meta_path.read_text())

    live_osc = read_rosparam("/move_base/oscillation_timeout")
    try:
        live_osc = float(live_osc) if live_osc is not None else None
    except ValueError:
        pass  # leave as the raw string if rosparam returned something non-numeric

    data["liveOscillationTimeout"] = live_osc
    data["bringupRunId"] = read_rosparam("/run_id")
    data["bringupMode"] = bringup_mode
    data["rosSessionAgeSec"] = read_ros_session_age_sec()
    data["trialPosition"] = trial_position

    meta_path.write_text(json.dumps(data, indent=2))


def run_single_trial(args, out_dir, windowed=False, reused_ros=True):
    """Returns (success: bool, reason: str)."""
    out_dir.mkdir(parents=True, exist_ok=True)
    config = build_config(args, out_dir)
    config_path = out_dir / "config.json"
    config_path.write_text(json.dumps(config, indent=2))

    log_path = out_dir / "unity.log"
    hard_timeout = args.duration + 60

    if reused_ros:
        prepare_reused_ros_for_new_trial()

    goal_listener = None
    if config["hasGoalPose"]:
        goal_listener = start_goal_listener(timeout=hard_timeout)

    cmd = build_unity_cmd(log_path, windowed=windowed, extra_args=[
        "-executeMethod", "SEAN.AutoTrial.AutoTrialEditorRunner.EnterPlay",
        "-trialConfig", str(config_path),
    ])
    returncode, timed_out = guarded_unity_run(cmd, timeout=hard_timeout)

    if timed_out:
        if goal_listener is not None:
            goal_listener.kill()
        return False, "hard timeout exceeded ({}s)".format(hard_timeout)

    if goal_listener is not None:
        delivered = finish_goal_listener(goal_listener)
        if not delivered:
            return False, "goal delivery verification failed (no message captured on /move_base_simple/goal during the trial)"

    if returncode != 0:
        errors = tail_has_error(log_path)
        return False, "Unity exited with code {}. Errors in log: {}".format(returncode, errors)

    frames_csv = out_dir / "frames.csv"
    if not frames_csv.exists():
        return False, "frames.csv was not produced"

    return True, "ok"


# ---------------------------------------------------------------------------
# Phase 3: post-processing
# ---------------------------------------------------------------------------

def assemble_video(frame_dir, prefix, fps, out_path):
    pattern = str(frame_dir / (prefix + "_%05d.jpg"))
    cmd = ["ffmpeg", "-y", "-framerate", str(fps), "-i", pattern,
           "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", str(out_path)]
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        eprint("[run_trial] ffmpeg failed assembling {}: {}".format(out_path, result.stderr[-2000:]))
        return False
    return True


def find_near_spans(frames_csv_path, near_dist):
    spans = []
    with open(frames_csv_path, newline="") as f:
        reader = csv.DictReader(f)
        rows = list(reader)
    in_span = False
    start_t = None
    for row in rows:
        try:
            md = float(row["min_dist"])
        except (ValueError, KeyError):
            md = None
        t = float(row["t"])
        near = md is not None and md < near_dist
        if near and not in_span:
            in_span = True
            start_t = t
        elif not near and in_span:
            in_span = False
            spans.append((start_t, prev_t))
        prev_t = t
    if in_span:
        spans.append((start_t, prev_t))
    return spans


def cut_clip(src_video, out_path, start, end, pad=2.0):
    ss = max(0.0, start - pad)
    duration = (end - start) + 2 * pad
    cmd = ["ffmpeg", "-y", "-ss", str(ss), "-i", str(src_video), "-t", str(duration),
           "-c:v", "libx264", "-pix_fmt", "yuv420p", str(out_path)]
    result = subprocess.run(cmd, capture_output=True, text=True)
    return result.returncode == 0


def frame_sanity_check(pov_dir, tp_dir, sample_n=8):
    """Statistical black-frame check via PIL. Returns (ok: bool, detail: str)."""
    from PIL import Image
    import statistics

    def sample_stats(d):
        files = sorted(Path(d).glob("*.jpg"))
        if not files:
            return None
        step = max(1, len(files) // sample_n)
        sampled = files[::step][:sample_n]
        means, stdevs = [], []
        for f in sampled:
            img = Image.open(f).convert("L")
            pixels = list(img.getdata())
            means.append(sum(pixels) / len(pixels))
            stdevs.append(statistics.pstdev(pixels))
        return means, stdevs

    pov_stats = sample_stats(pov_dir)
    tp_stats = sample_stats(tp_dir)
    if pov_stats is None or tp_stats is None:
        return False, "no JPGs found to sample"

    detail_lines = []
    ok = True
    for name, (means, stdevs) in (("pov", pov_stats), ("tp", tp_stats)):
        avg_mean = sum(means) / len(means)
        avg_std = sum(stdevs) / len(stdevs)
        detail_lines.append("{}: mean_brightness={:.2f} mean_stdev={:.2f} (n={})".format(name, avg_mean, avg_std, len(means)))
        # Near-black or perfectly flat frames indicate a batchmode rendering failure.
        if avg_mean < 3.0 or avg_std < 1.0:
            ok = False
    return ok, "; ".join(detail_lines)


def actual_achieved_fps(frames_csv_path, configured_fps):
    """Real per-tick capture cost (two 1280x720 Camera.Render()+ReadPixels()+JPEG-encode+write
    calls) can exceed the 1/fps budget under sustained load, so the achieved rate may fall short
    of --fps. Assembling with the configured fps in that case would play the video faster than
    the trial's real elapsed time -- exactly the wall-clock desync the hard constraints rule out
    (that's the whole reason this pipeline avoids Time.captureFramerate in the first place).
    Using frames/actual_duration instead keeps the video's timeline matched to real time."""
    with open(frames_csv_path, newline="") as f:
        rows = list(csv.DictReader(f))
    if len(rows) < 2:
        return configured_fps
    n = len(rows)
    duration = float(rows[-1]["t"]) - float(rows[0]["t"])
    if duration <= 0:
        return configured_fps
    achieved = n / duration
    return achieved


def post_process(out_dir, fps, near_dist, keep_full):
    pov_dir = out_dir / "pov"
    tp_dir = out_dir / "tp"
    pov_full = out_dir / "pov_full.mp4"
    tp_full = out_dir / "tp_full.mp4"

    real_fps = actual_achieved_fps(out_dir / "frames.csv", fps)
    if abs(real_fps - fps) > 0.5:
        eprint("[run_trial] achieved capture rate {:.2f} fps differs from configured {} fps -- "
               "assembling at the achieved rate to keep real-time pacing.".format(real_fps, fps))

    ok_pov = assemble_video(pov_dir, "pov", real_fps, pov_full)
    ok_tp = assemble_video(tp_dir, "tp", real_fps, tp_full)
    if not (ok_pov and ok_tp):
        return {"ok": False, "reason": "ffmpeg assembly failed"}

    spans = find_near_spans(out_dir / "frames.csv", near_dist)
    near_clips = []
    for i, (start, end) in enumerate(spans):
        pov_clip = out_dir / "pov_near_{:02d}.mp4".format(i)
        tp_clip = out_dir / "tp_near_{:02d}.mp4".format(i)
        cut_clip(pov_full, pov_clip, start, end)
        cut_clip(tp_full, tp_clip, start, end)
        near_clips.append({"index": i, "start": start, "end": end, "pov": pov_clip.name, "tp": tp_clip.name})

    if not keep_full:
        shutil.rmtree(pov_dir, ignore_errors=True)
        shutil.rmtree(tp_dir, ignore_errors=True)

    return {"ok": True, "near_clips": near_clips, "pov_full": pov_full.name, "tp_full": tp_full.name}


def summarize(out_dir):
    frames_csv = out_dir / "frames.csv"
    n_frames = 0
    min_dist = None
    with open(frames_csv, newline="") as f:
        reader = csv.DictReader(f)
        for row in reader:
            n_frames += 1
            try:
                md = float(row["min_dist"])
                if min_dist is None or md < min_dist:
                    min_dist = md
            except (ValueError, KeyError):
                pass
    return n_frames, min_dist


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--appearance")
    p.add_argument("--personality", default="indifferent")
    p.add_argument("--duration", type=float, default=90.0)
    p.add_argument("--fps", type=int, default=15)
    p.add_argument("--near-dist", type=float, default=3.0)
    p.add_argument("--out", default=None, help="output directory (default: trial_outputs/<appearance>_<personality>_<timestamp>)")
    p.add_argument("--windowed", action="store_true", help="drop -batchmode (black-frame fallback)")
    p.add_argument("--keep-full", action="store_true")
    p.add_argument("--fresh-ros", action="store_true")
    p.add_argument("--reused-ros", dest="reused_ros", action="store_true", default=True,
                   help="run inter-trial ROS hygiene (cancel+clear costmaps) before launching -- default on")
    p.add_argument("--no-reused-ros-hygiene", dest="reused_ros", action="store_false")
    p.add_argument("--spawn", type=float, nargs=4, metavar=("X", "Y", "Z", "YAW_DEG"))
    p.add_argument("--goal", type=float, nargs=4, metavar=("X", "Y", "Z", "YAW_DEG"), default=None)
    p.add_argument("--patrol", type=float, nargs=3, action="append", metavar=("X", "Y", "Z"),
                   help="repeatable; first two given are used (ping-pong)")
    p.add_argument("--chase-distance", type=float, default=3.0)
    p.add_argument("--chase-height", type=float, default=2.0)
    p.add_argument("--chase-look-height", type=float, default=1.0)
    p.add_argument("--jpg-quality", type=int, default=85)
    p.add_argument("--trial-position", type=int, default=1,
                   help="1-based position of this trial within its sequential run on one shared "
                        "bringup -- recorded into meta.json, never inferred (default: 1)")
    p.add_argument("--compile-check", action="store_true",
                   help="diagnostic mode: guarded -batchmode -quit launch to force recompilation, then exit")
    p.add_argument("--diag-cmdvel", type=float, metavar="SECONDS", default=None,
                   help="diagnostic mode: guarded launch of DiagCmdVel for SECONDS, then exit")
    p.add_argument("--warmup", dest="warmup", action="store_true", default=True,
                   help="prime a fresh ROS session with a real nav cycle before the batch "
                        "(Session 8 operational recipe) -- default on")
    p.add_argument("--no-warmup", dest="warmup", action="store_false")
    args = p.parse_args()

    if args.compile_check:
        log_path = Path("/tmp/run_trial_compile_check.log")
        cmd = build_unity_cmd(log_path, windowed=False, quit_after=True)
        returncode, timed_out = guarded_unity_run(cmd, timeout=180)
        print("compile-check: returncode={} timed_out={} log={}".format(returncode, timed_out, log_path))
        errors = tail_has_error(log_path)
        if errors:
            print("COMPILE ERRORS:\n" + "\n".join(errors))
            sys.exit(1)
        sys.exit(0 if not timed_out else 1)

    if args.diag_cmdvel is not None:
        check_editor_lock()
        returncode, timed_out, text = run_diag_cmdvel(args.diag_cmdvel, windowed=args.windowed)
        print("diag-cmdvel: returncode={} timed_out={}".format(returncode, timed_out))
        for line in text.splitlines():
            if "[Diag]" in line:
                print(line)
        sys.exit(0)

    if not args.appearance or not args.spawn:
        p.error("--appearance and --spawn are required unless --compile-check or --diag-cmdvel is given")

    validate_appearance_friendly(args.appearance)
    if args.personality.lower() not in PERSONALITIES:
        raise SystemExit("--personality '{}' invalid. Valid: {}".format(args.personality, PERSONALITIES))

    check_editor_lock()
    ensure_ros_healthy(args.fresh_ros)
    if args.warmup:
        warmup_ros_session()

    if args.out is None:
        ts = time.strftime("%Y%m%d_%H%M%S")
        out_dir = DEFAULT_OUT_ROOT / "{}_{}_{}".format(args.appearance, args.personality.lower(), ts)
    else:
        out_dir = Path(args.out)

    bringup_mode = "fresh" if args.fresh_ros else "reused"

    windowed = args.windowed
    success, reason = run_single_trial(args, out_dir, windowed=windowed, reused_ros=args.reused_ros and not args.fresh_ros)
    if not success:
        eprint("[run_trial] trial FAILED: {}".format(reason))
        sys.exit(1)

    sanity_ok, sanity_detail = frame_sanity_check(out_dir / "pov", out_dir / "tp")
    eprint("[run_trial] frame sanity: {} ({})".format("OK" if sanity_ok else "FAILED", sanity_detail))
    if not sanity_ok and not windowed:
        eprint("[run_trial] batchmode frames look black/flat -- retrying in --windowed mode.")
        shutil.rmtree(out_dir, ignore_errors=True)
        success, reason = run_single_trial(args, out_dir, windowed=True, reused_ros=args.reused_ros and not args.fresh_ros)
        if not success:
            eprint("[run_trial] windowed retry FAILED: {}".format(reason))
            sys.exit(1)
        sanity_ok, sanity_detail = frame_sanity_check(out_dir / "pov", out_dir / "tp")
        eprint("[run_trial] windowed frame sanity: {} ({})".format("OK" if sanity_ok else "FAILED", sanity_detail))
        windowed = True

    augment_trial_meta(out_dir, bringup_mode, args.trial_position)

    result = post_process(out_dir, args.fps, args.near_dist, args.keep_full)
    if not result["ok"]:
        eprint("[run_trial] post-processing FAILED: {}".format(result["reason"]))
        sys.exit(1)

    n_frames, min_dist = summarize(out_dir)
    print("=== trial complete ===")
    print("out_dir: {}".format(out_dir))
    print("mode: {}".format("windowed" if windowed else "batchmode"))
    print("frames: {}".format(n_frames))
    print("near-spans: {}".format(len(result["near_clips"])))
    print("min_dist reached: {}".format(min_dist))
    print("pov_full: {}".format(out_dir / result["pov_full"]))
    print("tp_full: {}".format(out_dir / result["tp_full"]))
    for c in result["near_clips"]:
        print("near clip {}: {} / {}".format(c["index"], out_dir / c["pov"], out_dir / c["tp"]))


if __name__ == "__main__":
    main()
