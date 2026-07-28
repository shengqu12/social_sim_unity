# Merge notes — `sheng/ped-behavior-v2`

For 嘉诚. Branch is on `myfork` (`git@github.com:shengqu12/social_sim_unity.git`).

- **Base**: `208d92c` = `sheng/auto-capture` HEAD = `myfork/sheng/auto-capture`. Fast-forward, no rebase needed.
- **Head**: `53cc10d`
- **35 commits**, 110 files, **+17833 / −84** lines.

> Supersedes the S41–S47 version of this file. That version's "7 commits / 68 files / 5 modified
> files" figures are stale; the conflict-surface argument below is the current one.

---

## 1. Conflict surface: 9 existing files, 84 deleted lines

Everything else is new files.

| File | What changed |
|---|---|
| `Assets/Scripts/AutoTrial/S32AnimatorSpeedScaler.cs` | the loop break — see §2. Largest single change on the branch |
| `Assets/Scripts/AutoTrial/AutoTrialBootstrap.cs` | new defaulted-off config blocks; `--ped-motion standing` extended to the Zone B branch |
| `Assets/Scripts/AutoTrial/AutoTrialConfig.cs` | new fields, all defaulted off |
| `Assets/Scripts/AutoTrial/TrialController.cs` | `frames.csv` columns (`pedestrian_y`, physics-body velocity) |
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianModulator.cs` | absolute-target modulation (e); `baseWalkSpeedMps` 1.3 → 1.0476 |
| `Assets/Resources/Animation/SocialForcesAnimatorController.controller` | `657cd1d` only — 4 hunks, reaction states only |
| `tools/run_trial.py` | corridor profile, `safety_label`, Zone-A jitter, speed multipliers |
| `tools/overlay.py` | overlay re-cut for near clips |
| `Assets/Scripts/AutoTrial/S32AnimatorSpeedScaler.cs.meta` | execution order 100 (S44 FIX B) |

**Not touched**, verified by `git diff --name-status 208d92c..HEAD`:

- No `Base.cs`, no `SFAgent.cs`, no `IVI/` source. Every upstream defect below is worked *around*,
  never patched in place.
- No scene file. The corridor is runtime geometry built by `S41CorridorBuilder` inside the existing
  `Outdoor.unity` — navigation is map-bound to `Outdoor` through a ROS occupancy map in the
  read-only `sim_ws` repo, so a new scene would leave the robot immobile.
- No submodule pointer change. `git submodule status` still reports `9e1048a`. (The submodule's
  *working tree* is dirty — see §5, that is load-bearing and needs action.)

`657cd1d` remains the only commit that edits a shared asset and is deliberately alone in its commit;
`git revert 657cd1d` was verified clean with `--no-commit` (exit 0, no conflicts, exactly its own 4
files). The two Session 54+ commits (`0ab6886`, `53cc10d`) touch no shared asset at all.

---

## 2. The headline: `animator.speed` was a closed feedback loop

The user-visible complaint was *"walks faster and faster, and the top speed is far too high"*. It
had survived several sessions of tuning because every attempted fix looked for a **more accurate**
speed signal.

For a root-motion agent, `Base.Move()` does not translate the transform. Every metre comes from
`PedestrianModulator.ApplyAnimatorRootMotion()`'s `transform.position += animator.deltaPosition`,
and `deltaPosition` scales with `animator.speed`. The scaler set `animator.speed` from an EMA of
`transform.position` differencing. So:

```
animator.speed → ground speed → smoothedSpeed → animator.speed
```

Loop gain = (ground speed at `animator.speed` 1.0) / `referenceSpeedMps` = **1.556 / 1.3 = 1.20 > 1**
→ divergent, terminating only at `maxSpeedScale`. Closed-form prediction of the terminal ground
speed: `3.0 × 1.556 = 4.67 m/s`; independently measured by windowed endpoint displacement:
**4.6–4.7 m/s**.

**The fix, and the rule it encodes:**

```
animator.speed = |Base.velocity| / (authored ground speed of the clip currently playing)
```

> A control loop's feedback signal is not required to be **accurate**. It is required not to be a
> **function of the loop's own output**.

`Base.velocity` qualifies not because it measures the body better — it is the social-force model's
*command*, not a measurement — but because it is independent of `animator.speed`.

**Measured after**: corridor 1 s windows `1.45 1.46 1.45 1.43 1.44 …` flat, against
`1.71 → 3.44 → 4.60` before; commanded 1.450 vs realised 1.455. Controls that were never in the loop
(`directVelocityDrive == true`) are unchanged: cyclist 6.257 vs commanded 6.240, scooter 4.801 vs
4.810. `maxSpeedScale` engaged 0 times across all ten final-verification trials.

Full derivation: `trial_outputs/S53_ROOT_CAUSE.md`. Session-by-session evidence: `S54_REPORT.md`.

### Everything else in the same commit

| Change | Why |
|---|---|
| Removed the `Idling` write (S44 FIX A's second half) | Zone A's controller has no such parameter, so it was always a no-op there. `white_cane`'s does, and it self-latched: not moving → smoothed ≈ 0 → `Idling` true → held in Idle → idle clip has no root motion → not moving. **The same defect class as the speed loop, one layer up.** FIX A's *first* half is kept, restated on the clip instead of on measured motion |
| Control-law domain | `realised = authored × anim` only holds for clips that **translate**. `scared` spent 3.4 s in `StandQuarterTurnRight` (turn-in-place, weight 1.000 — settled, not a blend transition) with commanded 1.425 over authored 0.0848, sat on the clamp. Boundary 0.20 is the geometric midpoint of the measured 4.6× gap between non-translating clips (≤ 0.0848) and translating ones (≥ 0.3915) — read off the distribution, not chosen to make a check pass |
| Zone-A pace 1.3 → 1.0476 | The old 1.3 came from a retracted measurement and silently scaled the jitter's intended 1.10 / 0.18 to 1.365 / 0.221. Now mean **1.0991**, stdev **0.1745** |
| `APPEARANCE_SPEED_MULT` rescaled; `dog_walker` / `phone_user` added | Without an entry, `pedSpeedMultiplier` falls through to 1.0, which skips modulator attachment entirely — see §4 |
| `--ped-motion standing` honoured in the Zone B branch | Session 28 added it to Zone A only, so the flag was a **silent no-op for every Zone B container**; both children walked the full 14.0 m |
| `Old_Man_Walk` target 0.7 → 0.45 | Its loop gain was 1.00, so it had been travelling at roughly its authored 0.392 — **56 % of its command**. The loop break made the command real for the first time, and the number turned out never to have been reviewed |
| `Pacing_Phone`: explicit per-clip reference wins over the live `averageSpeed` | `averageSpeed` is net displacement over duration, invalid for a clip whose root paces back and forth. The live read gave 1.928 where FIX C intended 1.419 |
| `GetLocomotionAnimator()` prefers `avatar.isHuman` | so a prop's or an animal's Animator cannot take over locomotion — bike and dog are the triggers |

**Human review of both verification batches accepts the pedestrian-side behaviour.**

---

## 3. Before you build on it — known unfixed defects

1. **`scooter_user`: the robot stalls, and it is not the pedestrian's fault.** 60 % of frames below
   0.05 m/s; the robot covers 7.6 m in 60 s where every other configuration covers 31–32 m.
   `cmd_lin_x` is 0.00 during the stalls, so the *planner* is commanding zero.
   **Long-standing, not a regression** — demo_s44 42 %, demo_s45 41 %, unchanged across the loop
   break. It went unnoticed because every check in this pipeline was pedestrian-side.
   The defect starts at **t ≈ 12.6 s**, not at the encounter (t = 4.2 s, where stopping is correct
   yielding). `cyclist` is *faster* (6.24 vs 4.81 m/s) and completely unaffected, which refutes the
   "fast obstacle" hypothesis. See `S54_REPORT.md` §14. **Do not put this configuration in a
   dataset until it is fixed.**

2. **Upstream: `IVelocityModulator` compounds.** `Base.cs:122` writes `ModulateVelocity`'s result
   back into the field `SFAgent.cs:71` integrates from, so a multiplicative modulation is applied to
   its own previous output every frame — geometric in the factor. The hook is the documented
   pattern for personality logic, so every user of it is exposed. This branch sidesteps it by making
   the modulation return an **absolute target** rather than a multiplier (idempotent: `f(f(v)) =
   f(v)`), but the root cause is upstream and unpatched. A separate report goes to Nathan/Howard.

3. **Retargeting loss on nested-Animator avatars.** `AnimationClip.averageSpeed` is a property of
   the clip *on its source rig*, not of the (clip, avatar) pair. Measured `offered/expected`:
   `business_male_01` (Animator on root) **1.016**, `dog_walker` (nested) **0.710**, `white_cane`
   (nested) **0.418**. Scale (all exactly 1.0), `humanScale` (1.010–1.043) and the application path
   (`applied/offered ≈ 1.0`) are all excluded. The ratio is constant within a trial, so it is a
   per-(clip, avatar) constant, not speed-dependent. **`white_cane`'s manifest speed is therefore
   wrong by ~9×** (commanded 0.45, realised 0.049) — its on-screen appearance passed human review,
   but the metadata has not been corrected yet. An offline calibration is the intended fix.

4. **Mixamo pedestrians drift during the frozen spawn.** Their generated single-state controllers
   have no `Forward`/`Idling`, so the clip just plays and root motion is applied unconditionally;
   the SLATE freeze only zeroes `destPos`, which gates `Base.Move()`, not root motion. Measured
   drift: `old_man` 2.87 m, `Drunk_Walk` 3.12 m, `carry_and_walk` 4.79 m, `Pacing_Phone` 7.73 m.
   `dist0` consequently ranges 3.98–8.0 across configurations, i.e. **the controlled variable of the
   encounter geometry is not controlled**. Fix designed, not yet landed.

5. **4 of the 9 Mixamo clips from S41 were broken**; `--ped-motion standing` fixed the glide on all
   four (`353bed1`). `Stroke_Shaking_Head` remains excluded — see §6.

---

## 4. Implicit dependency: `WHEELCHAIR_SPEED_MULT` must be **exactly** 1.0

`AutoTrialBootstrap.cs` gates modulator attachment on
`!Mathf.Approximately(config.pedSpeedMultiplier, 1.0f)`. At exactly 1.0 the appearance gets **no
`PedestrianModulator` at all**, so it runs on raw social-force velocity capped by
`Parameters.MAX_VEL = 0.95 m/s`, solution (e) never applies, and `BASE_PED_SPEED_MPS` never applies
either.

**That no-modulator behaviour is what passed human review.** Change the value to 1.001 and the agent
gains a modulator, its speed law changes, and **nothing reports it** — no error, no warning, no gate.

The underlying coupling — one test deciding both "do not rescale speed" and "do not apply
personality modulation" — is the same one behind the S46-E Indifferent defect. Decoupling it with an
explicit flag is the correct fix and is deferred until after dataset generation.

---

## 5. ⚠️ Submodule patch dependency — this one is silent if you miss it

Two sets of changes live **only in the `Microsoft-Rocketbox` submodule's working tree** and are
committed nowhere. They are preserved as patches in `patches/`, with apply instructions in
`patches/README.md`.

| Patch | Without it |
|---|---|
| `patches/rocketbox_sticky_guard.patch` | (a) the project-wide `AssetPostprocessor` **throws on every non-Rocketbox model** — every Zone-B FBX and every Mixamo clip — because it dereferences a missing `Bip01`; (b) any rig set to Humanoid reverts to Generic on the next reimport, including entering Play |
| `patches/rocketbox_rig_import_settings.patch` | `Female_Adult_05` and **all four children** import with different rig settings: `animationType` Generic instead of Humanoid, `optimizeGameObjects` on (which strips the bone hierarchy), and no exposed Hand/Head transforms for `AttachPropToHand` |

**The second one is the dangerous one.** Clone the code, use your own submodule checkout, and every
file is present — nothing fails. `male_child` and `female_child` are roster configurations, and both
of their rigs are in that patch, so their behaviour would differ from this dataset's **silently**.

This is more insidious than the missing Zone-B binaries in §7: those at least fail loudly.

> Better long-term fix, recorded but not implemented: a project-owned `AssetPostprocessor` with a
> `GetPostprocessOrder()` above Microsoft's, restoring `animationType` for the affected avatars. It
> would travel with the code and need no manual patching. It needs testing; the patches were stored
> first so the better fix would not delay the safe one.

### Known submodule noise, deliberately not cleaned

`git config core.fileMode false` in the submodule removes 13 `.tga` entries that were pure
`100644 → 100755` mode changes (0 insertions, 0 deletions, identical byte counts). The remaining 13
`.tga.meta` are importer re-serialization churn — reverting them just makes Unity write them again.
**Nothing was reverted**; submodule state has wide blast radius and its disposition is Sheng's call.

> Noise reduction here is not tidiness. The sticky guard — a load-bearing change — sat undiscovered
> inside 32 dirty files for weeks. A working tree's signal-to-noise ratio decides whether important
> changes can be seen at all.

---

## 6. Excluded assets

| Asset | Why |
|---|---|
| `Running` | retarget failed |
| `talking_standing` | user does not want it |
| `Stroke_Shaking_Head` | its grounding defect could not be diagnosed because "Optimize GameObjects" stripped the bone hierarchy at import. ⚠️ **That blocker may no longer apply** — `optimizeGameObjects` is now `0` in the patch above. Not re-evaluated: the user has stated they no longer want the asset, and the patch covers `Female_Adult_05` + children, which may not include the avatar it used |
| `phone_user` | human review: "the model looks weird". Two independent, located defects — a 3.7532× uniform scale override, and a ~70° heading-vs-velocity mismatch that makes it sidestep its whole path at 17 % of commanded speed. Note the directions **disagree**: scaling up would make root motion travel *further*, and the measured travel is *shorter*. See `trial_outputs/known_issues/phone_user.md`. **A2 is 7 special characters, not 8** — any configuration count built on 8 needs recomputing |

---

## 7. External asset dependency

The Zone-B character meshes, textures and two `.controller` files are **not in version control**.
`c3c0adb`'s own title says so: *"Import special-character packages and spawner containers (code+config
only)"*.

- **Present and tracked**: every `*Container.prefab` (so `AppearanceAvatar.directVelocityDrive`
  *does* travel with the code), the avatar `.prefab`s under `Community-informed Model/`, and the
  materials.
- **Missing**: 37 asset files — `cur.fbx`, `Walk W_ Briefcase.fbx`, `Ch22_nonPBR@Holding Walk.fbx`,
  the Cyclist and Scooter FBX, `Wheelchair (1).prefab`, `Wheelchair.controller`,
  `wheelchairuser-women.controller`, and 22 textures.

`Wheelchair (1).prefab` is the worst of these: it is the `wheelchair_user` avatar itself, so losing
it removes the whole configuration rather than degrading its materials.

**Backup**: `/mnt/ssd/Social_Navigation/asset_backup/zoneB_assets_424MB.tar.gz`
sha256 `2b39f959e3a22f9737c8571b096f80f76b394588f72767b3d89dff265dedea5e`
74 files (37 assets + 37 `.meta`), each verified byte-identical with `cmp`, and the tarball verified
by extracting it and running `diff -r`. The superseded 248 MB archive is kept in `superseded/`
rather than deleted.

⚠️ The backup currently exists **on one machine only**. It needs to reach a second location.

---

## 8. Diagnostics, and local dirt

All probes added by this branch are **env-var gated and off by default** — `AUTOTRIAL_S44_PROBE`,
`AUTOTRIAL_S54_PROBE`, `AUTOTRIAL_S55_PROBE`. They self-attach via `RuntimeInitializeOnLoadMethod`
so that enabling one requires editing no existing file, and they write CSVs without setting any
value on any component.

`Assets/Resources/ROSConnectionPrefab.prefab` and `UserSettings/Layouts/default-2022.dwlt` are
rewritten by Unity batchmode on every run. They appear in no commit on this branch. They are tracked
files, so `.gitignore` cannot suppress them; the working rule is simply never to stage them.

---

## 9. Measurement rules — read these before quoting any number from this branch

Several of this branch's sessions were spent recovering from measurement error, not code error.

- **Speed comes from the physics body (`robot_vel_*`, `robot_speed_ground`) or from 1 s windowed
  endpoint displacement.** Never from per-frame position differencing: the transform advances as a
  discrete event, so per-frame deltas are zero on most frames and spike on the rest.
- **Per-frame `|Δpos|` summing and whole-interval net displacement fail in opposite directions** —
  the first inflates slow characters with jitter (`white_cane` advances ~2×10⁻⁵ m per probe frame),
  the second deflates curved paths. 1 s windows are stable against both.
- **The probes and `frames.csv` are two clocks**, offset by roughly 12 s (probes attach during scene
  setup, `frames.csv` starts at capture). Window selection must use the probe's own coordinates. An
  earlier analysis in this branch labelled the frozen-spawn segment as the walk window by mixing
  them.
- **Whole-trial averages are diluted.** `corridor` walks for 3.8 s and stands for 56 s; a trial mean
  is 93 % standing, which is exactly how the loop was missed for several sessions.
- **`min_dist` from N=1 is not a safety result.** Run-to-run spread on identical commands was
  measured at up to 1.4 m.
