#!/usr/bin/env python3
"""
Standalone post-processor: burns a per-frame telemetry overlay onto trial videos.

    python tools/overlay.py <trial_dir>              # one trial
    python tools/overlay.py --all trial_outputs/      # every trial dir found under a root

Reads frames.csv + meta.json from a trial directory (as written by run_trial.py) and produces
*_ov.mp4 siblings of pov_full.mp4 / tp_full.mp4 with a burned-in subtitle overlay, then re-cuts
near-pedestrian clips from the *overlaid* fulls using the same span logic as run_trial.py
(trial_lib.find_near_spans / cut_clip -- re-derived from frames.csv, not copied from the
originals, so it stays correct if near_dist ever changes).

Mechanism: generate one .ass subtitle track per trial and burn it in with a single ffmpeg pass
(libass -vf, not per-frame image compositing) -- zero frame re-rendering. Each row's own `t`
column defines its subtitle window [t_i, t_{i+1}); nominal/configured fps is never used for
timing (Session 1's achieved-vs-configured-fps lesson applies here too -- see run_trial.py's own
actual_achieved_fps()).
"""
import argparse
import csv
import json
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import trial_lib

DEFAULT_NEAR_DIST = 3.0  # matches run_trial.py's --near-dist default; not persisted into
                          # meta.json/config.json by older trials, so this is the best available
                          # default when re-deriving spans standalone. Override with --near-dist.


def eprint(*a, **kw):
    print(*a, file=sys.stderr, **kw)
    sys.stderr.flush()


def ffprobe_duration(video_path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "default=noprint_wrappers=1:nokey=1", str(video_path)],
        capture_output=True, text=True)
    try:
        return float(result.stdout.strip())
    except ValueError:
        return None


def ass_time(t):
    if t < 0:
        t = 0.0
    h = int(t // 3600)
    m = int((t % 3600) // 60)
    s = t % 60
    return "{:d}:{:02d}:{:05.2f}".format(h, m, s)


def build_ass(frames_csv, meta, near_dist, video_duration, trial_label, out_path):
    """One dialogue event per frames.csv row (timed on that row's own `t`, window =
    [t_i, t_{i+1}), last row extended to the real video duration from ffprobe) plus one static
    footer event spanning the whole video."""
    with open(frames_csv, newline="") as f:
        rows = list(csv.DictReader(f))
    if not rows:
        raise ValueError("frames.csv has no rows: {}".format(frames_csv))

    appearance = meta.get("config", {}).get("appearance", "?")
    personality = meta.get("config", {}).get("personality", "?")
    termination = meta.get("terminationReason", "?")

    header = """[Script Info]
ScriptType: v4.00+
PlayResX: 1280
PlayResY: 720
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Frame,Consolas,20,&H00FFFFFF,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,2,0,7,14,14,14,1
Style: Near,Consolas,20,&H000040FF,&H000000FF,&H00000000,&H80000000,1,0,0,0,100,100,0,0,1,2,0,7,14,14,14,1
Style: Footer,Consolas,15,&H00CCCCCC,&H000000FF,&H00000000,&H80000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,8,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""

    lines = [header]
    n = len(rows)
    running_min = None
    for i, row in enumerate(rows):
        t = float(row["t"])
        t_next = float(rows[i + 1]["t"]) if i + 1 < n else (video_duration if video_duration else t + 0.1)
        if t_next <= t:
            continue  # degenerate/duplicate timestamp -- skip rather than emit a zero-length event

        speed = float(row.get("robot_speed", 0.0) or 0.0)
        dist = row.get("dist_to_pedestrian", "")
        dist_f = float(dist) if dist not in ("", None) else None
        md = row.get("min_dist", "")
        md_f = float(md) if md not in ("", None) else dist_f
        if md_f is not None:
            running_min = md_f if running_min is None else min(running_min, md_f)

        is_near = md_f is not None and md_f < near_dist
        style = "Near" if is_near else "Frame"

        dist_str = "{:.2f}m".format(dist_f) if dist_f is not None else "n/a"
        min_str = "{:.2f}m".format(running_min) if running_min is not None else "n/a"
        text = "t={:.2f}s  speed={:.2f}m/s  dist={}  min_so_far={}\\N{} / {}".format(
            t, speed, dist_str, min_str, appearance, personality)

        lines.append("Dialogue: 0,{},{},{},,0,0,0,,{}".format(
            ass_time(t), ass_time(t_next), style, text))

    footer_end = video_duration if video_duration else float(rows[-1]["t"])
    footer_text = "trial: {}  |  termination: {}".format(trial_label, termination)
    lines.append("Dialogue: 1,{},{},Footer,,0,0,0,,{}".format(
        ass_time(0.0), ass_time(footer_end), footer_text))

    out_path.write_text("\n".join(lines) + "\n")


def burn_overlay(src_mp4, ass_path, out_mp4):
    # subtitles filter needs a path ffmpeg's internal path-escaping can parse; simplest robust
    # form is to cd into the ass file's directory and reference it by bare filename.
    cmd = ["ffmpeg", "-y", "-i", str(src_mp4),
           "-vf", "ass={}".format(ass_path.name),
           "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "copy",
           str(out_mp4)]
    result = subprocess.run(cmd, capture_output=True, text=True, cwd=str(ass_path.parent))
    if result.returncode != 0:
        eprint("[overlay] ffmpeg failed on {}: {}".format(src_mp4, result.stderr[-2000:]))
        return False
    return True


def process_trial_dir(trial_dir, near_dist=DEFAULT_NEAR_DIST, force=False):
    """Returns (ok: bool, detail: str)."""
    trial_dir = Path(trial_dir)
    frames_csv = trial_dir / "frames.csv"
    meta_path = trial_dir / "meta.json"
    pov_full = trial_dir / "pov_full.mp4"
    tp_full = trial_dir / "tp_full.mp4"

    if not (frames_csv.exists() and meta_path.exists() and pov_full.exists() and tp_full.exists()):
        return False, "missing frames.csv/meta.json/pov_full.mp4/tp_full.mp4"

    pov_ov = trial_dir / "pov_full_ov.mp4"
    tp_ov = trial_dir / "tp_full_ov.mp4"
    if pov_ov.exists() and tp_ov.exists() and not force:
        eprint("[overlay] {}: *_ov.mp4 already present, skipping (use --force to redo).".format(trial_dir.name))
        return True, "already overlaid"

    meta = json.loads(meta_path.read_text())

    pov_duration = ffprobe_duration(pov_full)
    tp_duration = ffprobe_duration(tp_full)

    ass_pov = trial_dir / "overlay_pov.ass"
    ass_tp = trial_dir / "overlay_tp.ass"
    build_ass(frames_csv, meta, near_dist, pov_duration, trial_dir.name, ass_pov)
    build_ass(frames_csv, meta, near_dist, tp_duration, trial_dir.name, ass_tp)

    ok_pov = burn_overlay(pov_full, ass_pov, pov_ov)
    ok_tp = burn_overlay(tp_full, ass_tp, tp_ov)
    if not (ok_pov and ok_tp):
        return False, "ffmpeg burn-in failed"

    spans = trial_lib.find_near_spans(frames_csv, near_dist)
    for i, (start, end) in enumerate(spans):
        pov_near_ov = trial_dir / "pov_near_{:02d}_ov.mp4".format(i)
        tp_near_ov = trial_dir / "tp_near_{:02d}_ov.mp4".format(i)
        trial_lib.cut_clip(pov_ov, pov_near_ov, start, end)
        trial_lib.cut_clip(tp_ov, tp_near_ov, start, end)

    return True, "{} near clip(s) re-cut".format(len(spans))


def find_trial_dirs(root):
    """A directory counts as a trial dir if it directly contains frames.csv + meta.json."""
    root = Path(root)
    found = []
    for meta in sorted(root.rglob("meta.json")):
        d = meta.parent
        if (d / "frames.csv").exists() and (d / "pov_full.mp4").exists():
            found.append(d)
    return found


def update_index_html(index_path, near_dist=DEFAULT_NEAR_DIST):
    import re
    index_path = Path(index_path)
    if not index_path.exists():
        eprint("[overlay] {} not found, skipping index update.".format(index_path))
        return
    html = index_path.read_text()
    root = index_path.parent

    pattern = re.compile(
        r'(<div class="video-block"><div class="label">([^<]*)</div>'
        r'<video controls preload="metadata" src="([^"]+\.mp4)"></video></div>)'
    )

    def repl(m):
        block, label, src = m.group(1), m.group(2), m.group(3)
        src_path = root / src
        ov_name = src_path.stem + "_ov" + src_path.suffix
        ov_path = src_path.parent / ov_name
        if not ov_path.exists():
            return block
        ov_src = "{}/{}".format(Path(src).parent.as_posix(), ov_name)
        ov_block = ('<div class="video-block"><div class="label">{} (overlay)</div>'
                    '<video controls preload="metadata" src="{}"></video></div>').format(label, ov_src)
        return block + ov_block

    new_html = pattern.sub(repl, html)
    if new_html != html:
        index_path.write_text(new_html)
        eprint("[overlay] {} updated with overlay video blocks.".format(index_path))
    else:
        eprint("[overlay] {}: no matching overlay files found, left unchanged.".format(index_path))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("trial_dir", nargs="?", help="a single trial directory")
    p.add_argument("--all", metavar="ROOT", help="process every trial dir found under ROOT")
    p.add_argument("--near-dist", type=float, default=DEFAULT_NEAR_DIST)
    p.add_argument("--force", action="store_true", help="redo even if *_ov.mp4 already exists")
    p.add_argument("--index", metavar="INDEX_HTML", default=None,
                   help="regenerate this index.html with overlay video blocks after processing")
    args = p.parse_args()

    if not args.trial_dir and not args.all:
        p.error("give a trial_dir or --all ROOT")

    dirs = find_trial_dirs(args.all) if args.all else [Path(args.trial_dir)]
    if not dirs:
        eprint("[overlay] no trial dirs found.")
        sys.exit(1)

    ok_count, fail_count = 0, 0
    for d in dirs:
        ok, detail = process_trial_dir(d, near_dist=args.near_dist, force=args.force)
        status = "OK" if ok else "FAILED"
        eprint("[overlay] {}: {} ({})".format(d, status, detail))
        ok_count += ok
        fail_count += not ok

    if args.index:
        update_index_html(args.index, near_dist=args.near_dist)

    print("overlay: {} ok, {} failed".format(ok_count, fail_count))
    sys.exit(0 if fail_count == 0 else 1)


if __name__ == "__main__":
    main()
