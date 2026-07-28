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
import random
import re
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import trial_lib
import vlm_eval_export
import overlay

PROJECT_DIR = Path(__file__).resolve().parent.parent  # .../social_sim_unity
DOCKER_CONTAINER = "ros"
DEFAULT_OUT_ROOT = Path.home() / "Desktop" / "research" / "social_navigation" / "trial_outputs"
OUTPUT_ROOT_SENTINEL_NAME = ".output_root_ok"
OUTPUT_ROOT_MIN_FREE_GB = 5.0
# Ops patch (2026-07-20), post-Session-19 disk-full incident: root-caused to
# ~/.ros/autotrial/*/*.csv, a per-bringup ROS-side telemetry log (upstream trial_info.launch,
# never read by any of this project's own tooling) that grows UNBOUNDED for the life of a
# bringup -- five old bringups' logs totaled ~87GB before that session's manual cleanup. This is
# the permanent fix: a size-capped pre-flight purge, not a one-off manual truncate.
ROS_LOG_GLOB = "/home/sheng/.ros/autotrial/*/*.csv"
ROS_LOG_MAX_MB = 500.0
ROS_LOG_ARCHIVE_DIRNAME = "_ros_log_archive"

# Session 55: `phone_user` remains a VALID appearance (assets on disk, still runnable) but is
# EXCLUDED FROM THE DATASET ROSTER after the s54_batch human review -- a 3.7532x uniform scale
# override on its prefab plus a ~70 deg heading-vs-velocity mismatch that makes it sidestep its
# whole path at 17% of commanded speed. See trial_outputs/known_issues/phone_user.md. A2 is
# therefore 7 special characters, not 8.
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
# Session 17 (Step 2): raised from the original 8.0 -- the corridor (ROBOT_START -> DEFAULT_
# ROBOT_GOAL) is ~43.6m long, so 25.0 (+ --slate-margin's frozen-spawn buffer, ~29m total) is
# still comfortably inside it, but gives the video's opening frame a real "pedestrian is a small
# distant figure, growing through the approach" read instead of an already-close encounter --
# verified on the NavMesh at spawn time (AutoTrialBootstrap.ValidateSpawnOnNavMesh), not assumed
# from the 1D bearing math alone.
DEFAULT_PED_DISTANCE = 25.0

# Session 30R (Howard priority #1): the 25m default above serves "full story arc" framing, not
# scoring -- interaction too small/distant in frame for a human/VLM to score against. 'scoring'
# retargets the SLATE v2 trigger to 8.0m (Session 16-and-earlier's own old default, see
# DEFAULT_PED_DISTANCE's history above -- not a new, unvalidated number) for a shorter approach
# and tighter framing. 'arc' preserves today's behavior byte-for-byte; default stays 'arc' so no
# existing caller's behavior changes -- only --profile scoring (or an explicit --ped-distance)
# moves off it.
PROFILES = ("arc", "scoring", "corridor")
# Session 41 TASK 5: 'corridor' uses the 'scoring' trigger distance (8m) -- the corridor is only
# 12m long, so a 25m 'arc' approach would put the encounter well outside the walls entirely.
PROFILE_PED_DISTANCE = {"arc": DEFAULT_PED_DISTANCE, "scoring": 8.0, "corridor": 8.0}

# Session 41 TASK 5: safety_label thresholds. The ticket asked for min_dist to be converted from a
# hard pass/fail gate into a recorded label, because a 1.2m corridor head-on pass breaks 0.5m by
# geometric necessity and the whole point of the scene is to generate those cases.
#
# IMPORTANT CORRECTION, verified this session: min_dist was NEVER a gate in this script. The
# permanent gates are content / aspect / approach-geometry / trigger-speed / overlay /
# file-manifest, plus the output-root sentinel and the editor-lock check -- min_dist is measured,
# printed and written to meta.json but has never affected the exit code. So there was no rejection
# to remove; what was genuinely missing, and is added here, is the LABEL itself.
SAFETY_LABEL_SAFE_M = 0.5      # >= this: clears the operational floor
SAFETY_LABEL_BREACH_M = 0.36   # < this: below the physical floor (robot 0.16 + pedestrian 0.2)


def safety_label_for(min_dist):
    """{safe|marginal|breach} for a measured min_dist, or None if unmeasured."""
    if min_dist is None:
        return None
    if min_dist >= SAFETY_LABEL_SAFE_M:
        return "safe"
    if min_dist >= SAFETY_LABEL_BREACH_M:
        return "marginal"
    return "breach"

# Session 29 STEP 2: scooter_user's own default --ped-speed multiplier. Parameters.MAX_VEL
# (Assets/Scripts/Agents/Parameters.cs) = 0.6 m/s is the shared social-force speed cap every
# character (Zone A and B alike) was measured hitting -- walking pace, not e-scooter cruise
# (real-world reference 3-4 m/s). 5.5x -> ~3.3 m/s, mid-range. PedestrianModulator.Scale()
# multiplies AFTER SFAgent.UpdateVelocity()'s own MAX_VEL clamp, so this can exceed 0.6 m/s
# without editing SFAgent.cs/Base.cs (both off-limits).
# Session 30R STEP 2: real-world speed audit. Session 29's SCOOTER_SPEED_MULT=5.5 was landed via
# commit message only -- its own REPORT.md section was never written, so the "5.5x -> ~3.3 m/s"
# comment above was never actually verified against a live trial (see PROJECT_HANDOFF's "Session
# 29 gap"). Measured live this session (frames.csv pedestrian_x/z, post-release): scooter_user at
# mult=5.5 actually ran ~5.23 m/s, not ~3.3 -- well outside the 3-4 m/s real-world e-scooter
# reference. Retuned to 3.7 below (implied base pace ~0.95 m/s * 3.7 ~= 3.5 m/s, mid-range).
# cyclist and wheelchair_user had NO multiplier at all (both measured ~0.95 m/s,
# walking-pace default -- cyclist is way under the 4-5 m/s bicycle reference; wheelchair_user is
# just under the 1.0-1.5 m/s reference). white_cane_user measured ~0.2-0.25 m/s, well under the
# 0.8-1.0 m/s reference (slower-than-walking, not stationary -- the Session 21-era transform-reset
# defect was NOT reproduced this session, positions traced smooth/monotonic; this is a genuine
# gait-speed gap, not that old bug resurfacing). All four retuned below from the SAME live
# measurements (implied base pace at mult=1.0, then solved for the multiplier landing near the
# middle of each reference range) -- see REPORT.md Session 30R STEP 2 for the full before/after
# table and the safety-rail (min_dist/ENCOUNTER-spin/collision) check on every change.
# Session 31 FIX 4: re-verified every actor against a REAL trial's frames.csv this session too
# (not multiplier arithmetic alone -- Session 29's scooter number was wrong for exactly that
# reason). Methodology refined from Session 30R's: measure per-frame instantaneous pedestrian
# speed (position delta / dt) across the whole post-release trial, then take the mean of frames
# where speed > 0.05 m/s ("moving" frames) -- NOT total-displacement/total-trial-duration, which
# undercounts badly for any actor that reaches its goal and then stands still for the remainder of
# the (90s) trial (found this session measuring business_male_01: naive whole-trial-average gave
# 0.189 m/s, nonsense against its own known ~1.3 m/s walking pace -- it simply arrives and stops
# around frame 74 of ~1100, and idles for the rest). Re-measured under --profile scoring, current
# (pre-fix) multipliers:
#   human (business_male_01, no multiplier): 1.285 m/s (reference 1.2-1.4 -- in range, confirms
#     Session 30R's number, no change).
#   scooter_user (mult 3.7): 3.515 m/s (reference 3-4 -- in range, confirms Session 30R, no change).
#   cyclist (mult 4.8): 4.560 m/s (reference 4-5 -- in range, confirms Session 30R, no change).
#   wheelchair_user (mult 1.3): 1.231 m/s -- AT/ABOVE human's own pace, wrong (should be SLOWER
#     than human, reference 0.8-1.0). Implied base pace 1.231/1.3=0.947, matching
#     Parameters.DESIRED_SPEED=0.95 almost exactly (sanity check).
#     IMPORTANT, found this session: the whole-trial mean/median methodology above is misleading
#     for any actor that reaches its own pedGoal and then stops or wanders -- naive
#     total-displacement/total-duration gave 0.189 m/s for business_male_01 (nonsense against its
#     own known ~1.3 m/s). Re-measured wheelchair_user using an early-window (first 8s
#     post-release, before any goal-arrival/wander contamination) method instead: mult=1.3 (the old
#     value) gives 1.175 m/s there, confirming the whole-trial number was roughly right for this
#     appearance. But a naive linear retune to mult=0.95 (the first attempt) collapsed to just
#     0.050 m/s (a 23x undershoot, not the ~10% reduction simple scaling predicts) -- reproduced
#     twice. wheelchair_user's root-motion/animation-blend response to the commanded velocity is
#     NOT linear across this range: mult=1.15 measured 1.037 m/s (consistent with 1.3's own linear
#     trend), but somewhere between 0.95 and 1.15 there's a real cliff where the blend tree stops
#     producing proportional root motion (a genuine animation-engineering quirk, not a math bug --
#     flagged to Howard, out of scope to fix at the animation-graph level this session). mult=1.0
#     sits just clear of that cliff: measured 0.890 m/s, reproduced identically (0.890 m/s) on a
#     second independent trial, min_dist 2.46-2.52m (safe). Landed at 1.0, not the arithmetically
#     "obvious" 0.95 -- the empirical cliff, not the target-midpoint math, is what actually governs
#     which values are usable here.
#   white_cane_user (mult 3.2): mean 0.943 m/s (moving frames, whole-trial) -- AT/ABOVE human's
#     pace, wrong (should be SLOWER than human, reference 0.6-0.8). Its own tap-and-pause gait
#     animation produces real burstiness confirmed again this session (early-window reads varied
#     0.549-0.851 m/s across different multipliers AND across repeat runs at the SAME multiplier --
#     e.g. mult=2.9 measured 0.608/0.518/0.640/0.553/0.563 m/s across two runs at three window
#     widths each -- consistent with Session 30R's own "real run-to-run variance, likely inherent
#     to its tap-and-pause gait" finding, not a measurement bug). Landed mult=2.9: consistently,
#     repeatably slower than human (~0.55-0.64 m/s vs human's 1.29) even though individual-run
#     point estimates don't always land inside the literal 0.6-0.8 band -- the qualitative goal
#     (clearly slower than human) is met more reliably than the precise numeric target is, and
#     chasing tighter precision against this much intrinsic gait noise has diminishing returns.
# Every change re-verified live post-retune (trial + speed recompute + min_dist + ENCOUNTER-spin +
# collision safety rail) before being called landed -- see REPORT.md Session 31 FIX 4 for the full
# before/after table, including the wheelchair cliff-discovery data.
SCOOTER_SPEED_MULT = 4.5914  # Session 54: was 3.7; rescaled by 1.3/1.0476 so the absolute pace stays 4.81 m/s
CYCLIST_SPEED_MULT = 5.9565  # Session 54: was 4.8; rescaled by 1.3/1.0476 so the absolute pace stays 6.24 m/s
# Session 54: LEAVE AT EXACTLY 1.0. Mathf.Approximately(pedSpeedMultiplier, 1.0f) in
# AutoTrialBootstrap.cs:798 means this appearance gets no PedestrianModulator at all, so it runs on
# raw social-force velocity capped by Parameters.MAX_VEL (0.95, not the 0.6 that a stale comment in
# AutoTrialBootstrap claims). That is the behaviour its eyeball pass approved. It is also why the
# BASE_PED_SPEED_MPS change does not touch it: with no modulator, the base is never applied.
WHEELCHAIR_SPEED_MULT = 1.0
# Session 33 FIX 6: user wants white-cane reduced FURTHER, target ~0.4-0.5 m/s (S31's 2.9 measured
# ~0.55-0.64 m/s -- clearly slower than human but not slow enough per this session's ask). Scaled
# down proportionally (2.9 * 0.45/0.6 ~= 2.175, rounded to 2.2) and re-verified live against a real
# trial rather than trusting the arithmetic (this project's own standing rule after S29's scooter
# mistake) -- see REPORT.md Session 33 FIX 6 for the measured on-disk speed.
# Session 54: 2.2 -> 0.4296, and deliberately NOT a like-for-like rescale like the two above.
# Scooter and cyclist are directVelocityDrive==true, so their translation is velocity*dt and their
# S31/S33 calibrations were measured through an intact chain -- preserving their absolute commanded
# speed is correct. white_cane is directVelocityDrive==false, so every metre came from root motion,
# through the animator.speed loop that Session 53 found and the Idling deadlock Session 54 found.
# Its "2.2 gives ~0.4-0.5 m/s" calibration measured the attenuation of a broken chain, not the
# multiplier. With the loop open, commanded speed IS realised ground speed, so 2.2 would now render
# a white-cane user travelling at 2.86 m/s. Set from the documented INTENT above (~0.45 m/s)
# instead: 0.45 / 1.0476 = 0.4296. This is an assumption, flagged for the eyeball pass.
WHITE_CANE_SPEED_MULT = 0.4296

# Session 54-C section 3: the four never-run Zone B characters had no entry here at all, so
# args.ped_speed fell through to the 1.0 default -- and pedSpeedMultiplier == 1.0 makes
# AutoTrialBootstrap skip attaching a PedestrianModulator entirely (Mathf.Approximately), which
# means solution (e) never applies and |Base.velocity| is not pinned. Both values below are != 1.0,
# which is a requirement, not a coincidence.
DOG_WALKER_SPEED_MULT = 1.0500   # 1.10 m/s / 1.0476 -- ordinary walking pace
PHONE_USER_SPEED_MULT = 0.9068   # 0.95 m/s / 1.0476 -- distracted pedestrians walk measurably slower
# male_child / female_child get no multiplier on purpose: they have no walking animation, so they
# run as --ped-motion standing (S54-C section 1) and are never commanded to travel.


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


SCENARIOS = ("headon", "overtake", "overtaken")


# Session 33: 'crossing' REMOVED per the user's own explicit instruction ("last chance... if it
# cannot be made to work this session, DELETE the crossing preset entirely"). Session 32 fixed
# crossing's TIMING (time-to-intersection matching, verified correct) but left a real, unresolved
# visual-framing problem open. This session root-caused it: `PovCameraSmoother`'s "course" yaw
# mode (Session 26, the standing default) locks the camera's yaw to the ROBOT'S OWN direction of
# travel, smoothed over a trailing window -- it never looks toward the pedestrian specifically.
# For headon (pedestrian ahead, on the robot's own path) this is invisible, since "where the robot
# is going" and "where the pedestrian is" coincide. For crossing (pedestrian approaching
# PERPENDICULAR to the robot's course), they structurally do NOT coincide -- the camera has no
# mechanism to ever pan toward a laterally-approaching pedestrian, regardless of how close or
# well-timed the numeric encounter is. This is the SAME root cause as Session 31's own
# unconfirmed "assertive gesture visibility blocked by camera framing" finding (assertive's
# straight-line pass doesn't make the robot react/turn either, so the course-locked camera stays
# pointed at its own forward heading there too) -- both symptoms of one underlying camera-design
# limitation, not two separate bugs. A real fix would mean adding a pedestrian-relative yaw-bias
# blend to PovCameraSmoother.cs (in scope, AutoTrial/**) for perpendicular-approach scenarios --
# not attempted this session given the remaining time budget and the risk of shipping an
# unverified camera behavior change affecting every OTHER scenario's framing too; flagged to
# Howard as a precise, actionable lead for a future session instead of another vague "some other
# framing property, not diagnosed" entry. See HOWARD_HANDOFF.md.


def resolve_scenario_geometry(scenario, ped_distance, goal_xyz, robot_start=ROBOT_START, overshoot=PED_OVERSHOOT_M,
                               trigger_distance=None, ped_speed_mps=None, robot_speed_mps=0.5):
    """Session 28 PART 2: pure-geometry scenario presets, all computed from the robot's own
    start->goal bearing (no new assets, no Unity changes). Returns ((ped_x, ped_y, ped_z,
    ped_yaw_deg), (dest_x, dest_y, dest_z), default_ped_speed_mult_or_None). ped_distance here is
    always the FROZEN SPAWN distance (ped_distance + slate_margin at the call site, matching
    headon's own existing convention) -- the SLATE v2 trigger (live dist_to_pedestrian <=
    config.triggerDistanceMeters) is what actually releases the pedestrian and defines dist0,
    unaffected by how far away any of these presets spawn it; only the geometry differs.

    headon (default): unchanged, delegates to resolve_head_on_geometry -- ped ahead on the
    robot's own path, facing back at the robot, goal overshoots past robot_start (genuine
    pass-through). Ped frozen at spawn regardless of scenario (SLATE v2), so pre-trigger
    dist_to_pedestrian shrinks from the robot's own approach alone in every scenario below too.

    ('crossing' was removed in Session 33 -- see the module-level comment above SCENARIOS for the
    full root-cause writeup, a camera-framing limitation, not a geometry bug.)

    overtake: ped ahead on the robot's OWN path (the same spawn point headon uses), but facing
    and moving in the SAME direction as the robot instead of back toward it -- the faster robot
    catches up and passes. Goal continues further along the same bearing past the spawn point.
    Default ped-speed multiplier 0.5 (slower than the robot) unless overridden.

    overtaken: HONEST GEOMETRY NOTE -- literally spawning the pedestrian BEHIND robot_start
    turns out to be structurally incompatible with the existing SLATE v2 trigger: the pedestrian
    is frozen until release, so only the ROBOT's own pre-release motion can shrink
    dist_to_pedestrian toward the trigger threshold, and the robot only ever moves FORWARD
    (robot_start -> goal) -- a fixed point behind robot_start only gets further away as the robot
    advances, so the distance-shrink trigger condition can never fire (confirmed empirically this
    session: the 30s timeout guard fires instead, same failure mode --scenario crossing hit
    before its own fix). Implemented instead as the mirror of `overtake`: ped spawns AHEAD on the
    robot's own path (the same achievable-trigger spawn point headon/overtake use), same
    direction of travel as the robot, default ped-speed multiplier 1.5 (faster than the robot) --
    post-release the faster pedestrian pulls further ahead/away rather than the robot passing it,
    the mirror dynamic of overtake's "robot catches up and passes." This is a documented
    approximation of "overtaken," not literal starts-behind-catches-up motion -- said so plainly
    here and in REPORT.md rather than silently claiming geometry that isn't actually produced.
    """
    if scenario not in SCENARIOS:
        raise ValueError("unknown --scenario '{}', valid: {}".format(scenario, SCENARIOS))

    sx, sy, sz = robot_start
    gx, gy, gz = goal_xyz[0], goal_xyz[1], goal_xyz[2]
    dx, dz = gx - sx, gz - sz
    norm = math.hypot(dx, dz)
    if norm < 1e-6:
        raise ValueError("robot start {} and goal {} coincide on the ground plane -- cannot "
                          "compute a bearing for --scenario '{}' placement".format(robot_start, goal_xyz, scenario))
    ux, uz = dx / norm, dz / norm

    if scenario == "headon":
        ped_pose, dest = resolve_head_on_geometry(ped_distance, goal_xyz, robot_start, overshoot)
        return ped_pose, dest, None

    if scenario == "overtake":
        ped_x, ped_z = sx + ux * ped_distance, sz + uz * ped_distance
        dest_x, dest_z = ped_x + ux * overshoot, ped_z + uz * overshoot
        yaw = math.degrees(math.atan2(ux, uz)) % 360.0  # same direction of travel as the robot
        return (ped_x, sy, ped_z, yaw), (dest_x, sy, dest_z), 0.5

    # overtaken -- see the docstring's "HONEST GEOMETRY NOTE" above: same achievable spawn point
    # as overtake (ahead on the robot's path), not literally behind. Only the default speed
    # multiplier differs from overtake (1.5 vs 0.5), producing the mirror dynamic.
    ped_x, ped_z = sx + ux * ped_distance, sz + uz * ped_distance
    dest_x, dest_z = ped_x + ux * overshoot, ped_z + uz * overshoot
    yaw = math.degrees(math.atan2(ux, uz)) % 360.0  # same direction of travel as the robot
    return (ped_x, sy, ped_z, yaw), (dest_x, sy, dest_z), 1.5


# Loop 1 Bug 1: was 0.9. TrialController.cs's own min_dist/minDistanceMeters previously only
# ever tracked robot<->pedestrian1 (a separate bug, fixed the same session) -- once fixed, fresh
# N=5 data showed pedestrian2 (this offset), not pedestrian1, is the actual binding safety
# constraint for both dyad and ped_count_3: on a headon scenario the extra pedestrian closes on
# a near-reciprocal course only this many meters laterally clear of the robot's own path, and
# that nominal clearance gets eaten by ordinary TEB path weave + the pedestrian's own walk noise.
# dyad's true (all-pedestrian) worst-of-5 at 0.9m was 0.279m -- a real physical-floor breach that
# had been invisible under the old primary-only metric. First widened to 1.5m: dyad cleared (worst
# 0.659m) but ped_count_3 N=5 still missed the 0.5m operational bar (worst 0.458m, pedestrian2
# again) -- 1.5m wasn't enough margin against the pass-timing variance. Widened further to 2.0m;
# verified below (see REPORT.md Loop 1 Session).
DYAD_LATERAL_OFFSET_M = 2.0
PED_COUNT3_LATERAL_OFFSET_M = 1.8

# Session 45 (1.5): ped_count_3 read as three unrelated individuals rather than a group, so the
# three are pulled together into one. Deliberately a SEPARATE constant rather than a change to
# DYAD_LATERAL_OFFSET_M above: that 2.0 is a verified Loop 1 safety fix (0.9 -> 1.5 -> 2.0, each
# step forced by a measured floor breach) and dyad passed the Session 44 eyeball pass, so neither
# may be disturbed. This constant applies ONLY when --ped-count >= 3.
#
# READ THIS BEFORE QUOTING ANY ped_count_3 CLEARANCE NUMBER. Loop 1 measured ped_count_3 at 1.5m
# spacing with worst-of-5 min_dist 0.458m -- below the 0.5m operational bar, pedestrian2. Grouping
# at 1.2m reproduces that geometry deliberately, because the ask is a group that actually
# constrains the robot's passage. The tighter clearance is the intended physical situation here,
# not a regression, and this configuration must not be used for a safety claim without re-measuring
# at N>=5.
PED_COUNT3_GROUP_OFFSET_M = 1.2


def resolve_extra_pedestrian_geometry(primary_spawn, primary_dest, robot_start, goal_xyz, lateral_offset_m):
    """Session 35 BLOCK 4 (FIX 8/9): places an EXTRA pedestrian (dyad's partner, or ped-count-3's
    third walker) by offsetting the PRIMARY pedestrian's own already-resolved spawn/dest pair
    sideways by `lateral_offset_m`, along the perpendicular to the robot's own start->goal bearing
    -- not re-derived from robot_start/goal_xyz independently. This makes the extra pedestrian
    walk a path parallel to whatever the primary pedestrian is doing, regardless of which
    --scenario produced the primary's own geometry (headon/overtake/overtaken all work
    identically here, since this only needs the corridor's own perpendicular direction, computed
    fresh from robot_start/goal_xyz exactly like resolve_scenario_geometry's own bearing math).
    Same facing (yaw) as the primary pedestrian -- a dyad partner walks the same direction, not a
    mirror image. Returns ((x,y,z,yawDeg), (dest_x,dest_y,dest_z))."""
    sx, sy, sz = robot_start
    gx, gz = goal_xyz[0], goal_xyz[2]
    dx, dz = gx - sx, gz - sz
    norm = math.hypot(dx, dz)
    if norm < 1e-6:
        raise ValueError("robot start {} and goal {} coincide -- cannot compute a perpendicular "
                          "offset for an extra pedestrian".format(robot_start, goal_xyz))
    ux, uz = dx / norm, dz / norm
    px, pz = -uz, ux  # perpendicular (rotate 90deg in the ground plane), same convention as crossing

    (spawn_x, spawn_y, spawn_z, yaw_deg) = primary_spawn
    (dest_x, dest_y, dest_z) = primary_dest
    extra_spawn = (spawn_x + px * lateral_offset_m, spawn_y, spawn_z + pz * lateral_offset_m, yaw_deg)
    extra_dest = (dest_x + px * lateral_offset_m, dest_y, dest_z + pz * lateral_offset_m)
    return extra_spawn, extra_dest


# Zone A is validated by convention (snake_case -> Rocketbox PascalCase), not enumerated here --
# Unity's Resources.Load is authoritative. This regex just catches obvious typos early.
ZONE_A_PATTERN = re.compile(r"^[a-z]+(_[a-z0-9]+)*$")


def _statvfs_free_gb(path):
    st = os.statvfs(str(path))
    return (st.f_bavail * st.f_frsize) / (1024 ** 3)


def _docker_root_dir():
    """Best-effort: the filesystem path Docker itself stores container data under (usually
    /var/lib/docker). Returns None (never raises) if docker isn't reachable -- callers must treat
    that as "skip this specific check", not as a reason to fail the whole guard."""
    try:
        result = subprocess.run(["docker", "info", "--format", "{{.DockerRootDir}}"],
                                 capture_output=True, text=True, timeout=10)
        path = result.stdout.strip()
        return path if result.returncode == 0 and path else None
    except Exception:
        return None


def require_output_root_healthy(root=None, min_free_gb=OUTPUT_ROOT_MIN_FREE_GB):
    """Round 3 (post-relocation guard); repointed Session 30 (T7 retired -> /mnt/ssd, the internal
    2TB drive). trial_outputs resolves through a symlink onto a dedicated output-root drive. If
    that drive isn't mounted/available, the symlink either dangles (obvious failure) or -- the
    actually dangerous case on some setups -- the path silently falls back to being created fresh
    on the internal `/` disk, quietly refilling it exactly the way this whole relocation was meant
    to prevent. Guard against both: resolve the REAL path (following the symlink) and REQUIRE a
    sentinel file (OUTPUT_ROOT_SENTINEL_NAME) that only exists on the intended drive; refuse
    loudly rather than writing anywhere else. Also requires >= min_free_gb free on the resolved
    path. The sentinel name itself is drive-agnostic on purpose (".output_root_ok", not
    ".output_root_on_t7") -- the concept (verify the resolved path is really the intended output
    drive before writing) is what matters, not which physical disk currently backs it.

    Ops patch (2026-07-20): Session 19's disk-full crisis was NOT on this output root (T7 had
    plenty of room) -- it was the HOST's internal `/` filesystem, which this guard never checked
    at all (the original comment here even said so explicitly: "space on the root filesystem is
    irrelevant once output lives elsewhere" -- true for OUTPUT space, false for the many other
    things sharing `/`, like Unity's own Library cache and the ros container's writable layer).
    Now also checks host `/`, and the docker root dir too if it resolves to a DIFFERENT
    filesystem than `/` (same device -> same statvfs call, skipped as redundant; checked via
    st_dev, not just path string equality, in case of bind-mount tricks). Each filesystem named
    explicitly in its own refusal so a failure is never ambiguous about which disk is the problem."""
    root = Path(root) if root is not None else DEFAULT_OUT_ROOT
    resolved = root.resolve()
    sentinel = resolved / OUTPUT_ROOT_SENTINEL_NAME
    if not resolved.is_dir() or not sentinel.exists():
        raise SystemExit(
            "[run_trial] REFUSING TO START: output root sentinel missing ({}). Resolved path: {}. "
            "This means trial_outputs isn't a symlink onto the intended output-root drive -- "
            "writing here would silently land on the wrong disk. Restore the trial_outputs -> "
            "/mnt/ssd/Social_Navigation/trial_outputs symlink (sentinel file {} must exist there) "
            "before running trials.".format(sentinel, resolved, OUTPUT_ROOT_SENTINEL_NAME))

    free_gb = _statvfs_free_gb(resolved)
    if free_gb < min_free_gb:
        raise SystemExit(
            "[run_trial] REFUSING TO START: only {:.2f}GB free at output root {} (need >= {}GB).".format(
                free_gb, resolved, min_free_gb))

    host_root = Path("/")
    host_free_gb = _statvfs_free_gb(host_root)
    if host_free_gb < min_free_gb:
        raise SystemExit(
            "[run_trial] REFUSING TO START: only {:.2f}GB free at host root / (need >= {}GB) -- "
            "this is the filesystem Unity's own Library cache and Docker's container storage "
            "live on, NOT the T7 output root (which is healthy). See ops patch 2026-07-20 / "
            "Session 19's disk-full incident (~/.ros/autotrial/*/*.csv unbounded growth was the "
            "root cause that time) for what to check first.".format(host_free_gb, min_free_gb))

    docker_root = _docker_root_dir()
    if docker_root is not None:
        try:
            distinct = os.stat(docker_root).st_dev != os.stat(str(host_root)).st_dev
        except OSError:
            distinct = False
        if distinct:
            docker_free_gb = _statvfs_free_gb(docker_root)
            if docker_free_gb < min_free_gb:
                raise SystemExit(
                    "[run_trial] REFUSING TO START: only {:.2f}GB free at Docker root {} (need "
                    ">= {}GB) -- this is on a different filesystem than / and was not covered by "
                    "either of the other two checks.".format(docker_free_gb, docker_root, min_free_gb))

    return resolved


def contain_ros_logs(out_root=None, max_mb=ROS_LOG_MAX_MB):
    """Ops patch (2026-07-20): permanent fix for Session 19's disk-full root cause. Pre-flight
    step (call before every trial, not just when things are already dire) -- any
    ~/.ros/autotrial/*/*.csv inside the `ros` container over max_mb is archived (docker cp, so
    the exact bytes are preserved, not re-derived) to trial_outputs/_ros_log_archive/ and then
    truncated in place (`truncate -s 0`, safe for a live O_APPEND writer -- the current bringup's
    process keeps writing from byte 0 with no error, confirmed empirically during Session 19's
    manual version of this same operation). Best-effort and non-fatal by design: a hygiene step
    failing must never block a trial that would otherwise run fine -- unlike
    require_output_root_healthy, which is a hard precondition."""
    try:
        find = subprocess.run(
            ["docker", "exec", DOCKER_CONTAINER, "bash", "-lc",
             "find /home/sheng/.ros/autotrial -name '*.csv' -size +{}M 2>/dev/null".format(int(max_mb))],
            capture_output=True, text=True, timeout=15)
        if find.returncode != 0:
            return
        paths = [p for p in find.stdout.splitlines() if p.strip()]
        if not paths:
            return

        archive_dir = (Path(out_root) if out_root is not None else DEFAULT_OUT_ROOT) / ROS_LOG_ARCHIVE_DIRNAME
        archive_dir.mkdir(parents=True, exist_ok=True)
        ts = time.strftime("%Y%m%d_%H%M%S")

        for container_path in paths:
            basename = container_path.rsplit("/", 1)[-1]
            local_path = archive_dir / "{}_{}".format(ts, basename)
            cp = subprocess.run(["docker", "cp", "{}:{}".format(DOCKER_CONTAINER, container_path), str(local_path)],
                                 capture_output=True, text=True, timeout=120)
            if cp.returncode != 0:
                eprint("[run_trial] contain_ros_logs: docker cp failed for {} (non-fatal, leaving it in place): {}".format(
                    container_path, cp.stderr[-300:]))
                continue
            trunc = subprocess.run(
                ["docker", "exec", DOCKER_CONTAINER, "bash", "-lc", "truncate -s 0 '{}'".format(container_path)],
                capture_output=True, text=True, timeout=15)
            if trunc.returncode == 0:
                eprint("[run_trial] contain_ros_logs: archived {} -> {}, truncated in place.".format(
                    container_path, local_path))
            else:
                eprint("[run_trial] contain_ros_logs: archived {} but truncate failed (non-fatal): {}".format(
                    container_path, trunc.stderr[-300:]))
    except Exception as e:
        eprint("[run_trial] contain_ros_logs failed (non-fatal, not blocking the trial): {}".format(e))


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
    """Session 30R fix: `subprocess.run(..., timeout=)` firing only kills the HOST-side `docker
    exec` client -- it does not propagate to the process actually running inside the container
    (docker exec without a TTY/signal-forwarding does not kill the in-container child on client
    disconnect). Found live this session: a handful of timed-out diagnostic calls (an errant
    `dynparam set`, a couple of `rostopic echo -n1` probes) left orphaned processes running
    inside the `ros` container indefinitely -- including, worst case, extra `move_base` instances
    contending for the same costmap/CPU budget as the real one, which is itself timing-sensitive
    (Session 24: local costmap already runs close to its 50ms/20Hz update budget with just ONE
    instance). Fixed at the root: wrap the inner command in the container's OWN `timeout`, so a
    hang is killed from inside regardless of what happens to the host-side client. The host-side
    subprocess timeout is kept as a backstop (a few seconds of slack beyond the inner one) in case
    `docker exec` itself hangs before ever reaching the inner timeout.
    """
    inner_timeout = max(1, timeout - 3)
    full = ["docker", "exec", DOCKER_CONTAINER, "bash", "-lc", "timeout {} {}".format(inner_timeout, cmd)]
    return subprocess.run(full, capture_output=True, text=True, timeout=timeout)


def move_base_pid():
    """Returns the live /move_base node's PID (str) or None if not resolvable. Used by
    ros_health_check's stability check below -- see that function's Session 30R comment."""
    info = docker_exec("rosnode info /move_base", timeout=10).stdout
    m = re.search(r"^Pid:\s*(\d+)", info, re.MULTILINE)
    return m.group(1) if m else None


def ros_health_check():
    """Returns (healthy: bool, warnings: list[str]).

    Session 30R finding: on a truly cold container (first bringup since container creation, not
    exercised by any prior session -- all reused a long-lived warm container), `move_base` was
    observed crash-looping (exit code 1, respawn=true) for ~14 minutes straight (312 consecutive
    deaths, roslaunch-*.log evidence) before finally staying up. A single-snapshot `rosnode list`
    check can catch `/move_base` mid-respawn (it re-registers near-instantly each cycle) and
    report "healthy" even though the node is unusable a moment later -- this is exactly what
    happened: the fresh-bringup health check passed in 2s, but the very next real trial ran with
    move_base dying and restarting roughly once per second the whole time (robot never received a
    stable planner to command it, robotSpeedAtTrigger stayed 0.000 for the full duration). Fixed
    by requiring the SAME move_base PID across two checks 3s apart -- a crash-looping node fails
    this immediately (different PID, or no PID at all), a genuinely stable one passes for free.
    """
    warnings = []
    try:
        nodes = docker_exec("rosnode list").stdout
    except (subprocess.TimeoutExpired, FileNotFoundError) as e:
        return False, ["could not reach ROS container: {}".format(e)]

    if "/move_base" not in nodes:
        return False, ["move_base node not running"]

    pid1 = move_base_pid()
    if pid1 is None:
        return False, ["move_base node present in rosnode list but PID unresolvable (likely mid-respawn)"]
    time.sleep(3)
    pid2 = move_base_pid()
    if pid2 is None or pid2 != pid1:
        return False, ["move_base PID unstable across a 3s window ({} -> {}) -- crash-looping, not "
                        "a stable planner (Session 30R)".format(pid1, pid2)]

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


def ensure_teb_plugin_installed():
    """Session 30R ROOT CAUSE, found live this session (not the historical Session 8 oscillation
    story -- that was ruled out first): on a truly cold container (first bringup since container
    creation), move_base was crash-looping every ~1.1s. `rosout.log` showed the real reason
    (never in move_base's own per-process log, which mysteriously never gets written) --
    MoveBase::MoveBase() FATAL: "Failed to create the teb_local_planner/TebLocalPlannerROS
    planner ... does not exist. Declared types are base_local_planner/TrajectoryPlannerROS" --
    `rospack find teb_local_planner` confirmed the package is genuinely absent from this
    container's ROS_PACKAGE_PATH, not a pluginlib cache race. Root cause: TEB (landed Session 23)
    was installed live into a long-running container that never got rebuilt into the `ros:latest`
    image (docker images don't auto-update from a live container's apt installs) -- so any FRESH
    container built from that image (exactly what a post-reboot session gets) starts back on the
    pre-TEB image and silently lacks it. The eventual "stabilizes on its own after several
    minutes" behavior earlier sessions never needed to explain is roslaunch's respawn --
    map_server.launch's OWN retries eventually happening to line up with some other transient
    state is not what's going on; every crash in this state is the exact same deterministic FATAL,
    forever, until the package is actually installed -- it does not self-resolve, unlike genuine
    priming/oscillation flakiness.

    Idempotent and cheap (a few hundred ms) once installed, so this runs unconditionally as a
    preflight rather than only after detecting a crash-loop -- matches contain_ros_logs()'s own
    "always run it, it's a no-op when already fine" pattern. Container-level package install, not
    a tracked-file edit (sim_ws/social_sim_unity git state untouched) -- but NOT persisted to the
    `ros:latest` image itself (would need `docker commit` or a Dockerfile change, both explicit,
    scoped host/infra actions out of this session's writable-file scope); flagged in
    HOWARD_HANDOFF.md as the durable fix still owed. Lost again if this container is ever
    recreated from the stale image -- this preflight is what makes that survivable automatically
    rather than costing another multi-hour diagnosis.
    """
    result = docker_exec("rospack find teb_local_planner", timeout=10)
    if result.returncode == 0:
        return
    eprint("[run_trial] preflight: teb_local_planner package missing from this container (Session "
           "30R root cause, see comment) -- installing now (apt-get, root, one-time per container "
           "lifetime)...")
    install = subprocess.run(
        ["docker", "exec", "-u", "root", DOCKER_CONTAINER, "bash", "-lc",
         "apt-get update -qq && apt-get install -y -qq ros-noetic-teb-local-planner"],
        capture_output=True, text=True, timeout=180)
    verify = docker_exec("rospack find teb_local_planner", timeout=10)
    if verify.returncode != 0:
        raise SystemExit("[run_trial] teb_local_planner install failed -- apt-get output:\n{}\n{}".format(
            install.stdout[-2000:], install.stderr[-2000:]))
    eprint("[run_trial] preflight: teb_local_planner installed successfully.")


DEFAULT_TEB_MIN_OBSTACLE_DIST = 0.3
DEFAULT_TEB_INFLATION_DIST = 0.5
# Session 31 FIX 1 (Howard priority #1/#6, "avoids too early" / "encounter window too short"):
# screened min_obstacle_dist/inflation_dist in {0.3/0.5 (baseline), 0.2/0.3, 0.15/0.2} against one
# business_male_01 x indifferent x --profile scoring trial each (repeated once for the chosen
# candidate). Absolute safety floor: robot footprint radius (0.16m, /move_base/*_costmap/
# robot_radius) + pedestrian collision-capsule radius (0.2m, Base.RADIUS) = 0.36m -- every
# candidate's measured min_dist stayed well clear of this (baseline 1.737, candidate 0.2/0.3:
# 1.722/1.475 across two runs, candidate 0.15/0.2: 1.334) -- no candidate came anywhere near
# vetoing on collision risk. A lateral-path-deviation-onset-distance metric (how far out the robot
# first deviates >0.3m from the straight corridor centerline) was tried to isolate "avoidance
# onset" specifically, but proved too noisy to trust at N=1-2 per candidate -- readings did not
# move monotonically with the parameter change, consistent with this project's own prior findings
# (Sessions 22-24) that TEB's own residual path weave is a real, intrinsic optimization artifact,
# not cleanly separable from pedestrian-specific avoidance with a simple threshold. Landed the
# MODERATE candidate (0.2/0.3, not the more aggressive 0.15/0.2): directionally exactly what the
# brief asked (robot permitted closer before its cost function penalizes proximity), safety margin
# preserved with more headroom than the aggressive candidate, chosen over 0.15/0.2 specifically
# because the aggressive candidate's extra clearance reduction did not show a clearly demonstrated
# framing/dwell benefit to justify its tighter min_dist. Reported honestly as directionally-correct
# and safety-verified, not as a proven "later avoidance" effect -- see REPORT.md Session 31 FIX 1.
# Session 32 FIX A: raised aggressiveness one notch from S31's landed 0.2/0.3 (see
# TEB_SCORING_WEIGHT_OBSTACLE's own comment below for the full before/after data -- these two
# geometric params turned out NOT to be the dominant lever, but were kept at this tighter setting
# alongside the new weight_obstacle reduction since the combination is what was actually verified
# safe and effective this session, not either change in isolation).
TEB_SCORING_MIN_OBSTACLE_DIST = 0.15
TEB_SCORING_INFLATION_DIST = 0.2

# Session 32 FIX A: S31's own REPORT.md admitted the "later avoidance onset" causal claim was
# never cleanly isolated from TEB's ambient weave -- the user's own eyes then confirmed it did
# NOT visibly work (indifferent and assertive looked identical). This session actually measured
# detour-onset distance as a hard number for the first time (lateral deviation from the corridor
# centerline, sustained >=3 frames past a 0.12m epsilon, first 15 frames excluded as spawn-
# transient noise -- see the analysis notes in REPORT.md Session 32). S31's own two TEB params
# (min_obstacle_dist/inflation_dist, already at their more aggressive 0.15/0.2 candidate here)
# were NOT the dominant lever -- baseline onset under 0.2/0.3 (S31's landed values) measured
# 3.79-4.78m across indifferent/scared/surprised trials, essentially unchanged from pre-S31.
# `weight_obstacle` (TEB's own optimization weight for obstacle-avoidance cost -- how strongly
# the trajectory optimizer penalizes proximity to obstacles, independent of the geometric
# min_obstacle_dist/inflation_dist margins) turned out to be the real lever: reducing it from its
# compiled-in 50.0 down to 15.0 (alongside 0.15/0.2) measured onset at 1.76m and 2.79m across two
# repeat trials (mean ~2.27m) -- squarely in the brief's ~2-3m target band, with min_dist staying
# safely above the 0.5-0.86m operational bar (1.39-1.393m across the two indifferent runs). A
# `social_layer` costmap plugin (amplitude=77/covariance=0.1/cutoff=10.0) also exists on both
# costmaps and was investigated as a suspect (a Gaussian pedestrian-comfort cost layer, cutoff=10m
# is a plausible far-field trigger) but is NOT a dynamic_reconfigure server (confirmed via
# `dynparam list`) -- a live rosparam set would not take effect without a relaunch that risks being
# overwritten by whatever yaml seeds it at roslaunch time, and TEB's own weight_obstacle lever
# alone already hit the target band, so the social_layer investigation was not pursued further
# this session (flagged to Howard as a real, uninvestigated lever if a future session needs an
# even later onset than weight_obstacle=15 provides).
TEB_SCORING_WEIGHT_OBSTACLE = 15.0
# FIX D(b): fast actors (scooter/cyclist) close distance faster, so the SAME clearance distance
# gives TEB less real time to react -- effectively an earlier onset in wall-clock terms even at
# an identical parameter value. Compensate with an even lower weight for these two appearances
# specifically under --profile scoring (empirically screened against a real scooter trial, see
# REPORT.md Session 32 FIX D).
TEB_SCORING_WEIGHT_OBSTACLE_FAST = 8.0
TEB_SCORING_FAST_APPEARANCES = ("scooter_user", "cyclist")
DEFAULT_TEB_WEIGHT_OBSTACLE = 50.0

# Session 33 FIX 1: audited the FULL clearance chain the user's brief suspected (costmap
# inflation_radius/cost_scaling_factor, robot footprint, obstacle-marking range), not just TEB's
# own two geometric params (already tuned by S31/S32). Live audit via `rosparam get` against the
# already-running node (not this repo's own checked-out, stale sim_ws copy -- see PROJECT_HANDOFF's
# "sim_ws is stale" note): local_costmap/inflater_layer inflation_radius=0.1, cost_scaling_factor=3.0
# -- ALREADY SMALL, not a legacy-oversized Kuri bubble as the brief suspected. robot_radius=0.16,
# footprint='' (circular approximation) -- and the robot_description IS actually generated from
# kuri2.urdf.xacro (confirmed via `rosparam get /robot_description`), i.e. this sim genuinely runs
# a Kuri-class round robot, NOT an A1 quadruped as the task brief assumed -- the circular
# 0.16m footprint is period-correct for the robot actually in use, not a mismatch to fix.
# obstacle_range/raytrace_range=10.0m looked suspicious in isolation but the local costmap's own
# window is only 6x6m (confirmed), which already caps any practical marking distance at 3m from
# the robot regardless of the 10m range setting -- not a live lever.
#
# CRITICAL FINDING (the session's real headline, more important than any parameter value):
# built a clean pedestrian-ABSENT control trial (--ped-distance 40, --duration 15, pedestrian never
# within 20m the whole recording) and ran the SAME lateral-deviation "detour-onset" instrumentation
# S32 built against it. It still fired an "onset" at 37.7m -- i.e. with a pedestrian that could not
# possibly be influencing the robot's path at all, the same ambient TEB path noise (~0.3-0.45m
# lateral drift amplitude, smooth and continuous, NOT a discrete avoidance event) crosses the
# metric's 0.12m threshold within ~2s of every trial, regardless of any pedestrian. This CONFIRMS
# S32's own suspicion ("TEB avoidance-onset benefit not cleanly isolated from ambient weave") as
# the dominant explanation for S32's own reported variance (1.76-6.70m for one identical landed
# config) -- "detour-onset distance" as defined is measuring mostly-or-entirely ambient path noise,
# not a real, tunable geometric avoidance bubble. There is no large oversized bubble left to
# deflate at the parameter level; the true remaining effect (if any) is small relative to this
# noise floor. Reported honestly rather than chasing a number that doesn't mean what it appears to.
#
# Given that, tuning proceeded on REAL, trustworthy evidence instead: min_dist (not onset distance)
# across matched N=3 trials at fixed --profile scoring/business_male_01/indifferent/30s duration.
# Screened three costmap+TEB combinations (min_dist m, N=3 each): baseline (S32's landed 0.15/0.2/
# inflation_radius=0.1/csf=3.0): 0.54, 1.008, 1.168. A "tight" candidate (min_obstacle_dist=0.08,
# inflation_dist=0.12, inflation_radius=0.08, csf=8.0): 0.526, 1.668, 1.831 -- one run at 0.526,
# uncomfortably close to the 0.5m operational floor (only 5% margin), rejected per the brief's own
# "back off one notch" instruction. Landed the "notch back" candidate instead: min_obstacle_dist=
# 0.10, inflation_dist=0.15, inflation_radius unchanged at 0.10, cost_scaling_factor steepened
# 3.0->6.0 (cost falls off faster with distance = a tighter effective bubble edge at the same
# radius): 0.821, 1.069, 1.212 (one run separately gated FAIL on trigger-speed, unrelated to this
# parameter -- excluded). All three candidates' min_dist ranges overlap substantially given N=3 --
# consistent with the noise-floor finding above, this is NOT a dramatic, clean win, but it is a
# real, safety-verified, directionally-correct tightening (every touched parameter moved toward
# permitting closer proximity, none moved away from it), landed honestly as such.
TEB_SCORING_MIN_OBSTACLE_DIST_S33 = 0.10
TEB_SCORING_INFLATION_DIST_S33 = 0.15
DEFAULT_COSTMAP_INFLATION_RADIUS = 0.10
DEFAULT_COSTMAP_COST_SCALING_FACTOR = 3.0
TEB_SCORING_COST_SCALING_FACTOR = 6.0

# Session 34 FIX 1: S34PedestrianReactDistGate's shared gate distance under --profile scoring.
# Swept {1.5, 2.0, 2.5}m, N=3 each -- all three landed safely (min_dist 0.92-1.58m across every
# gate-passing run, comfortably above the 0.5m operational floor; 2.0/2.5's one failed run each
# was the known cold-ROS-bringup triggerTimedOut flakiness, unrelated to the gate itself). Landed
# the TIGHTEST candidate (1.5m): zero pipeline failures across N=3, and the tightest gate best
# serves this session's own reframe -- "hold course longer, then react" is what makes personality
# visibly distinct, so the pedestrian should hold its line as long as safely possible before the
# gate engages. See REPORT.md Session 34 FIX 1 for the full table.
PED_REACT_DIST_SCORING_DEFAULT = 1.5

# Session 36 FIX 3: Scared's own larger gate distance (see S34PedestrianReactDistGate's
# scaredReactDistanceMetersOverride field) -- swept {2.5, 3.0, 3.5}m, see REPORT.md Session 36
# FIX 3 for the landed value and the lateral-deviation-curve comparison against indifferent.
SCARED_REACT_DIST_SCORING_DEFAULT = 3.0


def set_costmap_inflation_params(inflation_radius, cost_scaling_factor):
    """Session 33 FIX 1: live dynamic_reconfigure set against /move_base/local_costmap/
    inflater_layer (confirmed via `dynparam list` to be a real dynamic_reconfigure server, same
    discipline as set_teb_avoidance_params -- a runtime rosparam experiment, not a sim_ws file
    edit). Idempotent, best-effort (non-fatal on failure, mirrors set_teb_avoidance_params)."""
    param_str = "'inflation_radius': {}, 'cost_scaling_factor': {}".format(inflation_radius, cost_scaling_factor)
    cmd = ("rosrun dynamic_reconfigure dynparam set /move_base/local_costmap/inflater_layer "
           "\"{{{}}}\"".format(param_str))
    result = docker_exec(cmd, timeout=15)
    if result.returncode != 0:
        eprint("[run_trial] set_costmap_inflation_params: dynparam set failed (non-fatal): {}".format(
            result.stderr[-300:]))
        return
    verify_ir = docker_exec("rosparam get /move_base/local_costmap/inflater_layer/inflation_radius", timeout=10).stdout.strip()
    verify_csf = docker_exec("rosparam get /move_base/local_costmap/inflater_layer/cost_scaling_factor", timeout=10).stdout.strip()
    eprint("[run_trial] costmap inflation params: inflation_radius={} cost_scaling_factor={} "
           "(requested {}/{})".format(verify_ir, verify_csf, inflation_radius, cost_scaling_factor))


def set_teb_avoidance_params(min_obstacle_dist, inflation_dist, weight_obstacle=None):
    """Live dynamic_reconfigure set against the already-running move_base/TebLocalPlannerROS node
    -- same mechanism warmup_ros_session() already uses for oscillation_timeout (a runtime
    rosparam/dynparam experiment against the live ROS session, not a sim_ws file edit, per the
    standing guardrails). Idempotent (setting the same value twice is a no-op) and best-effort:
    logs a warning rather than raising if the set doesn't take, since a failed clearance tweak
    should not itself block a trial that would otherwise run fine at whatever value is already
    live (mirrors contain_ros_logs()'s non-fatal preflight style). weight_obstacle is optional
    (Session 32 FIX A addition) -- omitted, the node's currently-live value is left untouched."""
    params = {"min_obstacle_dist": min_obstacle_dist, "inflation_dist": inflation_dist}
    if weight_obstacle is not None:
        params["weight_obstacle"] = weight_obstacle
    param_str = ", ".join("'{}': {}".format(k, v) for k, v in params.items())
    cmd = ("rosrun dynamic_reconfigure dynparam set /move_base/TebLocalPlannerROS "
           "\"{{{}}}\"".format(param_str))
    result = docker_exec(cmd, timeout=15)
    if result.returncode != 0:
        eprint("[run_trial] set_teb_avoidance_params: dynparam set failed (non-fatal, trial will "
               "run with whatever TEB params are already live): {}".format(result.stderr[-300:]))
        return
    verify_min = docker_exec("rosparam get /move_base/TebLocalPlannerROS/min_obstacle_dist", timeout=10).stdout.strip()
    verify_inf = docker_exec("rosparam get /move_base/TebLocalPlannerROS/inflation_dist", timeout=10).stdout.strip()
    verify_wt = docker_exec("rosparam get /move_base/TebLocalPlannerROS/weight_obstacle", timeout=10).stdout.strip()
    eprint("[run_trial] TEB avoidance params: min_obstacle_dist={} inflation_dist={} weight_obstacle={} "
           "(requested {}/{}/{})".format(verify_min, verify_inf, verify_wt, min_obstacle_dist, inflation_dist,
                                          weight_obstacle if weight_obstacle is not None else "(unchanged)"))


def ros_fresh_bringup(scene="outdoor", prefix="autotrial"):
    ensure_teb_plugin_installed()
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

    # Session 30R: widened from 30 attempts * 2s (60s) after live evidence of a truly cold
    # container's move_base crash-looping for ~14 minutes (312 respawns) before stabilizing --
    # the old 60s budget would have raised SystemExit on this exact box on this exact day. Each
    # ros_health_check() attempt now itself costs >=3s (PID-stability check above), so the loop
    # naturally spaces out; 300 attempts is a ~20+ minute ceiling, well past the worst case
    # actually observed, while still failing loudly (not hanging forever) if something is
    # genuinely broken rather than just slow to stabilize.
    start = time.time()
    for attempt in range(300):
        healthy, warnings = ros_health_check()
        if healthy:
            eprint("[run_trial] fresh ROS bringup healthy after {:.0f}s.".format(time.time() - start))
            return
        if attempt % 10 == 0:
            eprint("[run_trial] fresh ROS bringup not yet healthy after {:.0f}s ({}) -- move_base may "
                   "still be crash-looping post-launch (Session 30R: observed up to ~14min on a cold "
                   "container); continuing to wait.".format(time.time() - start, warnings))
        time.sleep(2)
    raise SystemExit("Fresh ROS bringup did not become healthy within ~{:.0f}s.".format(time.time() - start))


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


def revert_newly_dirtied_tracked_files(before, after, expected_dirty=None):
    """Restore any tracked file that became modified during this run, via `git show` (a read
    operation) piped to a plain file write -- never git add/commit/checkout/restore/stash, per
    this session's git-is-read-only rule. Never touches files that were already dirty in `before`
    (e.g. the pre-existing Microsoft-Rocketbox submodule / UserSettings churn).

    `expected_dirty` (Session 21 POLICY UNLOCK): paths the caller explicitly declared this run is
    SUPPOSED to change (e.g. an -executeMethod prefab fix via PrefabUtility/SerializedObject).
    Without this, an intentional, sanctioned edit is indistinguishable from the accidental
    side-effect drift this guard exists to catch (Session 1/3's ROSConnectionPrefab/Outdoor.unity
    incidents) and gets silently reverted right along with it -- caught live (Session 21 STEP 1):
    the phone_user container fix round-tripped through PrefabUtility.SaveAsPrefabAsset
    successfully, then this guard reverted it anyway, because it has no way to know a tracked-file
    change was requested rather than incidental. Still an explicit allowlist, not a blanket
    exemption -- every other newly-dirtied tracked file is reverted exactly as before."""
    newly_dirty = after - before
    if expected_dirty:
        skipped = newly_dirty & expected_dirty
        for rel_path in sorted(skipped):
            eprint("[run_trial] '{}' changed as expected this run (declared via expected_dirty) "
                   "-- NOT reverting.".format(rel_path))
        newly_dirty = newly_dirty - expected_dirty
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
        "scenario": args.scenario,
        "pedSpeedMultiplier": args.ped_speed,
        "pedMotion": args.ped_motion,
        "pedDistracted": args.ped_distracted,
        "hasPostEncounterGrace": args.post_encounter_grace is not None,
        "postEncounterGraceSec": args.post_encounter_grace if args.post_encounter_grace is not None else 8.0,
        "hasScaredRadiusOverride": args.scared_radius is not None,
        "scaredRadiusOverride": args.scared_radius if args.scared_radius is not None else 3.0,
        "hasSurpriseRadiusOverride": args.surprise_radius is not None,
        "surpriseRadiusOverride": args.surprise_radius if args.surprise_radius is not None else 4.0,
        "hasSurpriseCooldownOverride": args.surprise_cooldown is not None,
        "surpriseCooldownOverride": args.surprise_cooldown if args.surprise_cooldown is not None else 4.0,
        "hasPedReactDistOverride": args.ped_react_dist is not None,
        "pedReactDistOverride": args.ped_react_dist if args.ped_react_dist is not None else 2.0,
        "scaredReactDistOverride": args.scared_react_dist if args.scared_react_dist is not None else 0.0,
        # Session 41 TASK 3/4/5. All three default to inert, so no existing caller changes.
        "mixamoClip": args.mixamo_clip or "",
        "carriedBox": bool(args.carried_box),
        "hasCorridor": args.corridor_width is not None,
        "corridorWidthMeters": args.corridor_width if args.corridor_width is not None else 2.0,
        "corridorLengthMeters": args.corridor_length,
        "hasPedestrian2": args.pedestrian2_spawn is not None,
        "pedestrian2Appearance": "business_male_01",
        "pedestrian2Personality": "Indifferent",
        "pedestrian2SpawnPose": pose(*args.pedestrian2_spawn) if args.pedestrian2_spawn is not None else pose(0, 0, 0, 0),
        "hasPedestrian2GoalPose": args.pedestrian2_goal is not None,
        "pedestrian2GoalPose": pose(args.pedestrian2_goal[0], args.pedestrian2_goal[1], args.pedestrian2_goal[2], 0.0) if args.pedestrian2_goal is not None else pose(0, 0, 0, 0),
        "hasPedestrian3": args.pedestrian3_spawn is not None,
        "pedestrian3Appearance": "business_male_01",
        "pedestrian3Personality": "Indifferent",
        "pedestrian3SpawnPose": pose(*args.pedestrian3_spawn) if args.pedestrian3_spawn is not None else pose(0, 0, 0, 0),
        "hasPedestrian3GoalPose": args.pedestrian3_goal is not None,
        "pedestrian3GoalPose": pose(args.pedestrian3_goal[0], args.pedestrian3_goal[1], args.pedestrian3_goal[2], 0.0) if args.pedestrian3_goal is not None else pose(0, 0, 0, 0),
        "camera": {
            "povOffsetX": 0.0, "povOffsetY": 0.0, "povOffsetZ": 0.0,
            "yawSmoothTau": args.yaw_smooth_tau,
            "fixedPitchDeg": args.cam_pitch,
            "camHeightMeters": args.cam_height,
            "rigidMount": args.rigid_mount,
            "camYawMode": args.cam_yaw_mode,
            "camCourseWindowSec": args.cam_course_window,
            "camYawTauCourse": args.cam_yaw_tau,
            "camHfovDeg": args.cam_hfov,
        },
        "jpgQuality": args.jpg_quality,
    }
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


def guarded_unity_run(cmd, timeout, extra_env=None, expected_dirty=None):
    """The ONLY sanctioned way to launch Unity from this toolset, for a trial OR any ad hoc
    diagnostic. Wraps every launch in the dirty-tracked-file snapshot/revert guard (Session 1/3:
    raw launches that bypassed this left ROSConnectionPrefab.prefab / Outdoor.unity modified as a
    side effect, twice, precisely because they went around this check).

    `expected_dirty` (Session 21): an explicit set of repo-relative paths this specific run is
    authorized to change (e.g. a PrefabUtility fix script) -- everything else newly-dirtied is
    still reverted exactly as before. Omit for ordinary trial runs, which should never legitimately
    dirty a tracked file. Returns (returncode_or_None, timed_out: bool)."""
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
        revert_newly_dirtied_tracked_files(dirty_before, snapshot_modified_tracked_files(), expected_dirty=expected_dirty)
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
    # Session 30R: on a truly cold bringup (fresh container, cold Unity shader/asset cache -- not
    # exercised by any prior session, which all reused a long-lived warm container), the 15s
    # DiagCmdVel window can elapse before /move_base/set_parameters ever registers, and the CLI
    # `dynparam set` below then hangs past its own subprocess timeout instead of failing fast,
    # raising an uncaught TimeoutExpired that crashes the whole pipeline. Per this function's own
    # docstring (Session 6/8 evidence, lines above): landing oscillation_timeout=3.0 is NOT the
    # fix -- priming (the DiagCmdVel cycle just above) is -- so a failure/timeout here is cosmetic,
    # not a warmup failure. Made non-fatal rather than touching any sim_ws launch file.
    eprint("[run_trial] warmup: setting oscillation_timeout=3.0 live via dynparam (best-effort)...")
    try:
        dynparam_result = docker_exec("rosrun dynamic_reconfigure dynparam set /move_base oscillation_timeout 3.0", timeout=30)
        if dynparam_result.returncode != 0:
            eprint("[run_trial] warmup: dynparam set non-zero exit (harmless, see docstring) -- {}".format(
                dynparam_result.stderr.strip()[:200]))
        val = docker_exec("rosparam get /move_base/oscillation_timeout", timeout=10).stdout.strip()
        eprint("[run_trial] warmup: verified live oscillation_timeout={}".format(val))
    except subprocess.TimeoutExpired:
        eprint("[run_trial] warmup: dynparam set timed out (harmless, see docstring -- priming above "
               "is the real fix, this cosmetic set is not required) -- continuing.")
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


# Session 44 FIX C. The unmodulated social-force pace of the Rocketbox actors, measured repeatedly
# across sessions at ~1.29-1.30 m/s (Session 30R, Session 41, and Session 44's own probe: sustained
# walking 1.27-1.31 m/s). walkSpeedMultiplier scales the social-force velocity, so a desired target
# speed becomes target / this.
BASE_PED_SPEED_MPS = 1.0476

# Session 46 (S46-D section 3): Zone-A pedestrian walk-speed diversity.
#
# Measured across the 24-appearance screen, every Zone-A actor walked at 1.0522 m/s with a stdev of
# just 0.0387 -- about ONE FIFTH the spread of real adult walking speed (~0.2 m/s). And that narrow
# spread was not even designed: --ped-speed is 1.0 for every Zone-A appearance, and the residual
# came from SFAgent's always-on random RobotRepulsion perturbing trajectories.
#
# That matters because "pedestrian diversity" is a headline contribution, and the appearance axis
# was already established to supply VISUAL diversity only -- all 24 share one skeleton and one
# locomotion controller, and all travelled ~14.0 m identically.
#
# Calibration. NOTE (Session 54): the original note here read "measured speed is ~1.05x the
# multiplier", which conflated the multiplier with a m/s target. The multiplier is applied to
# BASE_PED_SPEED_MPS, so commanded speed = BASE_PED_SPEED_MPS * multiplier -- with the old base of
# 1.3 the intended 1.10 mean was really 1.365 and the intended 0.18 stdev was really 0.221. The
# base is now 1.0476, which makes the numbers below mean what they say: mean 1.0476*1.05 = 1.100,
# stdev 1.0476*0.17 = 0.178.
# Mean is held near 1.10 deliberately -- the robot caps at 0.6 m/s, so raising pedestrian speed
# would compress the encounter window and change the encounter geometry itself.
ZONE_A_SPEED_MULT_MEAN = 1.05
ZONE_A_SPEED_MULT_STDEV = 0.17
# ~+/-2.35 sigma. Truncation barely moves the stdev but keeps animator.speed far from both clamps
# (0.05 / 3.0): this range maps to roughly 0.61-1.17.
ZONE_A_SPEED_MULT_MIN = 0.65
ZONE_A_SPEED_MULT_MAX = 1.45


def zone_a_speed_multiplier(seed):
    """Deterministic per-trial walk-speed multiplier for a Zone-A pedestrian.

    Seeded rather than free-running so a trial is reproducible from its manifest alone -- the
    multiplier drawn is recorded there, and re-running with the same seed reproduces it.

    Deliberately NOT applied to Mixamo clips or Zone-B specials: those carry their own designed
    paces (Old_Man_Walk 0.7 m/s, Drunk_Walk 0.6, wheelchair/scooter/cyclist their own), and
    randomising on top would make an "old man" occasionally stride at 1.5 m/s.
    """
    rng = random.Random(seed)
    m = rng.gauss(ZONE_A_SPEED_MULT_MEAN, ZONE_A_SPEED_MULT_STDEV)
    return max(ZONE_A_SPEED_MULT_MIN, min(ZONE_A_SPEED_MULT_MAX, m))

CLIP_SPEEDS_PATH = Path(__file__).resolve().parent.parent / "Assets/PedestrianAssets/Mixamo/clip_speeds.json"


def mixamo_target_speed(clip_name):
    """targetSpeedMps for a --mixamo-clip, or None if absent/unset.

    Deliberately the same file S41MixamoClipApplier reads authoredSpeedMps from: FIX C's whole
    point is that the authored pace (measured) and the target pace (designed) are two different
    quantities that must not live in two places. `null` means "no override", which is different
    from 0 ("should not travel") and must not collapse to it.
    """
    try:
        data = json.loads(CLIP_SPEEDS_PATH.read_text())
    except Exception as e:
        eprint("[run_trial] could not read {} ({}) -- no per-clip target speed applied.".format(
            CLIP_SPEEDS_PATH, e))
        return None
    for entry in data.get("clips", []):
        if entry.get("clip") == clip_name:
            return entry.get("targetSpeedMps")
    return None


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


def post_process(out_dir, fps, near_dist, keep_full, near_clip_min_sec=trial_lib.DEFAULT_NEAR_CLIP_MIN_SEC,
                  clip_mode="threshold", encounter_half_window=5.0, dense_encounter=False,
                  near_pre=trial_lib.DEFAULT_APPROACH_LEAD_SEC, near_post=5.0):
    """POV only (Session 10, D5 -- no chase/third-person camera). Builds pov_full.mp4, needed both
    as the Round 4 (Step 4) primary deliverable AND for overlay.py to burn+re-cut its own *_ov
    near clips from (overlay always re-derives spans from frames.csv rather than trusting these
    clip boundaries). Round 4: pov_full.mp4/pov_full_ov.mp4 are kept permanently now (output
    format v3) -- the near clips (pov_near_NN[_ov].mp4) are additional, VLM-prefilter material,
    not a replacement for the full video.

    Session 31 FIX 2: clip_mode="threshold" (default) preserves the original find_near_spans()
    behavior byte-for-byte -- no existing caller's output changes. clip_mode="centered" instead
    uses trial_lib.find_encounter_centered_span() -- a single [t_min-half, t_min+half] window
    anchored on the trial's own minimum dist_to_pedestrian frame, with no threshold/grace/tail
    logic at all. This is what session31_review's delivered clips use: user feedback was that
    delivered clips still carried a long solo-robot-driving-to-goal tail even after growth/merge
    tuning, and the fix is to stop trying to grow a threshold-crossing span and just cut a fixed
    window around the encounter's own climax instead."""
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

    approach_meta = None
    if clip_mode == "approach":
        # Session 36 FIX 1: re-anchors on interaction_start (first approach-radius crossing), not
        # just t_min -- see trial_lib.find_approach_centered_span()'s own docstring for the full
        # rationale (fixes assertive/dyad/ped-count's pre-encounter sequence getting cut off).
        result = trial_lib.find_approach_centered_span(
            out_dir / "frames.csv", lead_sec=near_pre, post_t_min_sec=near_post)
        if result is not None:
            start, end, seconds_of_approach_shown, interaction_start = result
            spans = [(start, end)]
            approach_meta = {"seconds_of_approach_shown": seconds_of_approach_shown,
                              "interaction_start": interaction_start}
        else:
            spans = []
    elif clip_mode == "centered":
        span = trial_lib.find_encounter_centered_span(out_dir / "frames.csv", half_window_sec=encounter_half_window)
        spans = [span] if span is not None else []
    else:
        spans = trial_lib.find_near_spans(out_dir / "frames.csv", near_dist, min_duration_sec=near_clip_min_sec)

    near_clips = []
    for i, (start, end) in enumerate(spans):
        pov_clip = out_dir / "pov_near_{:02d}.mp4".format(i)
        trial_lib.cut_clip(pov_full, pov_clip, start, end)
        near_clips.append({"index": i, "start": start, "end": end, "pov": pov_clip.name})
        if approach_meta is not None:
            near_clips[-1].update(approach_meta)

    # Session 43: export the VLM-teacher format (video/ + vlm_eval/) HERE, in the last moment the
    # raw frames still exist. Order is load-bearing: the exporter reads pov/*.jpg at native
    # 1280x720, which is both higher quality than re-decoding the H.264 we just muxed and immune to
    # burned-in telemetry by construction (the overlay is applied later, only to *_ov.mp4). Keeping
    # --keep-full on instead would cost ~180 MB/trial against ~13 MB for the export.
    #
    # Deliberately runs BEFORE any gate has been evaluated, so a trial that later fails a gate still
    # has a complete vlm_eval/ directory. A rejected trial is data, not garbage -- Session 40's
    # "39 of 40 have no meta.json" episode taught this project that a silently absent output reads
    # downstream as a bad result rather than as an absent one. The gate verdicts themselves land in
    # meta.json (augment_trial_meta_with_gate) and are what downstream should filter on.
    vlm_eval = None
    try:
        vlm_eval = vlm_eval_export.export(out_dir, dense_encounter=dense_encounter, quiet=True)
        eprint("[run_trial] vlm_eval export: {} frame(s), {} event frame(s){}".format(
            vlm_eval.get("framesWritten"), vlm_eval.get("eventFrames"),
            "" if vlm_eval.get("ok") else " -- FAILED: {}".format(vlm_eval.get("reason", "see result"))))
    except Exception as e:  # never let the export break a trial that otherwise succeeded
        vlm_eval = {"ok": False, "reason": "{}: {}".format(type(e).__name__, e)}
        eprint("[run_trial] vlm_eval export FAILED (non-fatal): {}".format(vlm_eval["reason"]))

    if not keep_full:
        shutil.rmtree(pov_dir, ignore_errors=True)

    return {"ok": True, "near_clips": near_clips, "pov_full": pov_full.name, "vlm_eval": vlm_eval}


def run_content_gate(out_dir, near_clips):
    """THE PERMANENT GATE (Round 3), wired into acceptance forever -- not a one-off check. For
    every near clip: (a) samples >=8 frames and requires luminance std AND edge density above
    scene thresholds (trial_lib.check_clip_content -- any uniform gray/black sample fails the
    clip), and (b) writes an 8-frame contact-sheet PNG into out_dir so a human reviewer can QA the
    whole clip's scene content in one glance instead of scrubbing every video (surfaced in
    index.html by overlay.py's generate_index_html()). Mutates each near_clips entry in place with
    its own gate result + contact sheet filename.

    Session 17 (Step 4 forensics): ALSO runs the same check against pov_full.mp4 directly, not
    just near clips -- found empirically while eyeballing battery v5's contact sheets under the
    new 25m geometry: a trial with zero near-spans (min_dist never dropped under --near-dist, now
    common with more passing room at 25m) had near_clips=[], so this gate's own loop never ran at
    all and trivially "passed" with an empty detail string -- while the SAME trial's full-video
    contact sheet plainly showed two uniform-black frames the (near-clip-only) gate never sampled.
    pov_full.mp4 is always present and always the primary deliverable (Round 4 output format v3);
    checking it unconditionally, regardless of whether any near clip exists, closes that gap.
    Returns (all_ok: bool, detail: str)."""
    all_ok = True
    details = []

    full_path = out_dir / "pov_full.mp4"
    if full_path.exists():
        full_ok, full_detail, full_samples = trial_lib.check_clip_content(full_path)
        all_ok = all_ok and full_ok
        details.append("pov_full.mp4: {}".format(full_detail))
    else:
        all_ok = False
        details.append("pov_full.mp4: MISSING")

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


def augment_trial_meta_with_gate(out_dir, gate_ok, near_clips, gate_detail=None,
                                  aspect_ok=None, aspect_detail=None,
                                  approach_ok=None, approach_detail=None,
                                  trigger_ok=None, trigger_detail=None,
                                  overlay_ok=None, overlay_detail=None,
                                  manifest_ok=None, manifest_detail=None,
                                  spin_phases=None, full_contact_sheet=None,
                                  min_dist=None, profile=None, corridor_width=None,
                                  vlm_eval=None, ped_motion=None, seed=None, zone_a_mult=None,
                                  sample_role=None):
    """Records every permanent gate's verdict (Round 3's content gate, Round 4's aspect + approach-
    geometry gates) and every near clip's final (post-growth/merge) window + contact sheet into
    meta.json, so a trial's pass/fail is inspectable without re-running anything."""
    meta_path = out_dir / "meta.json"
    if not meta_path.exists():
        return
    data = json.loads(meta_path.read_text())
    data["contentGateOk"] = gate_ok
    # Session 43: the content gate was the ONE gate whose verdict was recorded without its reason
    # -- every other gate below writes an ok/detail pair, this one wrote only the bool. Session 41's
    # w1.2_02 is the case that exposed it: contentGateOk=False with no contentGateDetail field at
    # all, so the only failing corridor trial in the sweep could not be explained from its own
    # meta.json. Unconditional, not `if gate_detail is not None` -- an absent reason is exactly the
    # state this fixes, so a None here must be recorded as an explicit null, never as a missing key.
    data["contentGateDetail"] = gate_detail
    if aspect_ok is not None:
        data["aspectGateOk"] = aspect_ok
        data["aspectGateDetail"] = aspect_detail
    if approach_ok is not None:
        data["approachGateOk"] = approach_ok
        data["approachGateDetail"] = approach_detail
    if trigger_ok is not None:
        data["triggerSpeedGateOk"] = trigger_ok
        data["triggerSpeedGateDetail"] = trigger_detail
    if overlay_ok is not None:
        data["overlayOk"] = overlay_ok
        data["overlayDetail"] = overlay_detail
    if manifest_ok is not None:
        data["fileManifestGateOk"] = manifest_ok
        data["fileManifestGateDetail"] = manifest_detail
    if spin_phases is not None:
        data["spinPhases"] = spin_phases
    if full_contact_sheet is not None:
        data["fullContactSheet"] = full_contact_sheet
    # Session 41 TASK 5: min_dist recorded as a LABEL, never as a gate. Written for every profile,
    # not just corridor, so one manifest schema covers the whole dataset.
    if min_dist is not None:
        data["minDistMeters"] = min_dist
        data["safetyLabel"] = safety_label_for(min_dist)
        data["safetyLabelThresholds"] = {"safe": SAFETY_LABEL_SAFE_M, "breach": SAFETY_LABEL_BREACH_M}
    # Session 43: the VLM-teacher export is written unconditionally, including for trials that go on
    # to fail a gate -- so downstream needs to be able to tell "passed" from "did not pass but the
    # data is complete" without re-deriving it. Roll the gate verdicts up next to the export result,
    # in the same file, rather than making a consumer re-check six separate booleans and guess what
    # a missing one means. `gatesAllOk` is None if any gate did not report at all.
    if vlm_eval is not None:
        verdicts = {"content": gate_ok, "aspect": aspect_ok, "approach": approach_ok,
                    "triggerSpeed": trigger_ok, "overlay": overlay_ok, "fileManifest": manifest_ok}
        reported = [v for v in verdicts.values() if v is not None]
        data["vlmEval"] = {
            "exported": bool(vlm_eval.get("ok")),
            "framesWritten": vlm_eval.get("framesWritten"),
            "statesRows": vlm_eval.get("statesRows"),
            "eventFrames": vlm_eval.get("eventFrames"),
            "denseEncounter": vlm_eval.get("denseEncounter"),
            "reason": vlm_eval.get("reason"),
            "gateVerdicts": verdicts,
            "gatesAllOk": all(reported) if len(reported) == len(verdicts) else None,
        }
    # Session 46 (S46-D 1). `scenario` names the GEOMETRY (robot approaching the pedestrian
    # head-on), which is a separate fact from whether that pedestrian walks or holds station.
    # Without this field, filtering on scenario == "headon" silently mixes stationary actors into a
    # set a consumer expects to be walking -- a data-quality problem that leaves no trace.
    if ped_motion is not None:
        data["pedestrian_motion"] = "stationary" if ped_motion == "standing" else "walking"
    # Session 46 (S46-D 3): the sampled Zone-A walk-speed multiplier, plus the seed that produced
    # it, so the trial reproduces from its own manifest. None for Mixamo clips and Zone-B specials,
    # which keep their designed paces and are never randomised.
    if seed is not None:
        data["seed"] = seed
    data["zoneASpeedMultiplier"] = zone_a_mult
    if sample_role is not None:
        data["sample_role"] = sample_role
    if profile is not None:
        data["profile"] = profile
    if corridor_width is not None:
        data["corridorWidthMeters"] = corridor_width
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
    p.add_argument("--ped-speed", type=float, default=None, metavar="MULT",
                   help="Session 28 PART 3b: pedestrian walk-speed multiplier, reusing the "
                        "existing PedestrianModulator.walkSpeedMultiplier mechanism (previously "
                        "only reachable via child-appearance presets, not a direct flag). Typical "
                        "range 0.5-1.6 -- e.g. ~0.6-0.7 for an 'elderly' gait, ~1.3-1.5 for "
                        "'hurried'. Zone A only (Zone B special-character containers lock their "
                        "own behavior, ignoring this like --personality). Default: 1.0, UNLESS "
                        "--scenario overtake/overtaken supplies its own default (0.5/1.5) when "
                        "this flag is not explicitly given.")
    p.add_argument("--ped-motion", choices=["normal", "standing"], default="normal",
                   help="Session 28 PART 3a: 'standing' freezes the pedestrian at its spawn pose "
                        "PERMANENTLY (SLATE release still fires the capture-start trigger, but "
                        "the pedestrian's own destination stays spawnPos instead of the real "
                        "goal) -- still a live costmap obstacle, and personality-driven upper-"
                        "body/gaze animation (via PedestrianModulator, if a personality/--ped-"
                        "speed/--ped-distracted forces one) continues normally. Zone A only.")
    p.add_argument("--ped-distracted", action="store_true",
                   help="Session 28 PART 3c: phone/texting upper-body distraction layer "
                        "(generalizes phone_user's own animation layer to any Zone A appearance) "
                        "+ a modulator flag that delays/suppresses the pedestrian's own reaction "
                        "to the robot. Indifferent x --ped-distracted is the classic unaware-"
                        "pedestrian case. Zone A only.")
    p.add_argument("--duration", type=float, default=90.0)
    p.add_argument("--fps", type=int, default=15)
    p.add_argument("--near-dist", type=float, default=3.0)
    p.add_argument("--near-clip-min-sec", type=float, default=trial_lib.DEFAULT_NEAR_CLIP_MIN_SEC,
                   help="Round 3: every near clip is grown symmetrically around its own "
                        "minimum-distance moment (bounded by trial length) until it reaches at "
                        "least this many seconds; overlapping spans after growth are merged. "
                        "Ignored when --clip-mode centered.")
    p.add_argument("--clip-mode", choices=["threshold", "centered", "approach"], default="approach",
                   help="Session 31 FIX 2: 'threshold' (default) is the original find_near_spans() "
                        "behavior, unchanged -- no existing caller's clips change. 'centered' cuts "
                        "a single [t_min-half, t_min+half] window anchored on the trial's own "
                        "minimum dist_to_pedestrian frame (--encounter-half-window), with no "
                        "solo-navigation tail -- used for session31_review's delivered clips. "
                        "Session 36 FIX 1: 'approach' instead anchors clip START on the pedestrian's "
                        "own approach-radius crossing (trial_lib.find_approach_centered_span(), "
                        "default 12m radius, 8s minimum pre-encounter segment enforced) so configs "
                        "with a pre-encounter sequence (assertive's walk-stop-gesture, dyad/ped-count's "
                        "buildup) show the approach instead of opening mid-action -- used for "
                        "session36_review's delivered clips.")
    p.add_argument("--encounter-half-window", type=float, default=5.0,
                   help="Session 31 FIX 2: half-width (seconds) of the --clip-mode centered "
                        "window around t_min. Default 5.0 -> a ~10s delivered clip.")
    p.add_argument("--out", default=None, help="output directory (default: trial_outputs/<appearance>_<personality>_<timestamp>)")
    p.add_argument("--windowed", action="store_true", help="drop -batchmode (black-frame fallback)")
    # Session 44 TASK 2. The near clip is the VLM's material, so a clip that opens after the
    # pedestrian has already reacted contains the outcome but not the behaviour -- there is nothing
    # left to judge. Measured on the Session 43 demo, the old t_min-anchored window cut the first
    # 5.25s of the assertive trial and the first 26.5s of every standing-clip trial, and produced NO
    # near clip at all for `scared` (its min_dist of 4.89m never crossed the 3.0m threshold).
    #
    # These name the two edges of the approach-anchored window directly.
    p.add_argument("--near-pre", type=float, default=trial_lib.DEFAULT_APPROACH_LEAD_SEC,
                   help="seconds of lead-in before the pedestrian first crosses the approach "
                        "radius (--clip-mode approach only). Default {}s; the window is additionally "
                        "pulled back so at least {}s of approach always precedes the closest point."
                        .format(trial_lib.DEFAULT_APPROACH_LEAD_SEC, trial_lib.DEFAULT_MIN_PREROLL_SEC))
    p.add_argument("--near-post", type=float, default=5.0,
                   help="seconds retained after the closest-approach frame (--clip-mode approach "
                        "only). Default 5.0s.")
    p.add_argument("--sample-role", choices=["normal", "negative_generator"], default="normal",
                   help="Session 46: negative_generator marks a configuration DESIGNED to produce "
                        "close passes and safety-line crossings. Such a trial's min_dist is not a "
                        "safety result and the 0.5m operational bar does not apply to it. Recorded "
                        "in meta.json, INDEX.md and DATASHEET.md -- v5 shipped a 0.279m dyad breach "
                        "that circulated as safety data precisely because no such marker existed.")
    p.add_argument("--seed", type=int, default=None, metavar="N",
                   help="Session 46: seed for per-trial Zone-A walk-speed sampling. Recorded in "
                        "meta.json so the trial reproduces from its own manifest. Omitted means no "
                        "speed randomisation (the pre-Session-46 constant 1.0).")
    p.add_argument("--dense-encounter", action="store_true",
                   help="Session 43: additionally infill the encounter span of vlm_eval/frames at "
                        "5 Hz (frame_NNNN_dK.png). OFF by default -- the agreed 1 Hz sequence is "
                        "never altered by this flag, the extra frames are only appended, so turning "
                        "it on cannot invalidate an existing consumer")
    p.add_argument("--keep-full", action="store_true",
                   help="keep the raw per-frame JPG directory (pov/) and config.json after "
                        "assembly; NOT related to pov_full.mp4/pov_full_ov.mp4, which are always "
                        "kept as of Round 4's output format v3 regardless of this flag")
    p.add_argument("--fresh-ros", action="store_true")
    p.add_argument("--reused-ros", dest="reused_ros", action="store_true", default=True,
                   help="run inter-trial ROS hygiene (cancel+clear costmaps) before launching -- default on")
    p.add_argument("--no-reused-ros-hygiene", dest="reused_ros", action="store_false")
    p.add_argument("--scenario", choices=list(SCENARIOS), default="headon",
                   help="Session 28 PART 2: pure-geometry encounter preset, computed from the "
                        "robot start->goal bearing (no new assets). 'headon' (default): ped ahead "
                        "on the robot's path, facing back, genuine pass-through -- the pipeline's "
                        "original behavior, unchanged. ('crossing' was removed in Session 33 -- "
                        "see REPORT.md/HOWARD_HANDOFF.md, a camera-framing limitation, not a "
                        "geometry bug.) 'overtake': ped ahead, same direction as the robot, "
                        "default --ped-speed 0.5 (robot catches up and passes). 'overtaken': ped "
                        "ahead on the SAME spawn point as overtake (a literal starts-BEHIND spawn "
                        "is geometrically incompatible with the SLATE trigger -- see the "
                        "function's own docstring), default --ped-speed 1.5 -- the mirror "
                        "dynamic: the faster pedestrian pulls further ahead/away rather than the "
                        "robot passing it. Only takes effect when --spawn is not "
                        "explicitly given. Near-field encounter geometry differs by scenario -- "
                        "SIF is reported per scenario, only far-field is gated (see REPORT.md "
                        "Session 28).")
    p.add_argument("--dyad", action="store_true",
                   help="Session 35 BLOCK 4 (FIX 8): adds a second pedestrian (business_male_01, "
                        "Indifferent) walking in parallel with the primary one, offset sideways "
                        "by DYAD_LATERAL_OFFSET_M (0.9m -- shoulder-width-plus) along the corridor's "
                        "perpendicular, same facing/destination direction -- a walking PAIR, not "
                        "two independent walkers. Combine with --ped-count 3 to add a third.")
    p.add_argument("--ped-count", type=int, choices=[1, 2, 3], default=1,
                   help="Session 35 BLOCK 4 (FIX 9): total pedestrian count in the scene. 1 "
                        "(default): unchanged, single-pedestrian trials exactly as every prior "
                        "session. 2: same as --dyad if --dyad wasn't already given. 3: adds a "
                        "THIRD pedestrian beyond --dyad's pair, offset further out "
                        "(PED_COUNT3_LATERAL_OFFSET_M, 1.8m) on the opposite side -- a small group "
                        "of 3, for scene diversity beyond a single walker or a pair.")
    p.add_argument("--profile", choices=list(PROFILES), default="arc",
                   help="Session 30R (Howard priority #1): 'arc' (default): today's 25m SLATE "
                        "trigger distance (Session 17), unchanged -- built for a full approach "
                        "story arc, not for scoring (interaction is small/distant in frame). "
                        "'scoring': 8m trigger (Session 16-and-earlier's own old default, see "
                        "--ped-distance's help) -- shorter approach, tighter framing, interaction "
                        "fills more of the frame; for the Howard scoring batch. Only sets the "
                        "--ped-distance DEFAULT -- an explicit --ped-distance always wins.")
    p.add_argument("--ped-distance", type=float, default=None,
                   help="Distance in meters from the robot's start position, measured along the "
                        "robot start->goal bearing, that defines the trial's dist0 target. Since "
                        "Session 14 (SLATE v2) this is ALSO the live trigger threshold: "
                        "TrialController.PollForTrigger releases the pedestrian and starts capture "
                        "the instant robot<->pedestrian ground-plane distance first drops to this "
                        "value or below (config.triggerDistanceMeters). Only takes effect when "
                        "--spawn is not explicitly given. Default resolved from --profile (Session "
                        "30R): 25.0 for 'arc' (Session 17) -- was 8.0 through Session 16 -- or 8.0 "
                        "for 'scoring'. An explicit --ped-distance always overrides the profile.")
    p.add_argument("--mixamo-clip", type=str, default=None, metavar="NAME",
                   help="Session 41 TASK 3: play one of the generated Mixamo behaviour clips on "
                        "the pedestrian instead of its normal locomotion controller. NAME is a "
                        "controller under Assets/PedestrianAssets/Mixamo/Resources/, e.g. "
                        "'Old_Man_Walk', 'Drunk_Walk', 'Running', 'carry_and_walk', "
                        "'Pacing_And_Talking_On_A_Phone', 'Sitting', 'Standing_Arguing', "
                        "'Talking_standing', 'Stroke_Shaking_Head' (spaces in the source FBX name "
                        "become underscores). The nine source FBXs are Mixamo animation-only "
                        "exports with no mesh, so the character on screen stays the ordinary "
                        "Rocketbox avatar and Unity's Humanoid retargeting supplies the motion. "
                        "Zone A only.")
    p.add_argument("--carried-box", action="store_true",
                   help="Session 41 TASK 4: attach a carried cardboard box primitive "
                        "(0.45x0.35x0.35m, matte #8B6F47, no collider) at the pedestrian's hands. "
                        "Intended with --mixamo-clip carry_and_walk. NOTE the box rides at ~1.1m "
                        "while the robot's sensor plane is 0.32m, so the robot cannot see it -- "
                        "that is a deliberately retained perception case, not a bug (see "
                        "Assets/PedestrianAssets/Mixamo/README.md).")
    p.add_argument("--corridor-width", type=float, default=None, metavar="METERS",
                   help="Session 41 TASK 5: spawn two parallel walls this far apart, centred on "
                        "the robot/pedestrian encounter point along the robot's start->goal "
                        "bearing, to force a controlled narrow pass. The ticket's sweep is "
                        "3.0 / 2.0 / 1.5 / 1.2. Omit for no corridor (default).")
    p.add_argument("--corridor-length", type=float, default=12.0, metavar="METERS",
                   help="Session 41 TASK 5: corridor length along the travel bearing (default 12).")
    p.add_argument("--slate-margin", type=float, default=4.0,
                   help="Session 14 (SLATE v2): extra distance beyond --ped-distance at which the "
                        "pedestrian actually spawns, frozen (default 4.0 -> ~29m from robot start "
                        "at the current default --ped-distance=25.0, Session 17). The robot's "
                        "goal is published early (pre-roll) so it reaches a normal cruise while "
                        "still further than --ped-distance away; only takes effect when --spawn "
                        "is not explicitly given.")
    p.add_argument("--scared-radius", type=float, default=None, metavar="METERS",
                   help="Session 31 FIX 5(b): override PedestrianModulator.scaredRadius (default "
                        "3.0m) -- the distance at which Scared's flee reaction itself starts. "
                        "Distinct from --ped-distance/--profile (the general SLATE release/"
                        "avoidance-onset distance). Zone A only.")
    p.add_argument("--surprise-radius", type=float, default=None, metavar="METERS",
                   help="Session 31 FIX 5(b): override PedestrianModulator.surpriseRadius (default "
                        "4.0m) -- the distance at which Surprised's freeze reaction itself starts. "
                        "Distinct from --ped-distance/--profile. Zone A only.")
    p.add_argument("--surprise-cooldown", type=float, default=None, metavar="SECONDS",
                   help="Session 33 FIX 3: override PedestrianModulator.cooldownDuration (default "
                        "4.0s) -- prevents a second, spurious reaction trigger firing during "
                        "post-pass separation noise. Zone A only.")
    p.add_argument("--ped-react-dist", type=float, default=None, metavar="METERS",
                   help="Session 34 FIX 1: distance-gated robot repulsion (S34PedestrianReactDist"
                        "Gate) -- zero robot-response beyond METERS, personality-scaled response "
                        "inside it, for every non-Assertive personality (Assertive is unaffected, "
                        "already permanently zeroed via ModulateAssertive()). None (default) means "
                        "no gate at all -- SFAgent's own always-on random 0.5-1.0 RobotRepulsion, "
                        "the pre-Session-34 behavior. Zone A only.")
    p.add_argument("--scared-react-dist", type=float, default=None, metavar="METERS",
                   help="Session 36 FIX 3: Scared-specific override on top of --ped-react-dist -- "
                        "the shared gate (1.5m under --profile scoring) made Scared react too LATE "
                        "since it shared the same threshold as indifferent/surprised. Larger than "
                        "the shared gate so Scared becomes the EARLIEST responder. None (default) "
                        "falls through to the shared --ped-react-dist value like every other "
                        "personality. Ignored for personalities other than Scared.")
    p.add_argument("--post-encounter-grace", type=float, default=None, metavar="SECONDS",
                   help="Session 15: end capture SECONDS after dist_to_pedestrian first "
                        "re-exceeds --ped-distance following a genuine pass (i.e. the encounter "
                        "is over and the pedestrian is moving away again) -- root-caused fix for "
                        "goal_reached almost never firing (the configured far corridor goal is "
                        "structurally unreachable within any trial's duration budget, not a "
                        "0.5m-tolerance bug; see REPORT.md Session 15). --duration remains the "
                        "hard cap regardless. Off by default (None) for backward compatibility; "
                        "pass e.g. 8.0 to stop filming shortly after the encounter concludes "
                        "instead of the full --duration of post-encounter driving.")
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
                        "only smoothed axis. Position is always rigid to the mount (X/Z) with an "
                        "absolute-height Y (see --cam-height); pitch/roll are constants (see "
                        "--cam-pitch). Default (0.5) is empirically re-tuned this round -- see "
                        "REPORT.md Round 3 Step 2. 0 = no smoothing (--rigid-mount).")
    p.add_argument("--cam-pitch", type=float, default=0.0,
                   help="Session 17 (Step 3, real-A1 pose): constant camera pitch in degrees "
                        "(positive = up, negative = down). Default 0.0 (LEVEL) -- retires Round "
                        "3's arbitrary -5 default; the cited real A1/RealSense D435i mount faces "
                        "level, not downtilted. Never derived from any transform -- was named "
                        "--fixed-pitch-deg through Round 3/Session 16.")
    p.add_argument("--cam-height", type=float, default=0.32,
                   help="Session 17 (Step 3, real-A1 pose): camera height in meters, ABSOLUTE "
                        "above the ground directly under the robot (verified by a downward "
                        "raycast at rig build time, not a blind local offset from the existing "
                        "first-person camera mount -- see AutoTrialBootstrap.BuildPovCamera). "
                        "Default 0.32 -- cited: the A1 stands ~0.40m tall, RealSense D435i in the "
                        "front head puts the lens at ~0.30-0.32m above ground.")
    p.add_argument("--rigid-mount", action="store_true",
                   help="Round 3 (D2): force yaw smoothing tau to 0 (raw chassis yaw every frame, "
                        "no filtering) for direct before/after comparison. Position was already "
                        "always rigid; pitch/roll are always constants regardless of this flag. "
                        "Chassis-mode only (--cam-yaw-mode chassis) -- no effect in course mode.")
    p.add_argument("--cam-yaw-mode", choices=["course", "chassis"], default="course",
                   help="Session 26 (course-locked camera, standing spec): POV camera yaw TARGET "
                        "source. 'course' (default): direction of travel, estimated from a "
                        "trailing position window (--cam-course-window) and low-passed "
                        "(--cam-yaw-tau); below 0.15 m/s the target holds instead of chasing "
                        "near-zero-displacement noise. 'chassis': the pre-Session-26 behavior "
                        "(robot body heading, --yaw-smooth-tau/--rigid-mount apply). Position/"
                        "pitch/roll are unaffected either way.")
    p.add_argument("--cam-course-window", type=float, default=8.0,
                   help="Session 26/27: trailing window (seconds) used to estimate direction of "
                        "travel for --cam-yaw-mode course. Default promoted (Session 27) from "
                        "S26's spec default (1.5s, which badly missed the SIF/swing bar -- a "
                        "1.5s low-pass cannot damp TEB's own ~9.6s-period residual weave) to "
                        "8.0s, period-matched -- S26's own N=3 confirmation: far-field (>15m) SIF "
                        # '%%' not '%': argparse runs every help string through %-expansion, so a
                        # bare '%' here raised ValueError and broke --help for the WHOLE parser.
                        # Pre-existing (Session 26/27); found in Session 41 while adding flags.
                        "98.45%%, landmark swing mean 2.74deg (see REPORT.md Session 26/27).")
    p.add_argument("--cam-yaw-tau", type=float, default=8.0,
                   help="Session 26/27: low-pass time constant (seconds) applied to the course-"
                        "direction yaw target for --cam-yaw-mode course -- separate from and not "
                        "shared with --yaw-smooth-tau (chassis mode's own tau). Default promoted "
                        "to 8.0s alongside --cam-course-window, same rationale.")
    p.add_argument("--cam-hfov", type=float, default=69.0,
                   help="Session 27 (FOV truth): POV camera horizontal field of view in degrees. "
                        "Default 69.0 -- the real A1's RealSense D435i own RGB horizontal FOV "
                        "(vertical 42deg; pass 87 for the D435i's depth-stream FOV, vertical "
                        "58deg). Prior sessions (S12-S26) inherited whatever vertical FOV the "
                        "legacy first-person camera happened to carry (22.0deg -> 38.1267deg "
                        "horizontal at this project's 16:9 capture aspect, per "
                        "S24CameraFovProbe) -- narrower than the real sensor, never audited "
                        "before this session. AutoTrialBootstrap.BuildPovCamera converts this to "
                        "Unity's own vertical Camera.fieldOfView using the actual capture aspect "
                        "-- sim-real fidelity, not a metric workaround.")
    p.add_argument("--jpg-quality", type=int, default=85)
    p.add_argument("--trial-position", type=int, default=1,
                   help="1-based position of this trial within its sequential run on one shared "
                        "bringup -- recorded into meta.json, never inferred (default: 1)")
    p.add_argument("--compile-check", action="store_true",
                   help="diagnostic mode: guarded -batchmode -quit launch to force recompilation, then exit")
    p.add_argument("--diag-cmdvel", type=float, metavar="SECONDS", default=None,
                   help="diagnostic mode: guarded launch of DiagCmdVel for SECONDS, then exit")
    p.add_argument("--exec-editor-method", type=str, metavar="FQN", default=None,
                   help="diagnostic mode (Session 21): guarded -executeMethod launch of an "
                        "arbitrary fully-qualified static method, then exit. The ONLY sanctioned "
                        "way to run one-off Editor-script prefab fixes (PrefabUtility/"
                        "AssetDatabase/SerializedObject) -- routes through guarded_unity_run's "
                        "dirty-tracked-file snapshot/revert guard like every other launch. The "
                        "target method owns its own EditorApplication.Exit() call.")
    p.add_argument("--allow-dirty", action="append", default=[], metavar="REPO_RELATIVE_PATH",
                   help="repeatable (Session 21): with --exec-editor-method, declares a "
                        "repo-relative path this run is explicitly authorized to leave modified "
                        "-- exempts it from the dirty-tracked-file revert guard. Every other "
                        "tracked-file change is still reverted. Named per-call, matching this "
                        "project's explicit-path-only convention -- never a blanket exemption.")
    p.add_argument("--warmup", dest="warmup", action="store_true", default=True,
                   help="prime a fresh ROS session with a real nav cycle before the batch "
                        "(Session 8 operational recipe) -- default on")
    p.add_argument("--no-warmup", dest="warmup", action="store_false")
    p.add_argument("--overlay", dest="overlay", action="store_true", default=True,
                   help="burn a per-frame telemetry overlay onto this trial's videos "
                        "(tools/overlay.py, Session 9) -- default on")
    p.add_argument("--no-overlay", dest="overlay", action="store_false")
    args = p.parse_args()

    # Session 30R: resolve --ped-distance's profile-dependent default. Must happen before any use
    # of args.ped_distance below (spawn_distance computation, dist0 gate target, etc.) -- an
    # explicit --ped-distance (not None) always wins over the profile.
    if args.ped_distance is None:
        args.ped_distance = PROFILE_PED_DISTANCE[args.profile]
        # Session 31 FIX 3: speed-tiered spawn/trigger distance, --profile scoring only. User
        # feedback: under scoring's flat 8m trigger, fast actors (scooter/cyclist) close the
        # remaining gap almost instantly (measured min-dist frame at t=1.6-2.0s post-release this
        # session -- ~2-3s of visible approach, "not enough time to see them"), while slow actors
        # (wheelchair/white_cane) get comparatively more dwell "for free". Fix: scale the trigger
        # distance by TARGET_DWELL_SEC * actor_speed, using this session's own FIX-4-verified
        # live speeds (not the raw multiplier), so every actor gets a comparable number of seconds
        # from release to close approach. TARGET_DWELL_SEC=6.2 is calibrated so business_male_01's
        # own ~1.29 m/s walking pace reproduces ~8.0m -- i.e. today's human scoring baseline is the
        # formula's fixed point, not a new number; only the four appearances below (which have a
        # verified live speed to plug in) move off it. Zone A/generic appearances (no verified
        # per-character speed) and --profile arc are both untouched -- this is scoring-profile-only
        # and Zone-B-only by construction (SCORING_TIER_SPEED_MPS.get() falls through to the flat
        # 8.0m default for anything not in the dict).
        SCORING_TIER_TARGET_DWELL_SEC = 6.2
        SCORING_TIER_SPEED_MPS = {
            "scooter_user": 3.515,
            "cyclist": 4.560,
            "wheelchair_user": 0.890,
            "white_cane_user": 0.6,
        }
        if args.profile == "scoring" and args.appearance in SCORING_TIER_SPEED_MPS:
            args.ped_distance = SCORING_TIER_SPEED_MPS[args.appearance] * SCORING_TIER_TARGET_DWELL_SEC

        # Session 44 FIX C, second half. A --mixamo-clip target speed changes how fast the
        # pedestrian travels, so it has to feed the SAME constant-dwell formula the Zone B
        # appearances above use -- otherwise the spawn geometry is sized for a pace the actor no
        # longer walks at.
        #
        # Caught by the checkpoint run: Running's target of 2.5 m/s against the unscaled 8.0m
        # spawn gave dist0=4.976 (target 8.000, FAIL) and robotSpeedAtTrigger=0.000 (FAIL) -- the
        # pedestrian crossed the trigger radius before the robot had even started moving. At
        # 2.5 m/s the correct distance is 2.5 * 6.2 = 15.5m.
        #
        # Static clips (target 0) are excluded: they do not travel, so dwell time is set by the
        # robot's own approach and scaling the distance to zero would be nonsense.
        if args.profile == "scoring" and getattr(args, "mixamo_clip", None):
            _t = mixamo_target_speed(args.mixamo_clip)
            if _t is not None and _t > 0.05:
                # max(), never a bare assignment: the constant-dwell formula is there to push FAST
                # actors further out, and letting it also pull SLOW ones in breaks the trial. At
                # 0.7 m/s it gave 4.34m, which put the frozen pedestrian inside the robot's own
                # approach envelope -- measured robotSpeedAtTrigger=0.000 (gate needs >=0.3), i.e.
                # the robot was already stopped for the obstacle when t=0 fired. The baseline is a
                # floor on how close the encounter may start, independent of pedestrian pace.
                args.ped_distance = max(args.ped_distance,
                                        _t * SCORING_TIER_TARGET_DWELL_SEC)

    # Session 31 FIX 5(b): under --profile scoring, Scared/Surprised default to a raised action-
    # trigger radius (up from PedestrianModulator's own compiled-in 3.0/4.0) so the reaction starts
    # well before the closest pass instead of ~0.5s before it (measured: default radius gave
    # scared only a 0.55s reaction window before t_min). An explicit --scared-radius/
    # --surprise-radius always wins over these defaults. 'arc' and every other personality are
    # unaffected.
    #   scared -> 7.0m (5.92s reaction window measured). Scared's reaction is a whole-body FLEE
    #   (translation), legible from a distance, so maximizing reaction time is pure upside here --
    #   no visibility tradeoff the way FIX 6(a)'s upper-body gesture has (below).
    #   surprised -> 4.5m (3.81s reaction window measured), NOT the same 7.0m used for scared.
    #   FIX 6(a) replaced SurprisedReaction's clip with a retargeted Mixamo gesture -- at 7.0m the
    #   robot is still 6-7m away for the ENTIRE reaction (frozen pedestrian, so only the robot's
    #   own approach changes the distance), too far for an upper-body gesture to read clearly on
    #   camera (verified via extracted frame strips: at 7.0m the gesture was essentially
    #   illegible). 4.5m still nearly 7x the old 0.55s baseline while keeping the pedestrian closer
    #   (down to ~1.1m by the reaction's end) during the reaction -- see REPORT.md Session 31 FIX
    #   6 for the frame-strip evidence and the honest verdict on gesture legibility even at this
    #   closer range.
    if args.profile == "scoring":
        # Session 45 (1.6): 7.0 -> 3.5. At 7.0 the pedestrian fled while the robot was still far
        # away, so no encounter occurred at all -- demo_s44's scared trial recorded min_dist 5.667,
        # i.e. the robot never came near. Footage of a pedestrian running off in the distance
        # carries no social interaction for a VLM to judge. 3.5m keeps a real reaction window while
        # putting the robot close enough that approach -> notice -> flee reads as one sequence.
        # Session 31 raised this to 7.0 to maximise reaction time; that reasoning optimised for
        # reaction legibility alone and did not weigh the encounter failing to happen.
        if args.personality.lower() == "scared" and args.scared_radius is None:
            args.scared_radius = 3.5
        if args.personality.lower() == "surprised" and args.surprise_radius is None:
            args.surprise_radius = 4.5
        # Session 33 FIX 3: 30s comfortably covers the rest of any --clip-mode centered delivered
        # clip (~10s) plus the full raw trial (up to 90s default duration, but the second spurious
        # trigger was observed within ~14s of trial start) -- long enough that no post-pass
        # separation-noise re-entry into surpriseRadius can re-arm a second trigger.
        if args.personality.lower() == "surprised" and args.surprise_cooldown is None:
            args.surprise_cooldown = 30.0
        # Session 34 FIX 1: distance-gated robot repulsion default under scoring, every
        # non-Assertive personality (S34PedestrianReactDistGate skips Assertive at the wiring
        # site regardless of this value). See REPORT.md Session 34 FIX 1 for the N=3 sweep across
        # {1.5, 2.0, 2.5}m that landed this number.
        if args.personality.lower() != "assertive" and args.ped_react_dist is None:
            args.ped_react_dist = PED_REACT_DIST_SCORING_DEFAULT
        # Session 36 FIX 3: Scared's own larger gate default under scoring.
        if args.personality.lower() == "scared" and args.scared_react_dist is None:
            args.scared_react_dist = SCARED_REACT_DIST_SCORING_DEFAULT

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

    if args.exec_editor_method is not None:
        check_editor_lock()
        log_path = Path("/tmp/run_trial_exec_editor_method.log")
        cmd = build_unity_cmd(log_path, windowed=args.windowed, extra_args=[
            "-executeMethod", args.exec_editor_method,
        ])
        returncode, timed_out = guarded_unity_run(cmd, timeout=180, expected_dirty=set(args.allow_dirty))
        print("exec-editor-method: {} returncode={} timed_out={} log={}".format(
            args.exec_editor_method, returncode, timed_out, log_path))
        text = log_path.read_text(errors="replace") if log_path.exists() else ""
        for line in text.splitlines():
            if "[S21" in line or "[AutoTrial]" in line or "error CS" in line:
                print(line)
        sys.exit(0 if (returncode == 0 and not timed_out) else 1)

    if not args.appearance:
        p.error("--appearance is required unless --compile-check, --diag-cmdvel, or --exec-editor-method is given")

    validate_appearance_friendly(args.appearance)
    if args.personality.lower() not in PERSONALITIES:
        raise SystemExit("--personality '{}' invalid. Valid: {}".format(args.personality, PERSONALITIES))

    require_output_root_healthy()
    contain_ros_logs()

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
        geom_spawn, geom_ped_goal, scenario_speed_mult = resolve_scenario_geometry(
            args.scenario, spawn_distance, bearing_goal)
        args.spawn = list(geom_spawn)
        if args.ped_speed is None and scenario_speed_mult is not None:
            args.ped_speed = scenario_speed_mult
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

    # Session 35 BLOCK 4 (FIX 8/9): extra pedestrians, offset from the PRIMARY pedestrian's own
    # final resolved spawn/goal (whatever --scenario produced, or an explicit --spawn/--ped-goal)
    # -- computed here, after args.spawn/args.ped_goal are both finalized above, not from
    # resolve_scenario_geometry's own internal bearing math directly.
    args.pedestrian2_spawn = None
    args.pedestrian2_goal = None
    args.pedestrian3_spawn = None
    args.pedestrian3_goal = None
    want_dyad = args.dyad or args.ped_count >= 2
    want_third = args.ped_count >= 3
    if want_dyad or want_third:
        primary_spawn = tuple(args.spawn)
        primary_dest = tuple(args.ped_goal) if args.ped_goal is not None else tuple(args.spawn[:3])
        # Session 45 (1.5): when there are three, they are one group and use the tighter grouping
        # offset. A plain dyad keeps the verified 2.0.
        p2_offset = PED_COUNT3_GROUP_OFFSET_M if want_third else DYAD_LATERAL_OFFSET_M
        p3_offset = PED_COUNT3_GROUP_OFFSET_M
        if want_dyad:
            p2_spawn, p2_dest = resolve_extra_pedestrian_geometry(
                primary_spawn, primary_dest, ROBOT_START, bearing_goal, p2_offset)
            args.pedestrian2_spawn = p2_spawn
            args.pedestrian2_goal = p2_dest
        if want_third:
            p3_spawn, p3_dest = resolve_extra_pedestrian_geometry(
                primary_spawn, primary_dest, ROBOT_START, bearing_goal, -p3_offset)
            args.pedestrian3_spawn = p3_spawn
            args.pedestrian3_goal = p3_dest

    # Session 30R STEP 2: per-appearance real-world-speed default multipliers, same mechanism/
    # precedent as Session 29's scooter_user default (PedestrianModulator.walkSpeedMultiplier
    # scaling AFTER SFAgent's own MAX_VEL clamp -- never touches Base.cs/SFAgent.cs). An explicit
    # --ped-speed always overrides.
    APPEARANCE_SPEED_MULT = {
        "scooter_user": SCOOTER_SPEED_MULT,
        "cyclist": CYCLIST_SPEED_MULT,
        "wheelchair_user": WHEELCHAIR_SPEED_MULT,
        "white_cane_user": WHITE_CANE_SPEED_MULT,
        "dog_walker": DOG_WALKER_SPEED_MULT,
        "phone_user": PHONE_USER_SPEED_MULT,
    }
    # Session 44 FIX C: a Mixamo clip's own target pace, read from the SAME clip_speeds.json that
    # S41MixamoClipApplier reads its authored pace from. Two quantities, one file -- separate files
    # would drift, and the drift presents as a slide with no obvious cause.
    #
    # Only applies when --mixamo-clip was given and --ped-speed was NOT set explicitly, so no
    # existing invocation changes behaviour. An explicit --ped-speed always wins.
    if args.ped_speed is None and getattr(args, "mixamo_clip", None):
        target = mixamo_target_speed(args.mixamo_clip)
        if target is not None:
            # walkSpeedMultiplier scales the social-force velocity, whose unmodulated pace for the
            # Rocketbox actors measures ~1.3 m/s (Session 30R/41). The multiplier is therefore
            # target / that base, not the target itself.
            args.ped_speed = target / BASE_PED_SPEED_MPS
            eprint("[run_trial] --mixamo-clip {}: target {:.2f} m/s -> --ped-speed {:.3f} "
                   "(base {:.2f} m/s, from clip_speeds.json)".format(
                       args.mixamo_clip, target, args.ped_speed, BASE_PED_SPEED_MPS))
    # Session 46 (S46-D 3): Zone-A only, and only when nothing more specific already set a speed.
    # The ordering matters -- an explicit --ped-speed, a Mixamo target, and a Zone-B per-appearance
    # multiplier all take precedence, so randomisation can never override a designed pace.
    zone_a_mult = None
    if (args.ped_speed is None and args.seed is not None
            and not getattr(args, "mixamo_clip", None)
            and args.appearance not in ZONE_B_APPEARANCES
            and args.appearance not in APPEARANCE_SPEED_MULT):
        zone_a_mult = zone_a_speed_multiplier(args.seed)
        args.ped_speed = zone_a_mult
        eprint("[run_trial] Zone-A walk-speed jitter: seed={} -> --ped-speed {:.4f}".format(
            args.seed, zone_a_mult))
    args.zone_a_speed_multiplier = zone_a_mult

    if args.ped_speed is None and args.appearance in APPEARANCE_SPEED_MULT:
        args.ped_speed = APPEARANCE_SPEED_MULT[args.appearance]
    if args.ped_speed is None:
        args.ped_speed = 1.0

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

    # Session 31 FIX 1: --profile scoring also tunes TEB's own avoidance-onset clearance (not just
    # the SLATE trigger distance) -- see TEB_SCORING_MIN_OBSTACLE_DIST's comment for the screening
    # data. 'arc' explicitly resets to the compiled-in TEB defaults, so a scoring trial earlier in
    # the same long-lived ROS session never leaks into a later arc trial's behavior.
    if args.profile == "scoring" and args.personality.lower() == "assertive":
        # Session 32 FIX A/B SAFETY OVERRIDE: assertive's own straight-line guardian (FIX B)
        # removes an entire safety degree of freedom other personalities still have (the
        # pedestrian's own social-force compliance/yielding) -- empirically, combining FIX A's
        # tuned clearance with a fully rigid pedestrian produced real, repeated sub-0.36m
        # (physical floor) passes across repeat trials (0.318/0.299/0.333m measured this session),
        # NOT explained by TEB parameter choice alone (0.15-0.3 min_obstacle_dist all showed at
        # least one unsafe repeat) -- inherent run-to-run planner variance against a
        # zero-compliance obstacle, not something a parameter value alone can guarantee against.
        # Zero-collision is absolute, so assertive always gets the ORIGINAL, compiled-in-safe TEB
        # defaults regardless of --profile, sacrificing FIX A's onset benefit for this one
        # personality -- its own straight-line behavior already makes it visually distinct from
        # indifferent without needing tighter TEB tuning too (see REPORT.md Session 32 FIX A/B).
        set_teb_avoidance_params(DEFAULT_TEB_MIN_OBSTACLE_DIST, DEFAULT_TEB_INFLATION_DIST, DEFAULT_TEB_WEIGHT_OBSTACLE)
        # Session 33: assertive's safety override extends to the costmap-layer tightening too --
        # the straight-line guardian's zero-compliance already proved dangerous even at S31/S32's
        # moderate TEB tightness (see the DEFAULT_TEB_WEIGHT_OBSTACLE comment above), so it gets
        # the original, compiled-in-safe costmap defaults as well, not this session's new tighter
        # inflation/cost_scaling_factor values.
        set_costmap_inflation_params(DEFAULT_COSTMAP_INFLATION_RADIUS, DEFAULT_COSTMAP_COST_SCALING_FACTOR)
    elif args.profile == "scoring":
        weight_obstacle = (TEB_SCORING_WEIGHT_OBSTACLE_FAST if args.appearance in TEB_SCORING_FAST_APPEARANCES
                            else TEB_SCORING_WEIGHT_OBSTACLE)
        set_teb_avoidance_params(TEB_SCORING_MIN_OBSTACLE_DIST_S33, TEB_SCORING_INFLATION_DIST_S33, weight_obstacle)
        set_costmap_inflation_params(DEFAULT_COSTMAP_INFLATION_RADIUS, TEB_SCORING_COST_SCALING_FACTOR)
    else:
        set_teb_avoidance_params(DEFAULT_TEB_MIN_OBSTACLE_DIST, DEFAULT_TEB_INFLATION_DIST, DEFAULT_TEB_WEIGHT_OBSTACLE)
        set_costmap_inflation_params(DEFAULT_COSTMAP_INFLATION_RADIUS, DEFAULT_COSTMAP_COST_SCALING_FACTOR)

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

    result = post_process(out_dir, args.fps, args.near_dist, args.keep_full, near_clip_min_sec=args.near_clip_min_sec,
                           clip_mode=args.clip_mode, encounter_half_window=args.encounter_half_window,
                           dense_encounter=args.dense_encounter,
                           near_pre=args.near_pre, near_post=args.near_post)
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

    # Session 15 (phase-aware spin, diagnostic -- not a hard exit-code gate, see REPORT.md Session
    # 15 for why): classifies every in-place-rotation episode by phase (APPROACH/ENCOUNTER/POST/
    # PARKING) instead of reporting one whole-trial number that conflates story-critical spin near
    # the pedestrian with post-encounter tail driving.
    meta_for_goal = json.loads((out_dir / "meta.json").read_text())
    spin_phases = trial_lib.classify_spin_phases(out_dir / "frames.csv", meta_for_goal["config"]["goalPose"])
    if spin_phases is not None:
        eprint("[run_trial] spin phases: {} total -- APPROACH={} ENCOUNTER={} POST={} PARKING={} "
               "(t_min={}s min_ped_dist={}m final_goal_dist={}m)".format(
                   spin_phases["n_episodes"], spin_phases["phase_counts"]["APPROACH"],
                   spin_phases["phase_counts"]["ENCOUNTER"], spin_phases["phase_counts"]["POST"],
                   spin_phases["phase_counts"]["PARKING"], spin_phases["t_min"],
                   spin_phases["min_ped_dist"], spin_phases["final_goal_dist"]))

    # Session 17 (Step 1): overlay now runs BEFORE the manifest gate (moved up from below) so the
    # gate can actually see whether pov_full_ov.mp4/pov_near_NN_ov.mp4 landed -- previously ov_ok/
    # ov_detail were computed after all gates were already written to meta.json and checked against
    # nothing at all (see check_file_manifest's own docstring for the root-cause finding: this was
    # never a grace-termination-specific bug, just an ungated post-step whose failure was invisible).
    ov_ok, ov_detail = True, "--no-overlay"
    if args.overlay:
        ov_ok, ov_detail = overlay.process_trial_dir(out_dir, near_dist=args.near_dist,
                                                       near_clip_min_sec=args.near_clip_min_sec,
                                                       clip_mode=args.clip_mode,
                                                       encounter_half_window=args.encounter_half_window,
                                                       near_pre=args.near_pre, near_post=args.near_post)
        eprint("[run_trial] overlay: {} ({})".format("OK" if ov_ok else "FAILED", ov_detail))

    # Session 43: re-link video/ now that the overlay exists. post_process's own call ran before
    # overlay.py had written pov_full_ov.mp4, so without this second (idempotent) call video/ would
    # permanently contain only the un-overlaid file.
    try:
        vlm_videos = vlm_eval_export.link_videos(out_dir)
        if result.get("vlm_eval") is not None:
            result["vlm_eval"]["video"] = vlm_videos
    except Exception as e:
        eprint("[run_trial] vlm_eval video/ link FAILED (non-fatal): {}: {}".format(type(e).__name__, e))

    # Session 17 (Step 1b, THE PERMANENT FILE MANIFEST GATE): enumerates the complete expected
    # per-trial deliverable set and fails loudly if anything is missing, closing the class where a
    # deliverable silently vanishes while every behavioral gate stays green.
    manifest_ok, manifest_detail, manifest_missing = trial_lib.check_file_manifest(
        out_dir, overlay_enabled=args.overlay, near_clips=result["near_clips"])
    eprint("[run_trial] file manifest gate: {} ({})".format(
        "OK" if manifest_ok else "FAILED", manifest_detail))

    # Read min_dist here rather than reusing the summarize() call further down: the safety label
    # has to be in meta.json, and meta.json is written by this call.
    _, meta_min_dist = summarize(out_dir)

    augment_trial_meta_with_gate(out_dir, gate_ok, result["near_clips"],
                                  gate_detail=gate_detail,
                                  aspect_ok=aspect_ok, aspect_detail=aspect_detail,
                                  approach_ok=approach_ok, approach_detail=approach_detail,
                                  trigger_ok=trigger_ok, trigger_detail=trigger_detail,
                                  overlay_ok=ov_ok, overlay_detail=ov_detail,
                                  manifest_ok=manifest_ok, manifest_detail=manifest_detail,
                                  spin_phases=spin_phases,
                                  full_contact_sheet=full_sheet_name if full_sheet_ok else None,
                                  min_dist=meta_min_dist, profile=args.profile,
                                  corridor_width=args.corridor_width,
                                  vlm_eval=result.get("vlm_eval"),
                                  ped_motion=args.ped_motion, seed=args.seed,
                                  zone_a_mult=getattr(args, "zone_a_speed_multiplier", None),
                                  sample_role=args.sample_role)

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
    print("min_dist reached: {} (safety_label={})".format(min_dist, safety_label_for(min_dist)))
    print("content gate: {}".format("PASS" if gate_ok else "FAIL"))
    print("aspect gate: {} ({})".format("PASS" if aspect_ok else "FAIL", aspect_detail))
    print("approach geometry gate: {} ({})".format("PASS" if approach_ok else "FAIL", approach_detail))
    print("trigger-speed gate: {} ({})".format("PASS" if trigger_ok else "FAIL", trigger_detail))
    if args.overlay:
        print("overlay: {} ({})".format("PASS" if ov_ok else "FAIL", ov_detail))
    print("file manifest gate: {} ({})".format("PASS" if manifest_ok else "FAIL", manifest_detail))
    if spin_phases is not None:
        print("spin phases (diagnostic, not gated): {} total -- APPROACH={} ENCOUNTER={} POST={} "
              "PARKING={}".format(spin_phases["n_episodes"], spin_phases["phase_counts"]["APPROACH"],
                                   spin_phases["phase_counts"]["ENCOUNTER"], spin_phases["phase_counts"]["POST"],
                                   spin_phases["phase_counts"]["PARKING"]))
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
    all_gates_ok = gate_ok and aspect_ok and approach_ok and trigger_ok and ov_ok and manifest_ok
    if not all_gates_ok:
        eprint("[run_trial] trial FAILED one or more permanent gates -- content={} aspect={} "
               "approach={} trigger-speed={} overlay={} file-manifest={}; see meta.json/contact "
               "sheets above for detail.".format(
                   gate_ok, aspect_ok, approach_ok, trigger_ok, ov_ok, manifest_ok))
        sys.exit(1)


if __name__ == "__main__":
    main()
