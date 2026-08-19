# KIMODO_UNITY_STEPS.md — S73 Phase 3 handoff (GUI work, Sheng)

Everything below is **GUI work in the Unity Editor**. CC has staged the assets and the
speed entries; it does not create prefabs, scenes, or `.meta` files — Unity generates
those when you import.

**Do not touch** the `Microsoft-Rocketbox` submodule at any point (see README §3.4).

---

## 0. What is already on disk

| Path | State |
|---|---|
| `Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk.fbx` | staged, no `.meta` yet |
| `Assets/PedestrianAssets/Kimodo/kimodo_elderly_shuffle.fbx` | staged, no `.meta` yet |
| `Assets/PedestrianAssets/Kimodo/kimodo_relaxed_walk_24s.fbx` | staged, no `.meta` yet |
| `Assets/PedestrianAssets/Kimodo/README.md` | provenance, flags, ERRATA, bone-trap table |
| `Assets/PedestrianAssets/Mixamo/clip_speeds.json` | 3 `kimodo_*` keys added (additions only) |

Nothing is committed. Rig: **78 bones**, 30 fps, 240 / 240 / 720 frames.

---

## 1. Import settings, per FBX

Select the FBX → Inspector.

**Model tab**
- **Scale Factor: 1** — *but see §2, do not trust this field.*
- Leave `Convert Units` at its default.

**Rig tab**
- **Animation Type: Humanoid**
- **Avatar Definition: Create From This Model**
- Click **Apply**, then **Configure…**

---

## 2. ⚠ VERIFY THE SCALE — do not trust the field

The exported FBX **re-imports into Blender in centimetres** (mean hip height 98.0 cm,
i.e. ×100). Whether Unity reads these files the same way is **unverified**, which is why
S72 `UNITY_STEPS.md` §3's flat "Scale 1" is now treated as suspect.

**Check:** drag the imported model into a scene next to any Rocketbox avatar, or read
its bounds. The character must stand **≈1.7–1.8 m**. Hips should sit at ≈0.98 m.

- If it is ~100× too large or too small, fix it with **Scale Factor**, re-Apply, and
  **write the value you used into README §3.1** — it changes the Phase 4 speed check.
- If it is already ≈1.75 m at Scale 1, note that in README §3.1 too. Either way the
  question stops being open.

---

## 3. ⚠ THE BONE TRAP — check four slots first, before anything else

In the Avatar Configuration window, SOMA's naming will mislead Unity's name-based
auto-mapper. **Check these four slots before looking at anything else:**

| Unity slot | MUST be | Unity will likely have guessed |
|---|---|---|
| LeftUpperLeg | `LeftLeg` | *(empty)* |
| RightUpperLeg | `RightLeg` | *(empty)* |
| LeftLowerLeg | `LeftShin` | `LeftLeg` ❌ |
| RightLowerLeg | `RightShin` | `RightLeg` ❌ |

SOMA names the **thigh** `LeftLeg` and the **calf** `LeftShin`. The chain from Hips is
`LeftLeg → LeftShin → LeftFoot → LeftToeBase`, so `LeftLeg` is the thigh. A wrong map
gives either an invalid avatar or a silently inverted leg chain.

Full required/optional bone tables: **README §5** (verbatim from S72 `UNITY_STEPS.md` §4).

Then **Apply** in the Avatar window.

---

## 4. After Apply — re-check the Rig tab

`FixRocketboxMaxImport` is a **project-wide** `AssetPostprocessor` with no path filter,
and it forces `animationType = Generic` on reimport.

These clips escape it **only because the SOMA rig roots are `Root`/`Hips`, not `Bip01`**
— the postprocessor early-returns at `FixRocketboxMaxImport.cs:44-45` when no `Bip01`
child exists, so it never reaches the forcing at `:69-70`.

**Re-open the Rig tab after Apply and confirm Animation Type is still `Humanoid`.** If
it has reverted to Generic, stop and report — it means something is matching the
`Bip01` path and the escape assumption is wrong.

> **NEVER name a Kimodo rig node `Bip01`.** The escape above is naming luck, not scope.
> Any node named `Bip01` would be silently forced to Generic and break retargeting.

---

## 5. Expected on import — not bugs

- **Materials come in white.** `FixRocketboxMaxImport.cs:6-16` sets
  `material.color = Color.white` on *every* imported material project-wide. Expected.
- **A `_Mode` shader error may log.** The same method calls
  `material.GetFloat("_Mode")`, which errors on shaders lacking that property.
  **Cosmetic — ignore it.**

Neither affects motion, retargeting, or measured speed.

---

## 6. Animation tab — Loop Time / Loop Pose

Kimodo has **no loopable-output mode**; nothing in the generation chain closed the loop.
The internal chunk seams of the 24 s clip are transition-blended, but the **Unity Loop
wrap (last frame → first frame) is not**, and 60 s trials live on that wrap.

Measured wrap discontinuity (max per-joint angular difference, first vs last frame):

| Clip | Max Δ | Worst joint |
|---|---|---|
| `kimodo_relaxed_walk_24s` | **55.98°** | RightShin |
| `kimodo_relaxed_walk` | **29.24°** | RightLeg |
| `kimodo_elderly_shuffle` | **23.58°** | RightShin |

Hips height delta across the wrap is ≤1.5 cm for all three — a limb snap, not a jump.

**Set `Loop Time` ON for the walking clips** (all three are walking clips).

`Loop Pose` is a judgement call, not a prescription: it makes Unity match the wrap by
adjusting the pose, which will mask the snap at the cost of warping the final frames —
and with a 56° shin delta on the 24 s clip that warp will not be subtle. Try it both
ways on the eyeball pass. The numbers above **pre-register the expectation: the 24 s
variant is the worst offender and the elderly shuffle the mildest.** Record which
setting you kept.

---

## 7. Create one single-state controller per clip

For each of the three clips, in the GUI:

1. Create an Animator Controller.
2. Save it to **`Assets/PedestrianAssets/Kimodo/Resources/`** — create that folder if it
   does not exist. It **must** be under a folder literally named `Resources`.
3. Name it **exactly** the canonical clip name (§8).
4. Open it and add **one** state, with the clip's AnimationClip as its Motion. No
   parameters, no transitions — a single default state.

This mirrors `Assets/PedestrianAssets/Mixamo/Resources/*.controller` and
`Assets/PedestrianAssets/S68Crouch/Resources/S68_CuriousCrouch.controller`.

> **NEVER put a file named `clip_speeds` in that Resources folder** — or any other.
> `S41MixamoClipApplier.cs:120` resolves `clip_speeds` by **global name**, and a second
> one would hijack the lookup for every Mixamo clip. See README §3.5.

---

## 8. Names — one string does two jobs

`--mixamo-clip NAME` resolves as:

```
run_trial.py:1962  --mixamo-clip NAME
run_trial.py:1109  -> config "mixamoClip"
AutoTrialConfig.cs:157        public string mixamoClip
AutoTrialBootstrap.cs:940     mixamoApplier.clipControllerName = config.mixamoClip
S41MixamoClipApplier.cs:60    Resources.Load<RuntimeAnimatorController>(clipControllerName)
S41MixamoClipApplier.cs:132   TryLookup(clip_speeds json, clipControllerName)
```

So the **same string** is both the Resources controller asset name and the
`clip_speeds.json` `"clip"` key. They must match exactly — case is preserved, and
**spaces are the one thing that must not appear** (`S41MixamoControllerGen.cs:93`
sanitises spaces to underscores).

| Canonical name | Controller asset | `clip_speeds` key | authoredSpeedMps |
|---|---|---|---|
| `kimodo_relaxed_walk` | `Kimodo/Resources/kimodo_relaxed_walk.controller` | present | 0.9715 |
| `kimodo_elderly_shuffle` | `Kimodo/Resources/kimodo_elderly_shuffle.controller` | present | 0.4786 |
| `kimodo_relaxed_walk_24s` | `Kimodo/Resources/kimodo_relaxed_walk_24s.controller` | present | 1.0182 |

All three keys are already in `clip_speeds.json`. Get the controller names wrong and
`S41MixamoClipApplier` logs `Resources.Load failed for controller '<name>'` and leaves
the original controller in place — the trial still runs, silently, with the wrong motion.

---

## 9. Checklist before handing back for Phase 4

- [ ] All three FBX import as **Humanoid**, avatar valid.
- [ ] The four leg slots map to `LeftLeg`/`RightLeg` (upper) and `LeftShin`/`RightShin` (lower).
- [ ] Rig tab still says **Humanoid** *after* Apply (§4).
- [ ] Character height verified **≈1.7–1.8 m**; the Scale Factor used is written into README §3.1.
- [ ] `Loop Time` ON for all three; `Loop Pose` decision recorded.
- [ ] Three controllers in `Kimodo/Resources/`, named exactly per §8, one state each.
- [ ] No file named `clip_speeds` anywhere under any `Resources` folder.

Phase 4 then runs `run_trial` with `--mixamo-clip kimodo_elderly_shuffle` (headon,
standard config) plus a **regression arm** on one existing Mixamo clip, to confirm the
`clip_speeds.json` edit did not move the frozen pipeline.
