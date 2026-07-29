#!/usr/bin/env python3
"""Session 62: INDEX.md for a verification batch, with the robot-side labels folded in.

    python3 tools/s62_make_index.py /mnt/ssd/.../s62_batch

Emits INDEX.md next to results.tsv. Two things it does that a plain results table does not:

**It checks the freeze log pairs, per trial.** Session 61 found that the Zone B branch attached its
PedestrianModulator after the freeze call, so the freeze was silently skipped on every Zone B agent
-- and dist0 read 7.997 anyway, because a slow agent does not drift far. "Never frozen" and "frozen
correctly" are the same number on the outcome metric. Only the paired FROZEN/RELEASED record
separates them, so an unpaired count is a hard stop, not a warning.

**R5 and eyeball are never filled here.** Same rule: a human writes them or they stay PENDING.

Nothing in this file rejects a trial. A robot that stops to let someone pass is behaving correctly
and is a sample the dataset wants -- the labels make behaviour visible, they do not filter it.
"""
import csv, math, os, re, subprocess, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from s62_robot_labels import robot_labels, md_row, HEADER, SEP  # noqa: E402

STATIC_CONFIGS = {"Sitting", "standing_arguing", "male_child", "female_child"}


def _grep_count(path, pattern):
    if not os.path.exists(path):
        return None
    n = 0
    for line in open(path, errors="replace"):
        if re.search(pattern, line):
            n += 1
    return n


def freeze_pairing(trial_dir):
    log = os.path.join(trial_dir, "unity.log")
    f = _grep_count(log, r"S59Freeze.*FROZEN")
    r = _grep_count(log, r"S59Freeze.*RELEASED")
    return f, r, (f is not None and f == r and f >= 1)


def clamp_hits(trial_dir):
    log = os.path.join(trial_dir, "unity.log")
    if not os.path.exists(log):
        return None
    hi = 0
    for line in open(log, errors="replace"):
        if "[S44Clamp]" in line:
            m = re.search(r"hiHits=(\d+)", line)
            if m:
                hi += int(m.group(1))
    return hi


def freeze_drift_and_speed(batch, name):
    """Pre-capture drift and walk-window speed, from the S54 probe's OWN coordinates.

    Never aligned against frames.csv -- those are two clocks, offset ~12 s, and mixing them is what
    once labelled a frozen-spawn segment as the walk window.
    """
    probe = os.path.join(batch, name + "_params.csv")
    frames = os.path.join(batch, name, "frames.csv")
    if not (os.path.exists(probe) and os.path.exists(frames)):
        return None, None, None
    rows = [r for r in csv.DictReader(open(probe)) if r.get("t")]
    fr = list(csv.DictReader(open(frames)))
    if not rows or not fr:
        return None, None, None
    cap = float(fr[-1]["t"]) - float(fr[0]["t"])
    t_cap0 = float(rows[-1]["t"]) - cap
    pre = [r for r in rows if float(r["t"]) < t_cap0]
    drift = None
    if pre:
        drift = math.hypot(float(pre[-1]["pos_x"]) - float(pre[0]["pos_x"]),
                           float(pre[-1]["pos_z"]) - float(pre[0]["pos_z"]))
    pts = [(float(r["t"]), float(r["pos_x"]), float(r["pos_z"])) for r in rows]
    seg = [(b[0], math.hypot(b[1] - a[1], b[2] - a[2])) for a, b in zip(pts, pts[1:])]
    tot = sum(d for _, d in seg)
    net = math.hypot(pts[-1][1] - pts[0][1], pts[-1][2] - pts[0][2])
    if tot < 0.5:
        return drift, net, None
    cum, lo, hi = 0.0, None, None
    for t, d in seg:
        cum += d
        if lo is None and cum >= 0.01 * tot:
            lo = t
        if hi is None and cum >= 0.99 * tot:
            hi = t
    ps = [p for p in pts if lo <= p[0] <= hi]
    out, t = [], lo
    while t < hi - 0.5:
        a = min(ps, key=lambda q: abs(q[0] - t))
        b = min(ps, key=lambda q: abs(q[0] - (t + 1.0)))
        if b[0] > a[0]:
            out.append(math.hypot(b[1] - a[1], b[2] - a[2]) / (b[0] - a[0]))
        t += 1.0
    return drift, net, out


def main():
    batch = sys.argv[1] if len(sys.argv) > 1 else "."
    tsv = os.path.join(batch, "results.tsv")
    rows = list(csv.DictReader(open(tsv), delimiter="\t")) if os.path.exists(tsv) else []
    rows = [r for r in rows if r.get("config") and os.path.isdir(os.path.join(batch, r["config"]))]

    L = []
    L.append("# %s — verification batch\n" % os.path.basename(os.path.normpath(batch)))
    L.append("**N=1 per configuration. This is an eyeball-validation batch, not a safety census.** "
             "`min_dist` is recorded because the pipeline emits it and must never be quoted as a "
             "safety result — run-to-run spread on identical commands was measured at up to 1.4 m.\n")
    L.append("All windowed statistics use 1 s endpoint displacement over the probe's own "
             "coordinates. `eyeball` and `R5` are filled by a human or stay PENDING.\n")

    L.append("\n## Trials\n")
    L.append("| config | exit | dist0 | min_dist | freeze FROZEN/RELEASED | clampHi | eyeball |")
    L.append("|---|---|---|---|---|---|---|")
    problems = []
    for r in rows:
        d = os.path.join(batch, r["config"])
        f, rel, ok = freeze_pairing(d)
        ch = clamp_hits(d)
        if not ok:
            problems.append("freeze log not paired on `%s` (FROZEN=%s RELEASED=%s)" % (r["config"], f, rel))
        if ch:
            problems.append("maxSpeedScale engaged %d times on `%s`" % (ch, r["config"]))
        L.append("| `%s` | %s | %s | %s | %s/%s%s | %s | PENDING |" % (
            r["config"], r.get("exit", "?"), r.get("dist0", "?"), r.get("min_dist", "?"),
            f, rel, "" if ok else " **UNPAIRED**", ch if ch is not None else "?"))

    L.append("\n## Robot-side labels\n")
    L.append("**Labels, not filters.** A robot that stops to let someone pass is behaving correctly. "
             "R3 is three columns because each alone has a blind spot: R3a (fraction of frames below "
             "0.05 m/s) catches dense short stalls, R3b (longest continuous run) catches a single long "
             "one, R3c (that stall's offset from the closest approach) separates yielding from "
             "failure. `scooter_user`'s R3b is 0.66 s against a healthy 0.33 s while its R3a is 60% "
             "against 1% — the original single-column definition could not detect the configuration "
             "the criterion was written for.\n")
    L.append(HEADER)
    L.append(SEP)
    for r in rows:
        L.append(md_row(robot_labels(os.path.join(batch, r["config"]))))

    L.append("\n## Freeze gate and pedestrian motion\n")
    L.append("| config | pre-capture drift | net displacement | walk-window 1 s windows |")
    L.append("|---|---|---|---|")
    for r in rows:
        drift, net, win = freeze_drift_and_speed(batch, r["config"])
        if drift is None:
            L.append("| `%s` | (no probe) | | |" % r["config"])
            continue
        if drift > 0.05:
            problems.append("pre-capture drift %.3f m on `%s` (expected 0.000)" % (drift, r["config"]))
        if r["config"] in STATIC_CONFIGS and net is not None and net >= 0.2:
            problems.append("static config `%s` moved %.3f m" % (r["config"], net))
        w = " ".join("%.2f" % v for v in (win or [])[:12]) or "—"
        L.append("| `%s` | %.4f m | %.3f m | %s |" % (r["config"], drift, net, w))

    L.append("\n## Objective self-check\n")
    if problems:
        L.append("**FAIL — %d item(s):**\n" % len(problems))
        for p in problems:
            L.append("- %s" % p)
        L.append("\nAn unpaired freeze log is a hard stop: `dist0` cannot distinguish "
                 "\"never frozen\" from \"frozen correctly\" on a slow agent.")
    else:
        L.append("**PASS** — freeze logs paired on every trial, no `maxSpeedScale` engagements, "
                 "pre-capture drift 0.000 m throughout, static configurations below 0.2 m.")

    out = os.path.join(batch, "INDEX.md")
    open(out, "w").write("\n".join(L) + "\n")
    print("\n".join(L))
    print("\nwrote %s" % out)
    if problems:
        sys.exit(1)


if __name__ == "__main__":
    main()
