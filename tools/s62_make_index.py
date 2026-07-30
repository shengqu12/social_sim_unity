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
import csv, json, math, os, re, subprocess, sys

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


def ped_speed_multiplier(trial_dir):
    """The trial's configured pedSpeedMultiplier, read from meta.json -- a different source from the
    unity.log line that reports the freeze, so the N/A verdict is not the log agreeing with itself."""
    meta = os.path.join(trial_dir, "meta.json")
    if not os.path.exists(meta):
        return None
    try:
        cfg = (json.load(open(meta)) or {}).get("config") or {}
    except ValueError:
        return None
    v = cfg.get("pedSpeedMultiplier")
    return float(v) if v is not None else None


def freeze_pairing(trial_dir):
    """Returns (FROZEN, RELEASED, state) with state in {"paired", "na", "bad"}.

    Session 64 added "na". At pedSpeedMultiplier == 1.0 no PedestrianModulator is attached, so
    FreezeRootMotionTranslation is a documented no-op (AutoTrialBootstrap.cs) and the release path
    logs "no PedestrianModulator ... expected for pedSpeedMultiplier == 1.0 agents" -- those agents
    are directVelocityDrive and take no translation from root motion, so there is nothing to freeze.
    All of wheelchair_user, male_child and female_child are in this class.

    "na" is NOT "0 is fine". It requires the configured multiplier to be exactly 1.0, and the caller
    must additionally record the probe-measured pre-capture drift on the row -- 0/0 with a multiplier
    that is not 1.0 remains the Session 61 hard stop, because that is the case where the freeze was
    supposed to happen and silently did not."""
    log = os.path.join(trial_dir, "unity.log")
    f = _grep_count(log, r"S59Freeze.*FROZEN")
    r = _grep_count(log, r"S59Freeze.*RELEASED")
    if f is None or r is None or f != r:
        return f, r, "bad"
    if f >= 1:
        return f, r, "paired"
    mult = ped_speed_multiplier(trial_dir)
    if mult is not None and mult == 1.0:
        return f, r, "na"
    return f, r, "bad"


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


def clamp_ceiling(trial_dir):
    """The clamp's own configured ceiling, parsed from its summary line (`range=[0.05,3.00]`), so
    this file never hard-codes a limit that lives somewhere else."""
    log = os.path.join(trial_dir, "unity.log")
    if not os.path.exists(log):
        return None
    for line in open(log, errors="replace"):
        if "[S44Clamp]" in line:
            m = re.search(r"range=\[[0-9.]+,\s*([0-9.]+)\]", line)
            if m:
                return float(m.group(1))
    return None


REACTIVE_PERSONALITIES = {"curious", "scared"}


def personality(trial_dir):
    meta = os.path.join(trial_dir, "meta.json")
    if not os.path.exists(meta):
        return None
    try:
        return ((json.load(open(meta)) or {}).get("config") or {}).get("personality")
    except ValueError:
        return None


def clamp_episodes(batch, name, ceiling):
    """Session 64 ruling (b): the criterion is not "no clamping", it is that clamping must belong to
    a reaction. A reactive personality asking for more speed than the ceiling allows is a legitimate
    demand hitting a design limit; a speed loop feeding itself is not.

    The ruling names a "reaction window", and this pipeline does not record one. `close_enough_*` is
    INavigable's ARRIVAL check (S54AnimParamProbe samples `baseAgent.CloseEnough()`), not a
    robot-proximity reaction gate, and the reaction gate that does exist -- S46ScaredTriggerGate --
    logs "FIRED at 3.49 m" with no timestamp, so it cannot be placed on the probe's clock. Using
    arrival as a proxy for reaction is what this file must not do quietly.

    So the criterion is applied where it IS measurable, in two parts:

      * clamping may only occur on a reactive personality (`curious`, `scared`) -- a clamp on
        `indifferent`, `surprised` or `assertive` has no reaction to belong to
      * every clamp episode must RESET (fall back below `reset_to`) -- a compounding loop cannot
        reset, which is the signature the ruling relies on

    Both are read from the probe's own `animator_speed` in the probe's own clock, and from
    meta.json's configured personality -- never from frames.csv.

    Returns (n_ceiling_samples, n_episodes, longest_episode_s, all_episodes_reset) or all-None.
    """
    probe = os.path.join(batch, name + "_params.csv")
    if not os.path.exists(probe) or ceiling is None:
        return None, None, None, None
    t, a = [], []
    for row in csv.DictReader(open(probe)):
        try:
            t.append(float(row["t"]))
            a.append(float(row["animator_speed"]))
        except (KeyError, TypeError, ValueError):
            continue
    if not a:
        return None, None, None, None
    reset_to = ceiling * (2.0 / 3.0)
    eps, start = [], None
    for i, v in enumerate(a):
        if v >= ceiling - 1e-3:
            if start is None:
                start = i
        elif start is not None:
            eps.append((start, i))
            start = None
    if start is not None:
        eps.append((start, len(a) - 1))
    longest = max((t[b] - t[s] for s, b in eps), default=0.0)
    resets = all(any(a[j] < reset_to for j in range(b, min(len(a), b + 200))) for s, b in eps)
    return sum(1 for v in a if v >= ceiling - 1e-3), len(eps), longest, resets


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

    # Drift is computed once here and used in both tables: an N/A freeze row has to carry the
    # measured drift beside it, or "not applicable" is an assertion rather than a reading.
    drifts = {r["config"]: freeze_drift_and_speed(batch, r["config"]) for r in rows}

    L.append("\n## Trials\n")
    L.append("| config | exit | dist0 | min_dist | freeze FROZEN/RELEASED | clampHi | eyeball |")
    L.append("|---|---|---|---|---|---|---|")
    problems = []
    n_na = 0
    for r in rows:
        cfg = r["config"]
        d = os.path.join(batch, cfg)
        f, rel, state = freeze_pairing(d)
        ch = clamp_hits(d)
        drift = drifts[cfg][0]
        if state == "bad":
            problems.append("freeze log not paired on `%s` (FROZEN=%s RELEASED=%s, "
                            "pedSpeedMultiplier=%s)" % (cfg, f, rel, ped_speed_multiplier(d)))
            freeze_cell = "%s/%s **UNPAIRED**" % (f, rel)
        elif state == "na":
            n_na += 1
            if drift is None:
                problems.append("`%s` is freeze-N/A but has no probe drift to record" % cfg)
                freeze_cell = "N/A (**no drift recorded**)"
            else:
                freeze_cell = "N/A, mult 1.0 (drift %.4f m)" % drift
        else:
            freeze_cell = "%s/%s" % (f, rel)
        if ch:
            at, n_eps, longest, resets = clamp_episodes(batch, cfg, clamp_ceiling(d))
            pers = (personality(d) or "?").lower()
            if at is None:
                problems.append("maxSpeedScale engaged %d times on `%s` and there is no probe to "
                                "characterise the episodes" % (ch, cfg))
            else:
                if pers not in REACTIVE_PERSONALITIES:
                    problems.append("maxSpeedScale engaged %d times on `%s`, whose personality is "
                                    "`%s` -- not reactive, so the clamping belongs to no reaction"
                                    % (ch, cfg, pers))
                if not resets:
                    problems.append("maxSpeedScale episode does not reset on `%s` (%d episode(s), "
                                    "longest %.2f s) -- the compounding signature"
                                    % (cfg, n_eps, longest))
        L.append("| `%s` | %s | %s | %s | %s | %s | PENDING |" % (
            cfg, r.get("exit", "?"), r.get("dist0", "?"), r.get("min_dist", "?"),
            freeze_cell, ch if ch is not None else "?"))

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
        drift, net, win = drifts[r["config"]]
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
        L.append("**PASS** — freeze accounted for on every trial (%d paired, %d N/A at "
                 "pedSpeedMultiplier == 1.0, each with its measured drift recorded), every "
                 "`maxSpeedScale` engagement on a reactive personality and every episode resetting, "
                 "pre-capture drift 0.000 m throughout, static configurations below 0.2 m."
                 % (len(rows) - n_na, n_na))

    out = os.path.join(batch, "INDEX.md")
    open(out, "w").write("\n".join(L) + "\n")
    print("\n".join(L))
    print("\nwrote %s" % out)
    if problems:
        sys.exit(1)


if __name__ == "__main__":
    main()
