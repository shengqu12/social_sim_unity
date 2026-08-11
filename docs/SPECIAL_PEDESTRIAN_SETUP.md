# Special-Pedestrian Setup — reproducible configuration guide

**Audience:** Jiacheng, reconfiguring special pedestrians and animations from scratch.

---

## 1. Scope

This document describes the special-pedestrian system as it exists at:

```
repo    shengqu12/social_sim_unity
branch  sheng/ped-behavior-v2
commit  0fa73a1b9684cede5263af6d2193489150ed88cc
        ("S68-D: archive what actually bounds the crouch hold, and a trial-guard hazard")
```

> **Note on the commit hash.** The ticket that commissioned this document named
> `5844e5c` as the expected HEAD. **That commit does not exist in this clone** — see
> Appendix B, ERRATA row (1) for the evidence. Everything below was read from disk at
> `0fa73a1`. If a statement here disagrees with a `5844e5c`-era note, this document is the
> one that was verified against a working tree.

**Verification rule used throughout.** Every path, field name, and value below was read
from disk and is cited with `file:line` (for code) or a verbatim asset path (for assets).
Nothing is inferred from naming convention. Where something could not be verified it is
marked `UNVERIFIED — not found on disk`. Inspector field names are only given where the
backing C# field was confirmed `public` or `[SerializeField]`; the access modifier is
stated in each case.

### 1.1 The three axes — independent and NOT interchangeable

A "special pedestrian" is produced by up to three *independent* mechanisms. They compose,
but none substitutes for another. Confusing them is the single most common setup error.

| Axis | What it changes | Selector | Authoritative definition |
|---|---|---|---|
| **A — Appearance** | which avatar/prefab is spawned | `--appearance <name>` | `Assets/Scripts/AutoTrial/AutoTrialBootstrap.cs:30-49` |
| **B — Mixamo clip** | which motion clip an *ordinary* avatar plays | `--mixamo-clip <Name>` | `Assets/PedestrianAssets/Mixamo/Resources/*.controller` |
| **C — Crouch** | curious-personality crouch behaviour | env `AUTOTRIAL_S68_CROUCH` | `Assets/Scripts/AutoTrial/S68CuriousCrouch.cs:180` |

**Axis A is authoritative in `ResolveAppearance()`, `AutoTrialBootstrap.cs:587-604`** — that
is the code that actually turns a name into a prefab.

**`ZONE_B_APPEARANCES` in `tools/run_trial.py:88-91` is a typo-catcher, NOT the source of
truth.** Its own consumer says so — `tools/run_trial.py:637-647`,
`validate_appearance_friendly()`, ends its error text with:

> `"this check is just an early typo catcher)."`

If you add an appearance, adding it to `ZONE_B_APPEARANCES` alone does nothing. You must
add it to `ZoneBContainers` in `AutoTrialBootstrap.cs`.

### 1.2 Alias table — shorthand names are NON-CANONICAL

Prior tickets, decks, and handoff notes use shorthand that does **not** match disk. Use the
canonical column everywhere; the shorthand column will not resolve.

| Shorthand (**NON-CANONICAL** — do not use) | Canonical on-disk name | Axis |
|---|---|---|
| `wheelchair` | `wheelchair_user` | A |
| `scooter` | `scooter_user` | A |
| `white_cane` | `white_cane_user` | A |
| `cyclist` | `cyclist` *(same)* | A |
| `dog_walker` | `dog_walker` *(same)* | A |
| `male_child` / `female_child` | *(same)* | A |
| `phone_user` | *(same)* | A |
| `Pacing_Phone` | **`Pacing_And_Talking_On_A_Phone`** | B |
| `old_man` | `Old_Man_Walk` | B |
| `Drunk` | `Drunk_Walk` | B |
| `standing_arguing` | `Standing_Arguing` | B |
| `talking_standing` | `Talking_standing` | B |
| `Sitting` | `Sitting` *(same)* | B |
| `carry_and_walk` | `carry_and_walk` *(same)* | B |
| `Running` | `Running` *(same)* | B |
| `Stroke_Shaking_Head` | `Stroke_Shaking_Head` *(same)* | B |

`Pacing_Phone` in particular is a **hard failure**: `Resources.Load` is exact-match
(`S41MixamoClipApplier.cs:60`), so it hits the error path at `:63` and the original
controller is silently left in place.

---

## 2. File map

Every source file involved, one line each.

### Unity runtime — `Assets/Scripts/AutoTrial/`

| File | Role |
|---|---|
| `AutoTrialBootstrap.cs` | CLI entry point; owns the axis-A roster (`:30-49`), resolves appearance→prefab (`:587-604`), spawns and wires every per-trial component |
| `AutoTrialConfig.cs` | Deserialization target for the trial config JSON (`appearance :46`, `personality :50`, `pedSpeedMultiplier :93`, `pedMotion :97`, `mixamoClip :157`) |
| `S41MixamoClipApplier.cs` | Axis B: swaps the avatar's controller to a Mixamo clip controller and applies its authored speed |
| `S32AnimatorSpeedScaler.cs` | Scales `animator.speed` from ground speed ÷ `referenceSpeedMps` |
| `S44ClipProps.cs` | Per-clip staging props (Sitting's stool, Standing_Arguing's second person) |
| `S68CuriousCrouch.cs` | Axis C: approach → step aside → kneel → watch → stand → leave |
| `S68CrouchSmokeRunner.cs` | Play-mode smoke test for the crouch controller |
| `S35HeadingAlignmentGuardian.cs` | Facing alignment + optional straight-line position correction (`hasLine`) |
| `S34PedestrianReactDistGate.cs` | TTC-based reaction gate |
| `S21PedestrianPositionGuardian.cs` | Guards against the origin-reset defect |
| `S21TransformWatcher.cs` | Diagnostic, attached only for `white_cane_user` (`AutoTrialBootstrap.cs:854-863`) |
| `IPedestrianVelocityOverride.cs` | Interface `S68CuriousCrouch` implements to take over velocity |
| `TrialController.cs` | Per-frame capture, `frames.csv`, trigger polling |
| `Editor/S41MixamoControllerGen.cs` | Editor tool that generates the single-state Mixamo controllers |
| `Editor/S41MixamoImport.cs` | Editor tool for Mixamo FBX import settings |
| `Editor/S68CrouchImport.cs` | Editor tool for the crouch clip import; owns the `UseIviCrouch` flag |
| `Editor/AutoTrialEditorRunner.cs` | Opens the scene and enters Play in batchmode |

### Unity shared (**READ-ONLY** — do not edit)

| File | Role |
|---|---|
| `Assets/Scripts/SEAN/Scenario/Agents/AppearanceAvatar.cs` | The component on every axis-A container prefab; instantiates the avatar and optionally overrides its controller |
| `Assets/Scripts/SEAN/Scenario/Agents/Base.cs` | Agent motion; owns the `directVelocityDrive` branch (`:335-344`) |
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianModulator.cs` | Personality behaviour; defines `PersonalityType` |
| `Assets/Scripts/Agents/Parameters.cs` | `CLOSE_ENOUGH_MIN_DIST = 1.0f` (`:34`) |
| `Assets/IVI/Scripts/Navigation/INavigable.cs` | Consumes `CLOSE_ENOUGH_MIN_DIST` (`:134`) |

### Python harness — `tools/`

| File | Role |
|---|---|
| `run_trial.py` | The launcher. Owns per-appearance speed constants, writes the config JSON, launches Unity |

### Data

| File | Role |
|---|---|
| `Assets/PedestrianAssets/Mixamo/clip_speeds.json` | Single source for both authored pace and target pace, per its own `_comment` |
| `Assets/PedestrianAssets/Mixamo/authored_speeds.json` | Raw measured `averageSpeed` dump (superset; includes non-roster clips) |
| `Assets/PedestrianAssets/S68Crouch/EXCLUDED_IviCrouch_copy.md` | Why the IVI crouch FBX is excluded |

---

## 3. Asset map

### 3.1 Axis A — the 8 special appearances

All paths verbatim. Container paths are **Resources-relative** (as stored in
`AutoTrialBootstrap.cs:30-49`); prepend `Assets/Resources/` and append `.prefab` for the
on-disk path. All 8 containers, all 8 avatar prefabs and all referenced controllers were
confirmed present on disk at `0fa73a1`.

| `--appearance` | Container (Resources-relative) | Avatar prefab | Animator Controller (on the container) | `directVelocityDrive` |
|---|---|---|---|---|
| `cyclist` | `Prefabs/CyclistContainer` | `Assets/Resources/Prefabs/Community-informed Model/Cyclist/Cyclist.prefab` | `Assets/Resources/Prefabs/Community-informed Model/Cyclist/Cycling Animation/CyclistController.controller` | `1` |
| `dog_walker` | `Prefabs/DogWalkerContainer` | `Assets/Resources/Prefabs/Community-informed Model/Dog Walker/Dog_Walker.prefab` | `Assets/IVI/Controllers/BaseSFControllerNormalized.controller` | **absent → `false`** |
| `female_child` | `Prefabs/FemaleChildContainer` | `Assets/Resources/Prefabs/Community-informed Model/Female Child/Female_Child.prefab` | `Assets/IVI/Controllers/BaseSFControllerNormalized.controller` | `1` |
| `male_child` | `Prefabs/MaleChildContainer` | `Assets/Resources/Prefabs/Community-informed Model/Male Child/Male_Child.prefab` | `Assets/IVI/Controllers/BaseSFControllerNormalized.controller` | `1` |
| `phone_user` | `Prefabs/PedetrainAvatars/PhoneUserContainer` | `Assets/Resources/Prefabs/PedetrainAvatars/PhoneUser_Ped.prefab` | `Assets/CustomAnimations/PhoneUser_TextingController.controller` | `0` |
| `scooter_user` | `Prefabs/ScooterUserContainer` | `Assets/Resources/Prefabs/Community-informed Model/Scooter User/Scooter_User.prefab` | **`{fileID: 0}` — none** (see §7.2) | `1` |
| `wheelchair_user` | `Prefabs/WheelChairUserContainer` | `Assets/Resources/Prefabs/Rocketbox/Wheelchair_Female.prefab` | **`{fileID: 0}` — none** (see §7.2) | `1` |
| `white_cane_user` | `Prefabs/WhiteCaneUserContainer` | `Assets/Resources/Prefabs/Community-informed Model/White Cane User/White_Cane_User.prefab` | `Assets/IVI/Controllers/BaseSFControllerNormalized.controller` | **absent → `false`** |

> The `PedetrainAvatars` folder name in `phone_user`'s path is **not a typo** — it is spelled
> that way on disk. `AutoTrialBootstrap.cs:28-29` says so explicitly: *"including the
> PedetrainAvatars folder nesting for phone_user (not a typo -- the other 7 live one level up)."*

Per-appearance speed multipliers, from `tools/run_trial.py`, assembled into
`APPEARANCE_SPEED_MULT` at `:2353-2360`:

| Appearance | Constant | Value | Defined at |
|---|---|---|---|
| `scooter_user` | `SCOOTER_SPEED_MULT` | `4.5914` | `run_trial.py:223` |
| `cyclist` | `CYCLIST_SPEED_MULT` | `5.9565` | `run_trial.py:224` |
| `wheelchair_user` | `WHEELCHAIR_SPEED_MULT` | **`1.0`** | `run_trial.py:249` — see §7.1 |
| `white_cane_user` | `WHITE_CANE_SPEED_MULT` | `0.4296` | `run_trial.py:273` — see §7.4 |
| `dog_walker` | `DOG_WALKER_SPEED_MULT` | `0.6271` | `run_trial.py:282` |
| `phone_user` | `PHONE_USER_SPEED_MULT` | `0.9068` | `run_trial.py:283` |
| `male_child`, `female_child` | *(none)* | — | not in the table; see §7.5 |

Base pace: `BASE_PED_SPEED_MPS = 1.0476` (`run_trial.py:1489`). Commanded speed =
`BASE_PED_SPEED_MPS × multiplier`.

Scoring-profile target speeds, `run_trial.py:2162-2167` (used to size spawn geometry for a
constant dwell of `SCORING_TIER_TARGET_DWELL_SEC = 6.2`, `:2161`):
`scooter_user` 3.515, `cyclist` 4.560, `wheelchair_user` 0.890, `white_cane_user` 0.6 m/s.

### 3.2 Axis B — the 9 Mixamo clips

Every controller is single-layer (`Base Layer`), single-state, and the state name equals the
controller name. All 9 controllers and all 9 clip FBXs are present on disk **and tracked by
git** at `0fa73a1`.

| `--mixamo-clip` | Controller (all under `Assets/PedestrianAssets/Mixamo/Resources/`) | Layer | State | Clip FBX (all under `Assets/PedestrianAssets/Mixamo/`) | authored m/s | target m/s |
|---|---|---|---|---|---|---|
| `carry_and_walk` | `carry_and_walk.controller` | Base Layer | `carry_and_walk` | `carry_and_walk.fbx` | 0.8969 | 1.0 |
| `Drunk_Walk` | `Drunk_Walk.controller` | Base Layer | `Drunk_Walk` | `Drunk Walk.fbx` | 0.7160 | 0.6 |
| `Old_Man_Walk` | `Old_Man_Walk.controller` | Base Layer | `Old_Man_Walk` | `Old Man Walk.fbx` | 0.3915 | **0.45** |
| `Pacing_And_Talking_On_A_Phone` | `Pacing_And_Talking_On_A_Phone.controller` | Base Layer | `Pacing_And_Talking_On_A_Phone` | `Pacing And Talking On A Phone.fbx` | **0.5636** | 0.8 |
| `Sitting` | `Sitting.controller` | Base Layer | `Sitting` | `Sitting.fbx` | 0.0 *(inPlace)* | 0 |
| `Standing_Arguing` | `Standing_Arguing.controller` | Base Layer | `Standing_Arguing` | `Standing Arguing.fbx` | 0.0 *(inPlace)* | 0 |
| `Stroke_Shaking_Head` | `Stroke_Shaking_Head.controller` | Base Layer | `Stroke_Shaking_Head` | `Stroke Shaking Head.fbx` | 0.0 *(inPlace)* | 0 |
| `Running` | `Running.controller` | Base Layer | `Running` | `Running.fbx` | **no entry** | **no entry** — §7.6 |
| `Talking_standing` | `Talking_standing.controller` | Base Layer | `Talking_standing` | `Talking_standing.fbx` | **no entry** | **no entry** — §7.6 |

Speed values are from `Assets/PedestrianAssets/Mixamo/clip_speeds.json`. Axis B does **not**
select an avatar — it retargets a clip onto whatever `--appearance` selected, normally an
ordinary Rocketbox actor (e.g. `--appearance business_male_01 --mixamo-clip Sitting`).

### 3.3 Axis C — crouch

| Item | Path / value |
|---|---|
| Env selector | `AUTOTRIAL_S68_CROUCH` (any non-empty value), `S68CuriousCrouch.cs:180` |
| Controller Resources name | `S68_CuriousCrouch`, `S68CuriousCrouch.cs:112` |
| Controller asset | `Assets/PedestrianAssets/S68Crouch/Resources/S68_CuriousCrouch.controller` |
| Layer | `Base Layer` |
| State | `S68CrouchPose` (`S68CuriousCrouch.cs:116`, `public const string StatePose`) |
| Clip FBX | `Assets/PedestrianAssets/S68Crouch/Kneeling Down.fbx` |
| Additional env | `AUTOTRIAL_S68_STOP_DIST` (`:67`), `AUTOTRIAL_S68_STANDUP_DIST` (`:79`) |

Also on disk in `Assets/PedestrianAssets/S68Crouch/`, **not** wired into the controller:
`Crouch To Stand.fbx`, `Crouch To Stand v2.fbx`, `IviCrouch_copy.fbx` (excluded — §7.8).

---

## 4. Unity Inspector setup

### 4.1 `AppearanceAvatar` — the only component you author by hand

**Component:** `AppearanceAvatar`
**Source:** `Assets/Scripts/SEAN/Scenario/Agents/AppearanceAvatar.cs:16`
**GameObject:** the root of each axis-A *container* prefab (e.g. `WheelChairUserContainer`).
Each container prefab is a bare GameObject with only a `Transform` and this one component.

All four fields are **`public`** (not `[SerializeField]`), so the Inspector label is the C#
field name with Unity's usual capitalisation.

| Inspector field | C# declaration | Type | Access | Purpose |
|---|---|---|---|---|
| Animation Controller | `animationController` `:18` | `RuntimeAnimatorController` | `public` | Overrides the avatar's own controller. **Leave empty to keep the avatar prefab's shipped controller** — see §7.2 |
| Avatars | `avatars` `:19` | `GameObject[]` | `public` | Candidate avatar prefabs; one is picked at random per spawn (`:32`). All 8 containers ship exactly one element |
| Controller | `controller` `:20` | `LowLevelControl` | `public` | Low-level control mode. **All 8 containers: `0`.** Overwritten at runtime if a `SEAN` singleton exists (`:55-58`) |
| Direct Velocity Drive | `directVelocityDrive` `:26` | `bool` | `public` | Default `false`. `true` ⇒ translation comes from social-force velocity, not root motion |

Current values in the repo, read from each container prefab's YAML:

| Container prefab | `animationController` | `avatars[0]` | `controller` | `directVelocityDrive` |
|---|---|---|---|---|
| `CyclistContainer` | `CyclistController.controller` | `Cyclist.prefab` | `0` | `1` |
| `DogWalkerContainer` | `BaseSFControllerNormalized.controller` | `Dog_Walker.prefab` | `0` | **field absent → `false`** |
| `FemaleChildContainer` | `BaseSFControllerNormalized.controller` | `Female_Child.prefab` | `0` | `1` |
| `MaleChildContainer` | `BaseSFControllerNormalized.controller` | `Male_Child.prefab` | `0` | `1` |
| `PhoneUserContainer` | `PhoneUser_TextingController.controller` | `PhoneUser_Ped.prefab` | `0` | `0` |
| `ScooterUserContainer` | **`{fileID: 0}` (empty)** | `Scooter_User.prefab` | `0` | `1` |
| `WheelChairUserContainer` | **`{fileID: 0}` (empty)** | `Wheelchair_Female.prefab` | `0` | `1` |
| `WhiteCaneUserContainer` | `BaseSFControllerNormalized.controller` | `White_Cane_User.prefab` | `0` | **field absent → `false`** |

### 4.2 Runtime-assigned components — leave these OUT of the Inspector

Every component below is added by `AddComponent` at runtime. **Do not attach any of them to
a prefab or scene object by hand** — the container prefabs contain only `AppearanceAvatar`.

`AutoTrialBootstrap` has **two spawn branches** — Zone B (the 8 special containers) and
Zone A (convention-resolved Rocketbox actors). Several components are attached in both, at
different lines. Both are given below.

| Component | Added at — Zone B | Added at — Zone A | Fields set by code |
|---|---|---|---|
| `S41MixamoClipApplier` | *(not attached)* | `AutoTrialBootstrap.cs:939` | `clipControllerName` ← `config.mixamoClip` (`:940`); `attachCarriedBox` ← `config.carriedBox` (`:941`) |
| `S35HeadingAlignmentGuardian` | `AutoTrialBootstrap.cs:841` | `AutoTrialBootstrap.cs:932` | `personality` (`:933`); `targetHeadingDeg`/`hasTargetHeading`/`lineStart`/`lineEnd`/`hasLine` (`:851-855`, `:1002`) |
| `S68CuriousCrouch` | *(not attached)* | `AutoTrialBootstrap.cs:1187` | `leaveDestination` (`:1189`), `hasLeaveDestination` (`:1190`) |
| `S21TransformWatcher` | `AutoTrialBootstrap.cs:858` | *(not attached)* | *(none)* — gated on `config.appearance == "white_cane_user"` at `:854` |
| `S34AnimatorCullingFix` | `AutoTrialBootstrap.cs:827` | `AutoTrialBootstrap.cs:925` | *(none)* |
| `S32AnimatorSpeedScaler` | attached with the modulator; `referenceSpeedMps` / `referenceSpeedMpsExplicit` set at `AutoTrialBootstrap.cs:795-801` and `S41MixamoClipApplier.cs:147,153` | same | `referenceSpeedMps` (default `1.3f`, `S32AnimatorSpeedScaler.cs:59`) |
| `S44ClipProps` | *(not attached)* | `S41MixamoClipApplier.cs:78` | `clipName` (`:79`) |
| `PedestrianModulator` | `Assets/Scripts/SEAN/Scenario/Agents/PedestrianSpawner.cs:148` | same | `walkSpeedMultiplier` |

`S32AnimatorSpeedScaler.referenceSpeedMpsExplicit` is declared
`[System.NonSerialized] public bool` (`S32AnimatorSpeedScaler.cs:77`) — it therefore **never
appears in the Inspector at all**, by design.

---

## 5. Runtime data flow

### 5.1 Axis A — `--appearance` → a spawned avatar

| # | Hop | Cite |
|---|---|---|
| 1 | `--appearance` parsed | `tools/run_trial.py:1828` |
| 2 | Friendly validation (**typo-catcher only**) | `run_trial.py:2279` → `:637-647` |
| 3 | Speed multiplier resolved if `--ped-speed` absent | `run_trial.py:2391-2392` from `APPEARANCE_SPEED_MULT` (`:2353-2360`) |
| 4 | Written into the config dict as `"appearance"` | `run_trial.py:1081` |
| 5 | Config dict serialized to `<out_dir>/config.json` | `run_trial.py:1383-1384` |
| 6 | Path passed to Unity as `-trialConfig <path>` | `run_trial.py:1398` |
| 7 | Unity reads it — `-trialConfig` argv, else `TRIAL_CONFIG` env | `AutoTrialBootstrap.cs:116-129` |
| 8 | Bootstrap starts after scene load | `AutoTrialBootstrap.cs:97-114` (`RuntimeInitializeOnLoadMethod(AfterSceneLoad)`) |
| 9 | JSON → `AutoTrialConfig` (`appearance` `:46`) | `AutoTrialBootstrap.cs:139` `LoadConfig()` |
| 10 | **Name → prefab.** Axis-A dict lookup first; else Zone-A Rocketbox convention | `AutoTrialBootstrap.cs:587-604` — dict at `:30-49`, fallback at `:597-602` |
| 11 | Container instantiated; `AppearanceAvatar.Awake()` runs | `AppearanceAvatar.cs:30` |
| 12 | Avatar picked from `avatars[]` and instantiated | `AppearanceAvatar.cs:32-33` |
| 13 | Locomotion Animator resolved (self/children, prefers Humanoid) | `AppearanceAvatar.cs:39` |
| 14 | Controller overridden **only if `animationController != null`** | `AppearanceAvatar.cs:45-48` |
| 15 | `SFAgent` (or ORCA) attached | `AppearanceAvatar.cs:59-66` |
| 16 | `directVelocityDrive` pushed onto `Base.DirectVelocityDrive` | `AppearanceAvatar.cs:67-73` |
| 17 | Per-frame motion takes the direct-velocity branch if set | `Base.cs:335-344` |

### 5.2 Axis B — `--mixamo-clip` → a clip playing

| # | Hop | Cite |
|---|---|---|
| 1 | `--mixamo-clip` parsed | `run_trial.py:1962` |
| 2 | Target pace → `--ped-speed`, if `--ped-speed` not given | `run_trial.py:2367-2376`; target read via `mixamo_target_speed()` (`:1536`) from `clip_speeds.json` |
| 3 | Spawn geometry resized for the new pace | `run_trial.py:2183-2184` |
| 4 | Written into config as `"mixamoClip"` | `run_trial.py:1109` |
| 5 | Deserialized to `AutoTrialConfig.mixamoClip` | `AutoTrialConfig.cs:157` |
| 6 | Applier attached iff `mixamoClip` non-empty or `carriedBox` | `AutoTrialBootstrap.cs:937-942` |
| 7 | `clipControllerName` assigned | `AutoTrialBootstrap.cs:940` |
| 8 | Apply deferred **one frame** (rebind must settle before `GetBoneTransform`) | `S41MixamoClipApplier.cs:42`, `:45-50` |
| 9 | Locomotion Animator resolved | `S41MixamoClipApplier.cs:51` |
| 10 | **`Resources.Load<RuntimeAnimatorController>(clipControllerName)`** — exact-match | `S41MixamoClipApplier.cs:60` |
| 11 | On failure: error logged, original controller kept | `S41MixamoClipApplier.cs:62-65` |
| 12 | On success: `animator.runtimeAnimatorController = rac` | `S41MixamoClipApplier.cs:71` |
| 13 | Authored speed applied | `S41MixamoClipApplier.cs:73` → `:115-156` |
| 14 | `clip_speeds` loaded from Resources, falling back to the literal asset path | `:120-121`, fallback `ReadFromAssetPath()` `:158-160` |
| 15 | No entry ⇒ warning, `referenceSpeedMps` left at its default | `:132-137` |
| 16 | In-place clip ⇒ scaling skipped entirely | `:138-144` |
| 17 | Otherwise `referenceSpeedMps` ← authored, marked explicit | `:146-153` |
| 18 | Per-clip staging props attached | `:78-79` |

**Ordering note (order matters).** Step 14's `Resources.Load<TextAsset>("clip_speeds")` will
**not** resolve: `clip_speeds.json` lives at `Assets/PedestrianAssets/Mixamo/clip_speeds.json`,
which is the *parent* of the `Resources` folder, not inside it. This is handled, not broken —
`S41MixamoClipApplier.cs:121` falls back to `ReadFromAssetPath()`, which reads the literal
path `"Assets/PedestrianAssets/Mixamo/clip_speeds.json"` (`:160`). Do not "fix" this by moving
the JSON without checking both readers.

### 5.3 Axis C — `AUTOTRIAL_S68_CROUCH` → a crouch

| # | Hop | Cite |
|---|---|---|
| 1 | Env var read (non-empty ⇒ enabled) | `S68CuriousCrouch.cs:180` |
| 2 | Attached **only** for Curious **and** only when enabled | `AutoTrialBootstrap.cs:1184-1187` |
| 3 | `leaveDestination` / `hasLeaveDestination` set | `AutoTrialBootstrap.cs:1189-1190` |
| 4 | `S35HeadingAlignmentGuardian.hasLine` forced `false` for this agent | `AutoTrialBootstrap.cs:1205-1210` |
| 5 | Crouch controller loaded by Resources name | `S68CuriousCrouch.cs:194` |
| 6 | Velocity taken over via `IPedestrianVelocityOverride.TryModulate` | `S68CuriousCrouch.cs:231` |

---

## 6. Reproduce from scratch — checklist

> **Stop Play before editing Inspector values.** Unity discards Play-mode Inspector edits on
> exit, and this project has separately observed Play-mode state leaking *back into* saved
> scene assets on the next domain reload. Edit in Edit mode only.

1. **Clone and check out.**
   `git clone git@github.com:shengqu12/social_sim_unity.git && git checkout sheng/ped-behavior-v2`
   Confirm `git rev-parse HEAD` = `0fa73a1b9684cede5263af6d2193489150ed88cc`.
2. **Restore the Zone-B binary payload.** **74 files (482,953,411 B / 460.58 MB)** are
   **not in git**. Copy them into the working tree at their exact paths, from the backup
   Sheng provides. Verify against `zoneB_asset_backup_manifest.txt` (sha256 + byte size
   per file). **The `.meta` files are mandatory** — see §7.9.

   The manifest tiers them:
   - **REQUIRED — 54 files (27 assets + 27 metas, 364.90 MB).** Reached by transitive GUID
     closure from the 8 avatar prefabs. Omitting any of these provably breaks a reference.
   - **EXTRA — 20 files (10 textures + 10 metas, 95.68 MB).** Texture files in the same
     character folders that are **not** GUID-referenced by any asset in `Assets/`. Restore
     them anyway: Unity FBX importers can bind embedded-material textures **by filename**
     rather than by GUID, so GUID absence does not prove disuse, and a wrong exclusion costs
     missing scooter/wheelchair materials. Do not prune them without an actual reimport test.
3. **Open the project in Unity** and let it import. Do not delete `Library/` afterwards
   without re-checking step 2.
4. **Verify the 8 axis-A containers resolve.** For each of the 8 Resources paths in §3.1,
   confirm the asset exists at `Assets/Resources/<path>.prefab` and that its
   `AppearanceAvatar` shows a non-missing `avatars[0]`. A missing avatar shows as
   `Missing (GameObject)` — that means step 2 was incomplete.
5. **Verify the 9 axis-B controllers.** Each of `Assets/PedestrianAssets/Mixamo/Resources/*.controller`
   must show one state whose Motion is a non-missing clip. These are tracked in git, so a
   miss here means a bad checkout, not a missing binary.
6. **Verify the axis-C controller.** `Assets/PedestrianAssets/S68Crouch/Resources/S68_CuriousCrouch.controller`,
   state `S68CrouchPose`, Motion = `Kneeling Down.fbx`.
7. **To add a NEW special appearance:**
   a. Create the avatar prefab under `Assets/Resources/Prefabs/...`.
   b. Create a container prefab: empty GameObject + `AppearanceAvatar` only.
   c. Set `avatars[0]` to the avatar prefab. Set `animationController` **only** if you
      intend to override the avatar's own controller (§7.2).
   d. Set `directVelocityDrive` **explicitly** — do not rely on the default (§7.3).
   e. **Add the name → Resources-path entry to `ZoneBContainers`,
      `AutoTrialBootstrap.cs:30-49`.** This is the step that actually registers it.
   f. Optionally add the name to `ZONE_B_APPEARANCES` (`run_trial.py:88-91`) so the
      typo-catcher stops rejecting it. This alone does nothing functionally.
   g. Optionally add a speed multiplier to `APPEARANCE_SPEED_MULT` (`run_trial.py:2353-2360`).
      **If you choose exactly `1.0`, read §7.1 first.**
8. **To add a NEW Mixamo clip:**
   a. Import the FBX under `Assets/PedestrianAssets/Mixamo/`.
   b. Generate its controller into `Assets/PedestrianAssets/Mixamo/Resources/` — use the
      editor tool `Editor/S41MixamoControllerGen.cs` rather than hand-authoring; it
      enforces the naming rule at `:93` (*"Resources.Load and the CLI both go through this
      name, so spaces are the one thing that…"*).
   c. **Add a `clips[]` entry to `clip_speeds.json`** with `authoredSpeedMps`, `inPlace`,
      and `targetSpeedMps`. Skipping this is the §7.6 failure.
   d. Regenerate measured values with the editor menu the file names:
      *"AutoTrial/Session 44/Refresh clip_speeds.json"* (per `clip_speeds.json` `_authored`).
9. **Smoke-test in Play mode, not Edit mode.** Edit-mode posing is unreliable on these
   avatars; use `S68CrouchSmokeRunner.cs` as the pattern for a Play-mode check.
10. **Run one trial per axis** to confirm end-to-end:
    - Axis A: `python tools/run_trial.py --appearance wheelchair_user --personality indifferent --duration 90`
    - Axis B: `python tools/run_trial.py --appearance business_male_01 --personality indifferent --mixamo-clip Sitting --ped-motion standing`
    - Axis C: `AUTOTRIAL_S68_CROUCH=1 python tools/run_trial.py --appearance business_male_01 --personality curious`
11. **Check the Unity log** for `[S41Mixamo]`, `[S60Calib]`, `[S68Curious]` lines. A
    `Resources.Load failed` error means a name mismatch — check §1.2.

---

## 7. Known pitfalls

### 7.1 `WHEELCHAIR_SPEED_MULT` must stay **exactly** `1.0` — silent behaviour change

`tools/run_trial.py:249` sets `WHEELCHAIR_SPEED_MULT = 1.0`, wrapped in this banner
(`run_trial.py:225-241`, quoted verbatim):

> ```
> # ############################################################################################
> # LEAVE AT EXACTLY 1.0. Changing it changes BEHAVIOUR, not just speed, and does so SILENTLY.
> # ############################################################################################
> #
> # AutoTrialBootstrap.cs:798 gates modulator attachment on
> # `!Mathf.Approximately(config.pedSpeedMultiplier, 1.0f)`. At exactly 1.0 this appearance gets
> # NO PedestrianModulator at all, so:
> #   - it runs on raw social-force velocity, capped by Parameters.MAX_VEL = 0.95 m/s (not the 0.6
> #     that a stale comment in AutoTrialBootstrap claimed until Session 54)
> #   - Session 47's absolute-target modulation (e) never applies to it
> #   - BASE_PED_SPEED_MPS never applies either, which is why Session 54's 1.3 -> 1.0476
> #     recalibration deliberately left this entry alone
> #
> # **That no-modulator behaviour is what passed human review.** Set this to 1.001 and the agent
> # gains a modulator, its speed law changes, and NOTHING reports it -- no error, no warning, no
> # gate. The only symptom is a trial that quietly behaves differently from the one that was
> # approved.
> ```

**Line-reference correction (verified this session).** The banner cites
`AutoTrialBootstrap.cs:798`. At `0fa73a1` that line is inside the `ZoneBRetargetCalibration`
block, not the modulator gate. The gate the banner describes is real but has **moved**:

| Gate | Actual location at `0fa73a1` |
|---|---|
| `if (!Mathf.Approximately(config.pedSpeedMultiplier, 1.0f))` — Zone B | `AutoTrialBootstrap.cs:872` |
| `bool wantsSpeedScale = !Mathf.Approximately(config.pedSpeedMultiplier, 1.0f);` — Zone A | `AutoTrialBootstrap.cs:1011` |

The **mechanism and the warning are unchanged and still correct** — only the line number in
the comment is stale. `Parameters.MAX_VEL = 0.95f` is confirmed at
`Assets/Scripts/Agents/Parameters.cs:32`.

### 7.2 `scooter_user` and `wheelchair_user` have `animationController: {fileID: 0}`

Both container prefabs leave the field empty. **This is deliberate, not a missing
reference.** `AppearanceAvatar.cs:42-48`:

> ```csharp
> // Leave animationController unset (null) to keep whatever
> // RuntimeAnimatorController the avatar prefab already ships with -- e.g. the
> // wheelchair avatar's own Wheelchair.controller, which must stay untouched.
> if (animationController != null)
> {
>     animator.runtimeAnimatorController = animationController;
> }
> ```

What drives each of them instead:

- **Posture** comes from the avatar prefab's own Animator, resolved at runtime by
  `IVI.AvatarAnimatorUtility.GetLocomotionAnimator(avatarObject)` (`AppearanceAvatar.cs:39`).
  - `wheelchair_user`: `Wheelchair_Female.prefab` carries an `Animator` whose
    `m_Controller` points at
    `Assets/Resources/Prefabs/Rocketbox/wheelchairuser-female/wheelchairuser-women.controller`.
  - `scooter_user`: `Scooter_User.prefab` carries **no `Animator` component of its own**.
    Its Animator is contributed by the nested model instances `casual_male.fbx` and
    `default.fbx`, resolved by the same `GetLocomotionAnimator` call at runtime.
- **Translation** comes from neither. Both set `directVelocityDrive: 1`, which
  `AppearanceAvatar.cs:67-73` pushes onto `Base.DirectVelocityDrive`, taking this branch in
  `Base.cs:335-344`:

> ```csharp
> if (directVelocityDrive)
> {
>     // No usable root motion from this avatar's Animator (e.g. a wheelchair's
>     // looping seated-idle has zero deltaPosition every frame) -- drive the
>     // transform directly from the social-force velocity instead. ...
>     transform.position += velocity * Time.deltaTime;
> }
> ```

So: **clip for posture only, social-force velocity for position.** Assigning a controller to
these two containers would override the chair/scooter rig's own animation and is not what
passed review.

### 7.3 `directVelocityDrive` absent from prefab YAML ⇒ C# default `false`

The field is declared `public bool directVelocityDrive = false;` at
`AppearanceAvatar.cs:26`. When a container prefab's YAML has no `directVelocityDrive:` line
at all, the field takes that C# default. Two containers rely on the default:

| Container prefab | Status |
|---|---|
| `DogWalkerContainer.prefab` | line absent → `false` |
| `WhiteCaneUserContainer.prefab` | line absent → `false` |

The other six write the value explicitly (`1` for cyclist / female_child / male_child /
scooter_user / wheelchair_user, `0` for phone_user). This matters because `false` is
load-bearing for both: `AutoTrialBootstrap.cs:258` records *"white_cane is
directVelocityDrive==false, so every metre came from root motion"*. If a future Unity
re-serialisation writes the field out explicitly, confirm it writes `0`, not `1`.

### 7.4 `white_cane_user` — commanded 0.45 m/s vs measured ~0.049 m/s (≈9×)

`WHITE_CANE_SPEED_MULT = 0.4296` (`run_trial.py:273`). The gap is known, documented, and
**deliberately left in place**. From `run_trial.py:264-272`:

> ```
> # Session 60: 0.4296 -> 0.0468. NOT a behaviour change -- 0.0468 * 1.0476 = 0.049 m/s is what
> # this agent has actually been travelling at all along. The old 0.45 was the commanded value,
> # and ~90% of it was lost to humanoid retargeting on this nested-Animator avatar, so the
> # manifest recorded a speed an order of magnitude away from what happened. ...
> # Session 60: REVERTED to 0.4296 (commanded 0.45 m/s) pending diagnosis -- see the calibration
> # table in AutoTrialBootstrap. This restores exactly the state human review approved: realised
> # ~0.049 m/s on screen, with the known and documented ~9x gap between commanded and realised.
> ```

**Any metadata that reports white_cane's speed reports the commanded 0.45, not the realised
~0.049.** Do not treat the manifest number as ground truth for this appearance.

### 7.5 `male_child` / `female_child` require `--ped-motion standing`

Both are static-obstacle characters with no walking animation. Every batch script runs them
with `--ped-motion standing` (e.g. `tools/s54_batch.sh:63-64`,
`tools/s63_dataset_planD.sh:102-103`). The flag was a **silent no-op for Zone B until
Session 54** — `AutoTrialBootstrap.cs:885-892`:

> ```
> // Session 54: honour --ped-motion standing here too. Session 28 PART 3a added it
> // to the Zone A branch only, so the flag was silently a no-op for every Zone B
> // container -- measured on male_child/female_child, which have no walking animation
> // and are meant to be static obstacles: both were released to the far goal and
> // travelled the full 14.0 m.
> ```

Omitting the flag makes both children walk the full 14 m with no walk cycle — a visible
foot-slide, not a crash.

### 7.6 `Running` and `Talking_standing` have no `clip_speeds.json` entry

`clip_speeds.json` defines 7 clips: `carry_and_walk`, `Drunk_Walk`, `Old_Man_Walk`,
`Pacing_And_Talking_On_A_Phone`, `Sitting`, `Standing_Arguing`, `Stroke_Shaking_Head`. The
two controllers `Running.controller` and `Talking_standing.controller` exist on disk and
will load, but fall to the warning path at `S41MixamoClipApplier.cs:132-137`:

> ```csharp
> if (!TryLookup(json, clipControllerName, out authored, out inPlace))
> {
>     Debug.LogWarning("[S41Mixamo] '" + clipControllerName + "' has no clip_speeds.json entry -- "
>         + "leaving referenceSpeedMps=" + scaler.referenceSpeedMps);
>     return;
> }
> ```

`referenceSpeedMps` therefore stays at its default `1.3f`
(`S32AnimatorSpeedScaler.cs:59`) — wrong for both clips. `authored_speeds.json` records
`Running.fbx` at 4.4063 m/s and `Talking_standing.fbx` at 0.0 (inPlace), so the default is
off by ~3.4× for Running. **This is a warning, not an error — it will not fail a trial.**

### 7.7 Per-character constants that look wrong but are not

- **`old_man` target 0.45 m/s.** `clip_speeds.json` `_targetNote` records the change from
  0.7 after human review (*"old_man walks a bit fast"*), chosen to keep `animator.speed` at
  1.15, inside the ±20% of authored that the file's own `_targetRule` allows.
- **`Pacing_And_Talking_On_A_Phone` authored `0.5636`, not `0.415`.** The clip paces back and
  forth, so `AnimationClip.averageSpeed` (net displacement ÷ duration) reads 0.415 and is
  invalid. `clip_speeds.json` `_refSource` records net/path = 0.144 and uses the median
  instantaneous speed over moving frames instead. Its target of 0.8 is flagged by the file's
  own `_targetRule` as **OUT OF RANGE** (ratio 1.42; rule-consistent would be ≤0.68), left at
  0.8 deliberately for the eyeball pass. If you regenerate this file, do not let a naive
  `averageSpeed` read overwrite 0.5636.

### 7.8 EXCLUDED assets — do not re-derive their usefulness from filenames

| Asset | Reason | Source |
|---|---|---|
| `Assets/IVI/Animations/Locomotion Pack/Interacting/Idle2Crouch_*.fbx` (and its copy `Assets/PedestrianAssets/S68Crouch/IviCrouch_copy.fbx`) | **The clip contains no crouch.** Despite the name, it is a 53.667 s mixed mocap take; measured body-height range 1.77–2.04 m, deepest *grounded* pose 1.754 m — a 0.330 m drop, i.e. a stride. A real Mixamo kneel reaches 1.354 m. At normalizedTime 0.40 the character is plainly walking. Kept on disk as the evidence for the exclusion; gated off by `S68CrouchImport.UseIviCrouch` = `false` | `Assets/PedestrianAssets/S68Crouch/EXCLUDED_IviCrouch_copy.md` |
| `phone_user` | Valid and runnable, but **excluded from the dataset roster**: a 3.7532× uniform scale override on its prefab plus a ~70° heading-vs-velocity mismatch that makes it sidestep its whole path at 17% of commanded speed. "A2 is therefore 7 special characters, not 8." | `run_trial.py:83-87`; `trial_outputs/known_issues/phone_user.md` |
| `Running` | Excluded from the shipped roster. Its 2.5 m/s target broke the spawn geometry — `dist0=4.976` against a target of 8.000 (FAIL) and `robotSpeedAtTrigger=0.000` (FAIL): the pedestrian crossed the trigger radius before the robot had started moving | `run_trial.py:2176-2179` |
| `talking_standing` | *"dropped on request (S44 5.1)"* | `tools/s44_make_index.py:26`, `tools/s45_make_index.py:55` |
| `Stroke_Shaking_Head` | *"permanently excluded"*. The cited rationale file is **missing on disk** — see Appendix C | `tools/s44_make_index.py:27-28`, `tools/s45_make_index.py:56-57` |

### 7.9 Four byte-identical texture pairs with **distinct GUIDs** — do not de-duplicate

Inside the REQUIRED tier's 27 assets, four pairs are byte-for-byte identical:

| sha256 (prefix) | Pair (both under `Assets/Resources/Prefabs/Community-informed Model/Dog Walker/`) | Each |
|---|---|---|
| `4e54663f…` | `Ch22_1001_Normal.png` / `Ch22_1001_Normal 1.png` | 22,809,147 B |
| `4a332818…` | `Ch22_1001_Diffuse.png` / `Ch22_1001_Diffuse 1.png` | 18,361,253 B |
| `e2bc7762…` | `Ch22_1002_Diffuse.png` / `Ch22_1002_Diffuse 1.png` | 5,601,219 B |
| `33a865b9…` | `Ch22_1002_Normal.png` / `Ch22_1002_Normal 1.png` | 3,425,163 B |

That is **50,196,782 B (47.87 MB) of exact duplication**. Both copies of each pair carry
**different GUIDs in their `.meta`** and both are reachable through different materials, so
**both must ship**. Deleting either half breaks a binding.

**Consequence for verification:** a content-hash completeness check will report
**23 unique / 27 REQUIRED assets**. That is expected, not a gap. Verify by
*path + sha256 pair*, as `zoneB_asset_backup_manifest.txt` does, not by unique-hash count.

### 7.9b The 20 EXTRA texture files — GUID-unreferenced, ship them anyway

Ten textures (plus their `.meta`) sit in the scooter and wheelchair character folders and
are **not referenced by GUID from any asset in `Assets/`** — verified by grepping each
GUID across the whole tree:

`Scooter User/map_ScooterG1_BaseColor.png`, `Scooter User/Materials/casual_man_color.png`,
`casual_man_normal.png`, `wheelchair-male/Ch31_1001_Diffuse.png`, `Ch31_1001_Normal.png`,
`Ch31_1002_Diffuse.png`, `wheelchairuser-female/Ch21_1001_Diffuse.png`,
`Ch21_1001_Normal.png`, `Ch21_1002_Diffuse.png`, `Ch21_1002_Normal.png`. Total 95.68 MB.

**GUID absence is not proof of disuse.** Unity's FBX importer can resolve embedded-material
textures **by filename** in the model's own folder, a path that leaves no GUID reference
behind. These files sit exactly where such a lookup would find them, and the pre-existing
2026-07-28 backup included them. They are therefore shipped in the **EXTRA** tier rather
than pruned. Do not drop them on the strength of a GUID search alone — that question is only
settled by a real Unity reimport with the files absent.

### 7.10 `AUTOTRIAL_S68_CROUCH` is default-OFF

`S68CuriousCrouch.cs:180`:

> ```csharp
> get { return !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("AUTOTRIAL_S68_CROUCH")); }
> ```

With the variable unset, Curious behaves as it did before S68 and **no crouch occurs**. The
reason is reproducibility, `S68CuriousCrouch.cs:29`: *"OFF unless AUTOTRIAL_S68_CROUCH is
set. dataset_planD has already shipped with the old…"* — see also
`AutoTrialBootstrap.cs:1182-1183`: *"Env-gated: dataset_planD shipped with the old Curious
and a re-run of it must produce the same thing."* A crouch trial that shows no crouch is
almost always a missing env var, not a broken controller.

### 7.11 S35 straight-line correction is disabled for the crouching Curious agent only

`AutoTrialBootstrap.cs:1205-1210` clears `hasLine` on the crouching agent:

> ```csharp
> var hg = navAgent.gameObject.GetComponent<S35HeadingAlignmentGuardian>();
> if (hg != null && hg.hasLine)
> {
>     hg.hasLine = false;
> ```

Scope is deliberately narrow — `AutoTrialBootstrap.cs:1202-1203`: *"Only hasLine is cleared.
The facing-alignment mechanism, which is what the guardian was actually added for (Session 35
FIX 1/2), is left running."* The guardian is still attached, still aligns facing, and every
other agent keeps its line correction. Without this, the line correction would pull the
sidestep back onto the spawn→goal line.

### 7.12 `CLOSE_ENOUGH_MIN_DIST = 1.0` silently swallows short destinations

`Assets/Scripts/Agents/Parameters.cs:34`:

> ```csharp
> public const float CLOSE_ENOUGH_MIN_DIST = 1.0f;
> ```

Consumed at `Assets/IVI/Scripts/Navigation/INavigable.cs:134`
(`bool closeEnough = closeness <= Parameters.CLOSE_ENOUGH_MIN_DIST;`). **Any destination
closer than 1.0 m is treated as already reached** — the agent never moves and nothing is
logged. `S68CuriousCrouch.cs:708` works around it explicitly: *"Push the target out until it
is comfortably beyond CLOSE_ENOUGH_MIN_DIST (1.0 m)."* If you author a short sidestep or
nudge destination, push it past 1.0 m or it will be a no-op.

### 7.13 `ZoneBRetargetCalibration` has exactly **one** live entry

`AutoTrialBootstrap.cs:57-66`. The only active entry is `{ "dog_walker", 1.003f }` (`:65`).
`white_cane_user`'s `0.150` is **reverted and present only as commentary** at `:60-64`:

> ```
> // Session 60: white_cane_user's 0.150 is REVERTED pending diagnosis. With it, animator
> // .speed hit its target exactly (0.327) but realised travel came out 0.0059 m/s against
> // a target of 0.049 -- 8x low -- and dist0 fell to 3.716. dog_walker, calibrated the
> // same way on the same nested-Animator path, verified end to end (0.6434 vs 0.657), so
> // the mechanism is not the calibration as such. Reverting isolates it.
> ```

Applied at `AutoTrialBootstrap.cs:792-804`, which must run **after** the `AddComponent` — an
earlier version sat before it and a second scaler carrying the defaults won (`:789-791`).

### 7.14 The Zone-B binaries are untracked but **NOT gitignored**

`git check-ignore` returns "not ignored" for `Assets/Resources/Prefabs/Community-informed Model`,
`Assets/CustomAnimations`, `Assets/PedestrianAssets`, and `Assets/Resources/Prefabs/Rocketbox`.
They are outside git only because nobody has added them. **A single `git add -A` would commit
615 MB of binaries to the branch.** Stage explicitly, by path, always.

---

## 8. Appendix

### Appendix A — stale references found

| Reference | Where | Status |
|---|---|---|
| `AutoTrialBootstrap.cs:44-46` — *"The wheelchair-male package has no .prefab at all (raw FBX/controller only) and is out of scope for v1."* | `AutoTrialBootstrap.cs:44-46` | **STALE / CONTRADICTED BY DISK.** The canonical `Wheelchair_Female.prefab` contains a `PrefabInstance` property override at lines 276-280 — `propertyPath: m_Controller`, `objectReference: {fileID: 9100000, guid: b014d50f52ca713429fdbc23927fd1d0, type: 2}` — and that GUID resolves to `Assets/Resources/Prefabs/Rocketbox/wheelchair-male/Wheelchair.controller`. The override targets an object inside the nested prefab `Wheelchair (1).prefab`. **The `wheelchair-male/` folder is a hard runtime dependency of the female wheelchair**, not out of scope: `Wheelchair.controller`, `Wheelchair.fbx`, `New Material.mat`, `model.dae` and `wheelchair_animation.fbx` are all in the required closure. Deleting that folder breaks `wheelchair_user`. |
| *"the 0.6 that a stale comment in AutoTrialBootstrap claimed until Session 54"* | `run_trial.py:232-233` | Already corrected upstream; recorded here so the 0.6 figure is not reintroduced. `Parameters.MAX_VEL = 0.95f` is the real cap — confirmed at `Assets/Scripts/Agents/Parameters.cs:32`. |
| *"AutoTrialBootstrap.cs:798 gates modulator attachment"* | `run_trial.py:229` | **STALE LINE NUMBER.** The gate exists but is at `AutoTrialBootstrap.cs:872` (Zone B) and `:1011` (Zone A) at `0fa73a1`; line 798 is now inside the `ZoneBRetargetCalibration` block. Mechanism and warning unaffected — see §7.1. |
| `sim_ws` checked-out copy | `run_trial.py:862` calls it *"this repo's own checked-out, stale sim_ws copy"* | Stale by the harness's own account; live `rosparam get` is authoritative for ROS params. |
| MetaUrban-era references | searched | **UNVERIFIED — not found on disk.** No MetaUrban reference was located in `Assets/Scripts/AutoTrial/`, `tools/`, or the roster data files at `0fa73a1`. |

### Appendix B — ERRATA against the prior handoff

Each row is sourced to evidence gathered in this session.

| # | Prior claim | Verdict | Evidence |
|---|---|---|---|
| 1 | HEAD is `5844e5c` | ❌ **Never existed in this clone** | `git reflog --all \| grep -i 5844` → no match (319 entries). `git fsck --lost-found` → 8 dangling objects (`2d4abc9`, `849b057`, `29bd606`, `61e471f` + 3 trees + 1 blob), none matching. `git cat-file --batch-all-objects` → **0 of 17,022 objects** have prefix `5844`. `.git/FETCH_HEAD`, `ORIG_HEAD`, `packed-refs`, `logs/` → no match. Sole near-miss is the *tree* `53dee483db03503f808f23a11cccca5cf5844ab6`, a coincidental mid-string substring. **Actual HEAD: `0fa73a1b9684cede5263af6d2193489150ed88cc`.** |
| 2 | Zone-B is a "74-file set" | ✅ **CORRECT — it names the backup, not the working tree** | 74 is the exact file count of `/mnt/ssd/Social_Navigation/asset_backup/zoneB_assets_424MB.tar.gz`, confirmed by `tar tzf`. It is **not** the count of untracked files in the working tree, which is **47 asset binaries (615.28 MB)** or **100 with `.meta` (615.58 MB)** — the difference is `Assets/CustomAnimations/MixamoCharacters/` and unreferenced duplicate textures, which the backup correctly omits. Both numbers are right about different sets; only conflating them is wrong. |
| 3 | "424 MB backup complete" | ✅ **CONFIRMED COMPLETE** | The archive exists: `/mnt/ssd/Social_Navigation/asset_backup/zoneB_assets_424MB.tar.gz`, 444,135,434 B (423.56 MB), mtime 2026-07-28. Its sha256 `2b39f959…dedea5e` matches its own sidecar. Streamed and hashed per file against the working tree: **74/74 byte-identical, 0 different, 0 absent.** Its listing **fully covers the 54-file required closure with zero gaps**, plus 20 extra texture files. Completeness is now verified, not assumed. This ticket's backup ships the **same 74-file set** with the per-file sha256 manifest the original lacked. |
| 4 | `dataset-planD-2026-07-30.tar.gz` is sha256 `c983a50e…955809`, 16,128,141,279 bytes | ✅ **MATCH — both** | `stat -c%s` → `16128141279`, exactly as claimed. `sha256sum` recomputed over the full 16.1 GB → `c983a50e8fa19918332bd3143fbf3e26d1ff7d25d8451fecebac26b7cd955809`, exactly as claimed. Not repackaged. **This is the one prior-handoff claim that verified clean.** |

### Appendix C — the `S44_stroke_shaking_head_excluded.md` dangling reference is **source-only**

Two generator scripts cite a rationale file that does not exist:

- `tools/s44_make_index.py:27-28` — `("Stroke_Shaking_Head", "permanently excluded -- see known_issues/S44_stroke_shaking_head_excluded.md")`
- `tools/s45_make_index.py:56-57` — identical string

`trial_outputs/known_issues/` contains only `phone_user.md` and
`scooter_user_robot_stall.md`. **`S44_stroke_shaking_head_excluded.md` is
`UNVERIFIED — not found on disk`,** so the stated reason for excluding
`Stroke_Shaking_Head` cannot be confirmed from this tree.

**The shipped artifacts are clean.** Verified against the packaged outputs, not the
generator source. `dataset-planD-2026-07-30.tar.gz` contains exactly three top-level
markdown artifacts — `dataset_planD/INDEX.md`, `DATASHEET.md`, `CHECKS.md` — each
**byte-identical (sha256)** to its extracted on-disk copy.

```
$ grep -rn "S44_stroke_shaking_head_excluded" dataset_planD/
(zero matches — extracted dir and inside the tarball)

$ grep -rhno "known_issues/[A-Za-z0-9_.-]*" dataset_planD/ | sort | uniq -c
      1 known_issues/phone_user.md
      1 known_issues/scooter_user_robot_stall.md
```

Both surviving references, verbatim, both in `DATASHEET.md`, both resolving to files that
exist:

- `DATASHEET.md:76` — ``see `known_issues/scooter_user_robot_stall.md`. The stall falls outside the near-clip window, so the``
- `DATASHEET.md:82` — ``**7. `phone_user` is not in the roster.** Excluded; see `known_issues/phone_user.md`.``

`INDEX.md` and `CHECKS.md` do not mention `known_issues` or `stroke` at all. **The dangling
reference never reached a shipped artifact**; it is confined to generator source and is
therefore a source-hygiene issue, not a dataset defect.

---

## 9. Items marked UNVERIFIED in this document

For a complete audit trail, every `UNVERIFIED` marker in this document:

1. **MetaUrban-era references** (Appendix A) — `UNVERIFIED — not found on disk`. Searched
   `Assets/Scripts/AutoTrial/`, `tools/`, and the roster data files at `0fa73a1`; no
   MetaUrban reference located. The ticket anticipated such references; none were found.
2. **`known_issues/S44_stroke_shaking_head_excluded.md`** (Appendix C, §7.8) —
   `UNVERIFIED — not found on disk`. Referenced by two generator scripts; absent from
   `trial_outputs/known_issues/`. The *reason* `Stroke_Shaking_Head` is permanently excluded
   therefore cannot be confirmed from this tree.

No other claim in this document is unverified. Everything else was read from disk at
`0fa73a1` and is cited above.
