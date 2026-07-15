# Character Import & Animation Gap Analysis

Date: 2026-07-05
Scope: 8 unitypackages in `~/Downloads/`, existing "Community-informed Model" characters,
`FixRocketboxMaxImport.cs` NRE, animation-system gap analysis for personality behaviors.

Branch note: working tree is currently on **`sheng-backup`**, not `sheng-snapshot` as stated in
the task brief. No git operations were performed either way (per instructions) — flagging the
name mismatch in case it matters for which branch you push.

---

## 0. Baseline verification (no changes made here)

- `Editor.log` (`~/.config/unity3d/Editor.log`) has **0 occurrences of `error CS`**, and the log's
  domain-reload timestamps postdate `AvatarAnimatorUtility.cs`'s creation. Compilation is clean.
- The only errors at the tail of Editor.log are **runtime/Play-mode** exceptions (missing script
  on `Female_Adult_01`, `RGBCameraPublisher.Start()` reflection error, `TrackedTrajectory`
  NRE in `Publisher.Update()`) — these are unrelated to compilation and look like they belong to
  the other agent's ROS/sensor work in progress. Not touched.
- `AttachPropToHand.cs`, `PedestrianModulator.cs`, `BaseSFControllerNormalized.controller`,
  `Unitree A1.prefab` — left untouched, exactly as described in the task brief.

---

## 1. `FixRocketboxMaxImport.cs` — fix applied

**File:** `Assets/ExternalAssets/Microsoft-Rocketbox/Assets/Editor/FixRocketboxMaxImport.cs`
(inside the `Microsoft-Rocketbox` git submodule — this change makes the submodule dirty; that is
expected and no git operations were run inside it.)

### Applied (autonomous, per your instruction)

```diff
         if (g.transform.Find("Bip02") != null) RenameBip(g);

-        Transform pelvis = g.transform.Find("Bip01").Find("Bip01 Pelvis");
+        Transform bip01 = g.transform.Find("Bip01");
+        if (bip01 == null) return;
+        Transform pelvis = bip01.Find("Bip01 Pelvis");
         if (pelvis == null) return;
```

This is an `AssetPostprocessor.OnPostprocessModel`, which Unity runs for **every** FBX import in
the whole project (no path filter), so it was previously one NRE away from aborting import
post-processing for any non-Bip01 skeleton. Confirmed via inspection of every FBX bone-naming
scheme in the 8 packages (§3) — Mixamo (`mixamorig:`/`mixamorig2:`), Reallusion Character Creator
(`CC_Base_*`, `clavicle_l`, `calf_l`), a Rigify quadruped rig (`DEF_*`), and a fully custom rig
(`Hips/Chest/Shoulder_L/Lower_Arm_L`) all now safely early-return instead of NRE'ing.

### Not touched — needs your decision: `importer.animationType = Generic;` (line ~65)

Found by direct inspection (not guessing): **every already-imported Rocketbox adult is
Humanoid (`animationType: 3`)** — 111/111 adult `.fbx.meta` in
`Assets/ExternalAssets/Microsoft-Rocketbox/Assets/Avatars/Adults/**/Export/*.fbx.meta` say
`animationType: 3`. Only the 4 **Rocketbox children** exports are Generic (`animationType: 2`),
and those 4 have **no prefab at all** anywhere in the project (orphaned, unused).

So the line's own comment ("If you need a humanoid avatar, change it here") is stale — the
project's actual convention for Bip01-named characters is Humanoid, not Generic. Confirmed the
two characters you flagged are currently Humanoid with full bone maps already baked into their
`.fbx.meta` (`Phone User/Female_Adult_05 1.fbx.meta` and `White Cane User/Male_Adult_12.fbx.meta`
both say `animationType: 3` with a populated `humanDescription.human` bone list). If this
postprocessor runs on a reimport of either file, it will silently downgrade them to Generic and
you'd lose the baked Avatar — a real regression, not hypothetical.

Options (pick one, I'll apply it):

| # | Change | Effect |
|---|--------|--------|
| A | **Delete the line entirely.** | Matches actual project convention (111/111 non-orphaned Bip01 characters are already Humanoid). Simplest. Newly-imported Bip01 characters default to whatever Unity's importer heuristic picks (usually Generic) and you set Humanoid manually in the Configure step anyway (Part C below already has you doing this by hand). |
| B | **Guard by path**, e.g. skip if `assetPath` is under `Community-informed Model/` or matches `Female_Adult_05`/`Male_Adult_12`. | Preserves old Generic-forcing behavior for "plain" Rocketbox mob characters, exempts the two prop-carrying ones. More surgical, but hardcodes filenames into a shared submodule file — brittle if more Bip01 prop characters get added later. |
| C | **Only force Generic if the asset doesn't already have a saved Humanoid Avatar** (`importer.animationType != ModelImporterAnimationType.Human`). | Closest to a no-op for already-configured assets; still forces Generic on first-time imports of new Bip01 mob characters, same as before. |

My read: **(A)** is the honest fix — the line is dead-convention already contradicted by 111 other
assets, and it's what's actively breaking Phone User/White Cane User. But it's your call since it
changes default behavior for future plain-mob-character imports too. Say which and I'll make the
one-line edit.

---

## 2. Pre-existing, out-of-scope finding (FYI only, not fixed)

While tracing why Phone User / White Cane User "worked by accident," I found their prefabs (plus
`Male_Child.prefab`, and the packaged `Scooter_User.prefab`) each carry **two MonoBehaviour
components whose script GUIDs don't resolve to any `.cs` file anywhere in the repo**
(`1c19610482eb06543aa9acbc1aa7d22e` and `82986ae7dfa33654295162cffcb1b085`). Field names visible
in the serialized data (`moveSpeed`, `joystickHorizontalAxis`, `stickToGround`, etc.) show it was
a WASD/joystick player-controller script — never committed, same class of problem as the
`AvatarAnimatorUtility` stub you already fixed, just on a script nobody's needed to run yet. This
likely explains the "referenced script ... is missing" warning for `Female_Adult_01` in
Editor.log. **Not fixed** — no prefab edits per your constraints, and it doesn't block compilation
or the animation work below. Flagging so it isn't mistaken for something today's changes caused.

---

## Part A — Package inventory & classification

All 8 packages were extracted read-only to a scratch dir (`tar -xzf`, not imported into Unity) and
inspected offline: pathnames, FBX headers/sizes, and bone names via `strings`/binary parsing.
**No Unity project files were touched by this inspection.**

### A.1 Already imported — nothing to do (4 of 8 packages are backups of existing work)

| Package | Target folder in project | Status |
|---|---|---|
| `phone_user.unitypackage` | `Assets/Resources/Prefabs/Community-informed Model/Phone User/` | **Already fully imported.** Same files, same prefab (`Phone_User.prefab`), byte-identical pathnames. |
| `white_cane_user.unitypackage` | `.../White Cane User/` | **Already fully imported.** Same for `White_Cane_User.prefab`. |
| `female_child.unitypackage` | `.../Female Child/` | **Already fully imported** (`kid2.fbx` + `Female_Child.prefab`). |
| `male_child.unitypackage` | `.../Male Child/` | **Already fully imported** (`KIDS-01.fbx` + `Male_Child.prefab`). |

These 4 Downloads are backup copies of what's already sitting in the project, not new content.
Re-importing them would be idempotent (same GUIDs) unless you specifically want a refreshed
export. Given they're already present, **Part C does not list them as "to import."**

### A.2 Not yet imported — pure pedestrian + prop (recommended to import)

| Package | Character rig | Bone naming | Rig type needed |
|---|---|---|---|
| `dog_walker.unitypackage` | Human: Mixamo `Ch22` (namespace `mixamorig2:`) | Mixamo | Humanoid |
| " | Dog: `cur.fbx`, "3D Stylized Animated Dogs Kit" (chihuahua) | Rigify `DEF_*` (quadruped) | **Generic** — Unity Humanoid Avatar doesn't apply to non-biped rigs |

Dog Walker is genuinely new content and is a pure walking pedestrian + prop (dog on a leash), not
a vehicle — recommended for import (Part C).

### A.3 Not yet imported — vehicle class (flagged, not integrated)

None of these exist in the project yet (verified: no `wheelchair`/`cyclist`/`scooter` asset
anywhere in `Assets/`).

| Package | Rider rig | Vehicle asset | Own controller shipped |
|---|---|---|---|
| `cyclist.unitypackage` | `Sports_Female_02.fbx` — **Bip01**, full face bones | `Sepeda Facific Invert.fbx` (static bike mesh) | `Bike Controller.controller` + `CyclistController.controller` |
| `scooter_user.unitypackage` | `casual_male.fbx` — custom rig (`Hips/Chest/Shoulder_L/Lower_Arm_L/Foot_L`, ASCII FBX, no shared controller in package) | `default.fbx` (scooter mesh) | none bundled (prefab reuses a WASD/joystick controller pattern, see below) |
| `wheelchair_users.unitypackage` | 2 prefabs (`wheelchair-male`, `wheelchairuser-female`), mixed rig (`mixamorig9:` in the animation FBX, jumbled/obfuscated names in the static rig — likely retargeted) | `Wheelchair.fbx` / `model.dae` (Collada) | `Wheelchair.controller` / `wheelchairuser-women.controller` |

**Why "needs Base.cs architecture changes, not connecting now" is correct, confirmed by code
inspection, not just the task description:** I checked what animator-driving script these vehicle
prefabs actually use. `Scooter_User.prefab` (inside the package) reuses the **same** two
component GUIDs found on `Male_Child.prefab` — one is a marker script, the other is the
missing WASD/joystick player controller (§2). None of the vehicle characters implement
`IVI.INavigable` or attach to `SEAN.Scenario.Agents.Base` — they're built as manually/
player-driven rigs, not NavMeshAgent-driven social-force pedestrians. Making them real SEAN
pedestrians would need a new `Agents.Base` subclass that drives a bike/scooter/wheelchair's own
lean/steer parameters from `velocity`/`ModulateVelocity()` instead of `Forward/Strafe` — real
architecture work, correctly out of scope here.

### A.4 Missing characters (expected but not in Downloads)

The task brief anticipated `Cane_User`/`Walker_User` variants distinct from `White_Cane_User` —
none found in Downloads. All 8 packages accounted for; nothing unaccounted-for is missing, but if
you were expecting e.g. a plain "walker/rollator user" (elderly mobility aid, not cane, not
wheelchair) that is **not** among these 8 packages and would need to be sourced separately.

### A.5 Prop mechanism check: does `AttachPropToHand` + the `AvatarAnimatorUtility` stub suffice?

Yes, for both existing prop characters and the new Dog Walker, confirmed by reading
`AttachPropToHand.ResolveAnchorTransform()`: it tries `Animator.GetBoneTransform(handBone)`
first (works for Humanoid rigs — Mixamo Dog Walker human qualifies once rigged Humanoid), and
**falls back to a literal child-name search** (`autoTagBoneName`) if that fails or the rig is
Generic. So it degrades gracefully even without a Humanoid avatar. `dog_walker.unitypackage`
ships its own `DynamicLeash.cs` (not currently in the project) for the leash-to-hand connection —
that's new functionality, not something `AttachPropToHand` does; you'll need to import that
script alongside the package. Also note: `dog_walker`, `phone_user`, and `white_cane_user` each
bundle their **own duplicate copy** of `AttachPropToHand.cs` at the same path
(`Assets/IVI/Scripts/AttachPropToHand.cs`) — Unity will just no-op/overwrite-with-same-GUID on
import since the project's copy already exists there; not a conflict, just noting it so it isn't
mistaken for a real diff during import.

---

## Part B — Animation gap analysis (core deliverable)

### B.1 Existing animation system, precisely as configured (no changes)

`Assets/Scripts/SEAN/Scenario/Agents/Base.cs` drives every "real" SEAN pedestrian
(`Move()`, ~line 201): sets `Animator.SetFloat("Forward", ...)`, `SetFloat("Strafe", ...)`,
`SetBool("Idling", ...)`, and `animator.speed = velocity.magnitude`, with
`animator.applyRootMotion = true`. This is the **shared** contract every pedestrian animator
controller must expose.

`Assets/IVI/Controllers/BaseSFControllerNormalized.controller` (the controller actually used by
spawnable pedestrians — confirmed via GUID cross-reference against
`SimpleAppearanceAgent.prefab`, `RocketboxRandomAnimatedAgent.prefab`,
`RocketboxRandomAnimatedPlayer.prefab`, `NewAnimatedAgent.prefab`, `AnimatedAgent2.prefab`,
`AnimatedAgent.prefab`) has:
- Parameters: `Forward` (float), `Strafe` (float), `Idling` (bool).
- One layer ("Base Layer"), 3 states: **`Locomotion`** (nested BlendTrees blending
  `Idle.anim`, `DefaultAvatar@TurnOnSpot.fbx`, `DefaultAvatar@WalkForwardTurnRight_NtrlMedium.fbx`
  and others — generic mocap, not Rocketbox-specific, works via Humanoid retargeting),
  **`Idling`**, and **`SurprisedReaction`** (Speed=1.8, ~4.0s clip, confirmed already fixed per
  the task brief — not touched).
- A few Locomotion blend-tree motion slots reference GUIDs that don't resolve anywhere in the
  repo (pre-existing, likely stale/empty diagonal-blend slots) — flagging as observed, not fixed,
  not blocking anything since blend trees tolerate null motions.

**Critical finding — the 4 already-imported special characters are *not* wired into this system
at all:**

| Prefab | Controller actually assigned | Parameters | Compatible with Base.cs? |
|---|---|---|---|
| `Phone_User.prefab` | `PhoneController.controller` | `Forward, Turn, OnGround, Crouch, Jump, JumpLeg` (Standard-Assets ThirdPersonCharacter scheme) | **No** |
| `White_Cane_User.prefab` | `HoldingController 1.controller` | same Standard-Assets scheme | **No** |
| `Male_Child.prefab` / `Female_Child.prefab` | `PlayerAnimatorController.controller` | (same family, player-control oriented) | **No** |

None of these set `Forward`/`Strafe`/`Idling` — `Base.cs.Move()` would be writing parameters that
don't exist on their controllers, and they have no `PedestrianModulator`/`Agents.Base` component
at all today. **This means: before any new animation clips matter, these 4 characters aren't
actually SEAN pedestrians yet** — they're standalone rigs (driven by the missing WASD/joystick
script from §2), not NavMeshAgent-driven, no personality behavior possible today regardless of
what clips exist. This is a bigger gap than "missing clips" and is worth deciding before
investing in Mixamo downloads for them. (Not something I should silently wire up myself — it
touches the shared controller architecture and prefabs, both off-limits this task — but you
should know the actual state before greenlighting animation purchases for these three.)

**One genuinely good precedent already exists**, worth reusing rather than reinventing: `PhoneController.controller`
already implements a 2-layer setup — `"Arms"` layer (weight 1, masked via
`PhoneMask.mask`, guid `5eac66c115c919d45a80cab05c2f814a`) layered over `"Base Layer"` — i.e.
exactly the "Avatar Mask + extra layer" pattern this analysis recommends below for upper-body
overlays (phone-looking, etc.). It's just built on the wrong base parameter scheme today.

### B.2 Personality → animation need mapping

| Personality | State today | Animation need |
|---|---|---|
| **Indifferent** | Done — just Locomotion/Idling, no modulator attached. | None. |
| **Surprised** | Done (task brief: verified, don't touch) — `SurprisedReaction` state, `TriggerAnimation("Surprised")`, gated by `SurpriseAnimationActive()` state-name check, not a fixed timer. | None — reference implementation for the pattern below. |
| **Scared** | `ModulateScared()` only adjusts velocity (flee vector, capped speed) — animation-wise it's still just faster Locomotion via `animator.speed = velocity.magnitude`. No dedicated clip. | A distinct **fast/panicked walk or light jog** clip would sell "scared" better than sped-up normal walk (which can look like moonwalking/foot-sliding at high playback speed on a walk cycle authored for normal pace). Optional, not blocking. |
| **Curious** | `ModulateCurious()` (Wander/Approach/Follow state machine) only touches velocity/destPos. No animation reaction to entering Approach (e.g., a head-turn/look glance) exists. | A **look-around / head-turn** clip triggered on `justEnteredApproach` would need the two-piece pattern (see B.3) since it's a "plays once then returns to locomotion" reaction, same shape as Surprised. |

### B.3 Design constraint — mandatory for every new reaction animation

Confirmed from reading `PedestrianModulator.cs` itself (not just the task brief): the Surprised
fix works because of **two specific pieces**, both required together:

1. **A dedicated Animator state + trigger** — `SurprisedReaction` state in the controller,
   fired via `Base.TriggerAnimation("Surprised")` → `animator.SetTrigger(...)`.
2. **State-name-based duration gating**, not a constant — `SurpriseAnimationActive()`
   (`PedestrianModulator.cs:192`) checks `animator.GetCurrentAnimatorStateInfo(0).IsName(...)`
   **and** `GetNextAnimatorStateInfo(0).IsName(...)` (the second check covers the ~0.25s entry
   crossfade window where the target state is "next" but not yet "current"). This is what
   `IsRotationSuppressed()` and `OnAnimatorMove()`'s freeze logic both key off — not
   `freezeDuration` (that constant only covers the initial freeze; gating stays active for the
   *actual* clip length via the state check, which is how the historical "1.5s freeze vs 4.0s
   clip" bug was actually fixed).

**Any new reaction animation (Scared flee-trigger, Curious look-around, etc.) must copy this
exact shape**: its own Animator state + trigger, and an `<Name>AnimationActive()`-style check
mirroring `SurpriseAnimationActive()` — never a `freezeDuration`-style constant standing in for
clip length. This is the #1 thing to get right and the #1 way to reintroduce the old bug if
skipped.

### B.4 Upper-body overlay (Phone User "look at phone" style)

Confirmed precedent exists already (B.1): Avatar Mask + extra Animator layer
(`PhoneController.controller`'s `Arms` layer + `PhoneMask.mask`). Recommendation for any future
upper-body-only reaction (looking at phone, waving, adjusting a bag strap): **reuse this exact
pattern** — a masked layer layered on top of `BaseSFControllerNormalized`'s Base Layer, weight
driven by a bool/trigger, not a whole-body clip swap. This **requires editing
`BaseSFControllerNormalized.controller`** (shared file) to add the layer — flagging per your
"shared file, needs your sign-off" instruction; not done.

### B.5 Character-specific animation needs — Mixamo shopping list

All new clips: download as **Humanoid**. Root-motion note per clip below — `Base.cs` drives
position from social-force `velocity` and expects `animator.applyRootMotion = true`
(`Base.cs:64`), consuming the clip's own translation each frame — so **locomotion clips need
"In Place" OFF (i.e., with root motion/translation baked in)** to match the existing Locomotion
blend tree's own clips (which are all root-motion walk cycles). **Reaction clips that don't
travel (look-around, phone-check, surprised-equivalent) should be downloaded "In Place" ON**
(no net translation) since `SurprisedReaction`'s own root motion is explicitly discarded/
overridden by `OnAnimatorMove()`'s freeze logic — a stationary source clip avoids fighting that
logic for any new reaction that follows the same pattern.

| Character / need | Mixamo search keyword | Format | Why |
|---|---|---|---|
| Scared personality — flee walk | `"Panicked" ` or `"Running Scared"` or `"Fast Run"` | Humanoid, **root motion (In Place OFF)** | Feeds the existing Locomotion blend tree at a higher Forward value, avoids foot-sliding vs. just speeding up the normal walk clip. |
| Curious personality — look-around reaction | `"Look Around"` or `"Head Turn"` or `"Curious"` | Humanoid, **In Place ON** | One-shot reaction clip on entering Approach state — needs the two-piece pattern (B.3), not a locomotion blend member. |
| Dog Walker — human side | *(none needed)* | — | Mixamo `Ch22` character already ships `Ch22_nonPBR@Holding Walk.fbx` + `Walk W_ Briefcase.fbx` — dedicated one-arm-occupied walk cycles already exist in the package. |
| Dog Walker — dog side | *(none needed — Mixamo has no quadruped retargeting anyway)* | — | `cur.fbx` (chihuahua) already ships baked takes: `Walking01`, `Walking02`, `Running`, `SittingStart/Cycle`, `EatingStart/Cycle`, `AngryStart/Cycle`, `Breathing` (idle), `WigglingTail`. Sufficient without sourcing anything new — just needs `DynamicLeash.cs` wired to sync the dog's Walking/Sitting/Breathing state to the human's Forward/Idling. |
| Phone User — upper-body "looking down" idle-walk overlay | `"Walking And Texting"` or `"Texting While Walking"` | Humanoid, **root motion (In Place OFF)** for a full-body version, **or In Place ON** if targeting the Arms-mask-layer approach (B.4) | Package already ships `Walking While Texting 2.fbx` as a full-body clip; only relevant if you want an *upper-body-only* version layered over normal walk instead (B.4's masked-layer approach), in which case a fresh Mixamo "texting" clip trimmed to arms-only via the mask is cleaner than authoring a full-body blend. |
| White Cane User — cane-sweeping walk | `"Blind Walk"` or `"Walking With Cane"` | Humanoid, **root motion (In Place OFF)** | Not present in the current package (`White Cane User` only ships static holding via `HoldingController`, no dedicated cane-sweep gait) — genuine gap if the sweeping-cane gait matters for the study; current setup is "holding a cane while walking normally," not "using a cane to navigate." |
| Child gait (Male/Female Child) | `"Child Walk"` or `"Kid Walk Cycle"` | Humanoid, **root motion (In Place OFF)** | Neither `kid2.fbx` (female, CC rig) nor `KIDS-01.fbx` (male) ship a child-specific walk in their packages — they currently rely on generic adult `HumanoidWalk.fbx`/Standard-Assets clips, which will look proportionally off on a child-sized rig (faster stride cadence expected for kids). Worth sourcing if child gait realism matters to the study. |

---

## Part C — Your Unity GUI checklist

### C.1 What to import

- **Import:** `dog_walker.unitypackage` (new pedestrian+prop content, not yet in project).
- **Skip / don't import (already present):** `phone_user`, `white_cane_user`, `female_child`,
  `male_child` — these 4 are backup copies of what's already imported; re-importing is harmless
  (same GUIDs) but adds nothing.
- **Skip for now (vehicle class, needs `Base.cs` architecture work first):** `cyclist`,
  `scooter_user`, `wheelchair_users`. Import only if you want to eyeball the assets in the
  Project window; don't wire them into any scene/spawner yet.

### C.2 Rig setup steps — `dog_walker.unitypackage`

After import, two separate FBX need Humanoid setup (do both — the dog needs Generic, not
Humanoid):

**Human character (`Ch22_nonPBR@Holding Walk.fbx`, Mixamo `mixamorig2:` namespace):**
1. Select the FBX → Inspector → **Rig** tab.
2. **Animation Type: Humanoid.**
3. **Avatar Definition: Create From This Model** (not "Copy From Other Avatar" — this is a
   distinct Mixamo character, not a Rocketbox one; no existing Avatar to copy from).
4. Click **Apply**, then **Configure...** to open the Avatar mapping window.
5. Checkpoint: confirm no red/missing bones in the mapping diagram (Mixamo rigs usually
   auto-map cleanly since `mixamorig:` is Unity's own recognized naming convention). Green
   humanoid icon in the muscle preview = success.
6. Same steps for `Walk W_ Briefcase.fbx` (namespace-less `mixamorig:`, the alternate walk clip)
   — **Avatar Definition: Copy From Other Avatar**, pointing at the Ch22 character's Avatar,
   *if* Mixamo exported both from the same source skeleton (check the muscle preview still maps
   cleanly; if it doesn't, fall back to Create From This Model for this one too and treat it as
   its own clip source only, not a shared Avatar).

**Dog (`cur.fbx`, Rigify `DEF_*` bones):**
1. Select the FBX → **Rig** tab.
2. **Animation Type: Generic** (this is a quadruped — Humanoid does not apply; do not attempt to
   force Humanoid on this one).
3. **Avatar Definition: Create From This Model.**
4. No Configure step needed for Generic — just Apply.
5. Checkpoint: expand the FBX in the Project window, confirm the 11 baked animation clips
   (`chihuahua_Walking01/02`, `chihuahua_Running`, `chihuahua_SittingStart/Cycle`,
   `chihuahua_EatingStart/Cycle`, `chihuahua_AngryStart/Cycle`, `chihuahua_Breathing`,
   `chihuahua_WigglingTail`) all appear as sub-assets with valid preview thumbnails (not
   "broken"/red icons).

**After rigging both:** drag `Dog_Walker.prefab` into a test scene, hit Play, confirm the human
walks (root motion moving it forward) and the dog doesn't visibly slide/detach from the leash
anchor — this exercises `DynamicLeash.cs`, which I have not been able to test since it requires
Play mode.

### C.3 Rig setup reminders for any future Rocketbox-family import (Bip01)

Once you've decided on the `animationType = Generic` line (§1):
1. **Animation Type: Humanoid.**
2. **Avatar Definition: Create From This Model** for the *first* character of a new body type;
   **Copy From Other Avatar** (pointing at an existing same-skeleton Rocketbox Avatar, e.g. any
   `Male_Adult_XX`) for subsequent same-skeleton characters — this is the existing project
   convention (all 111 adults share the Bip01 topology).
3. Configure → checkpoint: no red bones, especially check **LeftHand/RightHand** and
   **Neck/Head** map correctly, since `AttachPropToHand.cs` (§A.5) resolves props through
   exactly those bones via `Animator.GetBoneTransform`.

### C.4 Mixamo download settings (for the Part B.5 clip list)

For every clip in the B.5 table marked **root motion (In Place OFF)**: on the Mixamo download
dialog, **uncheck "In Place"** and use **Format: FBX for Unity (.fbx)**, **Skin: Without Skin**
(you're retargeting onto an existing rig, not bringing a new mesh). For clips marked **In Place
ON**: check "In Place" so the clip has no net translation — required for one-shot reaction clips
per B.3/B.5's reasoning.

---

## Summary of changes made this session

1. `Assets/ExternalAssets/Microsoft-Rocketbox/Assets/Editor/FixRocketboxMaxImport.cs` — added
   null-guard on the `Bip01`/`Bip01 Pelvis` `Find()` chain (§1, applied autonomously as
   instructed). **This dirties the `Microsoft-Rocketbox` submodule** — expected, no git
   operations were run in it or anywhere else.
2. Nothing else in the working tree was modified. No prefabs, no scenes, no controllers, no
   `PedestrianModulator.cs`/`Base.cs`, no git operations (no commit/push/checkout), `~/sim_ws`
   untouched.

## Waiting on you

1. **Pick an option for the `animationType = Generic` line** (§1: A/B/C) — I'll make the
   one-line edit once you decide.
2. **GUI steps in Part C** — importing `dog_walker.unitypackage`, setting rig types, Configure
   checkpoints, Play-mode test of the leash.
3. Everything in this session (the submodule fix + your new unitypackage work once you import) is
   an **unbacked increment on top of `sheng-backup`** — worth pushing to your remote backup
   (`sheng-snapshot`, or whichever is the actual intended remote name — see the branch-name note
   at the top) once you're happy with where things land.
