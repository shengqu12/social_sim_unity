#!/usr/bin/env python3
"""Session 68 §3 self-check for the crouch-to-watch Curious redesign.

Internal instrument, not a deliverable. Answers exactly the four questions the work order asks
about the demo run, and reports each one with the number it was decided on rather than a bare
pass/fail:

  1. state transition order and timing match §1, with timestamps and robot distances that agree
  2. net pedestrian displacement during CROUCH_HOLD is ~0, measured as 1 s windowed ENDPOINT
     displacement in the probe's own coordinates (params.csv pos_x/pos_z)
  3. after LEAVE the pedestrian walks and its speed returns to the Zone A calibrated band
  4. no ground penetration during the crouch -- pedestrian root y against the ground y beneath it

Windowed endpoint displacement, not per-frame differencing: the transform of a root-motion agent
advances in discrete animation steps, so a per-frame delta reads zero on most frames and spikes on
the rest. That is the measurement error Session 44 already made once on this project.

    python3 tools/s68_selfcheck.py <trial_out_dir> [--params <params.csv>]
"""
import argparse
import csv
import math
import re
import sys
from pathlib import Path

# PedestrianModulator: baseWalkSpeedMps 1.0476 * Zone A's N(1.05, 0.17) jitter -> mean 1.100 m/s,
# stdev 0.178. The band below is mean +/- 3 sigma, widened only by what a windowed measurement of a
# turning walk legitimately loses.
ZONE_A_SPEED_LO = 0.45
ZONE_A_SPEED_HI = 1.75

TRANSITION_RE = re.compile(
    r"\[S68Curious\]\s+(\w+)\s*->\s*(\w+)\s+t=([\d.]+)\s+dist_robot=([\d.]+)\s+\((.*?)\)")

EXPECTED_ORDER = ["Approach", "Stop", "CrouchEnter", "CrouchHold", "CrouchExit", "Leave"]


def parse_transitions(log_path):
    rows = []
    text = log_path.read_text(errors="replace")
    for m in TRANSITION_RE.finditer(text):
        rows.append({
            "from": m.group(1), "to": m.group(2),
            "t": float(m.group(3)), "dist": float(m.group(4)), "why": m.group(5),
        })
    return rows


def load_params(path):
    rows = []
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            try:
                rows.append({
                    "t": float(r["t"]),
                    "x": float(r["pos_x"]),
                    "z": float(r["pos_z"]),
                    "vel": float(r["base_vel_mps"]),
                    "anim": float(r["animator_speed"]),
                    "clip": r.get("clip", ""),
                })
            except (ValueError, KeyError):
                continue
    return rows


def load_frames(path):
    rows = []
    with open(path, newline="") as f:
        for r in csv.DictReader(f):
            try:
                rows.append({
                    "t": float(r["t"]),
                    "x": float(r["pedestrian_x"]),
                    "z": float(r["pedestrian_z"]),
                    "y": float(r["pedestrian_y"]),
                    "gy": float(r["pedestrian_ground_y"]),
                    "dist": float(r["dist_to_pedestrian"]),
                })
            except (ValueError, KeyError):
                continue
    return rows


def windowed_endpoint_speeds(rows, t0, t1, window=1.0):
    """Endpoint displacement over consecutive `window`-second slices, as m and m/s."""
    out = []
    seg = [r for r in rows if t0 <= r["t"] <= t1]
    if len(seg) < 2:
        return out
    start = seg[0]["t"]
    while start < seg[-1]["t"]:
        end = start + window
        inside = [r for r in seg if start <= r["t"] <= end]
        if len(inside) >= 2:
            a, b = inside[0], inside[-1]
            dt = b["t"] - a["t"]
            d = math.hypot(b["x"] - a["x"], b["z"] - a["z"])
            if dt > 1e-6:
                out.append((a["t"], b["t"], d, d / dt))
        start = end
    return out


def interp(t, xs, ys):
    import bisect
    if not xs or t <= xs[0] or t >= xs[-1]:
        return None
    i = bisect.bisect_left(xs, t)
    a, b = xs[i - 1], xs[i]
    if b - a < 1e-9:
        return ys[i - 1]
    return ys[i - 1] + (ys[i] - ys[i - 1]) * (t - a) / (b - a)


def align_frames_clock(frames, trans):
    """frames.csv timestamps are CAPTURE-relative (t=0 at the SLATE release); the transition trace
    is Time.time. Comparing them directly reads the wrong window entirely -- the first version of
    this script did exactly that and measured LEAVE over a stretch where the agent had already
    arrived and stopped, reporting 0.072 m/s.

    The offset is FITTED, not assumed to equal the release timestamp: each transition carries its
    own dist_robot, and frames.csv carries dist_to_pedestrian, so six independent (time, distance)
    anchors overdetermine a single scalar. The residual is returned and printed, which is what makes
    the alignment evidence rather than an assumption -- a bad fit shows up as a large residual
    instead of silently shifting every downstream window.
    """
    xs = [r["t"] for r in frames]
    ys = [r["dist"] for r in frames]
    anchors = [(r["t"], r["dist"]) for r in trans]
    best = None
    o = 0.0
    while o < 40.0:
        errs = []
        for ut, ud in anchors:
            v = interp(ut - o, xs, ys)
            if v is not None:
                errs.append(abs(v - ud))
        if len(errs) >= max(2, len(anchors) - 2):
            m = sum(errs) / len(errs)
            if best is None or m < best[1]:
                best = (o, m, len(errs))
        o += 0.01
    return best


TRANSITION_GUARD_SEC = 0.6


def phase_windows(trans, duration_hint=1e9):
    """(state -> (entered_t, left_t)) from the transition trace.

    The window CLOSES a little before the next transition. S68-B §3: run7's hold reported a 0.4884 m
    "slide" that came from a single window straddling the CrouchHold -> CrouchExit instant, at a
    moment when the robot was already 4.28 m away and receding -- the pedestrian was measured at
    exactly 0.0000 m for every sample of the actual close pass. The agent is legitimately unpinned
    and replanned as it leaves a state, so a window that includes the changeover measures the
    changeover, not the state.

    Applied to the closing edge only. The opening edge is the instant the state began and is exactly
    what should be measured.
    """
    spans = {}
    for i, r in enumerate(trans):
        state = r["to"]
        t_in = r["t"]
        if i + 1 < len(trans):
            t_out = max(t_in, trans[i + 1]["t"] - TRANSITION_GUARD_SEC)
        else:
            t_out = duration_hint
        spans.setdefault(state, (t_in, t_out))
    return spans


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out_dir")
    ap.add_argument("--params", default=None)
    args = ap.parse_args()

    out = Path(args.out_dir)
    log = out / "unity.log"
    frames_csv = out / "frames.csv"
    params_csv = Path(args.params) if args.params else out.parent / (out.name + "_params.csv")

    failures = []

    print("=" * 78)
    print("S68 §3 SELF-CHECK  --  {}".format(out))
    print("=" * 78)

    # ---- 1. transition order ----
    if not log.exists():
        print("FAIL  no unity.log at {}".format(log))
        return 1
    trans = parse_transitions(log)
    print("\n[1] STATE TRANSITIONS  ({} logged)".format(len(trans)))
    if not trans:
        print("  none -- the state machine never ran (env gate off? component not attached?)")
        failures.append("no transitions logged")
    for r in trans:
        print("  {:<12s} -> {:<12s}  t={:7.2f}  dist_robot={:6.2f}  ({})".format(
            r["from"], r["to"], r["t"], r["dist"], r["why"]))

    seen = [r["to"] for r in trans if r["to"] != "Frozen"]
    # Subsequence test, not equality: a re-freeze or an early timeout can legitimately add states,
    # but the six must appear in this order.
    it = iter(seen)
    ordered = all(s in it for s in EXPECTED_ORDER)
    print("  order {} expected {}".format("OK  --" if ordered else "MISMATCH --",
                                          " -> ".join(EXPECTED_ORDER)))
    if not ordered:
        failures.append("transition order does not contain the expected sequence")

    # ---- S68-D: how close was the robot when the pedestrian finished standing up? ----
    # The redesigned hold gets up as the robot bears down, so the stand-up is now a race. This
    # reports the margin it finished with. Flagged, never auto-tuned -- the knob is standUpDistance
    # and that is the user's call.
    print("\n[1b] STAND-UP MARGIN  (robot distance at CROUCH_EXIT -> LEAVE)")
    standup = [r for r in trans if r["from"] == "CrouchExit" and r["to"] == "Leave"]
    hold_exit = [r for r in trans if r["from"] == "CrouchHold"]
    if hold_exit:
        print("  CROUCH_HOLD exit reason: {}  (dist {:.2f} m)".format(
            hold_exit[0]["why"], hold_exit[0]["dist"]))
        if "TIMEOUT" in hold_exit[0]["why"]:
            failures.append("CROUCH_HOLD exited on TIMEOUT, not on the robot approaching")
    if not standup:
        print("  never completed a stand-up")
        failures.append("no CROUCH_EXIT -> LEAVE transition")
    else:
        m = standup[0]["dist"]
        print("  robot was {:.2f} m away when the pedestrian regained its feet".format(m))
        if m < 1.5:
            print("  NOTE: < 1.5 m -- recommend RAISING standUpDistance next round "
                  "(flagged only; not adjusted here)")
        else:
            print("  >= 1.5 m -- adequate margin")

    spans = phase_windows(trans)

    # ---- clock alignment, before any window is used ----
    frows = load_frames(frames_csv) if frames_csv.exists() else []
    frame_off = None
    if frows and trans:
        fit = align_frames_clock(frows, trans)
        if fit is not None:
            frame_off, resid, n = fit
            print("\n[0] CLOCK ALIGNMENT  frames.csv t + {:.2f} s = Time.time  "
                  "(fitted on {} transition anchors, mean |dist| residual {:.4f} m)".format(
                      frame_off, n, resid))
            if resid > 0.25:
                print("      residual is large -- treat every frames.csv window below as suspect")
                failures.append("frames.csv clock alignment residual {:.3f} m".format(resid))
            # Shift frames onto Time.time so every window below is in one clock.
            for r in frows:
                r["t"] += frame_off
        else:
            print("\n[0] CLOCK ALIGNMENT  FAILED -- frames.csv windows below are unreliable")
            failures.append("could not align frames.csv clock")

    # ---- 2. CROUCH_HOLD net displacement ----
    print("\n[2] CROUCH_HOLD NET DISPLACEMENT  (1 s windowed endpoint, probe coordinates)")
    if not params_csv.exists():
        print("  SKIP  no params CSV at {} (AUTOTRIAL_S54_PROBE not set?)".format(params_csv))
        failures.append("params CSV missing -- hold displacement unverified")
    elif "CrouchHold" not in spans:
        print("  SKIP  never entered CrouchHold")
    else:
        prows = load_params(params_csv)
        t0, t1 = spans["CrouchHold"]
        wins = windowed_endpoint_speeds(prows, t0, t1)
        if not wins:
            print("  SKIP  no probe samples inside [{:.2f}, {:.2f}]".format(t0, t1))
        else:
            worst = max(w[2] for w in wins)
            for a, b, d, v in wins:
                print("    [{:6.2f},{:6.2f}]  disp={:.4f} m   ({:.4f} m/s)".format(a, b, d, v))
            # A held kneel should not travel. 5 cm/s over a whole second is already visible slide.
            ok = worst <= 0.05
            print("  worst 1 s displacement = {:.4f} m  ->  {}".format(worst, "PASS" if ok else "FAIL"))
            if not ok:
                failures.append("CROUCH_HOLD slides: worst 1 s displacement {:.4f} m".format(worst))

    # ---- 3. LEAVE speed ----
    print("\n[3] LEAVE SPEED  (1 s windowed endpoint)")
    if "Leave" not in spans:
        print("  SKIP  never entered Leave")
        failures.append("never reached LEAVE")
    elif not frows:
        print("  SKIP  no frames.csv")
    else:
        t0, t1 = spans["Leave"]
        # Skip the first second: the agent is accelerating out of a standing start and its NavMesh
        # path is being replanned, so that window measures the transient, not the walking pace.
        wins = windowed_endpoint_speeds(frows, t0 + 1.0, t1)
        if not wins:
            print("  SKIP  no frames inside [{:.2f}, {:.2f}]".format(t0 + 1.0, t1))
        else:
            # Cut at ARRIVAL. LEAVE is the last state and its span therefore runs to the end of the
            # trial, but the pedestrian reaches its goal partway through and correctly stops there --
            # so averaging the whole span mixes a walking pace with a stationary tail and answers
            # neither question. (Measured: 14 windows at 1.03-1.09 m/s followed by 30 at 0.000,
            # which averaged to 0.338 and looked like a failure to walk.) Arrival is the first
            # sustained-zero window after at least one moving window.
            walking = []
            for w in wins:
                if w[3] < 0.10 and walking:
                    break
                if w[3] >= 0.10:
                    walking.append(w)
            for a, b, d, v in wins:
                mark = "" if any(w[0] == a for w in walking) else "   (post-arrival)"
                print("    [{:6.2f},{:6.2f}]  {:.3f} m/s{}".format(a, b, v, mark))
            if not walking:
                print("  never moved after LEAVE  ->  FAIL")
                failures.append("pedestrian never walked after LEAVE")
            else:
                speeds = [w[3] for w in walking]
                mean = sum(speeds) / len(speeds)
                arrived = walking[-1][1]
                ok = ZONE_A_SPEED_LO <= mean <= ZONE_A_SPEED_HI
                print("  walking mean = {:.3f} m/s over {} window(s) "
                      "(t={:.2f}..{:.2f}, arrived ~{:.2f}), Zone A band [{}, {}]  ->  {}".format(
                          mean, len(speeds), walking[0][0], arrived, arrived,
                          ZONE_A_SPEED_LO, ZONE_A_SPEED_HI, "PASS" if ok else "FAIL"))
                if not ok:
                    failures.append("LEAVE walking mean {:.3f} m/s outside Zone A band".format(mean))

    # ---- 4. ground contact during the crouch ----
    print("\n[4] GROUND CONTACT DURING CROUCH  (root y vs ground y)")
    crouch_states = [s for s in ("CrouchEnter", "CrouchHold", "CrouchExit") if s in spans]
    if not crouch_states or not frows:
        print("  SKIP  no crouch phase or no frames.csv")
    else:
        t0 = spans[crouch_states[0]][0]
        t1 = spans[crouch_states[-1]][1]
        seg = [r for r in frows if t0 <= r["t"] <= t1]
        if not seg:
            print("  SKIP  no frames inside [{:.2f}, {:.2f}]".format(t0, t1))
        else:
            deltas = [r["y"] - r["gy"] for r in seg]
            lo, hi = min(deltas), max(deltas)
            # The root of a standing avatar sits ON the ground, so this should hug zero. A
            # meaningfully negative value is the root under the terrain.
            ok = lo >= -0.05
            print("  root_y - ground_y over {} frames: min={:+.4f} max={:+.4f}  ->  {}".format(
                len(seg), lo, hi, "PASS" if ok else "FAIL"))
            if not ok:
                failures.append("root sinks {:.4f} m below ground during the crouch".format(lo))

            # Slide, restated on the capture stream rather than the probe, as an independent source.
            if "CrouchHold" in spans:
                h0, h1 = spans["CrouchHold"]
                hw = windowed_endpoint_speeds(frows, h0, h1)
                if hw:
                    print("  (frames.csv cross-check) worst 1 s hold displacement = {:.4f} m".format(
                        max(w[2] for w in hw)))

    print("\n" + "=" * 78)
    if failures:
        print("RESULT: {} CHECK(S) FAILED".format(len(failures)))
        for f in failures:
            print("  - " + f)
        return 1
    print("RESULT: ALL CHECKS PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
