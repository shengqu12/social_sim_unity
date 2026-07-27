# Session 41 — TASK 6.1 survival-screen failures

> **Session 43 update — the glide is fixed; the grounding defect is not.**
>
> `--ped-motion standing` was tested (the "suspected fix, not yet verified" below) and works. Net
> pedestrian displacement, N=1 per clip, measured from `frames.csv` in
> `trial_outputs/s43_verify/standing_*`:
>
> | clip | S41 net | S43 net, `--ped-motion standing` | path |
> |---|---|---|---|
> | `Sitting` | 14.04 m | **0.211 m** | 0.454 m |
> | `Standing_Arguing` | 14.04 m | **0.012 m** | 0.211 m |
> | `Talking_standing` | 14.04 m | **0.012 m** | 0.207 m |
> | `Stroke_Shaking_Head` | 14.04 m | **0.012 m** | 0.217 m |
>
> The mechanism is exactly the one diagnosed below: the translation was `SFAgent` walking to its
> release destination, and the flag pins that destination to the spawn pose
> (`AutoTrialBootstrap.cs:1076`). No new code.
>
> **`Stroke_Shaking_Head` is only half fixed.** Its second defect — not grounded — is *vertical*,
> and a horizontal-destination flag cannot address it. Note what the table above can and cannot
> show: `frames.csv` logs `pedestrian_x` and `pedestrian_z` but **no `pedestrian_y`**, so grounding
> is not measurable from the pipeline's data at all. Its 0.012 m displacement is evidence about
> gliding and *no* evidence about floating. Treat this clip as unresolved until someone watches it.
>
> Still open: whether the 0.32 m laser plane intersects a seated figure. Now testable, since
> `Sitting` holds station — but not yet tested.

Screen: `business_male_01` × indifferent × `--profile scoring`, N=2 per clip, open field.
Raw data: `trial_outputs/demo_s41/screen61/results.tsv`, per-run `meta.json`, contact sheets.
Index with per-run displacement figures: `trial_outputs/demo_s41/INDEX.md`.

All 18 runs exited 0 and every run labelled `safe` (worst min_dist 0.643m, `carry_and_walk_01`).
**That is not the screen's verdict.** A clean exit and a safe label say nothing about whether the
animation is visually correct, which is what 6.1 actually asks. The verdict below comes from the
contact sheets plus an objective displacement measure.

## The measurement that exposed it

`tools/s41_make_index.py` reports, per run, net start→end displacement and summed per-frame path
length from `frames.csv`. Result across **all nine clips**, moving and stationary alike:

```
net ≈ 14.04 m   (identical to 2 decimals for every clip)
path ≈ 15.5 – 19.1 m
```

Net displacement is the same for a sitting animation as for a running one. So the character's
translation is **not** coming from the clip's root motion at all — it is `SFAgent`/`Base` driving
the transform toward its goal, with the clip only changing the visible pose. Locking root
position/height/rotation at import (`S41MixamoImport`) stopped the *clip* from translating; it did
nothing about the *agent* walking to its destination.

This is the "idle 平移" failure mode the ticket warned about, and it is why the screen's third
question (foot-slide / root motion not matching displacement) is the one that matters here.

## FAIL — stationary clips glide (4 of 9)

`Sitting`, `Standing_Arguing`, `Talking_standing`, `Stroke_Shaking_Head`.

Visually confirmed on the contact sheets: the character holds a static pose (seated, standing,
talking) while sliding ~14 m across the ground. `Sitting` is the clearest — a seated figure, with
no chair, gliding toward the camera.

**These four do not advance to 6.2.**

**Suspected fix, not yet verified:** `run_trial.py` already has `--ped-motion standing`, which
pins the pedestrian's destination to its spawn pose so the social-force target speed is zero.
Combining `--mixamo-clip Sitting --ped-motion standing` should hold station and let the clip play
in place. This needs **no new code** — it is an existing flag. Untested as of end of Session 41;
verify before using these four clips for anything.

## FAIL (worse) — `Stroke_Shaking_Head` is not grounded

Reproduced in **both** runs (`Stroke_Shaking_Head_01`, `_02`). The character renders as a dark
mass floating well above the ground plane, near the top of frame and above the horizon — fully
detached from the surface, not merely sliding along it.

This is a distinct and more severe defect than the glide above, and `--ped-motion standing` should
**not** be assumed to fix it: the problem is vertical placement, not destination. Likely the clip's
root sits at a very different height (the source animation involves the body going down), which
interacts badly with `lockRootHeightY` and the agent's own ground placement. Needs its own
diagnosis.

## PASS — moving clips (5 of 9)

`carry_and_walk`, `Drunk_Walk`, `Old_Man_Walk`, `Pacing_And_Talking_On_A_Phone`, `Running`.

All four screening questions read correctly on the contact sheets: the character moves, the clip
plays (no T-pose/A-pose freeze), it is upright and grounded, and it faces its direction of travel.
`carry_and_walk` additionally carries the TASK 4 box correctly at 1.11 m.

**Caveat worth carrying forward:** because translation is agent-driven rather than root-motion
driven (see above), stride length is not guaranteed to match ground speed for any of these.
`S32AnimatorSpeedScaler` mitigates it by scaling `animator.speed` to measured speed, but a run
cycle covering more ground per stride than the character actually travels will still read slightly
off. `Running` is the most exposed to this. Not a blocker for 6.2; worth a look if the footage is
used for anything speed-sensitive.

## Not tested this session

Whether the robot's 0.32 m laser plane actually intersects a seated figure's collider. The ticket
flags this as a real perception question and an intentional negative-sample source rather than a
bug. It cannot be answered until `Sitting` holds station — see the `--ped-motion standing` note
above.
