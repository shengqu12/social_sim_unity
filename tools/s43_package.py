#!/usr/bin/env python3
"""Session 43 TASK 7: package the new-format trials as vlm_batch_v7.tar.gz.

Deliberately gated behind --approved. TASK 7 says to package only after a human has watched the
demo clips and signed off; a script that packages the moment the batch finishes would make that
step easy to skip, and the whole point of the demo batch is that someone looks before hundreds of
trials are produced in this format.

    python3 tools/s43_package.py --src <dir> --approved "watched all 14 _ov clips, format correct"

Writes vlm_batch_v7.tar.gz, its .sha256, and a README.md describing the format. Uses tar's hardlink
detection so video/pov_full.mp4 (a hardlink to the trial's own copy) is stored once, not twice.
"""
import argparse
import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path

DEFAULT_SRC = "/mnt/ssd/Social_Navigation/trial_outputs/demo_s43"


def sha256_of(path, chunk=1 << 20):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(chunk), b""):
            h.update(block)
    return h.hexdigest()


def collect_trials(src):
    return sorted(d for d in Path(src).iterdir()
                  if d.is_dir() and (d / "vlm_eval" / "states.csv").exists())


def build_readme(src, trials, approval):
    rows = []
    for d in trials:
        meta = {}
        p = d / "meta.json"
        if p.exists():
            try:
                meta = json.loads(p.read_text())
            except Exception:
                pass
        ve = meta.get("vlmEval") or {}
        n_png = len(list((d / "vlm_eval" / "frames").glob("*.png")))
        rows.append("| `{}` | {} | {} | {} | {} |".format(
            d.name, n_png, ve.get("eventFrames", "?"),
            meta.get("minDistMeters", "NA"),
            "pass" if ve.get("gatesAllOk") else "**did not pass all gates**"))

    return """# vlm_batch_v7

Trials in the Session 43 output format. Produced by `tools/run_trial.py`; packaged by
`tools/s43_package.py`.

Human sign-off recorded at packaging time: {approval}

## THIS BATCH IS FORMAT VALIDATION, NOT A SAFETY CENSUS

**N = 1 per configuration.** `min_dist` appears below because the pipeline records it. It must not
be quoted as a safety result. The standing rule in this project is worst-observed over N >= 5, and
a single sample supports no claim about worst-case clearance.

A trial that did not pass all gates is **still included, with its data complete**. That is
deliberate: a rejected trial is data, not garbage, and silently dropping it would make an absent
trial look downstream like a bad result rather than a missing one. Filter on `vlmEval.gatesAllOk`
in `meta.json` if you want only clean trials.

## Directory layout

```
<trial>/
  video/
    pov_full.mp4          <- "Unity output 1"
    pov_full_ov.mp4       <- telemetry burned in; HUMAN REVIEW ONLY, never a frame source
  vlm_eval/               <- "Unity output 2"
    frames/frame_0001.png ...
    states.csv
    README.md             <- per-trial units / frames / time origin
  frames.csv              <- the full 15 Hz log everything above is derived from
  meta.json
```

## `states.csv`

### The four agreed columns, first and unchanged

| column | meaning |
|---|---|
| `time` | seconds since the trial's t=0 (see "Time origin" below) |
| `Image_name` | exact filename in `frames/` -- capital I, as agreed |
| `robot_velocity` | ground-plane speed magnitude, m/s |
| `robot_heading` | Unity world yaw, degrees, [0, 360) |

### Appended columns, and why each exists

The four above cannot produce the ego-state a driving-style surrogate consumes, so these are
appended. A reader that selects columns by name is unaffected by their presence.

| column | why |
|---|---|
| `robot_vel_x/y/z` | signed velocity vector; the agreed `robot_velocity` is an unsigned magnitude and cannot give direction |
| `robot_speed_ground` | ground-plane speed; the legacy `frames.csv:robot_speed` includes vertical bob |
| `robot_ang_vel_y` | **yaw rate.** Cannot be recovered by differencing `robot_heading` at 1 Hz, so it is measured in-engine off the physics body |
| `robot_yaw_ros_rad`, `robot_ang_vel_ros` | the same heading and rate in ROS convention, unwrapped -- so a consumer never has to mix the two sign conventions |
| `cmd_vel_linear`, `cmd_vel_angular` | the commanded twist, i.e. the action |
| `robot_x`, `robot_z` | position on the ground plane |
| `lateral_offset_straightline` | offset from the start->goal straight line. **Read its caveat below before using it.** |
| `min_dist`, `nearest_ped_id`, `nearest_ped_x/z` | instantaneous clearance and which pedestrian it was |
| `phase` | `approach` / `encounter` / `depart` |
| `event` | `min_dist` / `encounter_start` / `encounter_end`, else empty |

## Units, coordinate frames, and one sign trap

Unity is **left-handed, Y-up**. ROS is **right-handed, Z-up**. The ground plane is Unity's
**(x, z)**; Unity's y is height. Position columns are therefore `robot_x` / `robot_z`, not
`robot_x` / `robot_y` -- naming the second ground axis "y" would collide with the vertical axis.

**`robot_heading` and `cmd_vel_angular` turn in opposite directions.** `robot_heading` is Unity
yaw: 0 = facing Unity +Z, increasing **clockwise** seen from above. `cmd_vel_angular` is ROS
REP-103 in `base_link`: **counter-clockwise** positive. This is inherent to the two conventions and
is present in every `frames.csv` this project has ever produced. `robot_yaw_ros_rad` and
`robot_ang_vel_ros` are provided so a consumer can stay entirely in ROS convention; the conversion
is a single sign flip applied in-engine.

### Time origin

`time = 0` is **not** when Unity started. It is the frame the robot-to-pedestrian ground distance
first crossed the trigger threshold, by which point the robot is already cruising. The preceding
pre-roll is not captured (`preRollDurationSec` in `meta.json`). Video time and `time` agree:
`pov_full.mp4` is assembled from the same frames at the achieved capture rate.

### Frame naming

`frame_0001.png` is t=1s, `frame_0002.png` is t=2s -- one frame per second, at native camera
resolution (1280x720). Frames named `frame_NNNN_e.png` are forced extra samples at moments 1 Hz
would miss; NNNN is still the second they fall in, so they never renumber the regular sequence. The
`event` column identifies them. Closing speeds here are ~1.8 m/s, so ~1.8 m of separation passes
between consecutive 1 Hz samples and the closest approach usually falls between two of them.

Frames are **not** downsampled. The 128x72 in the surrogate deck is that model's input size, to be
applied when consuming; storing at that size would be irreversible.

### `lateral_offset_straightline` -- two caveats

1. It is **not** the `lateral_offset_norm` of the driving-surrogate literature, which is offset from
   a lane centreline. This is offset from the robot's own start->goal straight line, **not** from
   the planner's path -- move_base publishes a global plan, but nothing in the running scene
   subscribes to it, so no path geometry reaches this pipeline. Different reference, different
   semantics; the name differs on purpose.
2. Its noise floor is high. A Session 34 control -- pedestrian parked 40 m away, physically unable
   to influence the robot -- still measured ~0.3-0.45 m of ambient lateral drift from the local
   planner itself. Deviations under ~0.5 m should not be read as avoidance behaviour.

### Why the frames cannot contain burned-in telemetry

They are exported from the raw per-frame JPGs written by the capture loop **before any overlay
exists**. Burn-in is applied later and only to `*_ov.mp4`. That structural fact is the guarantee.

A filename check also runs on every source path, but it inspects the *name*, not pixels -- do not
mistake it for overlay detection. `tools/s43_selfcheck.py` check 3 is the pixel-level test: it
compares each exported frame, inside the region the overlay writes into, against both videos and
requires it be much closer to the clean one.

## Contents

| trial | frames | event frames | min_dist (N=1, NOT a safety result) | gates |
|---|---|---|---|---|
{rows}
""".format(approval=approval, rows="\n".join(rows))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--src", default=DEFAULT_SRC)
    p.add_argument("--name", default="vlm_batch_v7")
    p.add_argument("--approved", required=True,
                   help="what the human actually checked, recorded verbatim into the README. "
                        "Required: TASK 7 packages only after a human has watched the demo clips.")
    args = p.parse_args()

    src = Path(args.src)
    trials = collect_trials(src)
    if not trials:
        print("no trials with vlm_eval/states.csv under {}".format(src), file=sys.stderr)
        return 1

    readme = src / "README.md"
    readme.write_text(build_readme(src, trials, args.approved))

    tarball = src.parent / "{}.tar.gz".format(args.name)
    members = [str(t.relative_to(src.parent)) for t in trials] + [str(readme.relative_to(src.parent))]
    # tar detects hardlinks and stores the second occurrence as a link, so video/pov_full.mp4 costs
    # nothing beyond its entry.
    r = subprocess.run(["tar", "-czf", str(tarball), "-C", str(src.parent)] + members)
    if r.returncode != 0:
        print("tar failed", file=sys.stderr)
        return 1

    digest = sha256_of(tarball)
    Path(str(tarball) + ".sha256").write_text("{}  {}\n".format(digest, tarball.name))

    print("packaged {} trial(s)".format(len(trials)))
    print("  {}  ({:.1f} MB)".format(tarball, tarball.stat().st_size / 1e6))
    print("  sha256 {}".format(digest))
    print("  approval recorded: {}".format(args.approved))
    return 0


if __name__ == "__main__":
    sys.exit(main())
