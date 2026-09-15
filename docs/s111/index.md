# S111 — Kimodo breadth batch: the MENU (nothing promoted, nothing gated)

Numbers are REPORTED for orientation only. Every candidate that Howard ticks needs Sheng's explicit per-clip ticket before any promotion round; `l6` additionally needs the p2_limp ethics review first (standing rule). One-shot reactions inherit the SurprisedReaction standing rule: upper-body, in place, no steps, no head-turns — net displacement below is the in-place check.

Per candidate: `SKELETON_<tag>.png` (S76 skeleton preview strip) and `ONBODY_<tag>.png` (Rocketbox Business_Male_01, S104b scratch path, third-person trial frames) in `strips/`.

Render provenance: every candidate ran one 30 s scratch trial (gitignored import, zero repo writes); two trials exited rc=1 on the robot-side trigger-speed gate (`r2_annoyed` seed 42: 0.208 m/s, `r4_nod` seed 42: 0.284 m/s vs min 0.3) -- a robot nav fluctuation with a stationary pedestrian, not a clip property; the strips are unaffected. Trial gates do not gate this menu.

## Family L — looping gaits (seed 42 + 1042 each; `--duration 8`)

| pick | candidate | prompt gist | net m/s | loop median m/s | net/path | wrap seam ° | continuity min/max (0.5 s windowed) | tag |
|---|---|---|---|---|---|---|---|---|
| [ ] | `kimodo_brisk_walk` seed 42 | brisk, purposeful pace [tests the >1.3 range] | 1.2789 | 1.4821 | 0.9928 | 20.96 | 0.13/1.08 | `s111_l1_brisk_seed42` |
| [ ] | `kimodo_brisk_walk` seed 1042 | " | 1.5269 | 1.9358 | 0.9901 | 8.55 | 0.04/1.15 | `s111_l1_brisk_seed1042` |
| [ ] | `kimodo_slow_stroll` seed 42 | slow leisurely stroll, looking around casually | 0.6460 | 0.6519 | 0.9957 | 54.78 | 0.73/1.25 | `s111_l2_stroll_seed42` |
| [ ] | `kimodo_slow_stroll` seed 1042 | " | 0.6608 | 0.6777 | 0.9866 | 50.36 | 0.69/1.23 | `s111_l2_stroll_seed1042` |
| [ ] | `kimodo_jog` seed 42 | light, easy jog | 1.1875 | 0.9493 | 0.9763 | 86.79 | 0.14/3.14 | `s111_l3_jog_seed42` |
| [ ] | `kimodo_jog` seed 1042 | " | 1.3633 | 1.5521 | 0.9951 | 48.11 | 0.07/1.11 | `s111_l3_jog_seed1042` |
| [ ] | `kimodo_walk_texting` seed 42 | walking, looking down at a phone in both hands | 0.8704 | 0.8797 | 0.9914 | 40.17 | 0.86/1.12 | `s111_l4_texting_seed42` |
| [ ] | `kimodo_walk_texting` seed 1042 | " | 0.6911 | 0.7115 | 0.9838 | 43.48 | 0.66/1.25 | `s111_l4_texting_seed1042` |
| [ ] | `kimodo_walk_carry` seed 42 | walking, carrying a bag in one hand | 1.0313 | 1.1500 | 0.9918 | 36.72 | 0.11/1.11 | `s111_l5_carry_seed42` |
| [ ] | `kimodo_walk_carry` seed 1042 | " | 0.9721 | 1.1463 | 0.9897 | 37.99 | 0.11/1.09 | `s111_l5_carry_seed1042` |
| [ ] | `kimodo_limp_mild` seed 42 | slight limp, favoring one leg -- PREVIEW ONLY: p2_limp ethics review required before any promotion (standing rule) | 0.4881 | 0.4948 | 0.9690 | 15.73 | 0.85/1.35 | `s111_l6_limp_seed42` |
| [ ] | `kimodo_limp_mild` seed 1042 | " | 0.6924 | 0.7144 | 0.9921 | 45.40 | 0.68/1.11 | `s111_l6_limp_seed1042` |

*Continuity min/max is measured on the UNTRIMMED 8 s source, so every row shows the S108 standing-start/standing-stop ease (min well below 0.85): that is a property of raw Kimodo output, not a defect of the candidate — a promotion round would trim to the cruise region per S109 and re-measure. Loop median m/s is the better pace estimate; net m/s averages the ease-in/out in.)*

## Family R — one-shot reactions (chained; not looped, wrap seam not applicable)

| pick | candidate | prompt gist | duration s | net displacement m | path length m | tag |
|---|---|---|---|---|---|---|
| [ ] | `startle_freeze` seed 42 | flinch, hands to chest, then relax (2+3 s chain) | 4.97 | 0.061 | 0.245 | `s111_r1_startle_seed42` |
| [ ] | `startle_freeze` seed 1042 | " | 4.97 | 0.011 | 0.298 | `s111_r1_startle_seed1042` |
| [ ] | `annoyed_gesture` seed 42 | exasperated palm-up gesture, then relax (2+3 s chain) | 4.97 | 0.022 | 0.085 | `s111_r2_annoyed_seed42` |
| [ ] | `annoyed_gesture` seed 1042 | " | 4.97 | 0.015 | 0.184 | `s111_r2_annoyed_seed1042` |
| [ ] | `step_aside_lean` seed 42 | upper-body lean aside, feet planted, then straighten (2+3 s chain) | 4.97 | 0.039 | 0.246 | `s111_r3_lean_seed42` |
| [ ] | `step_aside_lean` seed 1042 | " | 4.97 | 0.034 | 0.239 | `s111_r3_lean_seed1042` |
| [ ] | `nod_greeting` seed 42 | single greeting nod (3 s) | 2.97 | 0.033 | 0.039 | `s111_r4_nod_seed42` |
| [ ] | `nod_greeting` seed 1042 | " | 2.97 | 0.004 | 0.043 | `s111_r4_nod_seed1042` |
