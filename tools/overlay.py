#!/usr/bin/env python3
"""
Standalone post-processor: burns a per-frame telemetry overlay onto trial videos.

    python tools/overlay.py <trial_dir>              # one trial
    python tools/overlay.py --all trial_outputs/      # every trial dir found under a root

Reads frames.csv + meta.json from a trial directory (as written by run_trial.py) and produces
pov_full_ov.mp4 (burned-in subtitle overlay of pov_full.mp4 -- both are the Round 4/output-
format-v3 PRIMARY deliverable, kept permanently, not an internal scratch file), then re-cuts
near-pedestrian clips (pov_near_NN_ov.mp4, VLM-prefilter material) from that overlaid full using
the same span logic as run_trial.py (trial_lib.find_near_spans / cut_clip -- re-derived from
frames.csv, not copied from the originals, so it stays correct if near_dist ever changes).
Session 10 (D5): POV only -- the chase/third-person camera no longer exists.

Mechanism: generate one .ass subtitle track per trial and burn it in with a single ffmpeg pass
(libass -vf, not per-frame image compositing) -- zero frame re-rendering. Each row's own `t`
column defines its subtitle window [t_i, t_{i+1}); nominal/configured fps is never used for
timing (Session 1's achieved-vs-configured-fps lesson applies here too -- see run_trial.py's own
actual_achieved_fps()).

VLM-purity norm (Session 9, unchanged): *_ov.mp4 files are for HUMAN review only. Any VLM/model-
based scoring or evaluation pipeline must consume the non-overlaid originals (pov_near_NN.mp4),
never the *_ov.mp4 siblings. The overlay burns in dist_to_pedestrian, running min-distance, and a
near/far color cue directly onto the pixels -- exactly the kind of signal a proximity or social-
navigation-quality judgment task would ask a model to infer from the scene itself. Feeding it the
overlaid version lets the model read the answer off the frame instead of judging the scene,
silently corrupting the eval. Keep the two video sets strictly separated in any scoring pipeline
built on top of this tool.

--all archive-dir convention (Session 9): a top-level subdirectory of the scanned root is treated
as an internal/archive dir -- and excluded from a bare --all scan -- if its name starts with `_`
(e.g. _s6_cell1, _s7_landing, _s7_n6_verification). This keeps --all's default scope matched to
what index.html actually links (the named batch dirs) rather than also silently retrofitting every
forensics/cell archive this project has accumulated. Pass --include-archives to scan everything.
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
    footer event spanning the whole video. Session 10 (D5): fields = t, robot speed, appearance,
    personality, dist, min_dist -- top-left (ASS Alignment=7, already the style's value before
    this session; unchanged)."""
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
        text = "t={:.2f}s  speed={:.2f}m/s  dist={}  min_dist={}\\N{} / {}".format(
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


def process_trial_dir(trial_dir, near_dist=DEFAULT_NEAR_DIST, force=False,
                       near_clip_min_sec=trial_lib.DEFAULT_NEAR_CLIP_MIN_SEC):
    """Returns (ok: bool, detail: str). Session 10 (D5): POV only -- no chase/third-person camera
    exists anymore. Requires pov_full.mp4 -- present on every Round-4-or-later trial (output
    format v3 keeps it permanently); a pre-Round-4 trial with pov_full.mp4 already deleted by the
    old cleanup_full_video() cannot be overlaid standalone anymore."""
    trial_dir = Path(trial_dir)
    frames_csv = trial_dir / "frames.csv"
    meta_path = trial_dir / "meta.json"
    pov_full = trial_dir / "pov_full.mp4"
    pov_ov = trial_dir / "pov_full_ov.mp4"

    # Checked before requiring pov_full.mp4 to exist: a trial that already completed its overlay
    # pass (via run_trial.py's own default-on --overlay step) has pov_full.mp4 already deleted by
    # cleanup_full_video() (Session 10, D5) -- that is a valid "already done" state, not a missing-
    # input failure. Only a re-run (--force) or a trial that was never overlaid needs pov_full.
    existing_near_ov = sorted(trial_dir.glob("pov_near_*_ov.mp4"))
    if existing_near_ov and not force:
        eprint("[overlay] {}: pov_near_*_ov.mp4 already present, skipping (use --force to redo).".format(trial_dir.name))
        return True, "already overlaid"

    if not (frames_csv.exists() and meta_path.exists() and pov_full.exists()):
        return False, "missing frames.csv/meta.json/pov_full.mp4"

    meta = json.loads(meta_path.read_text())
    pov_duration = trial_lib.ffprobe_duration(pov_full)

    ass_pov = trial_dir / "overlay_pov.ass"
    build_ass(frames_csv, meta, near_dist, pov_duration, trial_dir.name, ass_pov)

    if not burn_overlay(pov_full, ass_pov, pov_ov):
        return False, "ffmpeg burn-in failed"

    spans = trial_lib.find_near_spans(frames_csv, near_dist, min_duration_sec=near_clip_min_sec)
    for i, (start, end) in enumerate(spans):
        pov_near_ov = trial_dir / "pov_near_{:02d}_ov.mp4".format(i)
        trial_lib.cut_clip(pov_ov, pov_near_ov, start, end)

    # Session 10 (D5 output-format spec: "exactly the near pairs + frames.csv + meta.json +
    # unity.log"): the .ass subtitle track is a pure ffmpeg-burn scratch artifact, not a
    # deliverable -- clean it up like pov_full.mp4/pov_full_ov.mp4 (deleted separately, by
    # run_trial.py's cleanup_full_video(), since this function doesn't own those).
    ass_pov.unlink(missing_ok=True)

    return True, "{} near clip(s) re-cut".format(len(spans))


def find_trial_dirs(root, include_archives=False):
    """A directory counts as a trial dir if it directly contains frames.csv + meta.json, and
    either pov_full.mp4 (not yet cleaned up -- process_trial_dir() can still build the overlay)
    or at least one pov_near_*.mp4 (already post-processed and cleaned up -- Session 10 (D5):
    pov_full.mp4 is an ephemeral scratch file by design, deleted by run_trial.py's
    cleanup_full_video() unless --keep-full, so requiring it unconditionally would make
    find_trial_dirs() go blind on every already-completed trial).

    Unless include_archives, skips anything under a top-level subdirectory of `root` whose name
    starts with `_` (the internal/archive-dir convention -- see module docstring)."""
    root = Path(root)
    found = []
    for meta in sorted(root.rglob("meta.json")):
        d = meta.parent
        has_full = (d / "pov_full.mp4").exists()
        has_near = any(d.glob("pov_near_*.mp4"))
        if not ((d / "frames.csv").exists() and (has_full or has_near)):
            continue
        if not include_archives:
            rel = d.relative_to(root)
            top = rel.parts[0] if rel.parts else ""
            if top.startswith("_"):
                continue
        found.append(d)
    return found


def generate_index_html(root, index_path, near_dist=DEFAULT_NEAR_DIST, include_archives=False):
    """Rewritten for the near-only output format in Session 10 (D5); Round 4 (Step 4) restored
    the full video as the primary block, shown first with its own whole-trial contact sheet, near
    clips listed below it as VLM-prefilter material. Fully regenerates index_path from the trial
    dirs found under root each call, rather than regex-patching an existing hand-authored file
    (the old update_index_html() approach). Trials with neither a full video nor near-spans are
    listed with an explicit note, not silently omitted."""
    import html as html_mod

    root = Path(root)
    dirs = find_trial_dirs(root, include_archives=include_archives)
    index_path = Path(index_path)

    def esc(s):
        return html_mod.escape(str(s))

    blocks = []
    for d in dirs:
        rel = d.relative_to(index_path.parent) if index_path.parent in d.parents or index_path.parent == d.parent else d
        meta = {}
        meta_path = d / "meta.json"
        if meta_path.exists():
            try:
                meta = json.loads(meta_path.read_text())
            except (json.JSONDecodeError, OSError):
                meta = {}
        cfg = meta.get("config", {})
        appearance = cfg.get("appearance", "?")
        personality = cfg.get("personality", "?")
        termination = meta.get("terminationReason", "?")
        min_dist = meta.get("minDistanceMeters", None)
        census = meta.get("agentCensus", [])
        stray_count = sum(1 for c in census if c.startswith("STRAY"))

        # Round 4 (Step 4, output format v3): the full POV video is now the primary deliverable --
        # shown first, with its own whole-trial contact sheet (8 frames spanning the trial, built
        # by run_trial.py's main()/trial_lib.build_contact_sheet on pov_full.mp4, filename recorded
        # into meta.json's fullContactSheet). Absent on trials captured before Round 4.
        full_html = []
        pov_full = d / "pov_full.mp4"
        pov_full_ov = d / "pov_full_ov.mp4"
        if pov_full.exists():
            rel_full = "{}/{}".format(esc(d.relative_to(root).as_posix()), esc(pov_full.name))
            aspect_ok = meta.get("aspectGateOk")
            aspect_extra = ""
            if aspect_ok is not None:
                aspect_extra = ' &middot; <span class="flag {}">aspect: {}</span>'.format(
                    "ok" if aspect_ok else "", "PASS" if aspect_ok else "FAIL")
            approach_ok = meta.get("approachGateOk")
            approach_extra = ""
            if approach_ok is not None:
                approach_extra = ' &middot; <span class="flag {}">approach: {}</span>'.format(
                    "ok" if approach_ok else "", "PASS" if approach_ok else "FAIL")
            full_html.append('<div class="video-block"><div class="label">full trial (primary deliverable){}{}</div>'
                              '<video controls preload="metadata" src="{}"></video></div>'.format(
                                  aspect_extra, approach_extra, rel_full))
            full_sheet_name = meta.get("fullContactSheet") or "contact_sheet_full.png"
            if (d / full_sheet_name).exists():
                rel_full_sheet = "{}/{}".format(esc(d.relative_to(root).as_posix()), esc(full_sheet_name))
                full_html.append('<div class="video-block contact-sheet"><div class="label">full-trial contact sheet (8 frames spanning the whole trial)</div>'
                                  '<img loading="lazy" src="{}" alt="full-trial contact sheet"></div>'.format(rel_full_sheet))
            if pov_full_ov.exists():
                rel_full_ov = "{}/{}".format(esc(d.relative_to(root).as_posix()), esc(pov_full_ov.name))
                full_html.append('<div class="video-block"><div class="label">full trial (overlay, human review only)</div>'
                                  '<video controls preload="metadata" src="{}"></video></div>'.format(rel_full_ov))

        near_clips = sorted(d.glob("pov_near_*.mp4"))
        near_clips = [c for c in near_clips if "_ov" not in c.stem]
        # Round 3 (THE PERMANENT GATE, part b): meta.json.nearClips (written by run_trial.py's
        # augment_trial_meta_with_gate()) carries each clip's duration/gate verdict/contact-sheet
        # filename, keyed by index -- absent on trials captured before Round 3, handled gracefully.
        near_clips_meta = {c["index"]: c for c in meta.get("nearClips", [])}

        clip_html = []
        for clip in near_clips:
            idx = clip.stem.split("_")[-1]
            idx_int = int(idx)
            clip_meta = near_clips_meta.get(idx_int, {})
            ov = d / (clip.stem + "_ov.mp4")
            rel_clip = "{}/{}".format(esc(d.relative_to(root).as_posix()), esc(clip.name))
            dur = clip_meta.get("durationSec")
            gate_ok = clip_meta.get("contentGateOk")
            label_extra = ""
            if dur is not None:
                label_extra += " &middot; {:.1f}s".format(dur)
            if gate_ok is not None:
                label_extra += ' &middot; <span class="flag {}">gate: {}</span>'.format(
                    "ok" if gate_ok else "", "PASS" if gate_ok else "FAIL")
            block = ['<div class="video-block"><div class="label">near clip {}{}</div>'
                     '<video controls preload="metadata" src="{}"></video></div>'.format(idx, label_extra, rel_clip)]

            sheet_name = clip_meta.get("contactSheet") or "contact_sheet_{}.png".format(idx)
            if (d / sheet_name).exists():
                rel_sheet = "{}/{}".format(esc(d.relative_to(root).as_posix()), esc(sheet_name))
                block.append('<div class="video-block contact-sheet"><div class="label">contact sheet (8 frames, one glance)</div>'
                             '<img loading="lazy" src="{}" alt="contact sheet for near clip {}"></div>'.format(rel_sheet, idx))

            if ov.exists():
                rel_ov = "{}/{}".format(esc(d.relative_to(root).as_posix()), esc(ov.name))
                block.append('<div class="video-block"><div class="label">near clip {} (overlay, human review only)</div>'
                             '<video controls preload="metadata" src="{}"></video></div>'.format(idx, rel_ov))
            clip_html.append("".join(block))

        stray_flag = '<span class="flag">D3: {} STRAY agent(s)</span>'.format(stray_count) if stray_count else '<span class="flag ok">D3: clean census</span>'

        body_parts = []
        if full_html:
            body_parts.append('<div class="video-grid">' + "".join(full_html) + '</div>')
        if clip_html:
            body_parts.append('<div class="section-label">near-pedestrian clips (VLM prefilter material)</div>')
            body_parts.append('<div class="video-grid">' + "".join(clip_html) + '</div>')
        if not body_parts:
            body_parts.append('<p class="stats">No full video or near-pedestrian spans found (min_dist={} m).</p>'.format(esc(min_dist)))
        body = "".join(body_parts)

        blocks.append("""
<div class="trial">
  <h2>{name}</h2>
  <div class="stats">{appearance} &times; {personality} &middot; termination: {termination} &middot; min_dist: {min_dist} m {stray_flag}</div>
  {body}
</div>""".format(name=esc(d.name), appearance=esc(appearance), personality=esc(personality),
                  termination=esc(termination), min_dist=esc(min_dist), stray_flag=stray_flag, body=body))

    html_out = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>AutoTrial review index (full video + near-clip prefilter)</title>
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
  :root {{ color-scheme: light dark; --bg:#fff; --fg:#1a1a1a; --muted:#666; --card:#f6f6f8; --border:#ddd; --accent:#2563eb; }}
  @media (prefers-color-scheme: dark) {{ :root {{ --bg:#14161a; --fg:#e8e8e8; --muted:#9aa0a6; --card:#1e2126; --border:#333; --accent:#7aa2ff; }} }}
  * {{ box-sizing: border-box; }}
  body {{ background: var(--bg); color: var(--fg); font-family: -apple-system, "Segoe UI", Roboto, sans-serif; max-width: 1100px; margin: 0 auto; padding: 24px 20px 80px; line-height: 1.5; }}
  h1 {{ font-size: 1.5rem; }}
  .subtitle {{ color: var(--muted); margin-bottom: 20px; }}
  .trial {{ border: 1px solid var(--border); border-radius: 12px; padding: 16px 18px; margin-bottom: 22px; background: var(--card); }}
  .trial h2 {{ margin: 0 0 6px; font-size: 1.1rem; }}
  .stats {{ color: var(--muted); font-size: 0.88rem; margin-bottom: 10px; }}
  .section-label {{ font-size: 0.78rem; text-transform: uppercase; letter-spacing: 0.03em; color: var(--muted); margin: 16px 0 8px; font-weight: 700; }}
  .video-grid {{ display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: 12px; }}
  .video-block {{ background: rgba(127,127,127,0.06); border-radius: 8px; padding: 8px; }}
  .video-block .label {{ font-size: 0.8rem; color: var(--muted); margin-bottom: 5px; font-weight: 600; }}
  .contact-sheet {{ grid-column: 1 / -1; overflow-x: auto; }}
  .contact-sheet img {{ display: block; height: 130px; width: auto; max-width: none; border-radius: 6px; }}
  video {{ width: 100%; border-radius: 6px; display: block; background: #000; }}
  .flag {{ display: inline-block; font-size: 0.75rem; background: #caa23a; color: #1a1a1a; border-radius: 4px; padding: 1px 6px; margin-left: 6px; font-weight: 600; }}
  .flag.ok {{ background: #3ecf6b; }}
</style>
</head>
<body>
<h1>AutoTrial review index -- full video (primary) + near-clip prefilter</h1>
<div class="subtitle">Round 4 output format v3: pov_full.mp4 (+ pov_full_ov.mp4 for human review)
is the primary deliverable, with its own whole-trial contact sheet. pov_near_NN.mp4 clips are
retained as VLM-prefilter material, not the primary output. Clean (non-_ov) videos = model/VLM
input; _ov siblings = human review only (VLM-purity norm, unchanged since Session 9).</div>
{blocks}
</body>
</html>
""".format(blocks="".join(blocks))

    index_path.write_text(html_out)
    eprint("[overlay] {} generated ({} trial(s)).".format(index_path, len(dirs)))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("trial_dir", nargs="?", help="a single trial directory")
    p.add_argument("--all", metavar="ROOT",
                   help="process every trial dir found under ROOT, EXCEPT top-level subdirs whose "
                        "name starts with `_` (internal/archive-dir convention -- pass "
                        "--include-archives to scan those too)")
    p.add_argument("--include-archives", action="store_true",
                   help="with --all, also scan `_`-prefixed archive/forensics dirs")
    p.add_argument("--near-dist", type=float, default=DEFAULT_NEAR_DIST)
    p.add_argument("--near-clip-min-sec", type=float, default=trial_lib.DEFAULT_NEAR_CLIP_MIN_SEC,
                   help="Round 3: must match the value run_trial.py cut the clean pov_near_NN.mp4 "
                        "clips with, or the *_ov re-cut spans will disagree with them.")
    p.add_argument("--force", action="store_true", help="redo even if *_ov.mp4 already exists")
    p.add_argument("--index", metavar="INDEX_HTML", default=None,
                   help="regenerate this index.html with overlay video blocks after processing")
    args = p.parse_args()

    if not args.trial_dir and not args.all:
        p.error("give a trial_dir or --all ROOT")

    dirs = find_trial_dirs(args.all, include_archives=args.include_archives) if args.all else [Path(args.trial_dir)]
    if not dirs:
        eprint("[overlay] no trial dirs found.")
        sys.exit(1)

    ok_count, fail_count = 0, 0
    for d in dirs:
        ok, detail = process_trial_dir(d, near_dist=args.near_dist, force=args.force,
                                        near_clip_min_sec=args.near_clip_min_sec)
        status = "OK" if ok else "FAILED"
        eprint("[overlay] {}: {} ({})".format(d, status, detail))
        ok_count += ok
        fail_count += not ok

    if args.index:
        index_root = Path(args.all) if args.all else Path(args.trial_dir).parent
        generate_index_html(index_root, args.index, near_dist=args.near_dist, include_archives=args.include_archives)

    print("overlay: {} ok, {} failed".format(ok_count, fail_count))
    sys.exit(0 if fail_count == 0 else 1)


if __name__ == "__main__":
    main()
