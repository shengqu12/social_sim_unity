# Project handoff — permanent constraints

Facts that are properties of the environment rather than of any one session's work. Each one cost
real time to establish; the point of recording them is that the next attempt starts from here.

## The Rocketbox avatar has "Optimize GameObjects" enabled — bone-level diagnostics are unavailable

Established Session 44 (TASK 5.4), while trying to determine a clip's authored pose.

At runtime the avatar's bone Transforms are **stripped from the hierarchy**, even though
`avatar.isHuman` reports `True`. Consequences:

- `Animator.GetBoneTransform(...)` returns `null` for every bone. Already hit once before, in
  Session 41, where the carried box's hand attachment failed for this reason and had to fall back
  to a body-relative anchor (`S41MixamoClipApplier.AttachBox`).
- `SkinnedMeshRenderer.bounds` after `AnimationClip.SampleAnimation` returns the **bind pose**, not
  the sampled pose. Bounds derive from `localBounds`, which is not recomputed per pose. Evidence:
  `Sitting` and `Standing Arguing` returned byte-identical bounds (minY −0.003, maxY 1.827, height
  1.830) at all five sampled timepoints, and those are unquestionably different poses.

So any diagnostic that needs to know where a character's limbs actually are cannot be written
against the production avatar. Remaining routes, neither attempted:

- Editor visual inspection.
- A separate diagnostic scene holding a **non-optimized copy** of the avatar. Note this means
  re-importing the shared Rocketbox asset, which changes every trial ever run with it — so it needs
  to be a copy, not a settings change on the shared original.

Probe left at `Assets/Scripts/AutoTrial/Editor/S44PoseProbe.cs`, non-functional by the above, kept
so the next attempt inherits the two dead ends.

## `AnimatorStateInfo.length` divides by both speeds

Established Session 44 (§9), after the figure sat unexplained across three work orders.

```
state_length = clip_length / (animator_speed * state_speed)
3.130        = 3.600       / (1.000 * 1.150)      <- observed exactly
```

Three consequences for anything written up:

1. Session 41's Surprised figure of **2.473 s is void** and must not be quoted. The apparent
   multiplicative reading (2.767 × 0.896 = 2.479) is a numerical coincidence; the value is not
   reproducible from its own recorded inputs, which give 2.767 / 0.896 = 3.088.
2. `AnimatorStateInfo.length` is a **nominal** duration, not the time anything was visible. Both
   reaction→locomotion transitions carry `hasExitTime=True, exitTime_norm=0.9000`, so the state is
   left at 90% of that length.
3. Report **visible** duration: the assertive gesture fix is **10.8 s → 2.82 s**, not
   "12.000 → 3.130".

## Run-to-run variance has no mechanical explanation

Established Session 43 (accidental control) and Session 44 (§5).

The same 18 configurations run twice with identical commands differed by up to **1.4 m** in
`min_dist` (`scared` 3.486 → 4.890). Frame-time jitter does not account for it: across the 18
trials, correlation with p99 frame interval is **+0.111** and with the fraction of intervals over
50 ms is **+0.142**. The stronger argument is that the >50 ms fraction is ~100% on *every* trial —
near-zero between-group variance cannot produce between-group spread of 1.0–1.4 m.

With n=18 the correct phrasing is **"no evidence for"**, not "absent".

Practical rule, applied since Session 44: a single trial's `min_dist` is never evidence that a fix
worked. Acceptance is by the objective self-tests (`tools/s44_selftest.py`) or by eye.
