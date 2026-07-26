# Mixamo behaviour clips (Session 41)

Nine Mixamo exports, kept in this directory deliberately: it sits outside
`Assets/ExternalAssets/Microsoft-Rocketbox/`, so `FixRocketboxMaxImport`'s
`OnPostprocessModel` cannot reach them. (That postprocessor already carries a sticky
`if (importer.animationType != ModelImporterAnimationType.Human)` guard, so it would not
have forced these to Generic anyway — the isolation is belt-and-braces, per the ticket's
own preference for the safer of its two options.)

## These are animations, not characters

All nine FBXs are Mixamo **animation-only** exports: 0 meshes, 0 materials, 0 renderers,
a bare 66-transform `mixamorig4:` skeleton (verified by `S41MixamoContentProbe`). They
cannot be spawned as pedestrians. The character on screen is always an ordinary Rocketbox
avatar; Unity's Humanoid retargeting supplies the motion. This is the same mechanism
Session 31 used for `point_backwards.fbx`.

`S41MixamoControllerGen` generates one single-state looping controller per clip into
`Resources/`, and `--mixamo-clip <Name>` forces it onto the spawned pedestrian at runtime.
Spaces in the source filename become underscores:

| Clip | Controller name | Class |
|---|---|---|
| Pacing And Talking On A Phone | `Pacing_And_Talking_On_A_Phone` | moving |
| carry_and_walk | `carry_and_walk` | moving |
| Old Man Walk | `Old_Man_Walk` | moving |
| Drunk Walk | `Drunk_Walk` | moving |
| Running | `Running` | moving |
| Standing Arguing | `Standing_Arguing` | stationary |
| Talking_standing | `Talking_standing` | stationary |
| Sitting | `Sitting` | stationary |
| Stroke Shaking Head | `Stroke_Shaking_Head` | stationary |

Moving clips keep root motion so they translate. Stationary clips have root position,
height and rotation locked at import, or they drift off their spawn point over a 90s
trial.

## The carried box is invisible to the robot — on purpose

`--carried-box` attaches a 0.45 × 0.35 × 0.35 m matte cardboard-brown cube, no collider.
Measured attachment height: **1.11 m**. The robot's RealSense sits at **0.32 m**.

**The robot physically cannot see the box.** This is not a bug to fix. It is a concrete
instance of a real perception challenge: a pedestrian's *effective occupied volume* is
larger than the volume the sensor can observe. Keep it — it is a natural source of
negative examples.

The box is attached to a body-relative anchor at chest height rather than to the hand
bones. The Rocketbox rigs are imported with **Optimize GameObjects**, which strips every
bone Transform out of the hierarchy, so `GetBoneTransform` and a name search both return
null even though `avatar.isHuman` is true. Exposing the hands would mean editing the
shared Microsoft-Rocketbox submodule's import settings, which is out of scope; the ticket
anticipates this and offers the body-relative anchor as the alternative. `carry_and_walk`
holds the hands nearly static relative to the chest, so the result is visually equivalent.

## Known issues

- **Standing Arguing is a single-person animation**, not two people. Staging a roadside
  argument needs two instances facing each other, each with a random phase offset —
  perfectly synchronised copies read as fake immediately.
- **Sitting** puts the torso low. Whether the 0.32 m laser plane intersects a seated
  figure's collider is genuinely uncertain and appearance-dependent. If the robot turns
  out not to see a seated person, that is a **real perception problem worth keeping as a
  negative case**, not a bug to patch.

## Corridor scenes

`--corridor-width W` (with `--profile corridor`) spawns two parallel walls W metres apart,
12 m long, 1.6 m tall, centred on the live robot/pedestrian midpoint once they close
within one corridor length. Sweep: 3.0 / 2.0 / 1.5 / 1.2.

| Width | Expected robot response | Expected label mix |
|---|---|---|
| 3.0 m | Slight detour, little slowing | all Pos (control) |
| 2.0 m | Slows, clear sidestep | Pos / Neutral |
| 1.5 m | Large slowdown, may squeeze along a wall | Neutral / Neg |
| 1.2 m | Cannot pass safely → stop and wait, or break the line | mostly Neg |

This is **runtime geometry inside the existing `Outdoor.unity`**, not the new
`Assets/Scenes/Corridor/CorridorTest.unity` the ticket named. Navigation in this pipeline
is map-bound: `AutoTrialEditorRunner` opens `Outdoor.unity`, and `move_base` plans against
a pre-built ROS occupancy map served from `social_sim_ros/maps/outdoor/`. A brand-new
Unity scene has no matching ROS map, so the robot would not move at all — and authoring
that map means writing into `sim_ws`, which is read-only here. Spawning the walls into the
already-map-matched corridor gets the property that actually matters (a controlled,
laser-visible passable width) with no shared-scene edit, no `sim_ws` edit, and no new map.

## min_dist is a label, not a gate

`meta.json` now carries `safetyLabel` ∈ {`safe` ≥ 0.5 m, `marginal` ≥ 0.36 m, `breach`
< 0.36 m} alongside `minDistMeters`, for every profile.

Note the ticket's premise here was already false: **`min_dist` was never a gate in
`run_trial.py`.** The permanent gates are content / aspect / approach-geometry /
trigger-speed / overlay / file-manifest, plus the output-root sentinel and the editor-lock
check. `min_dist` was always measured and recorded but never affected the exit code, so
narrow-corridor breaches were never at risk of being rejected. What was genuinely missing
— and is what got added — is the label itself.
