"""Shared helpers used by both run_trial.py and overlay.py.

Extracted from run_trial.py in Session 9 so overlay.py can re-derive the same near-pedestrian
spans from frames.csv without duplicating the logic (and without importing run_trial.py itself,
which has import-time side effects like argparse setup).
"""
import csv
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
