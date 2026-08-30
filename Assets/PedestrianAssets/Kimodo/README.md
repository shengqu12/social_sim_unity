# Kimodo-generated pedestrian clips — Axis-B motions (S73)

> ## ⛔ NOT IN THE PAPER DATASET
> These clips are **additive, NEXT-PAPER / DEMO only**. planD is frozen: they never
> enter the paper dataset, and any future re-render bus re-renders the **ORIGINAL 155
> configs only**. Nothing here is a paper artifact.

---

## 1. Provenance

| Field | Value |
|---|---|
| Model | `Kimodo-SOMA-RP-v1.1` (NVIDIA) |
| Weights sha256 | `ef0a0ca45a6089ab4532dde609785771ae3f38755b4ae6cf314b0213e07cd4a3` (`model.safetensors`, 1,133,185,036 B) |
| Licence | **NVIDIA Open Model License** — shipped verbatim as `LICENSE` with the weights; see S72 `LICENSES.md` §1.2 |
| Env | conda `kimodo313` (Python 3.13) — **not** `kimodo` (py3.10); upstream ships a cp313 `motion_correction` `.so` its own docs' py3.10 cannot load (S72 `GEN_REPORT.md` §8) |
| Post-processing | ON (foot-skate reduction + constraint refinement). It is the default; `--no-postprocess` is the opt-out |
| Text encoder | `TEXT_ENCODER_DEVICE=cpu` |
| Export chain | SOMA `--bvh --bvh_standard_tpose` → Blender 4.5.12 LTS plain BVH import → FBX. No SMPLX, no smplx addon, no SMPL-X body model |
| Rig | 78 bones (77-joint SOMA skeleton + Root), 30 fps |

### Clips

| Asset | Prompt | Seed | Duration | Generated | Source sha256 |
|---|---|---|---|---|---|
| `kimodo_relaxed_walk.fbx` **(S106)** | "a person walks forward at a relaxed, unhurried pace with natural arm swing" | 2042 | 8.0 s (240 f) | 2026-08-28 (S104 `r1_seed2042`) | `f3b537cb1fc4a9d8` |
| `kimodo_relaxed_walk_s73.fbx` **(RETIRED, off-roster)** | "a person walks forward at a relaxed pace" | 42 | 8.0 s (240 f) | 2026-08-15 | `66e1158e…525e1c73` |
| `kimodo_elderly_shuffle.fbx` | "a person shuffles forward slowly, hunched, like an elderly person" | 42 | 8.0 s (240 f) | 2026-08-15 | `b9e00830…f85a81cd` |
| `kimodo_relaxed_walk_24s.fbx` | relaxed-walk prompt **repeated 3×**, `--duration "8 8 8"`, `--num_transition_frames 5` | 42 | 24.0 s (720 f) | 2026-08-18 | `6c8a70be…0ab555bd` |

### Roster notes (S106, 2026-08-30)

| Roster name (`--mixamo-clip`) | Semantic label | Status |
|---|---|---|
| `kimodo_relaxed_walk` | **relaxed adult walk**, 1.1477 m/s | **SHIPPING.** S104 candidate `r1_seed2042`, promoted in S106. Rendered ankle-height \|L−R\| **2.9 mm**, stride 0.9832, wrap seam 19.32°, arms below shoulder (S104b gates, Business_Male_01). Imported headlessly by `S106KimodoImport.Promote` against the canonical reference (`kimodo_reference_skeleton.json`); `.meta` identical to the S104b scratch import the gates were measured on. b2 composition re-verified: `[S83]` rebind + `[S89IK]` engaged on the R1-config cell, S92 contact numbers reproduce (clearance 34.96–35.10 mm, penetration ≤ 2.63 mm, gap 11.74–14.18 mm). |
| `kimodo_relaxed_walk_s73` | — | **RETIRED from the roster** (S106). The S73 seed-42 relaxed walk, renamed in place (git mv, guid kept). Rendered ankle-height asymmetry **88.6 mm** / stride 0.8713 — the defect S102/S103 isolated as manufactured by the humanoid muscle bake. Kept on disk (and in `clip_speeds` under its new key) as planD-era provenance; not a paper artifact; do not put it on a reel. |
| `kimodo_elderly_shuffle` | **slow-gait specimen** (0.48 m/s) | **RETAINED, reclassified.** No longer the *elderly* semantic label: 0.40–0.55 m/s encodes a shuffling impairment, not typical elderly gait (healthy 70–79 comfortable 1.13–1.26 m/s, Bohannon, *Age and Ageing* 1997;26(1):15–19). Kept for **POV observability** — S94: the only gait that keeps the reaction in frame. S99 finding 1 (left arm raised to the face in the gait pose) stands. |
| `kimodo_elderly_walk` | **normal elderly walk** (target) | **NOT YET SHIPPED.** S106 generated 6 candidates (2 prompts × seeds 42/1042/2042) under G-speed-elderly = [0.95, 1.30]; all six measured 0.19–0.49 m/s and none passed. See the S106 record. |
| `kimodo_relaxed_walk_24s` | — | unchanged (S73 chained variant). |

The 24 s variant is **chained multi-prompt generation**, not a long single-shot clip:
Kimodo splits `--prompt` on `"."` and generates one chunk per segment, blending
`num_transition_frames` between them. It is the only seam mechanism the model has.

---

## 2. Measured root speeds (provisional)

Blender headless from the exported FBX, cross-validated against the source `.npz`
(agreement to 4 decimal places). Method family matches the `clip_speeds` discipline:
net endpoint displacement over duration is the headline, path length alongside,
`net/path` the validity ratio.

| Clip | Frames | Duration | Net disp. | **Net speed** | Path speed | net/path |
|---|---|---|---|---|---|---|
| `kimodo_relaxed_walk` (S106, `r1_seed2042`) | 240 | 7.967 s | 9.1431 m | **1.1477 m/s** | 1.1553 | 0.9934 |
| `kimodo_relaxed_walk_s73` (retired) | 240 | 7.967 s | 7.7396 m | **0.9715 m/s** | 0.9822 | 0.9891 |
| `kimodo_elderly_shuffle` | 240 | 7.967 s | 3.8130 m | **0.4786 m/s** | 0.5012 | 0.9549 |
| `kimodo_relaxed_walk_24s` | 720 | 23.967 s | 24.4032 m | **1.0182 m/s** | 1.0265 | 0.9919 |

All `net/path` ≥ 0.95, far above the 0.7 floor below which the root reverses and net
displacement stops being a valid pace measure.

**These are PROVISIONAL until Phase 4 in-engine confirmation.** They are one of two
independent sources; the in-engine measured walking speed is the other.

> **Measurement trap.** The BVH/FBX `Root` node is **static at the origin** — `Hips`
> carries all translation. Measuring "the root bone" naively returns 0.0 m/s for every
> clip. Measure `Hips`.

---

## 3. Flags

### 3.1 Unity import scale is SUSPECT — verify, do not trust the field
The exported FBX **re-imports into Blender in centimetres** (mean hip height 98.0 cm,
i.e. ×100). S72 `UNITY_STEPS.md` §3 says "Scale 1". Whether Unity also reads these
files as cm is **unverified**. On import, check the model's actual height resolves to
**≈1.7–1.8 m** rather than trusting the Scale field.

**As-imported (2026-08-18 GUI pass, read from the `.meta`):** all three clips came in at
`globalScale: 1`, `useFileScale: 1`, `animationType: 3` (Humanoid — so the `Bip01`
escape in §3.3 held). The direct height check was not recorded, but **this flag is now CLOSED by
evidence** from the 2026-08-19 Phase-4 run: the in-engine `S44Probe` measured
`clip_length = 7.967 s` (matching the source exactly) and a median moving ground speed
of **0.5129 m/s** against a 0.4786 m/s reference. A 100× scale error would have surfaced
as absurd speeds; it did not. **Unity does not read these FBXs as centimetres** — the
Blender-side cm reading is a Blender import convention, not a property of the file.

### 3.2 Materials WILL be whitened on import — expected, not a bug
`Assets/ExternalAssets/Microsoft-Rocketbox/Assets/Editor/FixRocketboxMaxImport.cs:6-16`
(`OnPostprocessMaterial`) is a **project-wide** `AssetPostprocessor` with no path
filter. It sets `material.color = Color.white` on every imported material, and calls
`material.GetFloat("_Mode")`, which logs an error on shaders that have no `_Mode`
property. Both effects reach these Kimodo FBXs. **Pre-registered as expected; the
`_Mode` error log is cosmetic.**

### 3.3 NEVER name a Kimodo rig node `Bip01`
The same postprocessor forces `animationType = Generic` at `:69-70`, but only after
`:44-45` `Transform bip01 = g.transform.Find("Bip01"); if (bip01 == null) return;`.
These clips escape that forcing **only because the SOMA rig roots are `Root`/`Hips`**.
That is naming luck, not scope. **Any Kimodo rig node named `Bip01` would be silently
forced to Generic and break Humanoid retargeting.**

### 3.4 Submodule dirty-state hazard — do NOT touch the submodule
`FixRocketboxMaxImport.cs` is **not in the parent repo's HEAD at all**; it lives in the
git submodule `Assets/ExternalAssets/Microsoft-Rocketbox` (submodule HEAD `9e1048a`,
2021-01-15), so parent HEAD carries only a gitlink. The working copy is **dirty in the
submodule**, and the dirt is wider than just this file: **19 modified files, +2726 /
−1220** vs `9e1048a` — `Assets/Editor/FixRocketboxMaxImport.cs` (+8/−3, lines 65-70:
the "don't fight a Humanoid rig" fix) plus **18 Rocketbox avatar `.meta` files**
(`Female_Adult_05`, `Female_Child_01/02`, `Male_Child_01/02` and their textures),
i.e. uncommitted import settings for avatars the paper pipeline uses. Nothing is
untracked.

**A submodule reset would silently revert all 19 — both the import behaviour for the
whole project and those avatar import settings.** Leave it alone. Do not run
`git submodule update`, `git checkout`, or `git restore` inside
`Assets/ExternalAssets/Microsoft-Rocketbox`.

### 3.5 NEVER create a file named `clip_speeds` under ANY `Resources` folder
`S41MixamoClipApplier.cs:120` does `Resources.Load<TextAsset>("clip_speeds")` — a
**name-based global lookup**, hijackable by any new same-named Resources file.

Today that lookup **misses**: `clip_speeds.json` lives at
`Assets/PedestrianAssets/Mixamo/clip_speeds.json`, which is *not* under a `Resources`
folder, so the code falls through to the editor-path fallback at `:158-160`
(`File.Exists` on a project-relative path — works in Editor/play mode, would fail in a
player build). **The latent editor-only fallback is documented here, NOT fixed.**

Consequence: dropping a `clip_speeds.json` into any `Resources` folder would make that
missing lookup suddenly **hit**, and it would then serve **every Mixamo clip too**. Any
Mixamo clip absent from the new file silently loses `authoredSpeedMps` and falls back
to a default the code's own warning calls "WRONG for every Mixamo clip" — a silent
global regression in the frozen paper pipeline.

### 3.6 Chained conditioning elevates mid-segment pace by ~15%
In `kimodo_relaxed_walk_24s` the three segments measure **0.9715 / 1.1160 / 0.9727 m/s**
— segment 1 runs ~15% faster than its neighbours. This is positionally smooth (see §4)
but a viewer sees the walker speed up for the middle third. **Known characteristic of
chained conditioning.** Phase 4 eyeball decides visibility.

---

## 4. Seam behaviour (measured)

### 4.1 Internal chunk seams — smooth
Frame-to-frame root delta across each transition window vs the within-segment median
(0.0355 m):

| Seam | Max Δ in window | Ratio vs median |
|---|---|---|
| frame 240 | 0.0370 m | **1.04×** |
| frame 480 | 0.0127 m | **0.36×** |

The global maximum single-frame root delta (0.0575 m) occurs **inside segment 1, not at
either seam**. There is no positional discontinuity where the chunks join.

### 4.2 Unity Loop wrap seam — NOT smoothed, and worst on the long clip
Internal seams are transition-blended by Kimodo. The Unity `Loop Time` wrap (last frame
→ first frame) is **not**, and 60 s trials live on it. Max per-joint angular difference,
first vs last frame:

| Clip | Max Δ | Worst joint |
|---|---|---|
| `kimodo_relaxed_walk_24s` | **55.98°** | RightShin |
| `kimodo_relaxed_walk` | **29.24°** | RightLeg |
| `kimodo_elderly_shuffle` | **23.58°** | RightShin |

**Setting kept (2026-08-18 GUI pass):** `loopTime: 1` (Loop Time ON) and
`loopBlend: 1` (**Loop Pose ON**) on all three clips. With a 55.98° shin delta on the
24 s variant, Loop Pose is doing real work to close that wrap and will warp the final
frames; that trade is now baked in and belongs on the Phase 4 eyeball list.

Hips height delta across the wrap is ≤1.5 cm for all three — a limb snap, not a vertical
jump. **Chaining did not help the loop wrap; it roughly doubled it** (the 24 s clip ends
mid-stride with the right shin swinging). Pre-registers the Phase 4 eyeball expectation:
24 s variant worst, elderly shuffle best.

---

## 5. Humanoid bone map — VERBATIM from S72 `UNITY_STEPS.md` §4

> Everything from here to §6 is copied **verbatim** from the S72 sandbox's
> `UNITY_STEPS.md` §4.1–4.3, including its original section numbering. Do not
> edit it here; edit it there and re-copy.

## 4. Humanoid bone map — prebuilt table

77 SOMA joints vs Unity Humanoid's ~54 slots. **Extra bones staying unmapped is fine
and expected** (fingers, eyes, jaw, `*End` leaf bones). What matters is that the 15
required slots bind to the *correct* bones.

### 4.1 ⚠ THE TRAP — read before clicking anything

SOMA names the **thigh** `LeftLeg` and the **calf** `LeftShin`. Unity's auto-mapper is
name-based, so it is liable to bind `LeftLeg → LowerLeg` (because the name contains
"Leg") and leave **UpperLeg empty**. That produces either an invalid avatar or, worse, a
silently inverted leg chain.

CC confirmed this by running both a name matcher and a structure walk over the actual
FBX and diffing them:

```
TRAP LeftUpperLeg     structure='LeftLeg'   naive_name_match=None
TRAP RightUpperLeg    structure='RightLeg'  naive_name_match=None
TRAP LeftLowerLeg     structure='LeftShin'  naive_name_match='LeftLeg'
TRAP RightLowerLeg    structure='RightShin' naive_name_match='RightLeg'
```

The skeleton itself is unambiguous — the chain from Hips is
`LeftLeg → LeftShin → LeftFoot → LeftToeBase`, so `LeftLeg` is the thigh.

**After clicking Configure, check these four slots first.** If Unity got them wrong,
fix them by hand per the table.

### 4.2 Required bones (all 15 resolve)

| Unity Humanoid slot | SOMA bone |
|---|---|
| Hips | `Hips` |
| Spine | `Spine1` |
| Head | `Head` |
| LeftUpperArm | `LeftArm` |
| RightUpperArm | `RightArm` |
| LeftLowerArm | `LeftForeArm` |
| RightLowerArm | `RightForeArm` |
| LeftHand | `LeftHand` |
| RightHand | `RightHand` |
| **LeftUpperLeg** | **`LeftLeg`** ← trap |
| **RightUpperLeg** | **`RightLeg`** ← trap |
| **LeftLowerLeg** | **`LeftShin`** ← trap |
| **RightLowerLeg** | **`RightShin`** ← trap |
| LeftFoot | `LeftFoot` |
| RightFoot | `RightFoot` |

### 4.3 Optional bones (map these too — they improve retarget quality)

| Unity Humanoid slot | SOMA bone |
|---|---|
| Chest | `Spine2` |
| Neck | `Neck1` |
| LeftShoulder | `LeftShoulder` |
| RightShoulder | `RightShoulder` |
| LeftToes | `LeftToeBase` |
| RightToes | `RightToeBase` |
| UpperChest | *(leave empty)* |

Note `Chest` maps to `Spine2` and not to the bone literally named `Chest`. SOMA's
spine chain is `Hips → Spine1 → Spine2 → Chest → Neck1 → Neck2 → Head`, i.e. four
segments where Unity has at most three (Spine/Chest/UpperChest). Mapping
Spine1→Spine and Spine2→Chest leaves SOMA's `Chest` and `Neck2` unmapped, which is the
correct trade — it keeps the mapped segments evenly distributed up the torso. If the
torso looks wrong, the alternative worth trying is Spine1→Spine, Spine2→Chest,
`Chest`→UpperChest.

There is also a `Root` bone **above** `Hips`. Leave it unmapped — Unity's Humanoid
treats Hips as the root, and `Root` is a reference/motion node.

Full machine-readable dump, including every bone's parent: `02_unity/soma_bones.json`.

### 4.4 If the avatar still will not validate

Fallback is **Option A — KimodoUnityBridge**. Read §7 first; it is a third-party
package, not an NVIDIA one, and it is *not* recommended for this spike.

---


---

## 6. ERRATA — ticket-author errors caught at Phase 0/1

Recorded per S73 D-10. All three were corrected before any work was committed.

**(i) The 30 s single-prompt premise.** Original Phase 1.1 asked for a 30 s
single-prompt clip to avoid the loop seam for 60 s trials. A single prompt produces
exactly **one** chunk, so `--num_transition_frames` is inert and the output is not
loopable — the mechanism could not address the seam it was chosen for, and 30 s was a
3.75× extrapolation beyond the 8 s S72 ever validated. Replaced with multi-prompt
chained generation (S73 D-1).

**(ii) `--duration` per-prompt semantics.** The amended ticket specified
`--duration 24` for a 3-segment chain. Per `kimodo/scripts/generate.py:139-142`, a
duration string containing no space is applied **per prompt** — `--duration 24` across
3 prompts would have produced 3 × 24 s = **72 s**, not 24 s. Corrected to
`--duration "8 8 8"` (the per-prompt list form, `:144-147`), giving 24.0 s total with
every chunk at exactly the S72-validated 8 s length.

**(iii) The original Phase-5 `clip_speeds` plan.** The ticket said "clip_speeds.json
gains entries" without specifying which file, on the assumption that a Kimodo-scoped
speeds file could be registered additively. Tracing both consumers showed it cannot:
`tools/run_trial.py:1532` hard-codes
`Assets/PedestrianAssets/Mixamo/clip_speeds.json` as a module constant and
`mixamo_target_speed()` (`:1535-1552`) reads only that file — no scan, glob, merge, or
fallback. A Kimodo-scoped file would be invisible to it, and a Kimodo
`Resources/clip_speeds.json` would be actively harmful (§3.5). Resolved by S73 D-7:
add `kimodo_*` keys to the existing file under explicit guardrails.

---

## 7. Provenance of this document

Generated in S73 Phase 2. Source measurements and raw generation logs live in the S72
sandbox at `/mnt/ssd/Social_Navigation/sandbox_s72_nextgen/` (`GEN_REPORT.md`,
`FEASIBILITY.md`, `UNITY_STEPS.md`, `logs/d_chain24_*`). §5 is copied verbatim from
that sandbox's `UNITY_STEPS.md` §4.

---

## 8. Phase 4 status — GREEN (2026-08-19)

**All gates PASS on both arms, exit 0.** `clip_speeds.json` entries are promoted from
provisional to **confirmed**.

| | probe: `kimodo_elderly_shuffle` | regression: `Old_Man_Walk` |
|---|---|---|
| measured speed (median moving) | **0.5106 m/s** | **0.4593 m/s** |
| reference (`authoredSpeedMps`) | 0.4786 m/s | 0.3915 m/s |
| ratio vs reference | **1.067** | **1.173** |
| clampHi hits | **0** | **0** |
| exit code | **0** | **0** |

Gates: content PASS, aspect PASS, approach geometry PASS (`dist0=7.996`, target 8.000
±0.3), trigger-speed PASS (`robotSpeedAtTrigger=0.601 m/s`), file manifest PASS.
`min_dist` 1.37 m (probe) / 0.657 m (regression), both `safety_label=safe`.

**D-7(4) REGRESSION: PASS.** `Old_Man_Walk` resolved to its pre-edit values exactly
(`authoredSpeedMps 1.3000 -> 0.3915`, `target 0.45 -> --ped-speed 0.430`) and measured
within 0.6% of its pre-edit run. The added `kimodo_*` keys did not move the frozen
pipeline.

The Kimodo clip tracks its authored pace **more closely (+6.7%)** than the known-good
Mixamo control does (+17.3%), so nothing about its pace is anomalous.

### Attempt 1 (same day) failed — root cause, for the record

The first attempt failed `approach geometry` and `trigger-speed` on **both** arms
identically (`dist0=11.999`, `robotSpeedAtTrigger=0.000`). Cause was **not** these
clips: `teb_local_planner` was absent from the freshly-created container, while
`move_base_params.yaml:21` requires `teb_local_planner/TebLocalPlannerROS`, so
`MoveBase::MoveBase()` threw FATAL at `move_base.cpp:142` and crash-looped 184 times.
The robot never got a planner, so it never moved.

This is Session 30R's documented root cause: TEB was installed live into a
long-running container and never baked into `ros:latest`, so any fresh container starts
without it. `run_trial.py` already carries the remedy —
`ensure_teb_plugin_installed()` — but it is called from inside `ros_fresh_bringup()`,
so it only fires under **`--fresh-ros`**. A hand-rolled bringup plus the default
`--reused-ros` skips it. Re-running with `--fresh-ros` installed the package and the
bringup was healthy in 8 s.

**Note the `scanReceived=False` red herring:** the costmaps here run with
`obstacles_layer.enabled: false` and no `observation_sources`, so move_base never
subscribes to `/scan` by design. `depthimage_to_laserscan` then never subscribes to the
depth image either (lazy subscription). That whole chain being silent is **normal** and
is not evidence of a sensor fault. See `tools/RESTORE_ROS.md` §4.
