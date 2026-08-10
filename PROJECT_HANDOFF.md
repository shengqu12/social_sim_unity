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

## Hidden guardian components silently revert spatial and state intent

Established Session 68 (four instances, all found the same way — by measuring a value that had
already been set correctly and finding it changed back).

This codebase carries a layer of per-frame guardian components that exist to correct earlier
defects. Each one asserts something every frame, and each will therefore quietly undo a *new*
intent that happens to occupy the same variable. Four confirmed cases:

| component | what it re-asserts | what it broke |
|---|---|---|
| `S44ClipProps` | `applyRootMotion`, re-asserted for 5 frames | any capture-then-restore of that flag races it |
| `INavigable` / `Parameters.CLOSE_ENOUGH_MIN_DIST` | destinations nearer than **1.0 m** count as already reached | a 0.94 m sidestep target → `StopNavigation()` → zero velocity → the agent never moved, for the full 8 s timeout |
| `S32AnimatorSpeedScaler` (pre-S54) | wrote the Animator's `Idling` bool from its own latch | self-latching deadlock: held in Idle → no root motion → still "idle" |
| `S35HeadingAlignmentGuardian` | snaps position back onto the spawn→goal straight line | erased the S68 lateral sidestep — a single-frame 0.4884 m teleport with `base_vel=0.000` and yaw forced to the spawn heading |

The `S35` case is the sharpest: the offset was created correctly, measured correctly, and then
removed by a component nobody was thinking about, ~10 s later, in one frame.

**Rule.** Before implementing any new spatial or state intent (a position, a heading, a
destination, an animator flag), grep for what else writes that variable every frame. If something
does, it will win. Disable the narrowest part of it — `S35HeadingAlignmentGuardian.hasLine` was
cleared while its facing-alignment mechanism, the reason it exists, was left running.

**Symptom to recognise:** a single-frame jump with the commanded velocity at exactly zero. Zero
velocity means neither the SFM nor the animation produced it, so something wrote the transform
directly.

## An attribution must be verified at the layer it claims

Established Session 68 (S68-B → S68-C).

Run7's `CROUCH_HOLD` self-check reported a 0.4884 m displacement. It was reported to the user as a
measurement-window artifact — a window straddling the `CrouchHold → CrouchExit` transition. That
attribution was **wrong**, and it was wrong because it was never checked at the frame level: the
jump is a single frame at t=41.504, a full second *before* the 42.51 transition, and its real cause
was `S35HeadingAlignmentGuardian` (above).

The claim was plausible, arithmetically compatible with the numbers on hand, and accepted without
the one check that could have refuted it — printing the per-frame deltas inside the window.

**Rule.** A causal claim is a claim about a layer (timing, geometry, physics, asset). Verify it at
that layer before reporting it. "It is a windowing artifact" is a claim about *when* the samples
were taken, so it is only supported by looking at the samples' timestamps — not by noting that a
transition happens nearby. Fixing the guessed cause (the window guard was added, and it changed
nothing) is not evidence either.

## The S68 sidestep closes on the robot, and it is what bounds the crouch hold

Observed Session 68-D (run9, run10). Recorded, not fixed.

`S68CuriousCrouch`'s SIDESTEP walks to a point offset laterally from the robot's latched route. The
path it actually takes is mostly *forward*, so the pedestrian closes on the robot while getting out
of its way. Decomposed from run9's frames.csv:

| phase | duration | closing rate | robot ground speed | pedestrian displacement |
|---|---|---|---|---|
| SIDESTEP | 3.77 s | **1.35 m/s** | 0.36 m/s | **3.99 m** (1.07 m/s) |
| STOP (pause) | 1.00 s | 0.52 m/s | 0.57 m/s | 0.00 m |
| CROUCH_ENTER | 2.77 s | 0.49 m/s | 0.56 m/s | 0.00 m |

During the two stationary phases the closing rate is just the robot's own speed, as expected. During
the sidestep it is nearly four times that, and the pedestrian supplies ~0.99 m/s of it: **3.99 m
walked to gain 0.61 m of lateral clearance** (0.59 -> 1.20 m).

The consequence is not merely inefficiency. How much approach runway the sidestep eats is **not a
constant**, and it is what bounds the crouch hold:

| run | `stopDistance` | start lateral | sidestep | pre-hold runway consumed | kneel completed at | hold |
|---|---|---|---|---|---|---|
| run9  | 10.0 m | 0.59 m | 3.77 s, reached 1.20 m | **6.97 m** | 2.94 m | 0.02 s |
| run10 | 16.5 m | 0.16 m | 8.00 s, **timed out** at 0.91 m | **12.71 m** | 3.78 m | 0.02 s |

The driver is the lateral offset the pedestrian happens to start with — it varies run to run with
spawn jitter and the latched route direction, not with `stopDistance`. Less initial offset means
more lateral gain needed; at the sidestep's ~0.16 m/s of *useful* lateral progress it cannot get
there, runs to its 8 s timeout, and burns ~10.7 m of runway instead of ~5.1 m.

**This is why raising `stopDistance` from 10.0 to 16.5 did not lengthen the hold.** Both runs
finished the kneel inside `standUpDistance` (2.94 m and 3.78 m against 4.0 m) and both held 0.02 s.
The extra 6.5 m of runway was absorbed entirely by the longer sidestep.

An estimate for a T-second hold is

    stopDistance ~= standUpDistance + C + 0.55*T        with C measured at 6.97-12.71 m

but C's 2x spread makes hold duration unpredictable rather than tunable. Making the sidestep travel
laterally rather than diagonally would shrink C *and* stabilise it, which is the change that would
make hold duration an actual function of the two distances. Until then, treat any `stopDistance`
picked from that formula as a lower bound that will sometimes still yield no hold at all.

## Never edit a tracked file while a trial is running

`run_trial.py`'s `guarded_unity_run` snapshots modified tracked files before launching Unity and
reverts anything *newly* dirtied once Unity exits (`git show HEAD`) — the guard that stops Unity
silently modifying prefabs and scenes.

It cannot tell your edit from Unity's. An append to `PROJECT_HANDOFF.md` made while run10 was still
capturing was silently reverted when that run finished; the file was back to its committed contents
with no error anywhere. Edit tracked files before starting a trial or after it exits, and re-check
`git status` afterwards either way.
