# `Stroke_Shaking_Head` — permanently excluded

Not a temporary deferral. This clip is removed from the roster and will not appear in any
deliverable.

## Cause chain

1. **S41 6.1** — failed the survival screen twice, with *two* defects: it glided ~14 m like the
   other stationary clips, and additionally rendered as a mass floating well above the ground.
2. **S42 TASK A** (landed in S44) — `--ped-motion standing` fixed the glide: net displacement
   14.04 m → **0.012 m**. The float was untouched, because that flag governs the horizontal
   release destination and the float is vertical.
3. **S43** — recorded as undecidable from pipeline data: `frames.csv` carried `pedestrian_x` and
   `pedestrian_z` but no `pedestrian_y`, so the 0.012 m displacement was evidence about gliding and
   *no* evidence about floating.
4. **S44 5.4** — `pedestrian_y` and `pedestrian_ground_y` were added, making grounding
   auto-checkable, but diagnosing the clip's authored pose (the prerequisite for fixing it) failed
   twice against the avatar limitation below.

## Why it is not merely deferred

Its role is covered. The stationary-pedestrian category is represented by `Sitting` (with a stool,
S44 5.2) and `Standing_Arguing` (two-person, S44 5.3), both of which pass the automated checks.

And its intended reading does not survive the pipeline. The clip is animation-only and retargets
onto a Rocketbox business avatar; the "homeless person" identity the clip was chosen for is not
recoverable from a man in a suit performing it. Fixing the float would yield a correctly-grounded
character still failing to convey what it was included to convey.

## The avatar limitation that blocked the diagnosis

Recorded separately in `PROJECT_HANDOFF.md` because it is permanent and general, not specific to
this clip.

Two independent measurement routes, both defeated:

| route | result |
|---|---|
| `Animator.GetBoneTransform(Hips/Feet/Head)` | every bone `n/a` — the Rocketbox avatar is imported with **Optimize GameObjects**, which strips bone Transforms from the hierarchy even though `avatar.isHuman` is `True` |
| `SkinnedMeshRenderer.bounds` after `clip.SampleAnimation` | bounds identical across clips and timepoints — they derive from `localBounds` and are not recomputed per sampled pose |

The second result is what makes this conclusive rather than inconclusive: `Sitting` and
`Standing Arguing` returned **byte-identical** bounds (minY −0.003, maxY 1.827, height 1.830) at all
five sampled times. Those are unquestionably different poses, so the probe was reading the bind
pose, not the animation.

Probe kept at `Assets/Scripts/AutoTrial/Editor/S44PoseProbe.cs` so the next attempt starts from the
two dead ends rather than rediscovering them.
