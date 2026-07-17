"""Shared helpers used by both run_trial.py and overlay.py.

Extracted from run_trial.py in Session 9 so overlay.py can re-derive the same near-pedestrian
spans from frames.csv without duplicating the logic (and without importing run_trial.py itself,
which has import-time side effects like argparse setup).
"""
import csv
import subprocess


def find_near_spans(frames_csv_path, near_dist):
    spans = []
    with open(frames_csv_path, newline="") as f:
        reader = csv.DictReader(f)
        rows = list(reader)
    in_span = False
    start_t = None
    prev_t = 0.0
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
