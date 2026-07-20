# Recording a trial (AutoTrial pipeline)

This is the operator doc for `tools/run_trial.py`, the CLI-driven capture pipeline that produces
robot-POV video of a scripted robot/pedestrian encounter, with zero manual Unity interaction.

This file lives at the repo root for discoverability. `README.md` at the repo root belongs to
upstream SEAN (Yale) and is not edited by this project's tooling — this file is additive, not a
replacement or pointer edit.

## 1. Quick start

```
python3 tools/run_trial.py --appearance dog_walker --personality indifferent
```

Three prerequisites, all checked automatically (the tool refuses to start, loudly, if any are
unmet):

1. **The Unity Editor must be closed** on this project. If it's open, you'll see:
   ```
   Refusing to start: the Unity Editor already has this project open
   (Temp/UnityLockfile is held by a live process). Close it first.
   ```
2. **The ROS backend (`ros` Docker container) must be running.** The tool health-checks it
   (`move_base` alive, `/map` present, goal topic has a subscriber) before launching Unity.
3. **The T7 drive must be mounted**, with `trial_outputs` resolving through the symlink onto it
   — output is deliberately refused elsewhere rather than silently falling back to the internal
   disk. If T7 isn't mounted, you'll see:
   ```
   [run_trial] REFUSING TO START: output root sentinel missing (...). Resolved path: ...
   This means either trial_outputs isn't the symlink onto the T7 drive, or T7 isn't
   mounted -- writing here would silently land on the internal disk. Mount T7 (or
   restore the trial_outputs -> /media/sheng/T7/Social_Navigation/trial_outputs symlink)
   before running trials.
   ```

## 2. What you get

Output lands in `trial_outputs/<appearance>_<personality>_<timestamp>/` (or `--out DIR` if given).
`trial_outputs` is not inside this repo — it's `~/Desktop/research/social_navigation/trial_outputs`,
itself a symlink onto `/media/sheng/T7/Social_Navigation/trial_outputs`.

Per trial:

| File | What it is |
|---|---|
| `pov_full.mp4` | The primary deliverable — full-trial robot-POV video, clean (no burned-in overlay). |
| `pov_full_ov.mp4` | Same video with a burned-in telemetry overlay (distance, min-distance, near/far cue) — for human review only, see §6. |
| `pov_near_NN.mp4` / `pov_near_NN_ov.mp4` | Only present if `min_dist` crossed `--near-dist` at some point in the trial — a clip (and its overlay sibling) cut around that closest-approach window, retained as VLM-prefilter material, not the primary output. |
| `frames.csv` | Per-frame robot/pedestrian telemetry (position, yaw, speed, distances, cmd_vel, camera pose). |
| `meta.json` | Full trial config as run, gate verdicts, resolved geometry/camera values, spin-phase breakdown. |
| `contact_sheet_full.png` (+ `contact_sheet_NN.png` per near clip) | An 8-frame evenly-sampled strip for one-glance human QA. |
| `unity.log` | Full Unity Editor batchmode log for this trial. |

All of the above (except the `_ov` files, if `--no-overlay` is passed) are enumerated and checked
present by the permanent file-manifest gate — a missing file fails the trial loudly rather than
silently shipping an incomplete deliverable set.

## 3. Choosing a pedestrian

**Zone A** — any Rocketbox pedestrian, referenced in `snake_case` (converted internally to the
Rocketbox `PascalCase` prefab name, e.g. `business_male_01` → `Business_Male_01`). Personality is
your choice, one of:

- `indifferent` — no reaction to the robot.
- `curious` — reacts with interest.
- `scared` — reacts with avoidance/fear.
- `surprised` — startle reaction.
- `assertive` — suppresses its own yielding/repulsion behavior (known to trigger elevated
  navigation-stack spin near the robot — see §8).

**Zone B** — 8 preset "special character" appearances. Each locks its own behavior; a
`--personality` you pass is ignored (with a warning) for these. Current status, verified against
the code (not asserted from memory):

| Appearance | Status |
|---|---|
| `cyclist` | OK |
| `dog_walker` | OK |
| `female_child` | OK |
| `male_child` | OK |
| `phone_user` | OK — fixed Session 21 (canonical container rewired to `PhoneUser_Ped.prefab` + `PhoneUser_TextingController` via `PrefabUtility`/`SerializedObject`, verified by a real trial). |
| `scooter_user` | OK |
| `wheelchair_user` | OK, but **female avatar only** — the wheelchair-male package has no importable prefab (out of scope for v1). |
| `white_cane_user` | OK |

## 4. Key parameters

Extracted from `python3 tools/run_trial.py --help` (live, current code) — every default below is
read from the argparse definitions, not asserted from memory.

| Flag | Default | What it does |
|---|---|---|
| `--appearance` | *(required)* | Zone A snake_case name or one of the 8 Zone B presets above. |
| `--personality` | `indifferent` | Ignored (with a warning) for Zone B. |
| `--duration` | `90.0` (seconds) | Hard cap on capture length regardless of anything else. |
| `--fps` | `15` | Capture rate. |
| `--near-dist` | `3.0` (meters) | Threshold for cutting `pov_near_NN.mp4` clips. |
| `--ped-distance` | `25.0` (meters) | Distance from the robot's start, along the start→goal bearing, that defines both the trial's dist0 target AND the live release trigger (the pedestrian is frozen further out and released the instant this distance is first crossed, robot already cruising). Was `8.0` through Session 16. |
| `--slate-margin` | `4.0` (meters) | Extra distance beyond `--ped-distance` at which the pedestrian actually spawns, frozen. |
| `--post-encounter-grace` | off (`None`) | Ends capture this many seconds after the pedestrian is passed and moving away again, instead of filming the full `--duration` (mostly empty post-encounter driving). Recommended for a clean "story" clip — e.g. `--post-encounter-grace 8.0`. |
| `--cam-height` | `0.32` (meters) | Absolute camera height above ground, verified by a downward raycast at rig build time (cited: A1 stands ~0.40m tall, RealSense D435i lens ~0.30-0.32m). |
| `--cam-pitch` | `0.0` (degrees) | Constant camera pitch, LEVEL by default (positive = up). |
| `--warmup` / `--no-warmup` | on | Primes a fresh ROS session with a real nav cycle before the trial. |
| `--fresh-ros` | off | Tears down and relaunches the ROS bringup cleanly instead of reusing it. |
| `--keep-full` | off | Keeps the raw per-frame JPG directory and `config.json` after assembly (unrelated to `pov_full.mp4`, which is always kept). |
| `--no-overlay` | overlay on by default | Skips the `_ov` burn-in pass entirely. |
| `--spawn X Y Z YAW_DEG` | computed from `--ped-distance` | Overrides the pedestrian spawn pose entirely. |
| `--goal X Y Z YAW_DEG` | the corridor's far end | Overrides the robot's goal pose. |
| `--windowed` | off (batchmode) | Drops `-batchmode` — black-frame fallback path. |

Run `python3 tools/run_trial.py --help` for the complete, current list (patrol waypoints, ped-goal
override, yaw smoothing, jpg quality, trial position, and diagnostic-only flags are omitted above
for brevity but are all live).

## 5. How a trial works

After ROS warmup priming, the pedestrian spawns frozen (`--ped-distance + --slate-margin` from the
robot's start, facing it) while the robot's goal is published and it settles into a normal cruise;
the pedestrian is released and capture begins in the same frame the live distance first drops to
`--ped-distance` (so the robot is already moving, not standing, at t=0, and dist0 is correct by
construction); capture continues through the encounter and, if `--post-encounter-grace` is set,
ends shortly after the pedestrian is passed; the pipeline then runs its permanent gates (content,
aspect, approach-geometry, trigger-speed, file-manifest), burns the human-review overlay, and
regenerates `index.html`.

## 6. Viewing results

Start at `trial_outputs/index.html` (regenerate with, from the repo root:
`python3 tools/overlay.py --all /media/sheng/T7/Social_Navigation/trial_outputs --index
/media/sheng/T7/Social_Navigation/trial_outputs/index.html` — use the absolute path, not the bare
`trial_outputs` relative name; `tools/overlay.py` resolves paths relative to the current working
directory, not to `run_trial.py`'s own `DEFAULT_OUT_ROOT`, so a relative `trial_outputs` run from
the repo root silently finds nothing) if it's stale — it lists every trial with its full video,
full contact sheet, and near clips. Use the contact sheets for one-glance QA before scrubbing any
video.

**VLM-purity norm** (quoted from `tools/overlay.py`'s own module docstring, unchanged since
Session 9): *"`*_ov.mp4` files are for HUMAN review only. Any VLM/model-based scoring or evaluation
pipeline must consume the non-overlaid originals (`pov_near_NN.mp4`), never the `*_ov.mp4`
siblings. The overlay burns in `dist_to_pedestrian`, running min-distance, and a near/far color
cue directly onto the pixels — exactly the kind of signal a proximity or social-navigation-quality
judgment task would ask a model to infer from the scene itself. Feeding it the overlaid version
lets the model read the answer off the frame instead of judging the scene, silently corrupting the
eval."* In short: clean (non-`_ov`) files are model input; `_ov` siblings are for you.

## 7. Troubleshooting

| Symptom | Meaning |
|---|---|
| `Refusing to start: the Unity Editor already has this project open` | `Temp/UnityLockfile` is held by a live process — close the Editor. A *stale* lockfile with no live holder is removed automatically, no action needed. |
| `REFUSING TO START: output root sentinel missing` | T7 isn't mounted, or the `trial_outputs` symlink is broken — see §1. |
| `REFUSING TO START: only X.XXGB free` | Less than 5GB free at the resolved output root (checked at trial start and again before video assembly). Free up space on T7. |
| `content gate: FAILED` | A sampled frame (from `pov_full.mp4` and/or a near clip) was statistically uniform gray/black — a batchmode render failure, not a real scene. |
| `aspect gate: FAILED` | The rendered POV camera's aspect ratio doesn't match the 1280x720 render target within 0.01 — should not happen; the in-engine rig-build assert should have already refused the trial before this gate could even run. |
| `approach geometry gate: FAILED` | Either dist0 isn't within 0.3m of `--ped-distance`, or `dist_to_pedestrian` didn't decrease monotonically (noise-tolerant) from frame 0 to the closest-approach frame. |
| `trigger-speed gate: FAILED` | The robot's speed at the release trigger was under 0.3 m/s — it was standing, not cruising, at t=0. |
| `file manifest gate: FAILED` | One or more expected deliverables (§2's table) are missing from the output directory. |
| `overlay: FAILED` | The `_ov` burn-in pass failed (e.g. an ffmpeg error) — now a hard gate failure, not a silent skip. |

Any gate failure exits non-zero; the full artifact set is still left on disk for forensics.

## 8. Known limitations

- **Encounter-phase spin residual.** The robot's local planner shows elevated in-place rotation
  near the pedestrian during the actual encounter, worst for the `assertive` personality (which
  suppresses the pedestrian's own yielding behavior). Confirmed, powered (N=6 per config)
  navigation-stack defect, out of this pipeline's editable scope (`move_base`/DWA local planner)
  — see `trial_outputs/REPORT.md` and `HOWARD_HANDOFF.md`'s Track-2 section for the full
  measurement history and current numbers.
- **`goal_reached` is uncommon in the specific configurations most sessions have battery-tested
  (Sessions 12-17 used `--post-encounter-grace` and/or a shorter `--duration`, both of which end
  the trial before the robot travels the full ~44m corridor), but it is NOT decorative — corrected
  here after this doc's own acceptance run reached it. Verified live: the bare quick-start command
  in §1 (`--duration` at its 90s default, no `--post-encounter-grace`, `dog_walker`/`indifferent`,
  low spin that trial) actually terminated with `terminationReason: goal_reached` at t≈66.4s,
  well inside the 90s budget. Whether a given trial reaches the goal in practice depends on
  `--duration`, whether `--post-encounter-grace` is set, and how much of the travel budget spin
  eats along the way (worse for `assertive`, see the item above) — not a fixed property of the
  pipeline.
- **Trigger-speed occasional resampling.** The robot's speed reading at the release trigger is
  occasionally implausible (a real, brief sub-frame position correction, not sensor noise) — when
  detected, it's rejected and resampled on the next frame rather than recorded, and
  `meta.json.triggerSpeedResampled` is set `true` so this is visible, not silent. The underlying
  mechanism isn't fully confirmed.
