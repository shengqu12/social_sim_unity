#!/usr/bin/env python3
"""Session 51: measure the ANIMATION layer and the BODY layer separately.

The reported symptom is "speeds up over time". Windowed endpoint displacement said the body does
not speed up, and human review said it visibly does. Both can be true only if the thing speeding up
is the animation rate, not the body -- so the two layers must be measured independently and never
inferred from one another.

  animation layer : animator.speed          (what the legs do)
  body layer      : Base.velocity           (what the transform does, SFM-driven agents)
  input layer     : scaler_smoothed         (position-differenced -- the suspected inflating input)

Split into thirds of the travel phase. Compounding or inflation shows as a rising trend across
thirds; a stable value does not.

CAVEAT carried from Session 36, and it decides how to read white_cane: Base.velocity is NOT the body
speed for a root-motion-driven agent. It reported 2.09 m/s there while visible movement was
~0.35 m/s. Where base_velocity and actual travel disagree wildly, the agent is root-motion driven
and its body speed is not measured by this column.
"""
import csv, os, statistics as st, sys

def thirds(rows, key):
    vals = [(float(r["t"]), float(r[key])) for r in rows if r.get(key) not in (None, "")]
    if len(vals) < 30:
        return None
    t0, t1 = vals[0][0], vals[-1][0]
    span = (t1 - t0) / 3.0
    out = []
    for k in range(3):
        seg = [v for t, v in vals if t0 + k * span <= t < t0 + (k + 1) * span]
        out.append(st.median(seg) if seg else float("nan"))
    return out

def trend(a):
    if a is None or any(x != x for x in a):
        return "n/a"
    if a[0] < 1e-6:
        return "grew" if a[2] > 0.05 else "flat"
    r = a[2] / a[0]
    return "GREW {:.2f}x".format(r) if r > 1.25 else ("fell {:.2f}x".format(r) if r < 0.8 else "flat")

print("%-13s %-26s %-26s %-24s" % ("trial", "animator.speed (3rds)", "Base.velocity (3rds)", "scaler_smoothed (3rds)"))
for d in sys.argv[1:]:
    p = os.path.join(d, os.path.basename(os.path.normpath(d)) + "_probe.csv")
    if not os.path.exists(p):
        p = os.path.normpath(d) + "_probe.csv"
    if not os.path.exists(p):
        print("%-13s NO PROBE" % os.path.basename(os.path.normpath(d))); continue
    rows = [r for r in csv.DictReader(open(p)) if r.get("t")]
    rows = [r for r in rows if r.get("reaction_hold") == "0"]      # exclude the held-at-1.0 window
    a = thirds(rows, "animator_speed_final")
    b = thirds(rows, "base_velocity_mps")
    s = thirds(rows, "scaler_smoothed")
    f = lambda x: "[" + ", ".join("%.3f" % v for v in x) + "]" if x else "insufficient"
    print("%-13s %-26s %-26s %-24s" % (os.path.basename(os.path.normpath(d)), f(a), f(b), f(s)))
    print("%-13s   anim=%-12s body=%-12s input=%s" % ("", trend(a), trend(b), trend(s)))
