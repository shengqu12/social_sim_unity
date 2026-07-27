# `Running` — permanently excluded

Removed from the roster, `clip_speeds.json`, and every batch. The FBX and its meta stay on disk;
neither appears in any configuration or deliverable.

## Cause chain

1. **S44 5.6** — flagged on review as "the legs cross, the running posture is odd". Hypothesised as
   retarget failure, with a two-attempt budget.
2. **S44, fix C** — the budget was deliberately withheld first, because a competing explanation
   existed: `referenceSpeedMps` was a hard-coded 1.3 while Running is authored at 4.406 m/s, so the
   clip was playing at ~3.4x. Combined with the irregular render sampling (p99 70 ms, max 134 ms),
   aliasing could produce apparent leg-crossing with no rig fault at all.
3. **S45** — fix C brought playback from 3.4x down to 0.567x, a **6x reduction**. The artefact
   persisted, which **falsifies the aliasing hypothesis**. Cost of testing it: zero extra work, since
   fix C was needed regardless.
4. **S45 1.3, the one sanctioned attempt** — the suggested route (UpperLeg/LowerLeg mapping,
   muscle rotation limits) was **not available**: those live on the destination Rocketbox avatar, a
   shared prefab, and editing it would alter every trial ever run with that avatar. The in-bounds
   alternative was taken instead: Running is a 0.700 s single stride looped with `loopTime` but no
   pose matching at the seam, and a run cycle holds the legs at opposite extremes, so a mismatched
   seam snaps them past each other once per cycle. `loopBlend: 1` was set, on Running only.
5. **S46** — reviewed again; the animation is still unusable. Excluded by prior agreement.

## What remains untested

The originally-suggested cause. Both remaining routes are out of bounds under the current red lines:

- Editing the shared Rocketbox avatar's Avatar Configuration or muscle limits.
- Bone-level diagnosis of the retarget, which is blocked anyway by the "Optimize GameObjects"
  limitation recorded in `PROJECT_HANDOFF.md`.

So this is an exclusion for lack of an in-bounds route, not a demonstration that the rig is at
fault. Anyone reopening it should start by lifting one of those two constraints deliberately.

## Cost of keeping it

Running was also the most gate-troubled asset in the roster: it failed both the approach and
trigger-speed gates in demo_s44 and demo_s45, and ran a 26–40% clamp engagement. Shipping it with a
visible animation defect would put a known-bad sample into VLM training material, which is worse
than its absence.
