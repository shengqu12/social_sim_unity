# Merge notes — `sheng/ped-behavior-v2`

For 嘉诚. Branch is on `myfork` (`git@github.com:shengqu12/social_sim_unity.git`).

- **Base**: `208d92c` = `sheng/auto-capture` HEAD = `myfork/sheng/auto-capture`. Fast-forward, no rebase needed.
- **Head**: `e4eac6d`
- **7 commits**, 68 files, +4110 / −11 lines.

## The 11 deleted lines are the whole conflict surface

Only **5 existing files** are modified. Everything else is new files.

| File | Commit(s) | What changed |
|---|---|---|
| `Assets/Resources/Animation/SocialForcesAnimatorController.controller` | `657cd1d` **only** | 4 hunks, reaction states only |
| `Assets/Scripts/AutoTrial/AutoTrialConfig.cs` | `332d446` | +5 fields, all defaulted off |
| `Assets/Scripts/AutoTrial/AutoTrialBootstrap.cs` | `332d446` | 2 new `if` blocks, both gated on the new defaulted-off fields |
| `Assets/Scripts/AutoTrial/S32AnimatorSpeedScaler.cs` | `657cd1d` | +49 lines, hold `animator.speed` at 1.0 during reactions |
| `tools/run_trial.py` | `332d446`, `b0bac72` | new `corridor` profile + `safety_label` in meta |

**Not touched, verified by `git diff --name-status`:**

- No scenario preset (`headon` / `overtake` / `overtaken` / `dyad` / `ped_count`). The only 6 removed lines in `run_trial.py` are: extending the `PROFILES` tuple, extending `PROFILE_PED_DISTANCE`, one function signature, one stale comment, one call site, one `print`.
- No personality prefab.
- No scene file. The corridor in TASK 5 is **runtime geometry** built by `S41CorridorBuilder` inside the existing `Outdoor.unity`, not a new scene — navigation is map-bound to `Outdoor` through a ROS occupancy map in the read-only `sim_ws` repo, so a new scene file would leave the robot immobile.
- No `Base.cs`, no `SFAgent.cs`, no `IVI/`.
- No submodule pointer change. `Assets/ExternalAssets/Microsoft-Rocketbox` shows dirty *content* locally (texture/meta edits) but `git submodule status` reports the recorded SHA still `9e1048a`, and no commit on this branch touches the submodule.

## The one shared-asset edit, and how to revert it

`657cd1d` is the **only** commit that edits an existing shared asset, and it is deliberately alone in its commit so it can be dropped without losing anything else.

```
git revert 657cd1d
```

Verified clean: applied with `--no-commit` on `e4eac6d`, exit 0, no conflicts, touching exactly its own 4 files.

The 4 controller hunks:

| Hunk | Before | After |
|---|---|---|
| Transition → `AssertiveGesture` | `m_TransitionDuration: 0.15` | `0.08` |
| State `AssertiveGesture` | `m_Speed: 1` | `1.15` |
| Transition → `SurprisedReaction` | `m_TransitionDuration: 0.15` | `0.08` |
| State `SurprisedReaction` | `m_Speed: 1` | `1.15` |

Locomotion states and their transitions are untouched. `m_HasExitTime` was already `0` everywhere before this branch — the ticket assumed it needed setting; it did not, so no hunk exists for it.

All four edits were written through `UnityEditor.Animations` (`Assets/Scripts/AutoTrial/Editor/S41ReactionTransitionTuning.cs`), not by hand-editing YAML.

## What the branch is actually for

The headline result is in `657cd1d`: the two separate complaints ("slow to start", "slow to play") were **one** bug. `S32AnimatorSpeedScaler` set `animator.speed` to match walking pace, but that is a whole-Animator multiplier, so it also stretched one-shot reaction clips — worst exactly when Assertive stops to gesture and the speed hits its 0.3 clamp floor. An authored 3.600 s gesture was playing as 12.000 s. Holding speed at 1.0 during reactions: 12.0 s → 3.13 s, trigger-to-animation latency 0.43–0.47 s → 0.12–0.18 s.

Full session write-up: `trial_outputs/REPORT.md`, Session 41.

## Two things to know before you build on it

1. **4 of the 9 new Mixamo clips are broken.** `Sitting`, `Standing_Arguing`, `Talking_standing` glide ~14 m while playing a stationary animation; `Stroke_Shaking_Head` additionally is not grounded. Diagnosed but **not fixed** — see `known_issues/S41_mixamo_screen61.md`. Every one of those 18 screening runs exited 0 and was labelled `safe`; exit code and safety label are not the verdict for this class of defect.
2. **The corridor width sweep does not establish an ordering among 3.0 / 2.0 / 1.5 / 1.2 m.** N=5 per width; 2.0 m and 1.5 m differ by 0.001 m. The 6.0 m control (worst 0.894 m, 5/5 safe) is what makes the sweep interpretable at all, and it arrived late — read `b0bac72`'s message before quoting any number from it.

## Local dirt, deliberately not committed

`Assets/Resources/ROSConnectionPrefab.prefab` and `UserSettings/Layouts/default-2022.dwlt` are rewritten by Unity batchmode on every run. They appear in no commit on this branch. They are tracked files, so `.gitignore` cannot suppress them; the working rule is simply never to stage them.
