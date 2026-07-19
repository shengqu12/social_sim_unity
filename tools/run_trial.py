#!/usr/bin/env python3
"""
CLI-driven trial runner for the SEAN 2.0 AutoTrial pipeline.

    python tools/run_trial.py --appearance wheelchair_user --personality indifferent --duration 90

produces, with zero manual Unity interaction: robot-POV video (pov_full.mp4 + overlay), the
primary deliverable as of Round 4's output format v3 (no chase/third-person camera, removed
Session 10 D5); near-pedestrian clips (pov_near_NN[_ov].mp4) cut from it, retained as VLM-
prefilter material; frames.csv of per-frame robot data; and meta.json.

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
import math
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import trial_lib
import overlay

PROJECT_DIR = Path(__file__).resolve().parent.parent  # .../social_sim_unity
DOCKER_CONTAINER = "ros"
DEFAULT_OUT_ROOT = Path.home() / "Desktop" / "research" / "social_navigation" / "trial_outputs"
OUTPUT_ROOT_SENTINEL_NAME = ".output_root_on_t7"
OUTPUT_ROOT_MIN_FREE_GB = 5.0

ZONE_B_APPEARANCES = [
    "cyclist", "dog_walker", "female_child", "male_child",
    "phone_user", "scooter_user", "wheelchair_user", "white_cane_user",
]
PERSONALITIES = ["scared", "curious", "surprised", "indifferent", "assertive"]

# Session 10 (D4): canonical head-on-encounter geometry, used whenever --spawn/--goal/--ped-goal
# are omitted. ROBOT_START is not itself a CLI-settable pose -- it's the scene's own teleport
# target (Tasks.Base.UpdatePositions(), confirmed across Sessions 1-9 as deterministic and
# scene-authored, robot_nav_A), recorded here so both the pedestrian geometry below and the
# printed resolved-pose summary have something to compute against. ROBOT_GOAL is robot_nav_B, the
# far end of the same confirmed-clear corridor.
ROBOT_START = (-1.058, 0.0007, -109.424)
DEFAULT_ROBOT_GOAL = (-44.659, 0.0007, -108.947, 0.0)

# Round 4 (Session 12): Session 10's PED_SPAWN/PED_GOAL were a single hardcoded pose pair
# (distance from ROBOT_START ~= 13.94m, not independently adjustable) -- generalized here into
# resolve_head_on_geometry(), parametrized by --ped-distance. PED_OVERSHOOT_M reproduces the same
# "destination beyond the robot's start" margin the old DEFAULT_PED_GOAL=(2.0, ..., -109.424) used
# (2.0 - (-1.058) = 3.058m past ROBOT_START.x, ~0 z-offset since the corridor bearing is almost
# pure -x) -- kept as a round 3.0m rather than the exact legacy 3.058m; the guarantee that matters
# (a genuine pass-through, not a meet-and-stop) doesn't depend on the extra 0.058m.
PED_OVERSHOOT_M = 3.0
DEFAULT_PED_DISTANCE = 8.0


def resolve_head_on_geometry(ped_distance, goal_xyz, robot_start=ROBOT_START, overshoot=PED_OVERSHOOT_M):
    """Round 4 (Step 2): places the pedestrian exactly `ped_distance` meters from robot_start,
    along the robot_start->goal_xyz bearing (i.e. on the robot's own path), facing back toward
    the robot (yaw = bearing reversed -- see ROBOT_START's comment above for the yaw-90==+x
    convention this relies on, empirically confirmed via frames.csv robot_yaw_deg in prior
    sessions). Destination continues PAST robot_start by `overshoot` meters in the pedestrian's
    own direction of travel (the reverse of the bearing), guaranteeing a genuine head-on PASS
    rather than the two agents converging and stopping at the same point.

    Returns ((ped_x, ped_y, ped_z, ped_yaw_deg), (dest_x, dest_y, dest_z)).
    Raises ValueError if robot_start and goal_xyz coincide (no bearing to compute)."""
    sx, sy, sz = robot_start
    gx, gy, gz = goal_xyz[0], goal_xyz[1], goal_xyz[2]
    dx, dz = gx - sx, gz - sz
    norm = math.hypot(dx, dz)
    if norm < 1e-6:
        raise ValueError("robot start {} and goal {} coincide on the ground plane -- cannot "
                          "compute a bearing for --ped-distance placement".format(robot_start, goal_xyz))
    ux, uz = dx / norm, dz / norm

    ped_x = sx + ux * ped_distance
    ped_z = sz + uz * ped_distance
    # Facing the oncoming robot == facing back along -bearing. Unity yaw=0 faces +z, yaw
    # increases toward +x (yaw 90 == facing +x, confirmed by ROBOT_START's own comment/prior
    # sessions' census data) -- i.e. yaw = atan2(dir_x, dir_z) in degrees.
    yaw = math.degrees(math.atan2(-ux, -uz)) % 360.0

    dest_x = sx - ux * overshoot
    dest_z = sz - uz * overshoot

    return (ped_x, sy, ped_z, yaw), (dest_x, sy, dest_z)
# Zone A is validated by convention (snake_case -> Rocketbox PascalCase), not enumerated here --
# Unity's Resources.Load is authoritative. This regex just catches obvious typos early.
ZONE_A_PATTERN = re.compile(r"^[a-z]+(_[a-z0-9]+)*$")


def require_output_root_healthy(root=None, min_free_gb=OUTPUT_ROOT_MIN_FREE_GB):
    """Round 3 (post-relocation guard): trial_outputs now resolves through a symlink onto an
    external drive (T7). If that drive isn't mounted, the symlink either dangles (obvious failure)
    or -- the actually dangerous case on some setups -- the path silently falls back to being
    created fresh on the internal disk, quietly refilling it exactly the way this whole relocation
    was meant to prevent. Guard against both: resolve the REAL path (following the symlink) and
    REQUIRE a sentinel file that only exists on the intended drive; refuse loudly rather than
    writing anywhere else. Also requires >= min_free_gb free on the resolved path (not `/`) --
    space on the root filesystem is irrelevant once output lives elsewhere."""
    root = Path(root) if root is not None else DEFAULT_OUT_ROOT
    resolved = root.resolve()
    sentinel = resolved / OUTPUT_ROOT_SENTINEL_NAME
    if not resolved.is_dir() or not sentinel.exists():
        raise SystemExit(
            "[run_trial] REFUSING TO START: output root sentinel missing ({}). Resolved path: {}. "
            "This means either trial_outputs isn't the symlink onto the T7 drive, or T7 isn't "
            "mounted -- writing here would silently land on the internal disk. Mount T7 (or "
            "restore the trial_outputs -> /media/sheng/T7/Social_Navigation/trial_outputs symlink) "
            "before running trials.".format(sentinel, resolved))
    st = os.statvfs(str(resolved))
    free_gb = (st.f_bavail * st.f_frsize) / (1024 ** 3)
    if free_gb < min_free_gb:
        raise SystemExit(
            "[run_trial] REFUSING TO START: only {:.2f}GB free at {} (need >= {}GB).".format(
                free_gb, resolved, min_free_gb))
    return resolved


def mirror_notes(out_root=None):
    """Round 3: best-effort, non-fatal call to tools/mirror_notes.sh after every trial -- copies
    the paper trail (REPORT.md, HOWARD_HANDOFF.md, *.diff, index*.html; never video/CSV payloads)
    onto the internal disk's ~/trial_notes_mirror/, so it survives the T7 drive being unplugged or
    the git-less trial_outputs tree being otherwise unreachable. Never raises -- a mirroring
    failure must not fail a trial that otherwise succeeded."""
    out_root = Path(out_root) if out_root is not None else DEFAULT_OUT_ROOT
    script = PROJECT_DIR / "tools" / "mirror_notes.sh"
    try:
        result = subprocess.run(["bash", str(script), str(out_root)],
                                 capture_output=True, text=True, timeout=30)
        if result.returncode != 0:
            eprint("[run_trial] mirror_notes.sh exited {}: {}".format(result.returncode, result.stderr[-500:]))
    except Exception as e:
        eprint("[run_trial] mirror_notes.sh failed (non-fatal): {}".format(e))


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
        "hasPedGoalPose": args.ped_goal is not None,
        "pedGoalPose": pose(args.ped_goal[0], args.ped_goal[1], args.ped_goal[2], 0.0) if args.ped_goal is not None else pose(0, 0, 0, 0),
        "triggerDistanceMeters": args.ped_distance,
        "camera": {
            "povOffsetX": 0.0, "povOffsetY": 0.0, "povOffsetZ": 0.0,
            "yawSmoothTau": args.yaw_smooth_tau,
            "fixedPitchDeg": args.fixed_pitch_deg,
            "rigidMount": args.rigid_mount,
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

    Idempotent, keyed on a positive probe (Session 6's own discovery: /move_base/set_parameters
    registers iff a real nav cycle has been processed) rather than on session-reuse -- a session
    that was merely reused, not actually primed by this tool, must not slip through. Session 9
    hardening: also cross-checks the live registry hit against `/run_id` (roslaunch's own fresh
    per-bringup UUID) via a local marker of the last run_id THIS tool actually primed, since a
    registry query alone can't distinguish "this instance was primed" from "a same-named service
    from an earlier, now-dead instance is still (spuriously) listed" -- rosmaster does not always
    prune registrations synchronously when a node is killed rather than cleanly shut down. Tested
    live this session across a teardown+fresh-relaunch boundary and did NOT reproduce a stale
    registration (the registry correctly showed unregistered on the new instance) -- but priming
    is cheap and idempotent, so this check errs toward re-priming whenever run_id is unknown or
    mismatched rather than trusting the registry query alone.
    """
    run_id = read_rosparam("/run_id")
    marker = Path("/tmp/run_trial_warmed_run_id.txt")
    warmed_run_id = marker.read_text().strip() if marker.exists() else None
    if run_id and run_id == warmed_run_id:
        eprint("[run_trial] warmup: run_id {} already primed by this tool, skipping.".format(run_id))
        return

    services = docker_exec("rosservice list", timeout=15).stdout
    if "/move_base/set_parameters" in services:
        if run_id:
            eprint("[run_trial] warmup: /move_base/set_parameters registered but run_id {} isn't "
                   "one this tool primed -- treating as unverified (possible stale registration "
                   "from a prior instance) and priming anyway.".format(run_id))
        else:
            eprint("[run_trial] warmup: /move_base/set_parameters registered but /run_id unreadable "
                   "-- priming anyway to be safe.")

    eprint("[run_trial] warmup: priming move_base (run_id={}) with a guarded DiagCmdVel nav cycle (15s)...".format(run_id))
    run_diag_cmdvel(15.0)
    eprint("[run_trial] warmup: setting oscillation_timeout=3.0 live via dynparam...")
    docker_exec("rosrun dynamic_reconfigure dynparam set /move_base oscillation_timeout 3.0", timeout=30)
    val = docker_exec("rosparam get /move_base/oscillation_timeout", timeout=10).stdout.strip()
    eprint("[run_trial] warmup: verified live oscillation_timeout={}".format(val))
    if run_id:
        marker.write_text(run_id)


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
    # Round 3 bug fix: an --out pointed at a directory from a PREVIOUS trial (e.g. re-running the
    # acceptance battery in place) left stale pov_near_*_ov.mp4/contact_sheet_*.png/etc. on disk.
    # overlay.py's own "already overlaid" short-circuit (keyed only on pov_near_*_ov.mp4 existing,
    # not on it actually matching this run's clip) then silently kept the OLD overlay video paired
    # with the NEW pov_near_00.mp4 -- caught during this round's own acceptance battery (stale
    # 8.5s pre-fix overlay sitting next to a fresh 15.0s post-fix clip). A trial's out_dir is wholly
    # owned by that one trial's output; wipe it before writing anything new.
    if out_dir.exists():
        shutil.rmtree(out_dir)
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


def frame_sanity_check(pov_dir, sample_n=8):
    """Statistical black-frame check via PIL. Returns (ok: bool, detail: str). Session 10 (D5):
    POV only -- the chase/third-person camera and its `tp` frame dir no longer exist."""
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
    if pov_stats is None:
        return False, "no JPGs found to sample"

    means, stdevs = pov_stats
    avg_mean = sum(means) / len(means)
    avg_std = sum(stdevs) / len(stdevs)
    detail = "pov: mean_brightness={:.2f} mean_stdev={:.2f} (n={})".format(avg_mean, avg_std, len(means))
    # Near-black or perfectly flat frames indicate a batchmode rendering failure.
    ok = avg_mean >= 3.0 and avg_std >= 1.0
    return ok, detail


def green_pixel_fraction(jpg_path, g_dominance=1.5, g_min=120):
    """Session 10 (D1 verification): fraction of pixels where G > g_dominance*max(R,B) and
    G > g_min -- the pixel-level signature of the plan-line green this session disables. Used to
    compare a pre-fix frame (still on disk from an existing trial) against a post-fix frame."""
    from PIL import Image
    img = Image.open(jpg_path).convert("RGB")
    pixels = list(img.getdata())
    hits = 0
    for r, g, b in pixels:
        if g > g_dominance * max(r, b) and g > g_min:
            hits += 1
    return hits / len(pixels)


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


def post_process(out_dir, fps, near_dist, keep_full, near_clip_min_sec=trial_lib.DEFAULT_NEAR_CLIP_MIN_SEC):
    """POV only (Session 10, D5 -- no chase/third-person camera). Builds pov_full.mp4, needed both
    as the Round 4 (Step 4) primary deliverable AND for overlay.py to burn+re-cut its own *_ov
    near clips from (overlay always re-derives spans from frames.csv rather than trusting these
    clip boundaries). Round 4: pov_full.mp4/pov_full_ov.mp4 are kept permanently now (output
    format v3) -- the near clips (pov_near_NN[_ov].mp4) are additional, VLM-prefilter material,
    not a replacement for the full video."""
    pov_dir = out_dir / "pov"
    pov_full = out_dir / "pov_full.mp4"

    real_fps = actual_achieved_fps(out_dir / "frames.csv", fps)
    if abs(real_fps - fps) > 0.5:
        eprint("[run_trial] achieved capture rate {:.2f} fps differs from configured {} fps -- "
               "assembling at the achieved rate to keep real-time pacing.".format(real_fps, fps))

    # Round 3: re-check the output root right before ffmpeg assembly, not just at trial start --
    # a long trial (or several in a row on --reused-ros) can exhaust space between the two.
    require_output_root_healthy()

    if not assemble_video(pov_dir, "pov", real_fps, pov_full):
        return {"ok": False, "reason": "ffmpeg assembly failed"}

    spans = trial_lib.find_near_spans(out_dir / "frames.csv", near_dist, min_duration_sec=near_clip_min_sec)
    near_clips = []
    for i, (start, end) in enumerate(spans):
        pov_clip = out_dir / "pov_near_{:02d}.mp4".format(i)
        trial_lib.cut_clip(pov_full, pov_clip, start, end)
        near_clips.append({"index": i, "start": start, "end": end, "pov": pov_clip.name})

    if not keep_full:
        shutil.rmtree(pov_dir, ignore_errors=True)

    return {"ok": True, "near_clips": near_clips, "pov_full": pov_full.name}


def run_content_gate(out_dir, near_clips):
    """THE PERMANENT GATE (Round 3), wired into acceptance forever -- not a one-off check. For
    every near clip: (a) samples >=8 frames and requires luminance std AND edge density above
    scene thresholds (trial_lib.check_clip_content -- any uniform gray/black sample fails the
    clip), and (b) writes an 8-frame contact-sheet PNG into out_dir so a human reviewer can QA the
    whole clip's scene content in one glance instead of scrubbing every video (surfaced in
    index.html by overlay.py's generate_index_html()). Mutates each near_clips entry in place with
    its own gate result + contact sheet filename. Returns (all_ok: bool, detail: str)."""
    all_ok = True
    details = []
    for clip in near_clips:
        clip_path = out_dir / clip["pov"]
        ok, detail, samples = trial_lib.check_clip_content(clip_path)
        sheet_name = "contact_sheet_{:02d}.png".format(clip["index"])
        sheet_ok = trial_lib.build_contact_sheet(clip_path, out_dir / sheet_name)
        clip["contentGateOk"] = ok
        clip["contentGateDetail"] = detail
        clip["contentSamples"] = samples
        clip["contactSheet"] = sheet_name if sheet_ok else None
        all_ok = all_ok and ok
        details.append("{}: {}".format(clip["pov"], detail))
    return all_ok, "; ".join(details)


def augment_trial_meta_with_gate(out_dir, gate_ok, near_clips, aspect_ok=None, aspect_detail=None,
                                  approach_ok=None, approach_detail=None,
                                  trigger_ok=None, trigger_detail=None, full_contact_sheet=None):
    """Records every permanent gate's verdict (Round 3's content gate, Round 4's aspect + approach-
    geometry gates) and every near clip's final (post-growth/merge) window + contact sheet into
    meta.json, so a trial's pass/fail is inspectable without re-running anything."""
    meta_path = out_dir / "meta.json"
    if not meta_path.exists():
        return
    data = json.loads(meta_path.read_text())
    data["contentGateOk"] = gate_ok
    if aspect_ok is not None:
        data["aspectGateOk"] = aspect_ok
        data["aspectGateDetail"] = aspect_detail
    if approach_ok is not None:
        data["approachGateOk"] = approach_ok
        data["approachGateDetail"] = approach_detail
    if trigger_ok is not None:
        data["triggerSpeedGateOk"] = trigger_ok
        data["triggerSpeedGateDetail"] = trigger_detail
    if full_contact_sheet is not None:
        data["fullContactSheet"] = full_contact_sheet
    data["nearClips"] = [
        {
            "index": c["index"],
            "start": c["start"],
            "end": c["end"],
            "durationSec": c["end"] - c["start"],
            "pov": c["pov"],
            "contactSheet": c.get("contactSheet"),
            "contentGateOk": c.get("contentGateOk"),
            "contentGateDetail": c.get("contentGateDetail"),
        }
        for c in near_clips
    ]
    meta_path.write_text(json.dumps(data, indent=2))


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
    p.add_argument("--near-clip-min-sec", type=float, default=trial_lib.DEFAULT_NEAR_CLIP_MIN_SEC,
                   help="Round 3: every near clip is grown symmetrically around its own "
                        "minimum-distance moment (bounded by trial length) until it reaches at "
                        "least this many seconds; overlapping spans after growth are merged.")
    p.add_argument("--out", default=None, help="output directory (default: trial_outputs/<appearance>_<personality>_<timestamp>)")
    p.add_argument("--windowed", action="store_true", help="drop -batchmode (black-frame fallback)")
    p.add_argument("--keep-full", action="store_true",
                   help="keep the raw per-frame JPG directory (pov/) and config.json after "
                        "assembly; NOT related to pov_full.mp4/pov_full_ov.mp4, which are always "
                        "kept as of Round 4's output format v3 regardless of this flag")
    p.add_argument("--fresh-ros", action="store_true")
    p.add_argument("--reused-ros", dest="reused_ros", action="store_true", default=True,
                   help="run inter-trial ROS hygiene (cancel+clear costmaps) before launching -- default on")
    p.add_argument("--no-reused-ros-hygiene", dest="reused_ros", action="store_false")
    p.add_argument("--ped-distance", type=float, default=DEFAULT_PED_DISTANCE,
                   help="Distance in meters from the robot's start position, measured along the "
                        "robot start->goal bearing, that defines the trial's dist0 target. Since "
                        "Session 14 (SLATE v2) this is ALSO the live trigger threshold: "
                        "TrialController.PollForTrigger releases the pedestrian and starts capture "
                        "the instant robot<->pedestrian ground-plane distance first drops to this "
                        "value or below (config.triggerDistanceMeters). Only takes effect when "
                        "--spawn is not explicitly given. Default 8.0.")
    p.add_argument("--slate-margin", type=float, default=4.0,
                   help="Session 14 (SLATE v2): extra distance beyond --ped-distance at which the "
                        "pedestrian actually spawns, frozen (default 4.0 -> ~12m from robot start "
                        "at the default --ped-distance=8.0). The robot's goal is published early "
                        "(pre-roll) so it reaches a normal cruise while still further than "
                        "--ped-distance away; only takes effect when --spawn is not explicitly "
                        "given.")
    p.add_argument("--spawn", type=float, nargs=4, metavar=("X", "Y", "Z", "YAW_DEG"), default=None,
                   help="pedestrian spawn pose, overrides --ped-distance entirely; default (Round "
                        "4): computed from --ped-distance via resolve_head_on_geometry()")
    p.add_argument("--goal", type=float, nargs=4, metavar=("X", "Y", "Z", "YAW_DEG"), default=None,
                   help="robot goal override; default (Session 10, D4): robot_nav_B, the far end "
                        "of the census corridor -- pass an empty override with e.g. "
                        "--goal 0 0 0 0 only if you explicitly want hasGoalPose still computed "
                        "from that value (there is no way to request hasGoalPose=false other than "
                        "not asking for a default, i.e. this flag now always resolves to a value)")
    p.add_argument("--no-goal", action="store_true",
                   help="explicitly request hasGoalPose=false (robot keeps whatever the scene's "
                        "own active task does by default) instead of the Session 10 default goal")
    p.add_argument("--ped-goal", type=float, nargs=3, metavar=("X", "Y", "Z"), default=None,
                   help="pedestrian destination (Session 10, D4); default: the robot's own start "
                        "pose, slightly past it (a head-on pass-through). Drives INavigable."
                        "InitDest() on both Zone A and Zone B. Pass --no-ped-goal for the pre-"
                        "Session-10 behavior (dest == spawn, net-zero displacement).")
    p.add_argument("--no-ped-goal", action="store_true",
                   help="explicitly request hasPedGoalPose=false (pedestrian dest == spawn, the "
                        "pre-Session-10 default) instead of the Session 10 default head-on goal")
    p.add_argument("--patrol", type=float, nargs=3, action="append", metavar=("X", "Y", "Z"),
                   help="repeatable; first two given are used (ping-pong)")
    p.add_argument("--yaw-smooth-tau", type=float, default=0.5,
                   help="Round 3 (D2 fix): POV camera yaw low-pass time constant, seconds -- the "
                        "only smoothed axis. Position is always rigid to the mount; pitch/roll are "
                        "constants (see --fixed-pitch-deg). Default (0.5) is empirically re-tuned "
                        "this round -- see REPORT.md Round 3 Step 2. 0 = no smoothing (--rigid-mount).")
    p.add_argument("--fixed-pitch-deg", type=float, default=-5.0,
                   help="Round 3 (D2 fix): constant camera downtilt in degrees (positive = up, "
                        "negative = down; default -5 = slight downtilt). Never derived from any "
                        "transform -- this replaces Session 10's buggy mount-rotation decomposition.")
    p.add_argument("--rigid-mount", action="store_true",
                   help="Round 3 (D2): force yaw smoothing tau to 0 (raw chassis yaw every frame, "
                        "no filtering) for direct before/after comparison. Position was already "
                        "always rigid; pitch/roll are always constants regardless of this flag.")
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
    p.add_argument("--overlay", dest="overlay", action="store_true", default=True,
                   help="burn a per-frame telemetry overlay onto this trial's videos "
                        "(tools/overlay.py, Session 9) -- default on")
    p.add_argument("--no-overlay", dest="overlay", action="store_false")
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

    if not args.appearance:
        p.error("--appearance is required unless --compile-check or --diag-cmdvel is given")

    validate_appearance_friendly(args.appearance)
    if args.personality.lower() not in PERSONALITIES:
        raise SystemExit("--personality '{}' invalid. Valid: {}".format(args.personality, PERSONALITIES))

    require_output_root_healthy()

    # Session 10 (D4 QoL patch), regeared in Round 4 (Step 2) onto --ped-distance: --spawn/--goal/
    # --ped-goal are all optional, defaulting to the canonical head-on-encounter geometry --
    # resolved here (not in argparse defaults) so --no-goal/--no-ped-goal can still suppress them,
    # and so the resolved poses can be printed before launch regardless of which path (explicit
    # flag vs. default) produced them. Goal is resolved first since the pedestrian geometry needs
    # a bearing to compute against even when --no-goal is given (the robot still travels roughly
    # that way by the scene's own default task, so the bearing is still the right one to spawn on).
    if args.no_goal:
        args.goal = None
    elif args.goal is None:
        args.goal = list(DEFAULT_ROBOT_GOAL)
    bearing_goal = tuple(args.goal) if args.goal is not None else DEFAULT_ROBOT_GOAL

    if args.spawn is None:
        # Session 14 (SLATE v2): spawn further out than the trigger/dist0 target -- the pedestrian
        # is frozen there (AutoTrialBootstrap.SpawnPedestrian) until TrialController's live
        # distance trigger (config.triggerDistanceMeters == args.ped_distance) fires.
        spawn_distance = args.ped_distance + args.slate_margin
        geom_spawn, geom_ped_goal = resolve_head_on_geometry(spawn_distance, bearing_goal)
        args.spawn = list(geom_spawn)
    else:
        geom_ped_goal = None  # explicit --spawn overrides geometry; no matching auto dest to offer

    if args.no_ped_goal:
        args.ped_goal = None
    elif args.ped_goal is None:
        if geom_ped_goal is None:
            raise SystemExit("--ped-goal is required when --spawn is given explicitly without "
                              "--ped-goal (no geometry-derived destination to fall back to); pass "
                              "--no-ped-goal for dest==spawn instead.")
        args.ped_goal = list(geom_ped_goal)

    print("=== resolved poses ===")
    print("robot start (scene teleport target, not CLI-settable): {}".format(ROBOT_START))
    print("robot goal: {}".format(args.goal if args.goal is not None else "(none -- hasGoalPose=false, robot follows the scene's own active task)"))
    print("ped-distance (dist0 target / SLATE v2 trigger threshold): {}".format(args.ped_distance))
    print("slate-margin (extra frozen-spawn distance): {}".format(args.slate_margin))
    print("pedestrian spawn: {}".format(args.spawn))
    print("pedestrian goal: {}".format(args.ped_goal if args.ped_goal is not None else "(none -- hasPedGoalPose=false, dest==spawn)"))

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

    sanity_ok, sanity_detail = frame_sanity_check(out_dir / "pov")
    eprint("[run_trial] frame sanity: {} ({})".format("OK" if sanity_ok else "FAILED", sanity_detail))
    if not sanity_ok and not windowed:
        eprint("[run_trial] batchmode frames look black/flat -- retrying in --windowed mode.")
        shutil.rmtree(out_dir, ignore_errors=True)
        success, reason = run_single_trial(args, out_dir, windowed=True, reused_ros=args.reused_ros and not args.fresh_ros)
        if not success:
            eprint("[run_trial] windowed retry FAILED: {}".format(reason))
            sys.exit(1)
        sanity_ok, sanity_detail = frame_sanity_check(out_dir / "pov")
        eprint("[run_trial] windowed frame sanity: {} ({})".format("OK" if sanity_ok else "FAILED", sanity_detail))
        windowed = True

    augment_trial_meta(out_dir, bringup_mode, args.trial_position)

    result = post_process(out_dir, args.fps, args.near_dist, args.keep_full, near_clip_min_sec=args.near_clip_min_sec)
    if not result["ok"]:
        eprint("[run_trial] post-processing FAILED: {}".format(result["reason"]))
        sys.exit(1)

    # THE PERMANENT GATE (Round 3): runs on every trial forever, not just this acceptance battery.
    gate_ok, gate_detail = run_content_gate(out_dir, result["near_clips"])
    eprint("[run_trial] content gate: {} ({})".format("OK" if gate_ok else "FAILED", gate_detail))

    # Round 4 (THE PERMANENT ASPECT GATE, Step 1): meta.json's povCameraAspect/targetAspect were
    # written by TrialController from the live camera at capture time -- verify they agree here,
    # on every trial forever, not just trust the in-engine assert (AutoTrialBootstrap.Fail()
    # already refuses to even start a trial whose aspect is off by >0.01, so this is a second,
    # independent check from the artifact actually produced).
    aspect_ok, aspect_detail = trial_lib.check_aspect_gate(out_dir / "meta.json")
    eprint("[run_trial] aspect gate: {} ({})".format("OK" if aspect_ok else "FAILED", aspect_detail))

    # Round 4 (THE PERMANENT APPROACH-GEOMETRY GATE, Step 2): confirms --ped-distance actually
    # landed the pedestrian at the requested range and that the approach closes monotonically
    # (noise-tolerant) rather than erratically.
    dist0_ok, monotonic_ok, approach_detail = trial_lib.check_approach_geometry(
        out_dir / "frames.csv", args.ped_distance)
    approach_ok = dist0_ok and monotonic_ok
    eprint("[run_trial] approach geometry gate: {} ({})".format(
        "OK" if approach_ok else "FAILED", approach_detail))

    # Session 14 (SLATE v2, THE PERMANENT TRIGGER-SPEED GATE): confirms the video's t=0 shows the
    # robot cruising (>=0.3 m/s), not standing -- the failure mode Session 13's fixed-instant
    # slate produced (goal published from a standing start, see REPORT.md Session 13 Step 3).
    trigger_ok, trigger_detail = trial_lib.check_trigger_speed(out_dir / "meta.json")
    eprint("[run_trial] trigger-speed gate: {} ({})".format(
        "OK" if trigger_ok else "FAILED", trigger_detail))

    # Round 4 (Step 4, output format v3): full POV video is the primary deliverable now -- build
    # its own contact sheet (8 frames spanning the WHOLE trial, not just a near clip) so a human
    # reviewer can eyeball aspect/horizon/pedestrian-entry at a glance without scrubbing.
    full_sheet_name = "contact_sheet_full.png"
    full_sheet_ok = trial_lib.build_contact_sheet(out_dir / "pov_full.mp4", out_dir / full_sheet_name)
    eprint("[run_trial] full-video contact sheet: {}".format("OK" if full_sheet_ok else "FAILED"))

    augment_trial_meta_with_gate(out_dir, gate_ok, result["near_clips"],
                                  aspect_ok=aspect_ok, aspect_detail=aspect_detail,
                                  approach_ok=approach_ok, approach_detail=approach_detail,
                                  trigger_ok=trigger_ok, trigger_detail=trigger_detail,
                                  full_contact_sheet=full_sheet_name if full_sheet_ok else None)

    if args.overlay:
        ov_ok, ov_detail = overlay.process_trial_dir(out_dir, near_dist=args.near_dist,
                                                       near_clip_min_sec=args.near_clip_min_sec)
        eprint("[run_trial] overlay: {} ({})".format("OK" if ov_ok else "FAILED", ov_detail))

    # Round 4 (Step 4, output format v3): pov_full.mp4/pov_full_ov.mp4 are now the PRIMARY
    # deliverable, not an internal scratch file -- the old cleanup_full_video() deletion is gone.
    # The disk rationale for deleting them no longer applies (trial_outputs lives on the 511G T7
    # external drive; require_output_root_healthy()'s 5GB guard is still active and still checked
    # at both the top of main() and inside post_process()). Near clips (pov_near_NN[_ov].mp4) are
    # retained too, now framed as VLM-prefilter material rather than the primary output.

    # Session 10 (D5 output-format spec: "exactly the near pairs + frames.csv + meta.json +
    # unity.log"): config.json is Unity's *input* for this trial, but everything in it is also
    # embedded verbatim in meta.json's own "config" field (TrialController.WriteMetaJson) -- safe
    # to drop as a final-output artifact once the run that needed it as an input is done.
    if not args.keep_full:
        (out_dir / "config.json").unlink(missing_ok=True)

    # Round 3: mirror the paper trail (REPORT.md/HOWARD_HANDOFF.md/diffs/index pages) to the
    # internal disk after every trial, regardless of gate outcome below -- best-effort, non-fatal.
    mirror_notes()

    n_frames, min_dist = summarize(out_dir)
    print("=== trial complete ===")
    print("out_dir: {}".format(out_dir))
    print("mode: {}".format("windowed" if windowed else "batchmode"))
    print("frames: {}".format(n_frames))
    print("near-spans: {}".format(len(result["near_clips"])))
    print("min_dist reached: {}".format(min_dist))
    print("content gate: {}".format("PASS" if gate_ok else "FAIL"))
    print("aspect gate: {} ({})".format("PASS" if aspect_ok else "FAIL", aspect_detail))
    print("approach geometry gate: {} ({})".format("PASS" if approach_ok else "FAIL", approach_detail))
    print("trigger-speed gate: {} ({})".format("PASS" if trigger_ok else "FAIL", trigger_detail))
    print("pov_full: {}".format(out_dir / "pov_full.mp4"))
    if (out_dir / "pov_full_ov.mp4").exists():
        print("pov_full (overlay): {}".format(out_dir / "pov_full_ov.mp4"))
    if full_sheet_ok:
        print("full contact sheet: {}".format(out_dir / full_sheet_name))
    for c in result["near_clips"]:
        ov_name = "pov_near_{:02d}_ov.mp4".format(c["index"])
        print("near clip {} ({:.1f}s, gate={}): {}{}".format(
            c["index"], c["end"] - c["start"], "OK" if c.get("contentGateOk") else "FAILED",
            out_dir / c["pov"],
            "  (+ {})".format(out_dir / ov_name) if (out_dir / ov_name).exists() else ""))

    # THE PERMANENT GATE (Round 3, joined by Round 4's aspect + approach-geometry gates): full
    # artifact set (frames.csv, meta.json, contact sheets, clips) is left on disk either way, for
    # forensics -- but any gate failure fails the trial's exit code, same as any other hard
    # acceptance check in this script.
    all_gates_ok = gate_ok and aspect_ok and approach_ok and trigger_ok
    if not all_gates_ok:
        eprint("[run_trial] trial FAILED one or more permanent gates -- content={} aspect={} "
               "approach={} trigger-speed={}; see meta.json/contact sheets above for detail.".format(
                   gate_ok, aspect_ok, approach_ok, trigger_ok))
        sys.exit(1)


if __name__ == "__main__":
    main()
