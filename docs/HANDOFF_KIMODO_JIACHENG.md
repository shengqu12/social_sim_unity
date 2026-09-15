# Kimodo pedestrian handoff — for Jiacheng

Prepared 2026-09-15 (S114). Every path, hash and number below is copied from tool
output against the working tree at the commit named in §1.

---

## 1. Branch + commit

| field | value |
|---|---|
| remote | `myfork` = `git@github.com:shengqu12/social_sim_unity.git` |
| branch | `sheng/ped-behavior-v2` |
| HEAD | `0cd6743097c4c68a7c8366a2e83aedfaac1755a5` |
| HEAD subject | `S113: curious rework -- closure-solved crouch timing + S113CuriousGaze look-at layer + reel camera` |
| Unity | `2022.3.40f1` (`ProjectSettings/ProjectVersion.txt`: `2022.3.40f1 (cbdda657d2f0)`) |

Clone / checkout:

```bash
git clone git@github.com:shengqu12/social_sim_unity.git
cd social_sim_unity
git checkout sheng/ped-behavior-v2
git rev-parse HEAD      # expect 0cd6743097c4c68a7c8366a2e83aedfaac1755a5

# the Rocketbox avatars are a submodule and the pedestrians will not render without them
git submodule update --init --recursive
```

Upstream note: `origin` is `yale-sean/social_sim_unity` and `howard` is
`HowardHan99/social_sim_unity`. **This work has been pushed to `myfork` only.**
Do not push this branch to `origin` or `howard`.

---

## 2. What's included — the promoted roster

Three clips are promoted and default-on. Speeds are `authoredSpeedMps` from
`Assets/PedestrianAssets/Mixamo/clip_speeds.json`, measured Blender-headless from the
exported FBX as net Hips displacement / duration.

### `kimodo_relaxed_walk` — 1.3563 m/s (cruise-trimmed loop)

- S109 cruise-region trim of the S106-promoted clip (S104 `r1_seed2042`): source BVH motion
  rows 97..168 inclusive, 72 f / 2.3667 s out of the 240 f / 7.9667 s original, cut at
  matching foot-contact phase.
- net 3.2100 m / 2.3667 s; net/path 0.9967. `targetSpeedMps` = authored (ratio 1.00).
- Gates (S109): G-continuity **PASS** (0.89 / 1.05 of loop median, 0.5 s windowed, wrap
  included); wrap seam 5.79° (was 19.32°); rendered on Business_Male_01 ankle |L-R| 1.4 mm,
  stride 1.0136; arms below shoulder; G-speed relaxed band [0.85, 1.40].
- This trim is what fixed the S108 mid-walk stall: the untrimmed clip is authored
  standing-start to standing-stop, so its loop seam carried a ~1 s ease-out/ease-in down to
  ~0.12 m/s and read 0.15 of loop median (G-continuity FAIL).

### `kimodo_elderly_shuffle` — 0.4899 m/s (slow-gait POV specimen)

- S109 cruise-region trim of the S73 shuffle (`c_pp`): source BVH motion rows 57..155
  inclusive, 99 f / 3.2667 s out of 240 f / 7.9667 s, matching foot-contact phase.
- net 1.6003 m / 3.2667 s; net/path 0.9580. `targetSpeedMps` = authored.
- Speed is deliberately **not** band-gated — slow-gait specimen (S106 reclassification).
- Gates (S109): G-continuity **PASS** (0.87 / 1.11 of loop median; untrimmed reads 0.43);
  wrap seam 7.01°; rendered ankle |L-R| 0.1 mm, stride 1.0067; arms below shoulder.

### `kimodo_b2_surprised` — reaction clip, contact IK defaults-on

- One-shot reaction, not a looping gait, so the looping-gait promotion gate does not apply.
- Runtime contact-IK layer pins the hand to the mouth landmark. Its per-clip metadata row
  (`S89ContactIK.Specs`) keys on clip name `"Scene"` with length `5.9667` — see the clip
  identity boundary in §4.

### Asset paths (`git ls-files 'Assets/PedestrianAssets/Kimodo/*'`)

Animation FBXs:

```
Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk.fbx
Assets/PedestrianAssets/Kimodo/kimodo_elderly_shuffle.fbx
Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx
```

Controllers — **plain `AnimatorController` assets, not `AnimatorOverrideController` assets.**
(`git ls-files | grep -i overridecontroller` returns nothing. The override controllers in
this system are built at *runtime*; see §3.)

```
Assets/PedestrianAssets/Kimodo/Resources/kimodo_relaxed_walk.controller
Assets/PedestrianAssets/Kimodo/Resources/kimodo_elderly_shuffle.controller
```

Retired-but-kept assets, on disk for provenance and **not** part of the roster:

```
Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk_s73.fbx
Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk_s106.fbx
Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk_24s.fbx
Assets/PedestrianAssets/Kimodo/kimodo_elderly_shuffle_s73.fbx
Assets/PedestrianAssets/Kimodo/Resources/kimodo_relaxed_walk_s73.controller
Assets/PedestrianAssets/Kimodo/Resources/kimodo_relaxed_walk_s106.controller
Assets/PedestrianAssets/Kimodo/Resources/kimodo_relaxed_walk_24s.controller
Assets/PedestrianAssets/Kimodo/Resources/kimodo_elderly_shuffle_s73.controller
```

Runtime scripts (IK, gaze, gait install):

```
Assets/Scripts/AutoTrial/S41MixamoClipApplier.cs     # installs the gait
Assets/Scripts/AutoTrial/S79GaitOverrideBuilder.cs   # builds the runtime AnimatorOverrideController
Assets/Scripts/AutoTrial/S79StalledGaitIdler.cs      # idle when externally position-owned
Assets/Scripts/AutoTrial/S83SurpriseRebind.cs        # b2 reaction rebind, composes onto the gait override
Assets/Scripts/AutoTrial/S89ContactIK.cs             # contact IK layer (hand-to-mouth), default ON
Assets/Scripts/AutoTrial/S113CuriousGaze.cs          # gaze / look-at IK layer
Assets/Scripts/AutoTrial/S68CuriousCrouch.cs         # closure-solved crouch timing
```

Speed registry and roster notes:

```
Assets/PedestrianAssets/Mixamo/clip_speeds.json
Assets/PedestrianAssets/Kimodo/README.md
Assets/PedestrianAssets/Kimodo/KIMODO_UNITY_STEPS.md
Assets/PedestrianAssets/Kimodo/kimodo_reference_skeleton.json
```

---

## 3. How to enable (Unity 2022.3.40f1)

**Nothing attaches in the Inspector. There is no scene or prefab edit to make for the
promoted three. Do not hand-edit any `.prefab` / `.unity` YAML or `.meta` GUID.**

### 3.1 The attach path, end to end

1. Open the project in Unity `2022.3.40f1` and load the trial scene. `tools/run_trial.py`
   drives the ROS side with `scene:=outdoor` (`roslaunch social_sim_ros map_server.launch
   scene:=outdoor`), which corresponds to `Assets/Scenes/SEAN/Outdoor.unity`.
2. Select the clip with the `--mixamo-clip` flag on `tools/run_trial.py`. Despite the
   Mixamo-era name this is the flag that selects a Kimodo clip too:
   `tools/run_trial.py --mixamo-clip kimodo_relaxed_walk ...`
3. The flag lands in the trial config as `"mixamoClip"` (`tools/run_trial.py:1109`).
4. `AutoTrialBootstrap.cs:939-940` adds an `S41MixamoClipApplier` to the spawned pedestrian
   and sets `clipControllerName = config.mixamoClip`.
5. `S41MixamoClipApplier` defers one frame (assigning the controller rebinds the Animator),
   then calls `InstallGait`.
6. `InstallGait` builds a runtime `AnimatorOverrideController` via
   `S79GaitOverrideBuilder.Build(...)` and assigns it. See §3.2 — this is the part that
   matters.
7. `S83SurpriseRebind` (self-bootstrapping, `[RuntimeInitializeOnLoadMethod]`) waits on the
   applier's `GaitInstalled` flag, then calls
   `S79GaitOverrideBuilder.ApplySurpriseOverride`, which **mutates the already-installed
   override controller in place** so the gait remap and the b2 surprise remap live in ONE
   controller. It does not nest override-on-override.
8. `S89ContactIK` also self-attaches `[RuntimeInitializeOnLoadMethod(AfterSceneLoad)]`
   (`S89ContactIK.cs:311`) and adds itself to the pedestrian at `S89ContactIK.cs:352`.
9. `S113CuriousGaze` carries the gaze / look-at IK layer; `S68CuriousCrouch` carries the
   closure-solved crouch timing.

### 3.2 REQUIRED READING — the wholesale-swap regression, and why it will NOT reproduce

**Question asked at handoff: does the current path replace an existing `AnimatorController`
wholesale, the way `S41MixamoClipApplier` did before S79?**

**Answer: No, not for the promoted three. The override-controller approach was introduced in
Session 79 and scoped in Session 80. But the legacy path still exists and is still the
default for non-Kimodo clips, so the hazard is real and you need to know its shape.**

What the regression was (S78 root cause, quoted from `S79GaitOverrideBuilder.cs`):
`S41MixamoClipApplier` used to do `animator.runtimeAnimatorController = rac;` where `rac` is
one of the generated **single-state, zero-parameter** controllers. That is a wholesale
replacement, so the moment a gait was applied the pedestrian lost:

- every reaction **STATE** — `SurprisedReaction`, `AssertiveGesture`
- every **PARAMETER** — `Surprised`, `AssertiveGesture`, `Forward`, `Strafe`, `Idling`
- the **Idle** node — so a stopped pedestrian kept cycling its walk clip

S78 measured the consequence: `[S41Latency] T_SIGNAL=-1 T_STATE=-1` (trigger fired, state
never entered) and `Parameter 'Surprised' does not exist.` in every log.

What happens instead now: an `AnimatorOverrideController` is a thin clip→clip remap on top of
the pedestrian's own base controller
(`Assets/Resources/Animation/SocialForcesAnimatorController.controller`). Only the
straight-ahead forward-walk clip (`HumanoidWalk`) is overridden. The state machine, its
parameters and its transitions are the base controller's, unchanged — idle, strafes and both
reaction states survive exactly as authored. Turn clips are deliberately *not* overridden.

**Three ways you can still land on the legacy wholesale swap** (`S41MixamoClipApplier.cs`,
`InstallGait`): `bool legacy = forced || inPlaceClip || outOfScope;`

1. `forced` — the environment variable **`AUTOTRIAL_S79_LEGACY_SWAP`** is set to anything
   non-empty (`S79GaitOverrideBuilder.cs:87-91`). It exists to reproduce the pre-S79
   behaviour for comparison. **Make sure it is unset in your shell.**
2. `inPlaceClip` — clips authored in place (~0 authored speed) take the legacy path on
   purpose; putting them on the blend tree's forward node would make the character perform
   them only while moving.
3. `outOfScope` — **the clip name does not start with `kimodo_`**. `IsKimodoGait` is literally
   `controllerName.StartsWith("kimodo_", OrdinalIgnoreCase)`
   (`S41MixamoClipApplier.cs:210-214`). This is the S80 scoping decision: every non-Kimodo
   clip (stock, Mixamo, planD) keeps the legacy wholesale swap by default, because planD's
   frozen pipeline consumes the Mixamo clips and a nonzero regression-arm delta there costs
   more than one uniform install path.

**Correct enable order, and the rule that follows from the above:**

1. Unset `AUTOTRIAL_S79_LEGACY_SWAP`. Unset `AUTOTRIAL_S83_B2_OFF` and
   `AUTOTRIAL_S89_IK_OFF` (both layers are default-on; those variables only turn them off).
2. Pass the clip name **with its `kimodo_` prefix intact** —
   `--mixamo-clip kimodo_relaxed_walk`, not `relaxed_walk`. Dropping the prefix silently
   routes you down the legacy wholesale swap and destroys the reaction states. This is the
   single most likely way to reproduce the S78 symptom by accident.
3. Let the runtime do the rest. Do not pre-assign a `.controller` to the Animator in the
   Inspector — the `kimodo_*.controller` assets are single-state generated controllers and
   are the *source* the gait clip is extracted from, not something to install directly.
4. Verify in the log. A correct install prints
   `[S41Mixamo] '<name>' controller <before> -> override forward='HumanoidWalk' ...`.
   A legacy install prints `... (LEGACY wholesale swap; reason=...)` and names the reason.
   If you see `LEGACY` for a `kimodo_*` clip, stop and fix the environment.

On a failed clip lookup the builder **deliberately does not fall back** to the wholesale
swap — it logs `[S41Mixamo] ... gait override FAILED ... Not falling back to the legacy
swap.` and leaves the stock controller installed. The pedestrian then walks with its stock
gait and reactions still work. That is intended: a silent fallback would reintroduce the
dead reaction states under a different cause.

### 3.3 Default-on switches

| layer | script | default | opt-out |
|---|---|---|---|
| gait override | `S41MixamoClipApplier` / `S79GaitOverrideBuilder` | ON for `kimodo_*` | `AUTOTRIAL_S79_LEGACY_SWAP=1` forces legacy |
| b2 surprise rebind | `S83SurpriseRebind` | ON, scoped to Kimodo pedestrians (S97) | `AUTOTRIAL_S83_B2_OFF=1` |
| contact IK | `S89ContactIK` | ON (S97 retired the enable switch) | `AUTOTRIAL_S89_IK_OFF=1` |

`AUTOTRIAL_S89_IK` still exists as a `const` but is retired as a switch — it is kept only so
the S89–S92 record resolves against the code. Setting it does nothing.

`S89ContactIK` is inert unless **all** of: the clip matches a metadata entry by name AND
length, the playing state is the declared one, and the body has a mouth landmark. Any missing
piece and the component does nothing at all — that is what keeps the regression arms safe.

---

## 4. Known capability boundaries

*Copied verbatim from the S114 work order.*

- prompt speed ceiling ~0.97 m/s practical, slow gaits 0.44–0.97 m/s, fast gaits unreachable
  by prompting
- prompts cannot control body height / support surface / contact points (contact needs the
  runtime IK layer)
- clip identity requires (name, length) compound keys
- generator does not enforce human joint limits
- looping-gait promotion gate: 0.5 s windowed root speed within ±15% of median

> **Annotation added 2026-09-15 (S116) — the list above is unchanged and remains the verbatim
> S114 text. This note qualifies its first bullet; it does not rewrite it.**
>
> The **~0.97 m/s ceiling applies to the whole-clip average speed of UNTRIMMED generator
> output.** It is not a ceiling on reachable gait speed.
>
> Cruise-region trimming reached **1.19–2.12 m/s across 5 of 5 S116 candidates**, every one with
> G-continuity intact (0.5 s windowed root speed, ±15% band, measured on the loop). Measured
> post-trim: 1.1919, 1.2254, 1.5323, 1.5531 and 2.1185 m/s.
>
> Those trims were checked for the obvious failure mode — a momentary peak inside an
> accelerate/decelerate arc would pass a gate that only tests consistency *within* the trimmed
> window. They are **sustained cruise, not arc apexes**: the surrounding flat region runs
> **1.18–2.03× longer than the trim even at a ±5% band**, and the trim median sits at
> **90.7–97.7% of each clip's own peak** windowed speed (an apex would read ≈100%).
>
> **SCOPE LIMIT — read this before relying on the numbers.** This is **source-clip analysis
> only**. **No trial was run on any of the five.** The speeds are **not in-engine confirmed**,
> and **nothing from S116 is promoted** — no `clip_speeds.json` entries, no controller assets.
> Treat them as evidence about what the generator can reach, not as shipping figures.
>
> Full method, per-candidate tables and figures:
> `/mnt/ssd/Social_Navigation/sandbox_s72_nextgen/s116/analysis/s116_screening_report.md`

Two notes on applying these in practice:

- The `(name, length)` compound key is not theoretical. Every Kimodo clip exports with the
  clip name `"Scene"` — `S89ContactIK.Specs` distinguishes b2 purely by
  `clipName = "Scene", clipLength = 5.9667f`. Name alone will collide.
- `kimodo_relaxed_walk` at **1.3563 m/s** is above the ~0.97 m/s prompt ceiling. It is not a
  counterexample: it reached that speed by the S109 cruise-region **trim** (cutting the
  standing-start/standing-stop ease off an 8 s clip), not by prompting. S116 showed this
  trim route is **not a one-off**: it generalises to 5 of 5 candidates screened, over the
  1.19–2.12 m/s range (source-clip measurement; not in-engine confirmed).

---

## 5. Do-not-touch list

| item | owner / status | rule |
|---|---|---|
| `Assets/IVI/Scripts/SFAgent.cs` | Nathan, Yale | read-only; notify before any change |
| `Assets/Scripts/SEAN/Scenario/Agents/Base.cs` | Nathan, Yale | read-only; notify before any change |
| `Assets/Scripts/SEAN/Scenario/PedestrianBehavior/Base.cs` | Nathan, Yale | read-only; notify before any change |
| `Assets/Scripts/SEAN/Tasks/Base.cs` | Nathan, Yale | read-only; notify before any change |
| planD 155-trial dataset | frozen | no file under its directories may change. It lives **outside this repo**, at `/mnt/ssd/Social_Navigation/trial_outputs/dataset_planD` on Sheng's machine; only the generator `tools/s63_dataset_planD.sh` is in-repo |
| any `.prefab` / `.unity` YAML, any `.meta` GUID | — | never hand-edit |
| shared files generally | — | pushing a shared file requires notifying the teammate who owns it first |

`AutoTrialBootstrap.cs` is worth flagging too: S83's design notes record it as outside that
ticket's write boundary, which is why `S83SurpriseRebind` self-bootstraps instead of adding
an attach line there. Treat added attach calls in it as needing a conversation.

---

## 6. Gitignored scratch folders — what a fresh clone will NOT have

`.gitignore:93-94` ignores two Kimodo scratch directories:

```
Assets/PedestrianAssets/Kimodo/S104b/
Assets/PedestrianAssets/Kimodo/S106/
```

Verified with `git check-ignore -v`. Their `.meta` files
(`Assets/PedestrianAssets/Kimodo/S104b.meta`, `S106.meta`) are **intentionally excluded from
the commit** — Unity's own rule is that asset metadata is ignored when the corresponding
asset is ignored, and committing a `.meta` for an ignored folder would leave a dangling
metadata file for every other clone.

Consequence for you: a fresh clone will not contain `S104b/` or `S106/`, and Unity will
simply not show them. Nothing in the promoted roster depends on them — the three promoted
clips and their controllers are all tracked and listed in §2.

If you want them:

- `S104b/` holds the eight S104 relaxed-walk seed candidates (`s104_r1_seed{42,1042,2042}`,
  `s104_r2_seed{42,1042,2042,3042,4042}`) plus a generated `.controller` per candidate under
  `S104b/Resources/`. The winner, `r1_seed2042`, was already promoted and *is* in the repo as
  `kimodo_relaxed_walk_s106.fbx` (and, after the S109 trim, as `kimodo_relaxed_walk.fbx`).
- Regenerate via the in-repo editor importers rather than asking for a copy:
  `Assets/Scripts/AutoTrial/Editor/S104bScratchImport.cs` and
  `Assets/Scripts/AutoTrial/Editor/S106KimodoImport.cs`.
- Otherwise ask Sheng for the folders directly. They are scratch, not paper artifacts.

---

## 7. Extras — the S111 breadth menu

The S111 20-candidate breadth menu is included in this commit at:

```
docs/s111/index.md            # the menu itself: 12 looping-gait rows + 8 reaction rows
docs/s111/metrics_raw.txt     # raw per-candidate metrics
```

**Nothing in it is promoted or gated.** The numbers are reported for orientation only; every
candidate needs Sheng's explicit per-clip ticket before any promotion round, and
`kimodo_limp_mild` (`l6`) additionally needs the p2_limp ethics review first (standing rule).

The preview strips (`SKELETON_*.png` / `ONBODY_*.png`, ~18 MB) are **deliberately not
committed**. They live in the canonical bundle on Sheng's machine:

```
path:    /mnt/ssd/Social_Navigation/sandbox_s72_nextgen/s111/S111_menu.zip
size:    18315530 bytes
sha256:  84cccf22cc09334fb93d51731e2fda5bf6d7a822be36bfd72bda4751b49bc9ea
```

Verified with `sha256sum -c S111_menu.zip.sha256` → `OK` on 2026-09-15 before extraction. The
two files above were extracted from that verified archive.

One caveat carried over from the menu: the continuity min/max column there is measured on the
**untrimmed 8 s source**, so every row shows the S108 standing-start/standing-stop ease and
reads well below the 0.85 gate floor. That is a property of raw Kimodo output, not a defect of
the candidate — a promotion round trims to the cruise region per S109 and re-measures. The
gate script is `s109_continuity_gate.py`, and it is **not in this repo** — it lives in Sheng's
sandbox at `/mnt/ssd/Social_Navigation/sandbox_s72_nextgen/scripts/s109_continuity_gate.py`
(copy also at `.../sandbox_s72_nextgen/s109/scripts/`). The companion trim and speed scripts
are alongside it: `s109_trim_bvh.py`, `s73_root_speed.py`. Ask Sheng if you need them.
