#!/usr/bin/env python3
"""Session 62: robot-side labels R1-R5 for a trial.

Every criterion this pipeline had -- C1/C3, the 3.1-3.8 self-tests, windowed displacement, the slide
invariant -- measured the PEDESTRIAN. Nothing looked at the robot. `scooter_user` consequently spent
60% of every trial below 0.05 m/s since Session 44 and nobody noticed, while the thing the VLM is
asked to score is the robot's social behaviour.

  R1  reached the goal, plus terminationReason
  R2  mean robot speed, whole trial and encounter segment
  R3a fraction of frames below 0.05 m/s -- catches DENSE SHORT stalls
  R3b longest continuous stall and its bounds -- catches a SINGLE LONG stall
  R3c when that stall began relative to the closest approach -- separates yielding from failure
  R4  path length / straight-line distance (detour ratio)
  R5  behaviour tier 1-5 -- HUMAN-FILLED, never inferred here (same rule as `eyeball`)

LABELS, NOT FILTERS. A robot that stops to let someone pass is behaving correctly, and that is a
sample this dataset wants. Nothing here rejects a trial; it makes the robot's behaviour visible so a
consumer can decide. The one thing these numbers cannot do on their own is separate "stopped to
yield" from "stalled": `scooter_user` does both in one trial, yielding at t=4.2 s and then stalling
permanently from t=12.6 s with the pedestrian 40 m away. R3's bounds against the encounter time are
what distinguishes them, which is why R3c exists.

R3 is three columns rather than one because each alone has a blind spot, and the split is not
theoretical: scooter_user's R3b is 0.66 s against a healthy cyclist's 0.33 s -- indistinguishable --
while its R3a is 0.60 against 0.01. R3a misses a single long stall, R3b misses dense short ones, and
R3c has nothing to qualify without the other two. The statistic has to match the shape of the
failure mode you are trying to catch, not the shape that first comes to mind.

All speeds come from `robot_speed_ground` (the physics body). Never position differencing.
"""
import csv, json, math, os, sys

STALL_MPS = 0.05
GOAL_ARRIVAL_M = 0.5
# R2's encounter segment: +/- this around the closest approach, the window a near clip spans.
ENCOUNTER_HALF_WINDOW_S = 5.0


def _speed(row):
    v = row.get("robot_speed_ground", "")
    if v not in ("", None):
        return float(v)
    # Pre-S43 trials have no physics-body column. Fall back to the velocity vector, still the
    # physics body -- never to a position delta.
    if row.get("robot_vel_x", "") != "":
        return math.hypot(float(row["robot_vel_x"]), float(row["robot_vel_z"]))
    return float("nan")


def robot_labels(trial_dir):
    frames = os.path.join(trial_dir, "frames.csv")
    if not os.path.exists(frames):
        return {"error": "no frames.csv"}
    rows = list(csv.DictReader(open(frames)))
    if not rows:
        return {"error": "empty frames.csv"}

    t = [float(r["t"]) for r in rows]
    v = [_speed(r) for r in rows]
    if any(x != x for x in v):
        return {"error": "no physics-body speed column (pre-S43 output)"}

    out = {"config": os.path.basename(os.path.normpath(trial_dir))}

    # ---- R1 -------------------------------------------------------------------------------
    meta_path = os.path.join(trial_dir, "meta.json")
    meta = json.load(open(meta_path)) if os.path.exists(meta_path) else {}
    out["R1_termination"] = meta.get("terminationReason", "?")
    goal = (meta.get("config") or {}).get("goalPose") or {}
    if goal and "x" in goal:
        last = rows[-1]
        d = math.dist((float(last["robot_x"]), float(last["robot_z"])),
                      (float(goal["x"]), float(goal["z"])))
        out["R1_goal_dist_m"] = round(d, 3)
        out["R1_reached_goal"] = d <= GOAL_ARRIVAL_M
    else:
        out["R1_goal_dist_m"] = None
        out["R1_reached_goal"] = None

    # ---- R2 -------------------------------------------------------------------------------
    out["R2_mean_mps"] = round(sum(v) / len(v), 3)
    i_min = min(range(len(rows)), key=lambda i: float(rows[i]["dist_to_pedestrian"]))
    out["R2_t_closest_s"] = round(t[i_min], 2)
    enc = [v[i] for i in range(len(rows))
           if abs(t[i] - t[i_min]) <= ENCOUNTER_HALF_WINDOW_S]
    out["R2_mean_encounter_mps"] = round(sum(enc) / len(enc), 3) if enc else None

    # ---- R3 -------------------------------------------------------------------------------
    best, start = (0.0, None, None), None
    for i, (ti, vi) in enumerate(zip(t, v)):
        if vi < STALL_MPS:
            if start is None:
                start = ti
        else:
            if start is not None and ti - start > best[0]:
                best = (ti - start, start, ti)
            start = None
    if start is not None and t[-1] - start > best[0]:
        best = (t[-1] - start, start, t[-1])
    out["R3_longest_stall_s"] = round(best[0], 2)
    out["R3_stall_from_s"] = round(best[1], 2) if best[1] is not None else None
    out["R3_stall_to_s"] = round(best[2], 2) if best[2] is not None else None
    out["R3_frac_below_stall"] = round(sum(1 for x in v if x < STALL_MPS) / len(v), 3)
    # The discriminator: a stall that begins well after the closest approach is not yielding.
    if best[1] is not None:
        out["R3_starts_after_encounter_s"] = round(best[1] - t[i_min], 2)

    # ---- R4 -------------------------------------------------------------------------------
    pts = [(float(r["robot_x"]), float(r["robot_z"])) for r in rows]
    path = sum(math.dist(a, b) for a, b in zip(pts, pts[1:]))
    net = math.dist(pts[0], pts[-1])
    out["R4_path_m"] = round(path, 2)
    out["R4_net_m"] = round(net, 2)
    out["R4_detour_ratio"] = round(path / net, 3) if net > 0.1 else None

    # ---- R5 -------------------------------------------------------------------------------
    # 1 straight through | 2 slight detour | 3 slowed and followed | 4 stopped and yielded
    # 5 oscillated / backed off / replanning failed
    out["R5_behaviour_tier"] = "PENDING"
    return out


HEADER = ("| config | R1 goal / termination | R2 mean (enc) | R3a stall frac | "
          "R3b longest stall | R3c vs t_min | R4 detour | R5 |")
SEP = "|---|---|---|---|---|---|---|---|"


def md_row(d):
    if d.get("error"):
        return "| `%s` | %s | | | | | | PENDING |" % (d.get("config", "?"), d["error"])
    r1 = "%s / %s" % ({True: "yes", False: "no", None: "?"}[d["R1_reached_goal"]], d["R1_termination"])
    r2 = "%.3f (%s)" % (d["R2_mean_mps"],
                        "%.3f" % d["R2_mean_encounter_mps"] if d["R2_mean_encounter_mps"] is not None else "-")
    r3a = "%.0f%%" % (100 * d["R3_frac_below_stall"])
    r3b = ("%.2f s @%.1f-%.1f" % (d["R3_longest_stall_s"], d["R3_stall_from_s"], d["R3_stall_to_s"])
           if d["R3_stall_from_s"] is not None else "%.2f s" % d["R3_longest_stall_s"])
    r3c = ("%+.1f s" % d["R3_starts_after_encounter_s"]
           if d.get("R3_starts_after_encounter_s") is not None else "-")
    r4 = "%s" % (d["R4_detour_ratio"] if d["R4_detour_ratio"] is not None else "-")
    return "| `%s` | %s | %s | %s | %s | %s | %s | %s |" % (
        d["config"], r1, r2, r3a, r3b, r3c, r4, d["R5_behaviour_tier"])


if __name__ == "__main__":
    results = [robot_labels(p) for p in sys.argv[1:]]
    print(HEADER)
    print(SEP)
    for d in results:
        print(md_row(d))
    print()
    print("R5 is filled by a human, never here. Tiers: 1 straight through | 2 slight detour | "
          "3 slowed and followed | 4 stopped and yielded | 5 oscillated / backed off / replan failed.")
    print("These are LABELS. A stall is not a reason to reject a trial -- read R3's bounds against "
          "R2's closest-approach time before calling one a defect.")
