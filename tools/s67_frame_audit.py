#!/usr/bin/env python3
"""Session 67: is the pedestrian actually inside the frames the model was shown?

    python3 tools/s67_frame_audit.py <out_dir>

This started as the hallucination check the S67 work order asks for. One S65/S67A answer claims
"the pedestrian is not visible in the provided images", which looked like a hallucination and is
not: the frame at that trial's closest approach is empty pavement.

So the check is done properly here, geometrically, for every frame both protocols showed. The POV
camera is `resolvedCamHfovDeg` = 69 deg, `resolvedCamPitchDeg` = 0, mounted at ~0.66 m. The
pedestrian's bearing relative to the robot's heading follows from `robot_x/robot_z`,
`nearest_ped_x/nearest_ped_z` and `robot_heading` (Unity yaw, 0 = +Z, clockwise positive).

Visibility rule: the pedestrian's centre is inside the horizontal FOV, widened by the angle a
0.3 m body half-width subtends at that range. Two independently-checked calibration points, both
eyeballed against the actual PNG:

  A3_Pacing_Phone_r5 / frame_0009.png      rel bearing -30.0 deg at 1.14 m -> a trouser leg at the
                                           extreme left edge, no head, no body
  A3_Pacing_Phone_r5 / frame_0011_d4.png   rel bearing -67.2 deg at 0.83 m -> empty pavement

`strictly_behind` (|bearing| > 60 deg) is reported separately: those frames need no threshold
argument at all, the person is beside or behind the robot and cannot be in a forward camera.

Writes FRAME_AUDIT.json and FRAME_AUDIT.md. Reads the dataset only.
"""
import csv, json, math, os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from s65_vlm_judge import DATASET, TRIALS, N_FRAMES  # noqa: E402
from s67_vlm_judge import KEEP  # noqa: E402

HALF_HFOV_DEG = 34.5      # resolvedCamHfovDeg 69.0 / 2, constant across the dataset
BODY_HALF_W_M = 0.3
BEHIND_DEG = 60.0


def rel_bearing(row):
    dx = float(row["nearest_ped_x"]) - float(row["robot_x"])
    dz = float(row["nearest_ped_z"]) - float(row["robot_z"])
    world = math.degrees(math.atan2(dx, dz))          # Unity yaw convention: 0 = +Z, CW positive
    return (world - float(row["robot_heading"]) + 180) % 360 - 180, math.hypot(dx, dz)


def visible(row):
    rel, d = rel_bearing(row)
    return abs(rel) <= HALF_HFOV_DEG + math.degrees(math.atan2(BODY_HALF_W_M, max(d, BODY_HALF_W_M)))


def eight_frames(trial):
    """The S65 subsample, plus the trial's true minimum distance over every captured row -- not the
    minimum over the eight, which is a different and much larger number."""
    rows = list(csv.DictReader(open(os.path.join(DATASET, trial, "vlm_eval", "states.csv"))))
    true_min = min(float(r["min_dist"]) for r in rows)
    enc = [r for r in rows if r.get("phase") == "encounter"] or rows
    if len(enc) > N_FRAMES:
        step = (len(enc) - 1) / (N_FRAMES - 1)
        enc = [enc[round(i * step)] for i in range(N_FRAMES)]
    return enc, true_min


def main():
    out_dir = sys.argv[1]
    os.makedirs(out_dir, exist_ok=True)
    audit, md = [], []
    tot8 = vis8 = tot4 = vis4 = behind = 0
    for trial, expected in TRIALS:
        eight, true_min = eight_frames(trial)
        four = [eight[i] for i in KEEP]
        frames = []
        for i, r in enumerate(eight):
            rel, d = rel_bearing(r)
            frames.append({"idx": i, "image": r["Image_name"], "t": float(r["time"]),
                           "min_dist": float(r["min_dist"]), "rel_bearing_deg": round(rel, 1),
                           "visible": visible(r), "in_4frame_set": i in KEEP})
        v8 = [f for f in frames if f["visible"]]
        v4 = [f for f in frames if f["visible"] and f["in_4frame_set"]]
        tot8 += len(frames); vis8 += len(v8)
        tot4 += len(four); vis4 += len(v4)
        behind += sum(1 for f in frames if abs(f["rel_bearing_deg"]) > BEHIND_DEG)
        rec = {"trial": trial, "expected": expected,
               "true_min_dist": true_min, "min_dist_in_8_shown": min(f["min_dist"] for f in frames),
               "closest_visible_8f": min([f["min_dist"] for f in v8], default=None),
               "closest_visible_4f": min([f["min_dist"] for f in v4], default=None),
               "visible_8f": len(v8), "visible_4f": len(v4), "frames": frames}
        audit.append(rec)
        md.append("| `%s` | %s | %.2f | %d/8 | %.2f | %d/4 | %s |" % (
            trial, expected, rec["true_min_dist"], len(v8), rec["closest_visible_8f"],
            len(v4), ("%.2f" % rec["closest_visible_4f"]) if v4 else "never"))

    json.dump({"half_hfov_deg": HALF_HFOV_DEG, "body_half_width_m": BODY_HALF_W_M,
               "trials": audit}, open(os.path.join(out_dir, "FRAME_AUDIT.json"), "w"), indent=1)
    with open(os.path.join(out_dir, "FRAME_AUDIT.md"), "w") as f:
        f.write("# S67 frame audit — was the pedestrian in the picture at all?\n\n"
                "Camera: hFOV 69 deg, pitch 0, height ~0.66 m. A frame counts as showing the "
                "pedestrian if their centre is within %.1f deg of the optical axis plus the angle a "
                "%.1f m body half-width subtends at that range. Generated by "
                "`tools/s67_frame_audit.py`; per-frame bearings are in `FRAME_AUDIT.json`.\n\n"
                % (HALF_HFOV_DEG, BODY_HALF_W_M))
        f.write("| trial | expected | true min_dist | S65 8f in view | closest **seen** | "
                "S67A 4f in view | closest **seen** |\n|---|---|---|---|---|---|---|\n")
        f.write("\n".join(md) + "\n\n")
        f.write("**%d of %d frames S65 showed contain no pedestrian; %d of %d for S67A.** "
                "%d of the %d frames put the pedestrian more than %.0f deg off-axis — beside or "
                "behind the robot, where a forward camera cannot see them under any FOV "
                "assumption.\n" % (tot8 - vis8, tot8, tot4 - vis4, tot4, behind, tot8, BEHIND_DEG))
    print("S65 8-frame: %d/%d frames show the pedestrian" % (vis8, tot8))
    print("S67A 4-frame: %d/%d frames show the pedestrian" % (vis4, tot4))
    print("frames with the pedestrian >%.0f deg off-axis: %d/%d" % (BEHIND_DEG, behind, tot8))
    print("wrote %s/FRAME_AUDIT.{json,md}" % out_dir)


if __name__ == "__main__":
    main()
