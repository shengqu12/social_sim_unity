#!/usr/bin/env python3
"""Session 41 TASK 6.2: was the corridor actually the binding constraint?

For each corridor trial, compares how much lateral room the agents USED against how much
the walls ALLOWED. If peak lateral excursion stays well inside the corridor half-width, the
walls were never binding and any breach in that trial cannot be attributed to corridor
narrowness -- it is the underlying head-on yielding behaviour showing up in a new harness.

This exists because the 3.0m "control" width produced a 0.319m breach whose trajectory shows
0.118m lateral separation at the closest point: a near-centerline head-on pass, with both
agents using barely half the room available. Without this check the per-width label table
reads as a width effect when it may be nothing of the sort.

Corridor centerline is taken as the mean pedestrian z over the first 10 frames (both agents
spawn on it, and the corridor is built symmetrically about the robot/pedestrian midpoint).

Usage: python3 tools/s41_wall_clearance.py <trial_dir> [<trial_dir> ...]
"""
import csv
import json
import os
import sys

# The corridor profile's TEB avoidance settings (run_trial.py DEFAULT_TEB_*, confirmed in the
# run logs: min_obstacle_dist=0.3 inflation_dist=0.5). inflation_dist is what eats usable width.
TEB_INFLATION_DIST = 0.5
TEB_MIN_OBSTACLE_DIST = 0.3


def analyse(d):
    fp = os.path.join(d, "frames.csv")
    mp = os.path.join(d, "meta.json")
    if not os.path.exists(fp):
        return None
    rows = list(csv.DictReader(open(fp)))
    if not rows:
        return None

    def f(r, k):
        return float(r[k])

    width = None
    if os.path.exists(mp):
        try:
            width = json.load(open(mp)).get("corridorWidthMeters")
        except Exception:
            pass

    center = sum(f(r, "pedestrian_z") for r in rows[:10]) / min(10, len(rows))
    mn = min(rows, key=lambda r: f(r, "dist_to_pedestrian"))

    ped_exc = max(abs(f(r, "pedestrian_z") - center) for r in rows)
    rob_exc = max(abs(f(r, "robot_z") - center) for r in rows)
    used = max(ped_exc, rob_exc)
    half = (width / 2.0) if width else None

    # Geometric clearance-to-wall is NOT what constrains the robot -- costmap/TEB inflation is.
    # Walls inflate into the corridor, so the band the planner will freely use is roughly
    # (half_width - inflation_dist), not half_width. Reporting only the geometric fraction made
    # 3.0m look unconstrained when the 6.0m control proves it is not. Both are reported.
    eff = (half - TEB_INFLATION_DIST) if half is not None else None
    if eff is not None and eff <= 0:
        eff = None  # corridor narrower than the inflation band: no free space at all
    return {
        "dir": d,
        "width": width,
        "half_width": half,
        "effective_half_width": eff,
        "robot_frac_effective": (rob_exc / eff) if eff else None,
        "min_dist": f(mn, "dist_to_pedestrian"),
        "t_min": f(mn, "t"),
        "lateral_sep_at_min": abs(f(mn, "robot_z") - f(mn, "pedestrian_z")),
        "ped_peak_excursion": ped_exc,
        "robot_peak_excursion": rob_exc,
        "max_used": used,
        # <0.7 means neither agent got close to a wall: the corridor was not the constraint.
        "fraction_of_half_width_used": (used / half) if half else None,
    }


def main(dirs):
    out = []
    for d in dirs:
        r = analyse(d)
        if r:
            out.append(r)
    if not out:
        print("no analysable trials")
        return 1
    # Robot and pedestrian are reported separately on purpose: a combined max hides WHICH agent
    # the walls constrain, and they behave differently. The pedestrian is moved by SFAgent
    # writing the transform directly, so a collider need not stop it; the robot plans around the
    # walls through the costmap. A robot fraction at or above 1.0 means its CENTRE reached the
    # wall's inner face -- with a 0.16m robot radius that is a graze or worse, i.e. the corridor
    # is at the limit of what it can physically take.
    print("{:<26} {:>6} {:>9} {:>9} {:>7} {:>7} {:>8} {:>8} {:>9} {:>8}".format(
        "trial", "width", "min_dist", "lat_sep", "half_w", "eff_hw", "ped_frac", "rob_frac",
        "rob_eff", "binding"))
    for r in out:
        hw = r["half_width"]
        pf = (r["ped_peak_excursion"] / hw) if hw else None
        rf = (r["robot_peak_excursion"] / hw) if hw else None
        if hw is None:
            binding = "-"
        elif rf >= 0.9 and pf >= 0.9:
            binding = "both"
        elif rf >= 0.9:
            binding = "robot"
        elif pf >= 0.9:
            binding = "ped"
        else:
            binding = "no"
        ef = r.get("effective_half_width")
        ref = r.get("robot_frac_effective")
        # Binding is judged on the EFFECTIVE band: >=0.9 there means the robot is working
        # inside inflated space, which is the constraint that actually bites.
        if ef is None:
            binding = "NO FREE BAND" if hw else "-"
        elif ref is not None and ref >= 0.9:
            binding = "**inflation**"
        print("{:<26} {:>6} {:>9.3f} {:>9.3f} {:>7} {:>7} {:>8} {:>8} {:>9} {:>8}".format(
            os.path.basename(r["dir"].rstrip("/")),
            r["width"] if r["width"] else "-",
            r["min_dist"], r["lateral_sep_at_min"],
            "{:.2f}".format(hw) if hw else "-",
            "{:.2f}".format(ef) if ef else "-",
            "{:.2f}".format(pf) if pf is not None else "-",
            "{:.2f}".format(rf) if rf is not None else "-",
            "{:.2f}".format(ref) if ref is not None else "-",
            binding))
    bound = [r for r in out
             if r["fraction_of_half_width_used"] and r["fraction_of_half_width_used"] >= 0.9]
    print("\ntrials where an agent came within 10% of a wall (corridor plausibly binding): "
          "{}/{}".format(len(bound), len(out)))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
