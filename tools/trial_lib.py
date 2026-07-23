"""Shared helpers used by both run_trial.py and overlay.py.

Extracted from run_trial.py in Session 9 so overlay.py can re-derive the same near-pedestrian
spans from frames.csv without duplicating the logic (and without importing run_trial.py itself,
which has import-time side effects like argparse setup).
"""
import csv
import json
import math
import subprocess
import tempfile
from pathlib import Path

DEFAULT_NEAR_CLIP_MIN_SEC = 15.0

# Round 3, THE PERMANENT GATE: calibrated against real forensics data (REPORT.md Round 3 Step 0/4)
# -- a known uniform-gray frame (the Session 10 defect this gate exists to catch forever) measured
# std=1.32, edge_mean=0.48; real scene frames (including a legitimately dim, shadowed-by-a-building
# one, not just bright ones) measured std=23.5-47.0, edge_mean=3.0-9.5. These thresholds sit with a
# wide margin above the gray fingerprint and below every real sample checked.
GATE_STD_THRESHOLD = 6.0
GATE_EDGE_THRESHOLD = 1.0
GATE_SAMPLE_N = 8
CONTACT_SHEET_N = 8
CONTACT_SHEET_THUMB_W = 220


def _raw_near_spans(rows, near_dist):
    """Threshold-crossing spans (min_dist < near_dist), plus each span's own minimum-distance
    moment (the timestamp of its smallest min_dist reading) -- the anchor Round 3's duration
    guarantee grows around. Returns a list of (start_t, end_t, min_moment_t)."""
    spans = []
    in_span = False
    start_t = None
    prev_t = 0.0
    best_md = None
    best_t = None
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
            best_md, best_t = md, t
        elif near and in_span:
            if md is not None and (best_md is None or md < best_md):
                best_md, best_t = md, t
        elif not near and in_span:
            in_span = False
            spans.append((start_t, prev_t, best_t))
            best_md, best_t = None, None
        prev_t = t
    if in_span:
        spans.append((start_t, prev_t, best_t))
    return spans


def find_near_spans(frames_csv_path, near_dist, min_duration_sec=DEFAULT_NEAR_CLIP_MIN_SEC):
    """Near-pedestrian clip windows, each guaranteed >= min_duration_sec (Round 3 fix -- the old
    fixed +/-2s pad routinely produced clips well under half that, e.g. 7.6-8.8s). Each raw
    threshold-crossing span is grown SYMMETRICALLY around its own minimum-distance moment (not its
    own center) until it reaches min_duration_sec, while never shrinking the original crossing
    span itself (the union of the raw span and the symmetric window is taken). Growth is bounded
    by [0, trial_duration] (the last frame's own `t`, i.e. this never asks ffmpeg to cut past what
    the video actually contains); if one side clamps against a boundary, the other side compensates
    to still reach min_duration_sec where the trial is long enough to allow it. Spans that still
    overlap after growth (e.g. two close pedestrian passes) are merged into one clip, since cutting
    two overlapping clips would duplicate frames between them.

    Pad logic (the old cut_clip()'s own +/-2s) is subsumed here -- the (start, end) this returns
    is the final clip window; cut_clip() no longer adds any pad of its own."""
    with open(frames_csv_path, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return []
    trial_duration = float(rows[-1]["t"])

    raw = _raw_near_spans(rows, near_dist)
    grown = []
    for start_t, end_t, min_t in raw:
        anchor = min_t if min_t is not None else (start_t + end_t) / 2.0
        half = min_duration_sec / 2.0
        desired_start = anchor - half
        desired_end = anchor + half
        out_start = min(start_t, desired_start)
        out_end = max(end_t, desired_end)

        # Clamp to trial bounds, then let the unclamped side compensate for whatever the clamped
        # side lost, so min_duration_sec is still met wherever the trial is long enough to allow it.
        deficit_lo = max(0.0, 0.0 - out_start)
        deficit_hi = max(0.0, out_end - trial_duration)
        out_start = max(0.0, out_start)
        out_end = min(trial_duration, out_end)
        if deficit_hi > 0:
            out_start = max(0.0, out_start - deficit_hi)
        if deficit_lo > 0:
            out_end = min(trial_duration, out_end + deficit_lo)

        grown.append((out_start, out_end))

    if not grown:
        return []

    grown.sort()
    merged = [grown[0]]
    for s, e in grown[1:]:
        last_s, last_e = merged[-1]
        if s <= last_e:
            merged[-1] = (last_s, max(last_e, e))
        else:
            merged.append((s, e))
    return merged


def find_encounter_centered_span(frames_csv_path, half_window_sec=5.0):
    """Session 31 FIX 2: replaces the threshold/grace-based find_near_spans() for the delivered
    review clips. User feedback: the old near-clip logic (threshold-crossing span, grown to a
    minimum duration, sometimes merged with a post-encounter grace tail) still let a long
    solo-robot-driving-to-goal tail leak into delivered clips. This instead anchors directly on
    t_min -- the frame with the smallest dist_to_pedestrian in the whole trial (same definition
    classify_spin_phases() already uses) -- and returns a single fixed [t_min-half_window_sec,
    t_min+half_window_sec] window, clamped to [0, trial_duration]. No threshold, no growth, no
    merging: whatever happens outside that window (including all solo-navigation time after the
    pass) is simply not included. Returns (start, end) in seconds, or None if frames.csv has no
    valid dist_to_pedestrian samples."""
    with open(frames_csv_path, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return None
    trial_duration = float(rows[-1]["t"])

    dists = []
    for row in rows:
        try:
            dists.append((float(row["t"]), float(row["dist_to_pedestrian"])))
        except (ValueError, KeyError):
            pass
    if not dists:
        return None

    t_min = min(dists, key=lambda p: p[1])[0]
    start = max(0.0, t_min - half_window_sec)
    end = min(trial_duration, t_min + half_window_sec)
    return (start, end)


def cut_clip(src_video, out_path, start, end):
    ss = max(0.0, start)
    duration = end - start
    cmd = ["ffmpeg", "-y", "-ss", str(ss), "-i", str(src_video), "-t", str(duration),
           "-c:v", "libx264", "-pix_fmt", "yuv420p", str(out_path)]
    result = subprocess.run(cmd, capture_output=True, text=True)
    return result.returncode == 0


def ffprobe_duration(video_path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "default=noprint_wrappers=1:nokey=1", str(video_path)],
        capture_output=True, text=True)
    try:
        return float(result.stdout.strip())
    except ValueError:
        return None


def _sample_timestamps(duration, n):
    if duration is None or duration <= 0 or n <= 0:
        return []
    return [duration * (i + 0.5) / n for i in range(n)]


def extract_sample_frames(video_path, n=GATE_SAMPLE_N):
    """Extracts n evenly-spaced frames (via ffmpeg, one -ss/-frames:v 1 call each -- simple and
    robust, this project's existing pattern) as a list of (t, PIL.Image), skipping any timestamp
    ffmpeg fails to extract (e.g. a truncated clip) rather than raising."""
    from PIL import Image

    duration = ffprobe_duration(video_path)
    timestamps = _sample_timestamps(duration, n)
    frames = []
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        for i, t in enumerate(timestamps):
            fpath = tmp / "s_{}.jpg".format(i)
            cmd = ["ffmpeg", "-y", "-ss", str(t), "-i", str(video_path),
                   "-frames:v", "1", "-update", "1", str(fpath)]
            subprocess.run(cmd, capture_output=True, text=True)
            if fpath.exists():
                frames.append((t, Image.open(fpath).convert("RGB").copy()))
    return frames


def frame_content_stats(img):
    """(luminance_std, edge_density_mean) for one PIL image -- see GATE_STD_THRESHOLD/
    GATE_EDGE_THRESHOLD's calibration note for what separates a real scene from a uniform
    gray/black render failure."""
    import statistics
    from PIL import ImageFilter

    gray = img.convert("L")
    pixels = list(gray.getdata())
    std = statistics.pstdev(pixels)
    edges = gray.filter(ImageFilter.FIND_EDGES)
    epx = list(edges.getdata())
    edge_mean = sum(epx) / len(epx)
    return std, edge_mean


def check_clip_content(video_path, n=GATE_SAMPLE_N, std_threshold=GATE_STD_THRESHOLD,
                        edge_threshold=GATE_EDGE_THRESHOLD):
    """THE PERMANENT GATE (Round 3, part a): samples n frames from video_path and requires EVERY
    one to clear both the luminance-std and edge-density thresholds -- a single uniform gray/black
    sample fails the whole clip (and, by the caller's own contract, the whole trial). Returns
    (ok: bool, detail: str, samples: list of {t, std, edge_mean, ok})."""
    frames = extract_sample_frames(video_path, n=n)
    if not frames:
        return False, "no frames could be sampled from {}".format(video_path), []

    samples = []
    all_ok = True
    for t, img in frames:
        std, edge_mean = frame_content_stats(img)
        ok = std >= std_threshold and edge_mean >= edge_threshold
        all_ok = all_ok and ok
        samples.append({"t": round(t, 3), "std": round(std, 3), "edge_mean": round(edge_mean, 3), "ok": ok})

    detail = "{}/{} samples passed (std>={}, edge_mean>={})".format(
        sum(1 for s in samples if s["ok"]), len(samples), std_threshold, edge_threshold)
    if not all_ok:
        failed = [s for s in samples if not s["ok"]]
        detail += " -- FAILED: {}".format(failed)
    return all_ok, detail, samples


def check_aspect_gate(meta_path, tol=0.01):
    """Round 4, THE PERMANENT ASPECT GATE: reads povCameraAspect/targetAspect back from
    meta.json (written by TrialController.WriteMetaJson from the live povCam.aspect/
    CaptureWidth/CaptureHeight at capture time -- not re-derived here) and requires them to
    agree within `tol`. Catches a regression of the Round 4 aspect bug (a fresh Camera's
    .aspect defaulting to the batchmode GameView's 4:3 instead of the 1280x720/16:9 render
    target -- see AutoTrialBootstrap.BuildPovCamera's comment) on every trial forever, not
    just this session's battery. Returns (ok: bool, detail: str)."""
    meta_path = Path(meta_path)
    if not meta_path.exists():
        return False, "meta.json not found at {}".format(meta_path)
    data = json.loads(meta_path.read_text())
    actual = data.get("povCameraAspect")
    target = data.get("targetAspect")
    if actual is None or target is None:
        return False, "meta.json missing povCameraAspect/targetAspect (pre-Round-4 trial?)"
    err = abs(actual - target)
    ok = err <= tol
    detail = "povCameraAspect={:.4f} targetAspect={:.4f} |err|={:.4f} (tol={})".format(
        actual, target, err, tol)
    return ok, detail


def check_approach_geometry(frames_csv_path, ped_distance, dist0_tol=0.3, noise_tol=0.3):
    """Round 4, THE PERMANENT APPROACH-GEOMETRY GATE: (a) the first captured frame's
    dist_to_pedestrian should be within dist0_tol of the requested --ped-distance (verifies
    the spawn geometry actually landed the pedestrian at the requested range, not just that
    the CLI accepted the flag), and (b) dist_to_pedestrian should decrease monotonically
    (noise-tolerant: a single-step increase of up to noise_tol doesn't break the run, since
    per-frame position noise/AI steering micro-corrections are expected) from frame 0 through
    the trial's own minimum-distance frame, confirming a genuine closing approach rather than
    an erratic one. Returns (dist0_ok: bool, monotonic_ok: bool, detail: str).

    Session 14 (SLATE v2): dist0_tol tightened 0.5 -> 0.3 -- since t=0 is now the frame
    TrialController's own live distance trigger fires (== ped_distance, by construction, to
    within about one frame's worth of robot travel), 0.3m is a comfortable margin rather than
    the loose bound Round 4/Session 13's fixed-instant "t=0" needed."""
    with open(frames_csv_path, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return False, False, "frames.csv is empty"

    dists = []
    for row in rows:
        try:
            dists.append(float(row["dist_to_pedestrian"]))
        except (ValueError, KeyError):
            dists.append(None)
    valid = [(i, d) for i, d in enumerate(dists) if d is not None]
    if not valid:
        return False, False, "no dist_to_pedestrian samples in frames.csv"

    dist0 = valid[0][1]
    dist0_ok = abs(dist0 - ped_distance) <= dist0_tol

    min_idx = min(valid, key=lambda p: p[1])[0]
    violations = []
    prev = None
    for i, d in valid:
        if i > min_idx:
            break
        if prev is not None and d > prev + noise_tol:
            violations.append((i, round(prev, 3), round(d, 3)))
        prev = d
    monotonic_ok = not violations

    detail = "dist0={:.3f} (target {:.3f}+/-{}, {}); monotonic-to-min(frame {}): {} ({} violation(s){})".format(
        dist0, ped_distance, dist0_tol, "OK" if dist0_ok else "FAIL", min_idx,
        "OK" if monotonic_ok else "FAIL", len(violations),
        " e.g. {}".format(violations[:3]) if violations else "")
    return dist0_ok, monotonic_ok, detail


def check_trigger_speed(meta_path, min_speed=0.3):
    """Session 14 (SLATE v2), THE PERMANENT TRIGGER-SPEED GATE: reads robotSpeedAtTrigger back
    from meta.json (written by TrialController.WriteMetaJson from the actual robot displacement
    between the poll immediately before the trigger and the trigger frame itself -- not re-
    derived here) and requires it to be >= min_speed. This is the acceptance criterion for
    "the video's t=0 shows the robot cruising, not standing" -- frames.csv's own frame-0
    robot_speed column is seeded from the same measurement (see PollForTrigger), so this and a
    manual read of frame 0 should always agree. Also surfaces triggerTimedOut (the 30s guard
    path) in the detail string, though that alone doesn't fail this gate -- a timed-out trial
    that still happened to be cruising is not itself a speed-gate failure. Returns
    (ok: bool, detail: str)."""
    meta_path = Path(meta_path)
    if not meta_path.exists():
        return False, "meta.json not found at {}".format(meta_path)
    data = json.loads(meta_path.read_text())
    speed = data.get("robotSpeedAtTrigger")
    timed_out = data.get("triggerTimedOut", False)
    resampled = data.get("triggerSpeedResampled", False)
    if speed is None:
        return False, "meta.json missing robotSpeedAtTrigger (pre-Session-14 trial?)"
    ok = speed >= min_speed
    detail = "robotSpeedAtTrigger={:.3f} m/s (min {}) -- {}{}{}".format(
        speed, min_speed, "OK" if ok else "FAIL",
        ", triggerTimedOut=true (30s guard fired)" if timed_out else "",
        ", triggerSpeedResampled=true (Session 17: an implausible reading was rejected first)" if resampled else "")
    return ok, detail


def check_file_manifest(trial_dir, overlay_enabled, near_clips):
    """Session 17, THE PERMANENT FILE MANIFEST GATE: root cause for why `pov_full_ov.mp4` going
    missing (Sessions 15/16's smoke/statistics runs, all via explicit --no-overlay in this
    project's own driver scripts -- not a grace-termination-specific bug; tested and rejected as
    the cause, see REPORT.md Session 17 Step 1) went undetected wasn't the missing file itself,
    it was that NOTHING checked for it: `overlay.process_trial_dir`'s own (ok, detail) return
    value was printed but never wired into run_trial.py's exit-code/gate contract, so an overlay
    failure -- for any reason, not just an intentionally-skipped one -- left every other gate
    green. This enumerates the complete expected per-trial deliverable set and fails loudly
    (returns ok=False, never just silently omits) if anything is missing. Returns (ok: bool,
    detail: str, missing: list[str])."""
    trial_dir = Path(trial_dir)
    required = ["frames.csv", "meta.json", "unity.log", "pov_full.mp4", "contact_sheet_full.png"]
    if overlay_enabled:
        required.append("pov_full_ov.mp4")
    for c in near_clips:
        idx = c["index"] if isinstance(c, dict) else c
        required.append("pov_near_{:02d}.mp4".format(idx))
        if overlay_enabled:
            required.append("pov_near_{:02d}_ov.mp4".format(idx))

    missing = [name for name in required if not (trial_dir / name).exists()]
    ok = not missing
    detail = "{}/{} deliverables present".format(len(required) - len(missing), len(required))
    if missing:
        detail += " -- MISSING: {}".format(missing)
    return ok, detail, missing


# Session 15 (phase-aware spin, permanent): in-place-rotation episode definition unchanged since
# Session 12 (|wrapped(delta_yaw_deg)/dt| > SPIN_YAW_RATE_THRESHOLD while robot_speed <
# SPIN_SPEED_THRESHOLD) -- what's new is classifying each episode by phase instead of reporting one
# whole-trial number. Root cause for why this was needed (Session 15 Step 0/1): terminationReason is
# "duration" on 100% of trials checked across Sessions 12/13/14 -- the configured far corridor goal
# (44m from robot start) requires ~73s of pure driving at max_vel_x=0.6 m/s, exceeding every
# session's trial duration even before subtracting pre-roll, so "goal_reached" was never realistically
# reachable in ANY session (not a Session-13/14 regression) and every trial's tail end is,
# unavoidably, post-encounter driving with nothing left to film. A literal PARKING bucket
# (robot-to-goal <= PARKING_DIST_M) empirically fires ZERO times across every trial checked this
# session (S12/S13/S14 batteries) for exactly that reason -- the goal is simply never reached. POST
# (post-encounter, not yet arrived) is where that dead time actually shows up instead, and is
# reported here as the operationally meaningful "story is over" bucket; PARKING is kept as a
# separate, distinct category (reported, likely usually empty under this scene's goal geometry) in
# case a shorter-goal/longer-duration trial ever does reach it.
SPIN_YAW_RATE_THRESHOLD_DEG_S = 30.0
SPIN_SPEED_THRESHOLD_MPS = 0.1
PHASE_ENCOUNTER_TIME_WINDOW_SEC = 5.0
PHASE_ENCOUNTER_DIST_M = 3.0
PHASE_PARKING_DIST_M = 1.5


def _wrapped_delta_deg(a, b):
    return (b - a + 180.0) % 360.0 - 180.0


def _find_spin_episodes(rows):
    """Whole-trial in-place-rotation episodes -- consecutive frames where
    |wrapped(delta_robot_yaw_deg)/dt| > SPIN_YAW_RATE_THRESHOLD_DEG_S while robot_speed <
    SPIN_SPEED_THRESHOLD_MPS (Session 12's own definition, unchanged). Returns a list of
    {start_idx, end_idx, start_t, end_t}."""
    episodes = []
    in_episode = False
    cur = None
    prev = None

    for i, row in enumerate(rows):
        t = float(row["t"])
        yaw = float(row["robot_yaw_deg"])
        speed = float(row["robot_speed"])

        if prev is not None:
            dt = max(t - prev["t"], 1e-4)
            yaw_rate = abs(_wrapped_delta_deg(prev["yaw"], yaw)) / dt
            spinning = speed < SPIN_SPEED_THRESHOLD_MPS and yaw_rate > SPIN_YAW_RATE_THRESHOLD_DEG_S

            if spinning:
                if not in_episode:
                    cur = {"start_idx": i - 1, "start_t": prev["t"]}
                    in_episode = True
                cur["end_idx"] = i
                cur["end_t"] = t
            elif in_episode:
                episodes.append(cur)
                in_episode = False

        prev = {"t": t, "yaw": yaw}

    if in_episode:
        episodes.append(cur)
    return episodes


def classify_spin_phases(frames_csv_path, goal_pose):
    """Session 15, THE PERMANENT PHASE-AWARE SPIN CLASSIFIER (diagnostic/reporting, not a hard
    exit-code gate -- see REPORT.md Session 15 for why business_male's encounter-phase numbers are
    reported honestly rather than force-passed). Classifies each whole-trial spin episode (by its
    start frame) into one of four phases:

      PARKING  : robot-to-goal ground distance <= PHASE_PARKING_DIST_M (arrived, "story complete")
      ENCOUNTER: within +/-PHASE_ENCOUNTER_TIME_WINDOW_SEC of the trial's own minimum
                 dist_to_pedestrian frame, OR dist_to_pedestrian < PHASE_ENCOUNTER_DIST_M
      APPROACH : before the min-dist frame, not already ENCOUNTER
      POST     : everything else (after the encounter, not yet arrived at goal)

    goal_pose is a dict with x/z keys (e.g. meta.json's config.goalPose). Returns a dict:
    {n_episodes, t_min, min_ped_dist, final_goal_dist, phase_counts, phase_spin_seconds,
    episodes: [{phase, start_t, end_t, ped_dist, goal_dist}]}."""
    with open(frames_csv_path, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        return None

    dists = [(i, float(r["dist_to_pedestrian"])) for i, r in enumerate(rows) if r["dist_to_pedestrian"]]
    if not dists:
        return None
    min_idx, min_dist = min(dists, key=lambda p: p[1])
    t_min = float(rows[min_idx]["t"])

    def goal_dist(row):
        return math.hypot(float(row["robot_x"]) - goal_pose["x"], float(row["robot_z"]) - goal_pose["z"])

    episodes = _find_spin_episodes(rows)
    phase_counts = {"PARKING": 0, "ENCOUNTER": 0, "APPROACH": 0, "POST": 0}
    phase_spin_seconds = {"PARKING": 0.0, "ENCOUNTER": 0.0, "APPROACH": 0.0, "POST": 0.0}
    detail = []

    for ep in episodes:
        row = rows[ep["start_idx"]]
        t = ep["start_t"]
        ped_dist = float(row["dist_to_pedestrian"]) if row["dist_to_pedestrian"] else float("inf")
        gdist = goal_dist(row)

        if gdist <= PHASE_PARKING_DIST_M:
            phase = "PARKING"
        elif abs(t - t_min) <= PHASE_ENCOUNTER_TIME_WINDOW_SEC or ped_dist < PHASE_ENCOUNTER_DIST_M:
            phase = "ENCOUNTER"
        elif t < t_min:
            phase = "APPROACH"
        else:
            phase = "POST"

        phase_counts[phase] += 1
        phase_spin_seconds[phase] += ep["end_t"] - ep["start_t"]
        detail.append({"phase": phase, "start_t": round(t, 3), "end_t": round(ep["end_t"], 3),
                        "ped_dist": round(ped_dist, 3), "goal_dist": round(gdist, 3)})

    return {
        "n_episodes": len(episodes),
        "t_min": round(t_min, 3),
        "min_ped_dist": round(min_dist, 3),
        "final_goal_dist": round(goal_dist(rows[-1]), 3),
        "phase_counts": phase_counts,
        "phase_spin_seconds": {k: round(v, 3) for k, v in phase_spin_seconds.items()},
        "episodes": detail,
    }


def build_contact_sheet(video_path, out_png_path, n=CONTACT_SHEET_N, thumb_w=CONTACT_SHEET_THUMB_W):
    """THE PERMANENT GATE (Round 3, part b): an n-frame horizontal strip PNG, evenly sampled across
    video_path, so a human reviewer can QA a whole clip's scene content at a glance from index.html
    instead of scrubbing every video. Returns True/False."""
    from PIL import Image

    frames = extract_sample_frames(video_path, n=n)
    if not frames:
        return False

    thumbs = []
    for _, img in frames:
        w, h = img.size
        thumb_h = int(round(h * (thumb_w / w)))
        thumbs.append(img.resize((thumb_w, thumb_h)))

    sheet_h = max(t.size[1] for t in thumbs)
    sheet_w = thumb_w * len(thumbs)
    sheet = Image.new("RGB", (sheet_w, sheet_h), (0, 0, 0))
    for i, thumb in enumerate(thumbs):
        sheet.paste(thumb, (i * thumb_w, 0))
    sheet.save(out_png_path)
    return True
