# What changed, what your copy is missing, and how to use the branch

Branch: `sheng/ped-behavior-v2` on `myfork`, head `53cc10d`.
Detail for everything below: `MERGE_NOTES_ped-behavior-v2.md`.

---

## 1. The headline fix — pedestrians no longer accelerate

If your copy shows pedestrians walking faster and faster until they are sprinting, that is a
**closed feedback loop** in `S32AnimatorSpeedScaler`, and it is fixed on this branch.

`animator.speed` was computed from an EMA of the pedestrian's measured displacement. But for a
root-motion character, that displacement *is* produced by `animator.speed`
(`transform.position += animator.deltaPosition`). Loop gain was 1.556 / 1.3 = **1.20**, so it
diverged until the clamp caught it at ~4.7 m/s.

Now: `animator.speed = |Base.velocity| / (authored ground speed of the playing clip)` — open loop,
so realised ground speed equals the commanded speed. Measured 1.455 against a commanded 1.450.

**This is entirely in code. Pull the branch and you have it.**

---

## 2. What you get from pulling code alone

All of these are code, and all of them will work for you immediately:

- the loop break above
- removal of the `Idling` write (it deadlocked `white_cane`: not moving → judged stationary → held
  in Idle → idle clip has no root motion → not moving)
- turn-in-place clips play at their authored rate instead of being scaled by a meaningless ratio
- Zone-A walking pace recalibrated (mean 1.10 m/s, stdev 0.18)
- all `APPEARANCE_SPEED_MULT` values, including new entries for `dog_walker` and `phone_user`
- `--ped-motion standing` now works for Zone-B containers (it was a **silent no-op** there before —
  both children walked the full 14 m while nominally standing still)
- `Old_Man_Walk` target 0.45, `Pacing_Phone` using its corrected reference
- `GetLocomotionAnimator()` preferring a Humanoid avatar, so the dog's or the bike's Animator cannot
  take over locomotion

`AppearanceAvatar.directVelocityDrive` also transfers — it lives in the `*Container.prefab` files,
which **are** tracked. I did not change any of those values.

---

## 3. ⚠️ Two things that will NOT transfer, and one of them fails silently

### 3.1 Submodule patches — `patches/`

Two changes live only in the `Microsoft-Rocketbox` submodule's working tree and are committed
nowhere. Apply both before opening the project:

```bash
cd Assets/ExternalAssets/Microsoft-Rocketbox
git apply --check ../../../patches/rocketbox_sticky_guard.patch   # dry run
git apply         ../../../patches/rocketbox_sticky_guard.patch
git apply         ../../../patches/rocketbox_rig_import_settings.patch
```

**`rocketbox_sticky_guard.patch`** — two fixes to `FixRocketboxMaxImport.cs`, a *project-wide*
`AssetPostprocessor`:

- it dereferences `Find("Bip01")` without a null check, so it **throws on every model that is not a
  Rocketbox rig** — every Zone-B FBX and every Mixamo clip you import
- it assigned `animationType = Generic` unconditionally, so any rig you set to Humanoid **reverts on
  the next reimport, including entering Play**. It looks like the Inspector change did not take

**`rocketbox_rig_import_settings.patch`** — rig import settings for `Female_Adult_05` and **all four
children**: `animationType` Humanoid, `optimizeGameObjects` off (leaving it on strips the bone
hierarchy at import), and exposed Hand/Head transforms that `AttachPropToHand` needs.

> **This second one is the one to watch.** Skip it and nothing fails — every file is present, no
> error appears — but `male_child` and `female_child` import with different rigs from the ones this
> dataset was generated with. A silent behavioural difference is much worse than a missing file.

### 3.2 Zone-B binaries — not in version control at all

37 asset files were never committed. `c3c0adb`'s own title says so: *"code+config only"*. Missing:
`cur.fbx`, `Walk W_ Briefcase.fbx`, `Ch22_nonPBR@Holding Walk.fbx`, the Cyclist and Scooter FBX,
**`Wheelchair (1).prefab`**, `Wheelchair.controller`, `wheelchairuser-women.controller`, and 22
textures.

`dog_walker` and `wheelchair_user` will not run without these. `Wheelchair (1).prefab` is the
wheelchair avatar itself — losing it removes the configuration, not just its materials.

Backup: `zoneB_assets_424MB.tar.gz`, sha256 `2b39f959e3a22f9737c8571b096f80f76b394588f72767b3d89dff265dedea5e`,
74 files, each verified with `cmp` and the archive verified by extracting and running `diff -r`.
Ask Sheng for it — it currently exists on one machine.

---

## 4. One implicit dependency that will bite if you touch it

`WHEELCHAIR_SPEED_MULT` in `tools/run_trial.py` must stay **exactly 1.0**.

Modulator attachment is gated on `!Mathf.Approximately(pedSpeedMultiplier, 1.0f)`, so at exactly 1.0
the wheelchair gets **no `PedestrianModulator` at all** and runs on raw social-force velocity. That
is the behaviour that passed review. Set it to 1.001 and the agent gains a modulator, its speed law
changes, and **nothing reports it**.

---

## 5. Known broken, so you do not re-diagnose it

- **`scooter_user`: the robot stalls**, not the pedestrian. 60 % of frames below 0.05 m/s; the robot
  covers 7.6 m in 60 s where everything else covers 31–32 m, and `cmd_lin_x` is 0.00 during the
  stalls, so the planner is commanding zero. **This predates my work** — 42 % in demo_s44, 41 % in
  demo_s45, unchanged across the loop break. It was never noticed because every check in this
  pipeline was pedestrian-side. The defect starts at t ≈ 12.6 s; the stop at t = 4.2 s is correct
  yielding and should not be "fixed". `cyclist` is faster and completely fine, so it is not a
  fast-obstacle problem. **Do not put this configuration in a dataset yet.**
- **`phone_user` is out of the roster** — the model has a 3.7532× scale override and its heading
  lags its velocity by ~70°, so it sidesteps its whole path at 17 % of commanded speed. Special
  characters are **7, not 8**.
- **`white_cane`'s manifest speed is wrong by ~9×** (commanded 0.45, realised 0.049). Its appearance
  passed review; the metadata has not been corrected yet. Do not quote its commanded speed.
- **Mixamo pedestrians drift during the frozen spawn**, so `dist0` ranges 3.98–8.0 m across
  configurations. Fix designed, not landed. Encounter geometry is not yet fully controlled.

---

## 6. If you quote a number from this branch

- speed comes from the physics body or from **1 s windowed endpoint displacement** — never per-frame
  position differencing
- the probes and `frames.csv` are two clocks, ~12 s apart
- whole-trial averages are dilution traps: `corridor` walks 3.8 s and stands 56 s
- `min_dist` at N=1 is not a safety result; run-to-run spread reaches 1.4 m
