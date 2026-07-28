#!/usr/bin/env python3
"""Session 49: decide whether solution (e) removed the per-frame compounding.

The criterion is WINDOWED ENDPOINT DISPLACEMENT, not a speed measurement, and the distinction is
what makes it usable at all. Per-frame position differencing is unusable here because the transform
advances as a discrete event whose rate is unrelated to the 15 Hz capture. Endpoint displacement
over a 5 s window touches that granularity only at the two ends: each end carries at most one
un-applied update, measured at ~26 cm, against ~5 m of travel per window -- roughly 10% worst case.

Compounding would produce growth far outside that band. v_n = (v_{n-1} + a*dt) * k is geometric in
k, so successive windows would read something like 5 -> 7 -> 10 m. A flat sequence cannot be
produced by a compounding loop.

Verdict per trial:
    adjacent-window change < 20%   -> (e) EFFECTIVE
    monotonically increasing       -> (e) NOT effective, compounding remains
    fewer than 3 windows           -> INCONCLUSIVE, report the data and stop

Windows whose path is strongly curved are flagged: straight-line endpoint displacement understates
distance travelled when the actor turns, so a curved window can read short without any speed change.
The verdict still follows the trend, but a flagged window should not be read as a slowdown.
"""
import csv
import json
import math
import os
import sys

WINDOW_SEC = 5.0
WINDOW_STRIDE_SEC = 1.0
FLAT_TOLERANCE = 0.20          # adjacent-window relative change treated as constant
CURVATURE_FLAG = 0.85          # net/path below this = the window curves appreciably
MIN_WINDOWS = 3
MOVING_EPS = 0.05              # m of net travel below which a window is "not travelling"


def load(trial_dir):
    p = os.path.join(trial_dir, "frames.csv")
    if not os.path.exists(p):
        return None
    out = []
    for r in csv.DictReader(open(p)):
        try:
            out.append((float(r["t"]), float(r["pedestrian_x"]), float(r["pedestrian_z"])))
        except (ValueError, KeyError):
            continue
    return out or None


def travel_end(rows):
    """Cut the trial at the point the pedestrian stops travelling.

    Everything after arrival is stationary, and including it would dilute the sequence with zeros
    that look like deceleration. Detected as the last time the actor moved more than MOVING_EPS
    within a window-length span, rather than by reading a goal flag, so it works for every config.
    """
    if not rows:
        return 0.0
    last = rows[0][0]
    for i in range(len(rows)):
        t0, x0, z0 = rows[i]
        j = i
        while j + 1 < len(rows) and rows[j][0] - t0 < WINDOW_SEC:
            j += 1
        if math.hypot(rows[j][1] - x0, rows[j][2] - z0) > MOVING_EPS:
            last = rows[j][0]
    return last


def windows(rows, t_end):
    """[(t_start, net_displacement, net/path)] over consecutive WINDOW_SEC spans."""
    out = []
    if not rows:
        return out
    # SLIDING windows, stride WINDOW_STRIDE_SEC, not consecutive blocks.
    #
    # The travel phase is short: measured on a real trial the pedestrian covers its full 14.08 m in
    # 8.60 s and then stops, so consecutive 5 s blocks yield exactly ONE window and the criterion
    # cannot be evaluated at all. Sliding preserves the 5 s span -- and therefore the ~10% endpoint
    # error budget that makes this measurement safe -- while producing enough windows to see a
    # trend.
    #
    # The cost, stated plainly: overlapping windows are correlated, so "adjacent change < 20%" is a
    # weaker flatness test than it would be on independent blocks. Detection of MONOTONIC GROWTH,
    # which is what compounding produces, is unaffected -- a geometric series still climbs whether
    # sampled overlapping or not.
    t = rows[0][0]
    while t + WINDOW_SEC <= t_end:
        seg = [r for r in rows if t <= r[0] < t + WINDOW_SEC]
        if len(seg) >= 2:
            net = math.hypot(seg[-1][1] - seg[0][1], seg[-1][2] - seg[0][2])
            path = sum(math.hypot(seg[k][1] - seg[k - 1][1], seg[k][2] - seg[k - 1][2])
                       for k in range(1, len(seg)))
            out.append((t, net, (net / path) if path > 1e-6 else 1.0))
        t += WINDOW_STRIDE_SEC
    return out


def verdict(ws):
    if len(ws) < MIN_WINDOWS:
        return "INCONCLUSIVE", "only {} window(s), need {}".format(len(ws), MIN_WINDOWS)
    d = [w[1] for w in ws]
    # Ignore windows where the actor barely moved -- a stationary stretch is not evidence either way
    moving = [x for x in d if x > MOVING_EPS * 10]
    if len(moving) < MIN_WINDOWS:
        return "INCONCLUSIVE", "only {} travelling window(s): {}".format(
            len(moving), [round(x, 2) for x in d])

    rel = [abs(moving[i] - moving[i - 1]) / max(moving[i - 1], 1e-6) for i in range(1, len(moving))]
    worst = max(rel)
    strictly_up = all(moving[i] > moving[i - 1] for i in range(1, len(moving)))
    growth = moving[-1] / max(moving[0], 1e-6)

    if strictly_up and growth > 1.5:
        return "NOT EFFECTIVE", "monotonic growth {:.2f}x across {} windows".format(
            growth, len(moving))
    if worst < FLAT_TOLERANCE:
        return "EFFECTIVE", "max adjacent change {:.0%} (< {:.0%})".format(worst, FLAT_TOLERANCE)
    return "INCONCLUSIVE", "max adjacent change {:.0%}, not monotonic (growth {:.2f}x)".format(
        worst, growth)


def analyse(trial_dir):
    rows = load(trial_dir)
    if not rows:
        return {"trial": os.path.basename(os.path.normpath(trial_dir)), "verdict": "NO DATA"}
    t_end = travel_end(rows)
    ws = windows(rows, t_end)
    v, why = verdict(ws)
    return {
        "trial": os.path.basename(os.path.normpath(trial_dir)),
        "verdict": v,
        "reason": why,
        "travel_end_s": round(t_end, 2),
        "windows": [round(w[1], 2) for w in ws],
        "curved_windows": [i for i, w in enumerate(ws) if w[2] < CURVATURE_FLAG],
    }


def main():
    res = [analyse(d) for d in sys.argv[1:]]
    if "--json" in sys.argv:
        print(json.dumps(res, indent=2))
        return 0
    print("%-20s %-16s %-34s %s" % ("trial", "verdict", "windows (m per 5s)", "note"))
    for r in res:
        if r["verdict"] == "NO DATA":
            print("%-20s %s" % (r["trial"], "NO DATA"))
            continue
        curved = (" curved:" + ",".join(map(str, r["curved_windows"]))) if r["curved_windows"] else ""
        print("%-20s %-16s %-34s %s%s" % (
            r["trial"], r["verdict"], str(r["windows"])[:34], r["reason"], curved))
    return 0


if __name__ == "__main__":
    sys.exit(main())
