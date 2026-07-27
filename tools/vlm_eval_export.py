#!/usr/bin/env python3
"""Session 43: emit the VLM-teacher input format for one trial.

    <trial_dir>/
      video/
        pov_full.mp4          <- "Unity output 1"
        pov_full_ov.mp4       <- human review only, NEVER a frame source
      vlm_eval/               <- "Unity output 2"
        frames/frame_0001.png ...
        states.csv
        README.md
      meta.json               <- untouched

Two structural decisions carry most of the correctness here.

FRAMES COME FROM THE RAW JPGs, NOT FROM A VIDEO. The capture loop already writes every frame to
pov/pov_%05d.jpg at the camera's native 1280x720, and frames.csv has exactly one row per JPG, so
frame_idx indexes both. Decoding pov_full.mp4 instead would add a second lossy generation on top
of the JPEG and force a timestamp->frame remap. The cost of doing it this way is a hard ordering
constraint: run_trial.py deletes pov/ at the end of post_process() unless --keep-full, so this
export MUST be called before that deletion. It is not worth keeping --keep-full on instead --
15 Hz x 60 s x 1280x720 JPG is ~180 MB per trial, and a few hundred trials would be 40-70 GB;
~63 exported PNGs is ~13 MB.

That same decision is also the real overlay guarantee. The telemetry burn-in lives only in the
*_ov.mp4 files produced later by overlay.py; pov/*.jpg is written by the capture loop before any
overlay exists, so an exported frame CANNOT contain burned-in telemetry. score_batch.reject_overlay
is still called on every source path, but be clear about what it is: a check on the FILENAME STEM,
not on pixels. It cannot detect burn-in; it can only catch a path that was named *_ov. The
structural fact above is the guarantee, the name check is a cheap second line, and human spot-checks
of exported frames are the only pixel-level verification.

Derived quantities are computed on the full 15 Hz frames.csv and only then sampled down to the
1 Hz rows of states.csv. 1 Hz is an output rate, never a computation rate -- differencing a heading
column at 1 Hz to recover a turn rate would be garbage, which is why robot_ang_vel_y is read
straight off the physics body in-engine instead.
"""
import argparse
import csv
import json
import math
import os
import shutil
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import trial_lib  # noqa: E402

try:
    from PIL import Image
except ImportError:  # pragma: no cover - Pillow is already a dependency of tools/vlm
    Image = None


# The four columns 学长's tooling reads, in his order and his capitalisation ("Image_name" really
# does have a capital I). Anything appended after these is additive and invisible to a reader that
# selects columns by name -- which his tool, and every analysis script in this repo, does.
SENIOR_COLUMNS = ["time", "Image_name", "robot_velocity", "robot_heading"]

APPENDED_COLUMNS = [
    # Exact correspondence back to the two other representations of this same instant. These exist
    # because `time` alone CANNOT align a frame to the video: capture spacing is not uniform (dt
    # ranges ~0.047-0.094s), but the mp4 is assembled at a single constant rate, so a frame's trial
    # time and its position in the video are different clocks. Measured drift on a real 60s trial:
    # 0.87s, which at this project's ~1.8 m/s closing speeds is ~1.6m of separation -- enough to
    # change what the frame shows entirely. frame_idx indexes frames.csv and the mp4's frame
    # sequence; video_time is where to seek in the mp4.
    "frame_idx", "video_time",
    "robot_vel_x", "robot_vel_y", "robot_vel_z",
    "robot_speed_ground", "robot_ang_vel_y",
    "robot_yaw_ros_rad", "robot_ang_vel_ros",
    "cmd_vel_linear", "cmd_vel_angular",
    "robot_x", "robot_z",
    "lateral_offset_straightline",
    "min_dist", "nearest_ped_id", "nearest_ped_x", "nearest_ped_z",
    "phase", "event",
]

STATES_COLUMNS = SENIOR_COLUMNS + APPENDED_COLUMNS


def _f(row, key):
    """float(row[key]) or None -- blank cells are normal (cmd_vel before the first message,
    velocity columns on a trial recorded before Session 43, extra-pedestrian columns)."""
    v = row.get(key)
    if v is None or v == "":
        return None
    try:
        return float(v)
    except ValueError:
        return None


def read_frames(frames_csv):
    with open(frames_csv, newline="") as f:
        return list(csv.DictReader(f))


def straightline_reference(meta):
    """(unit bearing, origin) of the robot's own start->goal straight line.

    Deliberately NOT the planner's global path: move_base publishes one on
    move_base/GlobalPlanner/plan and SEAN.Display.PlanVisualizer can subscribe to it, but that
    component is not present in Outdoor.unity (its script GUID appears zero times in the scene) and
    it downsamples to 25 points anyway, so no path geometry reaches this pipeline at all. The
    start->goal line is what tools/lateral_offset_analysis.py already uses.
    """
    from run_trial import ROBOT_START, DEFAULT_ROBOT_GOAL

    goal = (meta.get("config") or {}).get("goalPose") or {}
    gx = goal.get("x", DEFAULT_ROBOT_GOAL[0])
    gz = goal.get("z", DEFAULT_ROBOT_GOAL[2])
    sx, sz = ROBOT_START[0], ROBOT_START[2]
    dx, dz = gx - sx, gz - sz
    norm = math.hypot(dx, dz)
    if norm < 1e-6:
        return None, None
    return (dx / norm, dz / norm), (sx, sz)


def lateral_offset(x, z, unit, origin):
    if unit is None:
        return None
    ux, uz = unit
    ox, oz = origin
    dx, dz = x - ox, z - oz
    along = dx * ux + dz * uz
    return math.hypot(dx - along * ux, dz - along * uz)


def nearest_pedestrian(row):
    """(id, x, z, dist) of whichever pedestrian is closest THIS frame.

    frames.csv carries dist_to_pedestrian for #1 and dist_to_pedestrianN for each extra one
    (dyad = 2, ped_count_3 = 3). Picking the argmin here matters: Loop 1 Bug 1 was exactly this
    mistake made in-engine, where only pedestrian1's distance fed the safety minimum.
    """
    best = None
    d1 = _f(row, "dist_to_pedestrian")
    if d1 is not None:
        best = (1, _f(row, "pedestrian_x"), _f(row, "pedestrian_z"), d1)
    n = 2
    while "dist_to_pedestrian{}".format(n) in row:
        dn = _f(row, "dist_to_pedestrian{}".format(n))
        if dn is not None and (best is None or dn < best[3]):
            best = (n, _f(row, "pedestrian{}_x".format(n)), _f(row, "pedestrian{}_z".format(n)), dn)
        n += 1
    return best if best is not None else (None, None, None, None)


def classify_phases(rows):
    """Per-frame approach | encounter | depart, on trial_lib's own thresholds.

    trial_lib.classify_spin_phases labels spin EPISODES, not frames, so this reuses its constants
    rather than its function -- importing them means the two definitions cannot drift apart. Its
    PARKING (arrived at goal) folds into `depart` here, since the ticket's vocabulary has three
    values and "parked at the goal" is unambiguously after the encounter.
    """
    dists = [(i, _f(r, "dist_to_pedestrian")) for i, r in enumerate(rows)]
    dists = [(i, d) for i, d in dists if d is not None]
    if not dists:
        return ["approach"] * len(rows), None, None
    min_idx, _ = min(dists, key=lambda p: p[1])
    t_min = _f(rows[min_idx], "t")

    phases = []
    for i, r in enumerate(rows):
        t = _f(r, "t")
        d = _f(r, "dist_to_pedestrian")
        near_in_time = t is not None and abs(t - t_min) <= trial_lib.PHASE_ENCOUNTER_TIME_WINDOW_SEC
        near_in_space = d is not None and d < trial_lib.PHASE_ENCOUNTER_DIST_M
        if near_in_time or near_in_space:
            phases.append("encounter")
        elif i < min_idx:
            phases.append("approach")
        else:
            phases.append("depart")
    return phases, min_idx, t_min


def select_frames(rows, phases, min_idx, dense_encounter=False):
    """Which frames.csv rows become PNGs, and what each one is called.

    Three groups:
      - the regular 1 Hz sequence, frame_0001.png <-> t=1s, frame_0002.png <-> t=2s, ...
      - forced event frames, frame_NNNN_e.png, at t_min and at the encounter's two edges. At
        0.6 m/s closing on a ~1.2 m/s pedestrian the separation changes ~1.8 m per second, so a
        breach at t=12.4s falls between the 12.0 and 13.0 samples and would otherwise be absent
        from the data entirely -- the one moment the whole trial exists to capture.
      - optional 5 Hz infill across the encounter (--dense-encounter, default off), frame_NNNN_dK.png.

    Returns [(row_index, image_name, event_label)] ordered by time. NNNN is always the second the
    frame falls in, so the extra rows never renumber the regular sequence.
    """
    picked = {}  # row index -> (image_name, event label)

    ts = [_f(r, "t") for r in rows]
    duration = max([t for t in ts if t is not None] or [0.0])

    def nearest_row(target_t):
        best_i, best_err = None, None
        for i, t in enumerate(ts):
            if t is None:
                continue
            err = abs(t - target_t)
            if best_err is None or err < best_err:
                best_i, best_err = i, err
        return best_i

    for sec in range(1, int(math.floor(duration)) + 1):
        i = nearest_row(float(sec))
        if i is not None:
            picked[i] = ("frame_{:04d}.png".format(sec), "")

    events = []
    if min_idx is not None:
        events.append((min_idx, "min_dist"))
    enc = [i for i, p in enumerate(phases) if p == "encounter"]
    if enc:
        events.append((enc[0], "encounter_start"))
        events.append((enc[-1], "encounter_end"))

    used_event_names = set()
    for i, label in events:
        if i in picked:
            # The event landed on a regular sample: keep the regular name and just label it, so the
            # 1 Hz sequence stays gapless and no duplicate image is written.
            picked[i] = (picked[i][0], label if not picked[i][1] else picked[i][1] + "|" + label)
            continue
        sec = int(math.floor(ts[i] or 0.0))
        base = "frame_{:04d}_e".format(sec)
        name, k = base + ".png", 2
        while name in used_event_names:
            name, k = "{}{}.png".format(base, k), k + 1
        used_event_names.add(name)
        picked[i] = (name, label)

    if dense_encounter and enc:
        t_lo, t_hi = ts[enc[0]], ts[enc[-1]]
        step = 0.2
        t = math.ceil(t_lo / step) * step
        while t <= t_hi:
            i = nearest_row(t)
            if i is not None and i not in picked:
                sec = int(math.floor(ts[i] or 0.0))
                k = int(round(((ts[i] or 0.0) - sec) / step))
                picked[i] = ("frame_{:04d}_d{}.png".format(sec, max(k, 1)), "")
            t += step

    return sorted(((i, n, e) for i, (n, e) in picked.items()), key=lambda p: ts[p[0]] or 0.0)


def _reject_overlay(path):
    """Reuse Loop 2's own guard rather than writing a second one. It checks the filename stem only
    -- see this module's docstring for why that is the weaker of the two protections here."""
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent / "vlm"))
        from score_batch import reject_overlay
        reject_overlay(Path(path))
    except ImportError:
        if Path(path).stem.endswith("_ov"):
            raise AssertionError("REFUSING to export from an overlay file: {}".format(path))


def link_or_copy(src, dst):
    """Hardlink when possible so video/ costs no extra disk, copy when the filesystem refuses."""
    if dst.exists():
        dst.unlink()
    try:
        os.link(src, dst)
        return "hardlink"
    except OSError:
        shutil.copy2(src, dst)
        return "copy"


def link_videos(trial_dir):
    """Populate video/ ("Unity output 1") from whichever full-length videos exist right now.

    Separate from export() and idempotent because the two are produced at different times: the
    frame export has to run while pov/ still exists, which is inside post_process(), whereas
    pov_full_ov.mp4 is not written until overlay.py runs afterwards. Calling this once more after
    the overlay step is what actually gets the *_ov file into video/ -- an export-only call would
    silently leave it out.
    """
    trial_dir = Path(trial_dir)
    video_dir = trial_dir / "video"
    video_dir.mkdir(parents=True, exist_ok=True)
    notes = []
    for stem in ("pov_full.mp4", "pov_full_ov.mp4"):
        src = trial_dir / stem
        if src.exists():
            notes.append("{} ({})".format(stem, link_or_copy(src, video_dir / stem)))
    return notes


def export(trial_dir, dense_encounter=False, quiet=False):
    trial_dir = Path(trial_dir)
    frames_csv = trial_dir / "frames.csv"
    pov_dir = trial_dir / "pov"
    meta_path = trial_dir / "meta.json"

    if not frames_csv.exists():
        return {"ok": False, "reason": "frames.csv missing"}
    if not pov_dir.is_dir():
        return {"ok": False, "reason": "pov/ missing -- export must run BEFORE post_process deletes it"}
    if Image is None:
        return {"ok": False, "reason": "Pillow not available, cannot write PNG"}

    meta = json.loads(meta_path.read_text()) if meta_path.exists() else {}
    rows = read_frames(frames_csv)
    if not rows:
        return {"ok": False, "reason": "frames.csv has no rows"}

    # --- derived quantities, all at the full 15 Hz ---
    unit, origin = straightline_reference(meta)
    lat = []
    for r in rows:
        x, z = _f(r, "robot_x"), _f(r, "robot_z")
        lat.append(lateral_offset(x, z, unit, origin) if (x is not None and z is not None) else None)
    phases, min_idx, _t_min = classify_phases(rows)

    # Constant rate the mp4 is assembled at -- mirrors run_trial.actual_achieved_fps exactly, so
    # video_time below matches where run_trial's own ffmpeg put each frame.
    t_first, t_last = _f(rows[0], "t"), _f(rows[-1], "t")
    span = (t_last - t_first) if (t_first is not None and t_last is not None) else 0.0
    real_fps = (len(rows) / span) if span > 0 else None

    # --- select, then export ---
    selection = select_frames(rows, phases, min_idx, dense_encounter=dense_encounter)

    vlm_dir = trial_dir / "vlm_eval"
    frames_out = vlm_dir / "frames"
    frames_out.mkdir(parents=True, exist_ok=True)
    video_dir = trial_dir / "video"
    video_dir.mkdir(parents=True, exist_ok=True)

    written, missing = 0, []
    for i, name, _event in selection:
        idx = rows[i].get("frame_idx")
        src = pov_dir / "pov_{:05d}.jpg".format(int(idx))
        if not src.exists():
            missing.append(src.name)
            continue
        _reject_overlay(src)
        with Image.open(src) as im:
            # Native capture resolution, deliberately. The 128x72 in the deck is the ResNet18
            # surrogate's input, applied at consumption time; downsampling here would be
            # irreversible.
            im.convert("RGB").save(frames_out / name, "PNG")
        written += 1

    # --- states.csv ---
    with open(vlm_dir / "states.csv", "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(STATES_COLUMNS)
        for i, name, event in selection:
            r = rows[i]
            ped_id, ped_x, ped_z, ped_d = nearest_pedestrian(r)
            ground = _f(r, "robot_speed_ground")
            fidx = r.get("frame_idx", "")
            vtime = ""
            if real_fps and fidx not in ("", None):
                vtime = "{:.3f}".format(int(fidx) / real_fps)
            w.writerow([
                "{:.3f}".format(_f(r, "t") or 0.0),
                name,
                "" if ground is None else "{:.4f}".format(ground),
                r.get("robot_yaw_deg", ""),
                fidx, vtime,
                r.get("robot_vel_x", ""), r.get("robot_vel_y", ""), r.get("robot_vel_z", ""),
                r.get("robot_speed_ground", ""), r.get("robot_ang_vel_y", ""),
                r.get("robot_yaw_ros_rad", ""), r.get("robot_ang_vel_ros", ""),
                r.get("cmd_lin_x", ""), r.get("cmd_ang_z", ""),
                r.get("robot_x", ""), r.get("robot_z", ""),
                "" if lat[i] is None else "{:.4f}".format(lat[i]),
                "" if ped_d is None else "{:.3f}".format(ped_d),
                "" if ped_id is None else ped_id,
                "" if ped_x is None else "{:.3f}".format(ped_x),
                "" if ped_z is None else "{:.3f}".format(ped_z),
                phases[i],
                event,
            ])

    video_notes = link_videos(trial_dir)

    (vlm_dir / "README.md").write_text(build_readme(meta, rows, selection, phases, unit, origin))

    result = {
        "ok": len(missing) == 0 and written > 0,
        "framesWritten": written,
        "statesRows": len(selection),
        "eventFrames": sum(1 for _, _, e in selection if e),
        "denseEncounter": bool(dense_encounter),
        "missingSourceFrames": missing,
        "video": video_notes,
    }
    if not quiet:
        print(json.dumps(result, indent=2))
    return result


def build_readme(meta, rows, selection, phases, unit, origin):
    have_vel = any(r.get("robot_vel_x") for r in rows[:50])
    n_event = sum(1 for _, _, e in selection if e)
    goal = (meta.get("config") or {}).get("goalPose") or {}
    return """# `vlm_eval/` -- conventions for this trial

This directory is "Unity output 2". `../video/` is "Unity output 1".

`states.csv` has one row per image in `frames/`, and `Image_name` is the exact filename. The first
four columns -- `time`, `Image_name`, `robot_velocity`, `robot_heading` -- are the agreed interface
and are always first, in this order and this capitalisation. Every other column is appended after
them; a reader that selects by column name is unaffected by their presence.

## Time

`time` is **seconds since the trial's t=0**, which is not the moment Unity started. t=0 is the frame
the robot-to-pedestrian ground distance first crossed the trigger threshold; the robot is already
cruising by then. The preceding pre-roll (`preRollDurationSec` in `meta.json`, {preroll}) is not
captured. `time` is sim-clock seconds from `Time.time`.

### `time` is NOT the video's timeline -- use `video_time` to seek the mp4

Capture spacing is **not uniform**: per-frame dt ranges roughly 0.047-0.094 s, because a capture
tick (two 1280x720 renders + readback + JPEG encode + write) sometimes overruns its budget. The mp4,
however, is assembled at a single constant rate. So a frame's `time` and its position in the video
are two different clocks, and they drift apart over a trial. Measured on a real 60 s trial here:
**0.87 s of drift**, which at this project's ~1.8 m/s closing speeds is ~1.6 m of separation --
easily enough to change what the frame shows.

Two columns exist so nobody has to reconstruct this:

- `frame_idx` -- the frame's index in `frames.csv` **and** in the mp4's frame sequence. This is the
  exact, unambiguous correspondence; prefer it.
- `video_time` -- `frame_idx / achieved_fps`, i.e. where to seek in `pov_full.mp4` to land on this
  exact frame.

Use `time` for physics and reasoning about the trial. Use `video_time` / `frame_idx` for anything
that touches the video.

The regular sequence is **one frame per second**, `frame_0001.png` at t=1s, `frame_0002.png` at
t=2s, and so on -- the nearest captured frame to each whole second (capture runs at ~15 Hz, so the
error is under ~33 ms). t=0 itself is not a regular sample.

## Extra frames

{n_event} row(s) in this trial are forced event frames, named `frame_NNNN_e.png`, where NNNN is the
second they fall in. They exist because 1 Hz is too coarse for the moment that matters: closing
speeds here are ~1.8 m/s, so the separation changes ~1.8 m between consecutive regular samples, and
the trial's minimum distance usually falls between two of them. The `event` column marks them
(`min_dist`, `encounter_start`, `encounter_end`); it is empty for regular frames. If an event lands
exactly on a regular sample, no extra image is written -- the regular frame simply carries the label.

`--dense-encounter` (off by default) additionally infills the encounter span at 5 Hz, named
`frame_NNNN_dK.png`. Off unless stated otherwise for this trial.

## Units, frames, and the sign trap

The simulator is Unity (**left-handed, Y-up**); ROS is **right-handed, Z-up**. The ground plane is
Unity's **(x, z)** -- Unity's y is height. That is why the position columns here are `robot_x` and
`robot_z`, not `robot_x`/`robot_y`: naming the second ground axis "y" would collide with the
vertical axis and is exactly the kind of pun that has cost this project time before.

| column | unit | frame / convention |
|---|---|---|
| `robot_heading` | **degrees**, [0, 360) | Unity world yaw of `base_link`. 0 = facing Unity **+Z**; increases **clockwise** seen from above |
| `robot_yaw_ros_rad` | **radians**, unwrapped (no 2*pi jump) | ROS convention: 0 = ROS **+x** (= Unity +Z), increases **counter-clockwise** |
| `robot_ang_vel_y` | rad/s | Unity yaw rate, sign matching `robot_heading` (CW positive) |
| `robot_ang_vel_ros` | rad/s | ROS yaw rate, CCW positive. Equals `-robot_ang_vel_y` |
| `cmd_vel_angular` | rad/s | ROS REP-103, `base_link`, **CCW positive** |
| `robot_velocity`, `robot_speed_ground` | m/s | ground-plane speed magnitude, unsigned |
| `robot_vel_x/y/z` | m/s | Unity world axes, signed; y is vertical |
| `robot_x`, `robot_z` | m | Unity world ground plane |

**`robot_heading` and `cmd_vel_angular` turn in opposite directions.** This is not a bug and it is
not new -- it is inherent to the two coordinate conventions and is already present in every
`frames.csv` this project has ever produced. `robot_yaw_ros_rad` and `robot_ang_vel_ros` are
provided so that a consumer can work entirely in ROS convention and never mix the two. The
conversion is a single sign flip applied in-engine (`TrialController.CaptureFrame`), the same flip
`SEAN.TF.OdometryPublisher` already applies before publishing.

## Where the velocity numbers come from

`robot_vel_*` and `robot_ang_vel_y` are read off the robot's physics body in-engine
({velsrc}), not differenced from positions. Note what that does and does not mean:
`SEAN.Control.VelocityController` *sets* those body velocities each FixedUpdate from the incoming
`cmd_vel`, and PhysX then integrates them. So they are the body's realised state, but they are
**not independent of the command** the way a wheel encoder would be. The legacy `robot_speed`
column in `frames.csv` (a finite difference of position) remains the more independent measure of
what actually happened, and it is left untouched for exactly that reason -- it is also the input to
the trigger-speed gate and the comparison basis for every trial ever run.

## `lateral_offset_straightline`

Perpendicular distance, in metres, from the robot to the **straight line from its start pose to its
goal** ({goaltxt}).

It is deliberately **not** measured against the planner's path. move_base does publish a global
plan, but no component in the running scene subscribes to it, so no path geometry reaches this
pipeline at all.

Two things follow, and both matter before anyone trains on this column:

1. It is **not** the `lateral_offset_norm` of the driving-surrogate literature, which is offset
   from a lane centreline. Different reference, different semantics. The name is different on
   purpose.
2. Its noise floor is high. Session 34's control trial -- a pedestrian parked 40 m away, physically
   incapable of influencing the robot -- still measured ~0.3-0.45 m of ambient lateral drift from
   TEB itself. There is signal here, but the SNR is limited, and a deviation under ~0.5 m should
   not be read as avoidance behaviour.

## `phase`

`approach` | `encounter` | `depart`, using this project's existing thresholds
(`trial_lib.PHASE_ENCOUNTER_TIME_WINDOW_SEC` = {tw}s, `PHASE_ENCOUNTER_DIST_M` = {dm} m): a frame is
`encounter` if it is within +/-{tw}s of the trial's minimum-distance frame **or** the pedestrian is
closer than {dm} m; otherwise `approach` before that frame and `depart` after.

## `min_dist` and `nearest_ped_*`

`min_dist` is the **instantaneous** distance to the closest pedestrian at that frame, minimised over
all pedestrians present (1 for most configs, 2 for dyad, 3 for ped_count_3). It is not a running
minimum -- the whole-trial minimum is `minDistanceMeters` in `meta.json`. `nearest_ped_id` says
which pedestrian that was.

## Why these frames cannot contain burned-in telemetry

They are exported from `pov/pov_%05d.jpg`, written by the capture loop **before any overlay exists**.
The telemetry burn-in is applied later and only ever to separate `*_ov.mp4` files. That structural
fact is the guarantee.

A filename check (`score_batch.reject_overlay`) also runs on every source path, but it inspects the
*name*, not the pixels -- it can catch a path called `*_ov`, and nothing else. Do not mistake it for
overlay detection. The only pixel-level check is a human looking at exported frames.

`../video/pov_full_ov.mp4` **is** overlaid and is for human review only. Never sample frames from it.
""".format(
        preroll="{:.1f}s".format(meta.get("preRollDurationSec", 0.0)) if meta.get("preRollDurationSec") else "see meta.json",
        n_event=n_event,
        velsrc=meta.get("velocitySource") or ("resolved at runtime" if have_vel else "NOT AVAILABLE in this trial -- columns are blank"),
        goaltxt="start {} -> goal ({}, {})".format(
            "(authored teleport pose)", goal.get("x", "?"), goal.get("z", "?")) if unit is not None
        else "UNAVAILABLE for this trial -- column is blank",
        tw=trial_lib.PHASE_ENCOUNTER_TIME_WINDOW_SEC,
        dm=trial_lib.PHASE_ENCOUNTER_DIST_M,
    )


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("trial_dir")
    p.add_argument("--dense-encounter", action="store_true",
                   help="additionally infill the encounter span at 5 Hz (default off; the 1 Hz "
                        "sequence is the agreed format and is never altered by this flag)")
    args = p.parse_args()
    result = export(args.trial_dir, dense_encounter=args.dense_encounter)
    return 0 if result.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
