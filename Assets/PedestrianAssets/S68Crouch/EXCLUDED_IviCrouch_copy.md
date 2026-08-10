# `IviCrouch_copy.fbx` — EXCLUDED, contains no crouch

Do **not** re-derive this asset's usefulness from its filename. It has already been measured and
rejected once (Session 68-B); this note exists so the next person does not spend the same hour.

## What it is

A byte-identical copy (md5 `773cc67ef4e6e412ef7980f5c9b2a354`) of

    Assets/IVI/Animations/Locomotion Pack/Interacting/Idle2Crouch_Neutral2Crouch2Idle.fbx

Copied, never edited — the IVI original is untouched. It was copied rather than referenced because
it needs in-place root import settings (`lockRootPositionXZ` / `lockRootHeightY` /
`lockRootRotation`), and those live in the FBX's import settings inside the IVI directory, which is
a red-line path. Owning a copy was the only way to set them without modifying IVI.

## Why it was excluded

**The clip contains no crouch.** Despite the name `Idle2Crouch_Neutral2Crouch2Idle`, it is a 53.667 s
mixed motion-capture take (internal clip name `_142_TO_161_a_U1_M_P_idle2Crouch_Neutral2Crouch2Idle_
Fb_p0_No_0_PJ_2`), and the body never lowers.

Measured on `male_adult_01`, 200-point scan across the whole clip, height = baked-mesh bounds:

| quantity | value |
|---|---|
| body height range across the entire clip | **1.77 – 2.04 m** |
| deepest **grounded** pose | 1.754 m at normalizedTime 0.835 |
| drop from standing to that pose | **0.330 m** |
| for comparison, a real kneel (Mixamo crouch clip) | **1.354 m**, drop 0.401 m and visibly a kneel |

A 0.330 m drop is a stride, not a crouch. Spot renders confirm it: at normalizedTime 0.40 the
character is plainly **walking**.

A naive scan that takes the global height minimum *does* find a 1.354 m pose here — but with the
feet ~0.6 m off the floor. That pose is airborne, not a crouch, which is why the smoke runner's
depth scan now requires ground contact before a pose can be called "deepest".

## Status

Kept on disk deliberately, as the evidence for this exclusion. Nothing references it: the crouch
controller is built from the Mixamo clip, gated by `S68CrouchImport.UseIviCrouch` (currently
`false`). Flipping that flag to `true` is the only thing that would bring this file back into play —
don't, unless the measurements above have been redone and have changed.
