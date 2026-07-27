#!/usr/bin/env python3
"""Session 43 TASK 6: the eight per-trial self-checks on the new vlm_eval/ output format.

Usage:
    python3 tools/s43_selfcheck.py <trial_dir> [<trial_dir> ...]
    python3 tools/s43_selfcheck.py --tsv <trial_dir> ...     # one machine-readable row per trial

Checks 1-8 are the ticket's list. Check 3 deserves a note, because it is stronger here than the
ticket asks for and it is the check that actually matters.

The ticket asks for a human to eyeball 3 random frames for burned-in subtitles. A human eyeballing
3 of ~60 frames catches a systematic overlay but is a coin flip on a partial one, and it cannot be
run in a batch. So check 3 instead decodes the SAME timestamp out of both full-length videos and
compares pixels:

    exported PNG  vs  pov_full.mp4     -> must be CLOSE   (this is where the frame came from)
    exported PNG  vs  pov_full_ov.mp4  -> must be FAR     (the overlaid stream is a different image)

If the export ever silently switched to the overlaid source, the two distances swap and this fails
loudly on every frame rather than probabilistically on three. The human eyeball pass is still worth
doing for everything else a frame can be wrong about (black frames, wrong camera, wrong scene) --
this replaces only the overlay half of it.

The comparison is restricted to the region the overlay actually writes into, and that restriction
is what makes it work at all. Measured on a real trial, whole-frame mean absolute difference:

    clean video vs overlaid video   MAD 1.54     <- the overlay's own footprint
    exported PNG vs clean video     MAD 4.63     <- JPEG-vs-H.264 codec noise

The burn-in is a small block of text on a 1280x720 frame, so over the whole frame it is SMALLER
than the codec noise between the PNG and either video. A whole-frame threshold therefore cannot
separate the two cases no matter where it is set. Cropping to the telemetry block (ASS Alignment=7,
top-left, MarginL/V=14, Consolas 20 -- see tools/overlay.py) inverts that: png-vs-clean drops to
0.8-1.5 while png-vs-overlaid stays 5.1-6.0, a 4-6.5x separation.

The test is a RATIO rather than an absolute threshold, so it calibrates itself per frame against
that frame's own codec noise instead of depending on scene brightness or encoder settings.
"""
import argparse
import csv
import json
import math
import re
import subprocess
import sys
import tempfile
from pathlib import Path

try:
    from PIL import Image, ImageChops
except ImportError:
    Image = None

# The rectangle overlay.py burns its telemetry block into: ASS Alignment=7 (top-left), MarginL=14,
# MarginV=14, Consolas 20 on a 1280x720 canvas, up to ~5 lines. Generous on both axes -- it only has
# to CONTAIN the text, and including some clean pixels around it costs sensitivity, not correctness.
OVERLAY_BOX = (0, 0, 560, 200)
# Ratio of (difference from the overlaid video) to (difference from the clean video), inside that
# box. Measured 4.0-6.5x on a real trial when the export is correct. 2.0 leaves wide headroom while
# still being far from 1.0, which is what "these two sources are indistinguishable" would look like.
OVERLAY_MIN_RATIO = 2.0
# Absolute ceiling on the difference from the clean video inside the box, as a backstop for the case
# where no overlaid video exists to form a ratio against (--no-overlay). Codec noise measured
# 0.8-1.5 there; 3.0 is comfortably above that and far below the 5.1-6.0 an overlay produces.
SAME_SOURCE_MAX_MAD = 3.0


def ffprobe_duration(path):
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "default=noprint_wrappers=1:nokey=1", str(path)],
        capture_output=True, text=True)
    if out.returncode != 0 or not out.stdout.strip():
        return None
    try:
        return float(out.stdout.strip())
    except ValueError:
        return None


def extract_frame(video, t, dst):
    """Extract the video frame at timestamp t.

    t MUST be a video timestamp, not a trial `time`. Those are different clocks: capture spacing is
    non-uniform while the mp4 is constant-rate, and they drift (0.87s measured on a real 60s trial).
    Seeking with a trial `time` lands on the wrong frame, and the resulting image difference then
    looks exactly like a failed overlay check. states.csv carries `video_time` for this reason.
    """
    r = subprocess.run(["ffmpeg", "-v", "error", "-y", "-ss", "{:.3f}".format(t),
                        "-i", str(video), "-frames:v", "1", str(dst)],
                       capture_output=True, text=True)
    return r.returncode == 0 and Path(dst).exists()


def mad(img_a, img_b, box=None):
    """Mean absolute difference in 0-255, optionally within a crop box, after matching sizes."""
    a = Image.open(img_a).convert("RGB")
    b = Image.open(img_b).convert("RGB")
    if a.size != b.size:
        b = b.resize(a.size)
    if box is not None:
        a, b = a.crop(box), b.crop(box)
    diff = ImageChops.difference(a, b)
    hist = diff.histogram()
    total, count = 0, 0
    for channel in range(3):
        for value in range(256):
            n = hist[channel * 256 + value]
            total += value * n
            count += n
    return total / count if count else 0.0


def check_trial(trial_dir, sample_frames=3):
    d = Path(trial_dir)
    checks = []

    def add(n, name, ok, detail):
        checks.append({"n": n, "name": name, "ok": bool(ok), "detail": detail})

    video = d / "video" / "pov_full.mp4"
    video_ov = d / "video" / "pov_full_ov.mp4"
    frames_dir = d / "vlm_eval" / "frames"
    states = d / "vlm_eval" / "states.csv"

    # 1. video present and actually decodable
    dur = ffprobe_duration(video) if video.exists() else None
    add(1, "video/pov_full.mp4 playable", dur is not None,
        "missing" if not video.exists() else ("ffprobe failed" if dur is None else "{:.2f}s".format(dur)))

    # 2. frame count tracks video duration, and the regular sequence has no gaps
    pngs = sorted(frames_dir.glob("*.png")) if frames_dir.is_dir() else []
    regular = sorted(p for p in pngs if re.fullmatch(r"frame_\d{4}\.png", p.name))
    nums = [int(re.match(r"frame_(\d{4})\.png", p.name).group(1)) for p in regular]
    gaps = [i for i in range(1, (max(nums) if nums else 0) + 1) if i not in set(nums)]
    ok2 = bool(nums) and not gaps and dur is not None and abs(len(regular) - dur) <= 2.0
    add(2, "frame count ~ video seconds, no gaps", ok2,
        "{} regular + {} extra png; video {}; missing indices: {}".format(
            len(regular), len(pngs) - len(regular),
            "{:.2f}s".format(dur) if dur else "unknown", gaps or "none"))

    # states.csv is read below for checks 4-8, but check 3 needs its video_time column first.
    prerows = []
    if states.exists():
        with open(states, newline="") as f:
            prerows = list(csv.DictReader(f))
    video_time_of = {}
    for r in prerows:
        vt = r.get("video_time")
        if vt not in (None, ""):
            try:
                video_time_of[r["Image_name"]] = float(vt)
            except ValueError:
                pass

    # 3. pixel-level overlay check (see module docstring)
    if Image is None:
        add(3, "no overlay in exported frames", False, "Pillow unavailable")
    elif not regular or not video.exists():
        add(3, "no overlay in exported frames", False, "no frames or no video to compare against")
    else:
        step = max(len(regular) // (sample_frames + 1), 1)
        picked = regular[step::step][:sample_frames] or regular[:sample_frames]
        results, ok3 = [], True
        with tempfile.TemporaryDirectory() as tmp:
            for p in picked:
                # Seek by video_time, NOT by the frame's index-as-seconds. Those differ by up to
                # ~0.9s and seeking on the wrong clock produces a large image difference that is
                # indistinguishable from a genuine overlay failure.
                sec = video_time_of.get(
                    p.name, float(re.match(r"frame_(\d{4})\.png", p.name).group(1)))
                clean = Path(tmp) / "clean.png"
                over = Path(tmp) / "over.png"
                d_clean = mad(p, clean, OVERLAY_BOX) if extract_frame(video, sec, clean) else None
                d_over = (mad(p, over, OVERLAY_BOX)
                          if video_ov.exists() and extract_frame(video_ov, sec, over) else None)
                if d_clean is None:
                    good, ratio = False, None
                elif d_over is None:
                    # No overlaid video to calibrate against (--no-overlay): fall back to the
                    # absolute ceiling. Weaker, and labelled as such in the detail string.
                    good, ratio = d_clean <= SAME_SOURCE_MAX_MAD, None
                else:
                    ratio = d_over / max(d_clean, 1e-6)
                    good = ratio >= OVERLAY_MIN_RATIO
                ok3 = ok3 and good
                results.append("{}: clean={} ov={} ratio={}".format(
                    p.name,
                    "n/a" if d_clean is None else "{:.2f}".format(d_clean),
                    "n/a" if d_over is None else "{:.2f}".format(d_over),
                    "n/a(abs)" if ratio is None else "{:.1f}x".format(ratio)))
        add(3, "no overlay in exported frames", ok3,
            "; ".join(results) + "  (need ratio>={}x in box {})".format(OVERLAY_MIN_RATIO, OVERLAY_BOX))

    # 4/5/6/7/8 all read states.csv
    rows = []
    if states.exists():
        with open(states, newline="") as f:
            rows = list(csv.DictReader(f))

    add(4, "states.csv rows == frame count", bool(rows) and len(rows) == len(pngs),
        "{} rows vs {} png".format(len(rows), len(pngs)))

    named = {r["Image_name"] for r in rows} if rows else set()
    on_disk = {p.name for p in pngs}
    add(5, "Image_name <-> frames/ is 1:1", bool(named) and named == on_disk,
        "in csv not on disk: {}; on disk not in csv: {}".format(
            sorted(named - on_disk) or "none", sorted(on_disk - named) or "none"))

    events = [r["event"] for r in rows if r.get("event")]
    add(6, "at least one event=min_dist", any("min_dist" in e for e in events),
        "events: {}".format(events or "none"))

    def numeric(col):
        out = []
        for r in rows:
            v = r.get(col, "")
            if v == "":
                continue
            try:
                x = float(v)
            except ValueError:
                continue
            if not math.isnan(x):
                out.append(x)
        return out

    head = numeric("robot_heading")
    ok7 = bool(head) and len(head) == len(rows) and (max(head) - min(head)) > 1e-6 \
        and all(-360.0 <= h <= 360.0 for h in head)
    add(7, "robot_heading sane (not all-zero / NaN)", ok7,
        "n={}/{} range [{:.2f}, {:.2f}]".format(len(head), len(rows), min(head), max(head))
        if head else "no numeric values")

    yaw = numeric("robot_ang_vel_y")
    nz = sum(1 for v in yaw if abs(v) > 1e-4)
    add(8, "vel_yaw has non-zero values", bool(yaw) and nz > 0,
        "n={}/{} nonzero={} max|.|={:.4f}".format(len(yaw), len(rows), nz, max((abs(v) for v in yaw), default=0.0))
        if yaw else "column absent or empty -- wrong topic/source?")

    return {"trial": str(d), "checks": checks, "pass": all(c["ok"] for c in checks)}


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("trial_dirs", nargs="+")
    p.add_argument("--tsv", action="store_true", help="one summary row per trial instead of full detail")
    p.add_argument("--json", action="store_true")
    args = p.parse_args()

    results = [check_trial(t) for t in args.trial_dirs]

    if args.json:
        print(json.dumps(results, indent=2))
    elif args.tsv:
        print("trial\tpass\t" + "\t".join("c{}".format(i) for i in range(1, 9)))
        for r in results:
            print("{}\t{}\t{}".format(
                Path(r["trial"]).name, "PASS" if r["pass"] else "FAIL",
                "\t".join("ok" if c["ok"] else "FAIL" for c in r["checks"])))
    else:
        for r in results:
            print("\n=== {} -- {} ===".format(Path(r["trial"]).name, "PASS" if r["pass"] else "FAIL"))
            for c in r["checks"]:
                print("  {} {}. {:<40} {}".format(
                    "ok  " if c["ok"] else "FAIL", c["n"], c["name"], c["detail"]))

    return 0 if all(r["pass"] for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
