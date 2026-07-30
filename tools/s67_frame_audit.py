#!/usr/bin/env python3
"""Session 67: what does the POV camera actually show of the pedestrian?

    python3 tools/s67_frame_audit.py <out_dir>

This started as the hallucination check the S67 work order asks for. One S65/S67A answer claims
"the pedestrian is not visible in the provided images", which looked like a hallucination and is
not: the frame at that trial's closest approach is empty pavement.

Checked properly, the camera turns out to constrain the whole task. `meta.json` gives
`resolvedCamHfovDeg` 69.0, `resolvedCamVfovDeg` 42.27, `resolvedCamPitchDeg` 0, and
`resolvedCamHeightWorldY` ~0.658 m. Two consequences, both pure geometry:

**Horizontally**, the pedestrian is in frame only while their bearing off the optical axis is under
34.5 deg. On a head-on pass that holds during the approach and fails the moment the robot draws
level -- after which the person is beside or behind a forward-only camera.

**Vertically**, the top of the frame sits at `0.658 + d*tan(21.14deg)` metres above the ground at
range `d`. A 1.7 m adult's head therefore enters the frame only beyond **2.69 m**; a child's at
2.05 m; the chest at 1.40 m. Inside ~1 m the frame is filled by a knee.

Every threshold below was checked against the actual PNGs rather than assumed:

  | frame | bearing | range | top of frame | what is rendered |
  |---|---|---|---|---|
  | `A2_white_cane_user_r2/frame_0004_d1` |   0.9 deg | 1.38 m | 1.20 m | legs and torso, no head; cane is one thin line |
  | `A2_white_cane_user_r2/frame_0005_d2` |   7.6 deg | 0.70 m | 0.93 m | trouser legs only |
  | `A2_white_cane_user_r2/frame_0005_d4` |  11.2 deg | 0.49 m | 0.85 m | denim texture filling the frame, unrecognisable |
  | `A2_white_cane_user_r2/frame_0006_e`  |  50.0 deg | 0.27 m |    --  | empty pavement (past the near plane and off-axis) |
  | `A2_white_cane_user_r2/frame_0007_d2` | 131.9 deg | 0.44 m |    --  | empty pavement, pedestrian behind the robot |
  | `A3_Pacing_Phone_r5/frame_0009`       | -30.0 deg | 1.14 m | 1.10 m | a trouser leg at the extreme left edge |
  | `A3_Pacing_Phone_r5/frame_0011_d4`    | -67.2 deg | 0.83 m |    --  | empty pavement |

An earlier version of this script widened the horizontal bound by the angle a 0.3 m body half-width
subtends, which at 0.27 m opens the cone to +/-79 deg and wrongly counted the empty-pavement frame
above as showing the pedestrian. The bound here is the bare FOV, checked against those renders.

Reads the dataset only; writes FRAME_AUDIT.json and FRAME_AUDIT.md.
"""
import csv, json, math, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from s65_vlm_judge import DATASET, TRIALS, N_FRAMES  # noqa: E402
from s67_vlm_judge import KEEP  # noqa: E402

HALF_HFOV_DEG = 34.5        # resolvedCamHfovDeg 69.0 / 2
HALF_VFOV_DEG = 21.14       # resolvedCamVfovDeg 42.2726 / 2
CAM_HEIGHT_M = 0.658        # resolvedCamHeightWorldY, pitch 0
ADULT_HEAD_M = 1.70
LEGIBLE_MIN_M = 1.0         # below this the body fills the frame as texture (verified above)


def rel_bearing(row):
    """Bearing of the pedestrian relative to the camera axis, in degrees, signed."""
    dx = float(row["nearest_ped_x"]) - float(row["robot_x"])
    dz = float(row["nearest_ped_z"]) - float(row["robot_z"])
    world = math.degrees(math.atan2(dx, dz))       # Unity yaw: 0 = +Z, clockwise positive
    return (world - float(row["robot_heading"]) + 180) % 360 - 180, math.hypot(dx, dz)


def frame_top_m(d):
    """Height above ground of the top edge of the frame at range d."""
    return CAM_HEIGHT_M + d * math.tan(math.radians(HALF_VFOV_DEG))


def classify(row):
    """absent | texture | partial | full -- how much of the pedestrian this frame renders."""
    rel, d = rel_bearing(row)
    if abs(rel) > HALF_HFOV_DEG:
        return "absent", rel, d
    if d < LEGIBLE_MIN_M:
        return "texture", rel, d
    return ("full" if frame_top_m(d) >= ADULT_HEAD_M else "partial"), rel, d


def eight_frames(trial):
    """The S65 subsample, plus the trial's true minimum distance over every captured row."""
    rows = list(csv.DictReader(open(os.path.join(DATASET, trial, "vlm_eval", "states.csv"))))
    true_min = min(float(r["min_dist"]) for r in rows)
    enc = [r for r in rows if r.get("phase") == "encounter"] or rows
    if len(enc) > N_FRAMES:
        step = (len(enc) - 1) / (N_FRAMES - 1)
        enc = [enc[round(i * step)] for i in range(N_FRAMES)]
    return enc, true_min, rows


def main():
    out_dir = sys.argv[1]
    os.makedirs(out_dir, exist_ok=True)
    audit, md = [], []
    tally8 = {k: 0 for k in ("absent", "texture", "partial", "full")}
    tally4 = dict(tally8)
    for trial, expected in TRIALS:
        eight, true_min, all_rows = eight_frames(trial)
        frames = []
        for i, r in enumerate(eight):
            kind, rel, d = classify(r)
            frames.append({"idx": i, "image": r["Image_name"], "t": float(r["time"]),
                           "min_dist": float(r["min_dist"]), "rel_bearing_deg": round(rel, 1),
                           "frame_top_m": round(frame_top_m(d), 2), "renders": kind,
                           "in_4frame_set": i in KEEP})
            tally8[kind] += 1
            if i in KEEP:
                tally4[kind] += 1
        # Best the dataset could do: the closest range at which any captured frame renders a
        # recognisable body, and the closest at which the head is in frame at all.
        best = [(classify(r)[2], classify(r)[0]) for r in all_rows]
        legible = [d for d, k in best if k in ("partial", "full")]
        headed = [d for d, k in best if k == "full"]
        rec = {"trial": trial, "expected": expected, "true_min_dist": true_min,
               "shown_8f": {k: sum(1 for f in frames if f["renders"] == k) for k in tally8},
               "shown_4f": {k: sum(1 for f in frames
                                   if f["in_4frame_set"] and f["renders"] == k) for k in tally8},
               "closest_legible_anywhere_m": round(min(legible), 2) if legible else None,
               "closest_head_in_frame_m": round(min(headed), 2) if headed else None,
               "frames": frames}
        audit.append(rec)
        s8, s4 = rec["shown_8f"], rec["shown_4f"]
        md.append("| `%s` | %s | %.2f | %d / %d / %d / %d | %d / %d / %d / %d | %s |" % (
            trial, expected, true_min,
            s8["full"], s8["partial"], s8["texture"], s8["absent"],
            s4["full"], s4["partial"], s4["texture"], s4["absent"],
            ("%.2f" % rec["closest_legible_anywhere_m"]) if legible else "never"))

    json.dump({"half_hfov_deg": HALF_HFOV_DEG, "half_vfov_deg": HALF_VFOV_DEG,
               "cam_height_m": CAM_HEIGHT_M, "legible_min_m": LEGIBLE_MIN_M,
               "adult_head_in_frame_beyond_m": round(
                   (ADULT_HEAD_M - CAM_HEIGHT_M) / math.tan(math.radians(HALF_VFOV_DEG)), 2),
               "trials": audit}, open(os.path.join(out_dir, "FRAME_AUDIT.json"), "w"), indent=1)

    head_d = (ADULT_HEAD_M - CAM_HEIGHT_M) / math.tan(math.radians(HALF_VFOV_DEG))
    with open(os.path.join(out_dir, "FRAME_AUDIT.md"), "w") as f:
        f.write("# S67 frame audit — what the camera actually shows\n\n"
                "POV camera: hFOV 69°, vFOV 42.27°, pitch 0, height %.3f m (all from `meta.json`). "
                "A frame is scored by what it renders of the pedestrian:\n\n"
                "- **full** — in frame and beyond %.2f m, so the head is above the camera's "
                "horizon line and the whole figure is in shot\n"
                "- **partial** — in frame, %.1f–%.2f m: body without a head (the top of the frame "
                "sits at `%.3f + d·tan(21.14°)` m)\n"
                "- **texture** — in frame but under %.1f m: the body fills the frame as an "
                "unrecognisable surface\n"
                "- **absent** — more than 34.5° off the optical axis: not in shot at all\n\n"
                "Thresholds are calibrated against rendered PNGs, listed in "
                "`tools/s67_frame_audit.py`. Per-frame bearings and ranges are in "
                "`FRAME_AUDIT.json`.\n\n"
                % (CAM_HEIGHT_M, head_d, LEGIBLE_MIN_M, head_d, CAM_HEIGHT_M, LEGIBLE_MIN_M))
        f.write("| trial | expected | true `min_dist` | S65 8f full/partial/texture/absent | "
                "S67A 4f same | closest **legible** frame in the whole trial |\n"
                "|---|---|---|---|---|---|\n")
        f.write("\n".join(md) + "\n\n")
        n8 = sum(tally8.values()); n4 = sum(tally4.values())
        f.write("**Across the 96 frames S65 showed: %d render the whole figure, %d a headless "
                "body, %d unrecognisable texture, and %d contain no pedestrian at all.** "
                "For the 48 frames of S67 A: %d / %d / %d / %d.\n\n"
                % (tally8["full"], tally8["partial"], tally8["texture"], tally8["absent"],
                   tally4["full"], tally4["partial"], tally4["texture"], tally4["absent"]))
        f.write("An adult's head is in frame only beyond **%.2f m**. Every trial here is selected "
                "on a sub-1.5 m closest approach, so at the moment being graded the pedestrian is "
                "always headless or absent — which is a property of the camera mount, not of any "
                "model.\n" % head_d)
    print("S65 8-frame (%d): %s" % (n8, tally8))
    print("S67A 4-frame (%d): %s" % (n4, tally4))
    print("adult head in frame only beyond %.2f m" % head_d)
    print("wrote %s/FRAME_AUDIT.{json,md}" % out_dir)


if __name__ == "__main__":
    main()
