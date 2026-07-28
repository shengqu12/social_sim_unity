# Patches against the `Microsoft-Rocketbox` submodule

These two patches hold changes that live in the **submodule's working tree and have never been
committed anywhere**. A `git submodule update`, a reset, or a fresh clone loses them silently, and
the symptoms they fix are the kind that get re-diagnosed from scratch — one of them already has
been.

They are kept here, in the parent repository, because the files they touch belong to Microsoft's
upstream repository. Committing into that submodule would fork it.

## Applying

```bash
cd Assets/ExternalAssets/Microsoft-Rocketbox
git apply --check ../../../patches/rocketbox_sticky_guard.patch     # dry run first
git apply         ../../../patches/rocketbox_sticky_guard.patch
git apply         ../../../patches/rocketbox_rig_import_settings.patch
```

Apply the sticky guard **before** opening the project in Unity. The rig settings patch can be
applied at any point, but Unity must be closed or the importer will race it.

---

## `rocketbox_sticky_guard.patch` — 29 lines, `Assets/Editor/FixRocketboxMaxImport.cs`

**Symptom without it**: a Rocketbox avatar set to Humanoid reverts to Generic on the next reimport,
including simply entering Play mode. It looks like the Inspector change "didn't take".

`FixRocketboxMaxImport` is an `AssetPostprocessor`, so it runs on **every** model import and
unconditionally assigned `animationType = Generic`. Two changes:

```csharp
// was: importer.animationType = ModelImporterAnimationType.Generic;
if (importer.animationType != ModelImporterAnimationType.Human)
    importer.animationType = ModelImporterAnimationType.Generic;
```

plus a null guard on `g.transform.Find("Bip01")`, without which the postprocessor throws on any
model that is not a Rocketbox rig — every Zone-B FBX (bike, dog, scooter) and every Mixamo clip.

Humanoid is required by `AttachPropToHand`, which resolves hands via `GetBoneTransform`, and by
`AvatarAnimatorUtility.GetLocomotionAnimator`, which prefers a Humanoid-avatar Animator so a
prop's or an animal's Animator cannot take over locomotion.

> **Better long-term fix, not done here**: a project-owned `AssetPostprocessor` in the parent
> repository with a `GetPostprocessOrder()` above Microsoft's, setting `animationType` back to
> Human for the affected avatars. That would travel with the code and need no manual patching.
> It needs testing, so the patch is stored first — the better fix should not delay the safe one.

## `rocketbox_rig_import_settings.patch` — 1866 lines, 5 `*.fbx.meta`

`Female_Adult_05`, `Female_Child_01`, `Female_Child_02`, `Male_Child_01`, `Male_Child_02`.

Three deliberate changes are mixed into Unity 2022 re-serialization churn
(`serializedVersion: 19301 -> 22200` and a batch of fields that simply did not exist before):

| change | why it matters |
|---|---|
| `animationType: 2 -> 3` | Generic → **Humanoid**. This is the setting the sticky guard exists to protect; without the guard it reverts on the next import |
| `optimizeGameObjects: 1 -> 0` | "Optimize GameObjects" strips the bone hierarchy at import. With it on, the transform tree cannot be inspected at runtime — it is what blocked the `Stroke_Shaking_Head` grounding diagnosis, recorded as unresolvable in `PROJECT_HANDOFF.md` |
| `extraExposedTransformPaths` | exposes `Bip01 R Hand`, `Bip01 L Hand`, `Bip01 Head` — exactly the bones `AttachPropToHand` needs to attach a cane, a phone or a box |

**The two children matter for the dataset**: `male_child` and `female_child` are roster
configurations, and both of their rigs are in this patch.

---

## Deliberately NOT patched: 13 `.tga` and 13 `.tga.meta`

The submodule also shows 26 texture files as modified. Those are **not** included, because they
are not real changes:

- the 13 `.tga` are `mode change 100644 => 100755` with **0 insertions, 0 deletions** and identical
  byte counts (`Bin 12582956 -> 12582956`) — only the executable bit moved, most likely from a copy
  across a filesystem that does not carry POSIX permissions
- the 13 `.tga.meta` carry the same mode change plus importer re-serialization churn

Nothing was reverted. Submodule state has wide blast radius, so the disposition of these 26 files
is Sheng's call, not an automated cleanup.
