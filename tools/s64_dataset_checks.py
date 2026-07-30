#!/usr/bin/env python3
"""Session 64: the three post-run questions for the plan D dataset, answered separately.

    python3 tools/s64_dataset_checks.py <dataset_dir>

Writes <dataset_dir>/CHECKS.md and prints it.

  1. what fraction of trials have min_dist < 0.5 m           criterion >= 10%
  2. is there a "never moves at all" configuration           on R2/R3a/R3b/R3c/R4, never on R1
  3. how many times maxSpeedScale engaged                    must belong to a reaction (S64 (b))

Two rules this file obeys and does not quietly relax:

**R1 is not used as a discriminant.** Its "reached goal" half is constant `no` across this dataset --
the goal is 43.6 m away and 60 s at the achievable speeds does not cover it -- so it carries no
information. Question 2 is answered from robot net displacement, R2, R3a/R3b/R3c and R4.

**Nothing here filters.** Trials that failed a pipeline gate (`exit != 0`) are counted in every
statistic below, and a stalled robot is reported, not removed. `maxSpeedScale` counting reuses
`s62_make_index.clamp_hits` rather than re-implementing the same grep, so the two files cannot drift.
"""
import collections, csv, math, os, re, statistics, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from s62_robot_labels import robot_labels  # noqa: E402
from s62_make_index import (clamp_hits, clamp_ceiling, clamp_episodes, personality,  # noqa: E402
                            REACTIVE_PERSONALITIES)

CLOSE_M = 0.5
CLOSE_CRITERION = 0.10
# "Never moves at all": net displacement over a 60 s trial small enough that no navigation happened.
NEVER_MOVED_NET_M = 1.0


def family(config):
    """`A1_male_adult_01_curious_r3` -> `A1_male_adult_01_curious`. Repeats collapse; the block
    prefix stays, because A2_dog_walker and A3_old_man are different things."""
    return re.sub(r"_r\d+$", "", config)


def main():
    batch = sys.argv[1]
    rows = [r for r in csv.DictReader(open(os.path.join(batch, "results.tsv")), delimiter="\t")
            if r.get("config") and r["config"] != "DATASET_PLAND COMPLETE"]
    skipped = []
    sk = os.path.join(batch, "skipped.tsv")
    if os.path.exists(sk):
        skipped = [r for r in csv.DictReader(open(sk), delimiter="\t") if r.get("config")]

    L = ["# Plan D dataset — the three post-run checks\n",
         "%d trials in `results.tsv`, %d in `skipped.tsv`. Every trial is counted here, including "
         "the %d with a non-zero `exit`: a failed pipeline gate is a label, not a rejection.\n"
         % (len(rows), len(skipped), sum(1 for r in rows if r["exit"] != "0"))]

    # ---- 1. min_dist tail -------------------------------------------------------------------
    md = [(r["config"], r.get("block") or "-", float(r["min_dist"]))
          for r in rows if r["min_dist"] not in ("NA", "", None)]
    close = [(c, b, x) for c, b, x in md if x < CLOSE_M]
    frac = len(close) / len(md) if md else 0.0
    verdict = "PASS" if frac >= CLOSE_CRITERION else "BELOW CRITERION"
    L.append("\n## 1. Fraction of trials with `min_dist` < %.1f m\n" % CLOSE_M)
    L.append("**%d / %d = %.1f%% — %s** (criterion >= %.0f%%).\n"
             % (len(close), len(md), 100 * frac, verdict, 100 * CLOSE_CRITERION))
    if frac < CLOSE_CRITERION:
        L.append("Below the criterion means the negative end is empty and needs 10-20 supplementary "
                 "narrow-corridor trials. The criterion is not adjusted to fit the result.\n")
    per_block = collections.defaultdict(lambda: [0, 0])
    for c, b, x in md:
        per_block[b][0] += 1
        if x < CLOSE_M:
            per_block[b][1] += 1
    L.append("| block | n | < %.1f m | share |" % CLOSE_M)
    L.append("|---|---|---|---|")
    for b, (n, c) in sorted(per_block.items()):
        L.append("| %s | %d | %d | %.0f%% |" % (b, n, c, 100 * c / n))
    L.append("\nWhere the negatives come from, by configuration (families with at least one):\n")
    fam_close = collections.defaultdict(lambda: [0, 0])
    for c, b, x in md:
        fam_close[family(c)][0] += 1
        if x < CLOSE_M:
            fam_close[family(c)][1] += 1
    L.append("| configuration | trials | < %.1f m |" % CLOSE_M)
    L.append("|---|---|---|")
    for f, (n, c) in sorted(fam_close.items(), key=lambda kv: (-kv[1][1], kv[0])):
        if c:
            L.append("| `%s` | %d | %d |" % (f, n, c))
    L.append("\n`min_dist` is a distribution property of the dataset here, not a safety result: "
             "N=3 (A1) or N=5 (A2/A3) per configuration, and run-to-run spread on identical commands "
             "has been measured at up to 1.4 m.\n")

    # ---- 2. never-moving configurations -----------------------------------------------------
    labels = {}
    for r in rows:
        d = os.path.join(batch, r["config"])
        if os.path.isdir(d):
            labels[r["config"]] = robot_labels(d)
    fam = collections.defaultdict(list)
    for cfg, lab in labels.items():
        if not lab.get("error"):
            fam[family(cfg)].append(lab)

    def med(vals):
        vals = [v for v in vals if v is not None]
        return statistics.median(vals) if vals else None

    agg = []
    for f, ls in fam.items():
        agg.append({
            "family": f, "n": len(ls),
            "net": med([l["R4_net_m"] for l in ls]),
            "r2": med([l["R2_mean_mps"] for l in ls]),
            "r2e": med([l["R2_mean_encounter_mps"] for l in ls]),
            "r3a": med([l["R3_frac_below_stall"] for l in ls]),
            "r3b": med([l["R3_longest_stall_s"] for l in ls]),
            "r3c": med([l.get("R3_starts_after_encounter_s") for l in ls]),
            "r4": med([l["R4_detour_ratio"] for l in ls]),
        })
    never = [a for a in agg if a["net"] is not None and a["net"] < NEVER_MOVED_NET_M]

    L.append("\n## 2. Is there a configuration where the robot never moves?\n")
    L.append("Answered on **R2 / R3a / R3b / R3c / R4 and net displacement**. R1's \"reached goal\" "
             "half is constant `no` across the whole dataset (goal 43.6 m, unreachable in 60 s at "
             "the achievable speeds) and carries no information, so it is not used here.\n")
    L.append("Criterion for \"never moved\": median robot net displacement below %.1f m over a 60 s "
             "trial.\n" % NEVER_MOVED_NET_M)
    if never:
        L.append("**%d configuration(s) meet it:**\n" % len(never))
        for a in sorted(never, key=lambda a: a["net"]):
            L.append("- `%s` — net %.2f m, R2 %.3f m/s, R3a %.0f%%" %
                     (a["family"], a["net"], a["r2"], 100 * a["r3a"]))
    else:
        L.append("**None.** Every configuration's median net displacement is at or above %.1f m.\n"
                 % NEVER_MOVED_NET_M)
    L.append("\nThe ten configurations with the most robot stalling, by R3a (median over repeats). "
             "R3c is what separates yielding from failure: a stall that begins well *after* the "
             "closest approach is not a yield.\n")
    L.append("| configuration | n | net disp | R2 all (enc) | R3a | R3b | R3c | R4 |")
    L.append("|---|---|---|---|---|---|---|---|")
    for a in sorted(agg, key=lambda a: -(a["r3a"] or 0))[:10]:
        L.append("| `%s` | %d | %.2f m | %.3f (%s) | %.0f%% | %.2f s | %s | %s |" % (
            a["family"], a["n"], a["net"], a["r2"],
            "%.3f" % a["r2e"] if a["r2e"] is not None else "-",
            100 * a["r3a"], a["r3b"],
            "%+.1f s" % a["r3c"] if a["r3c"] is not None else "-",
            "%.3f" % a["r4"] if a["r4"] is not None else "-"))
    L.append("\nHigh R3a with a small positive R3c is a robot that stopped near the closest approach "
             "-- yielding, and a sample this dataset wants. High R3a with a large positive R3c is a "
             "robot that stopped and stayed stopped after the encounter was over.\n")

    # ---- 3. maxSpeedScale ------------------------------------------------------------------
    hits, bad = {}, []
    for r in rows:
        cfg = r["config"]
        d = os.path.join(batch, cfg)
        if not os.path.isdir(d):
            continue
        h = clamp_hits(d)
        if not h:
            continue
        hits[cfg] = h
        at, n_eps, longest, resets = clamp_episodes(batch, cfg, clamp_ceiling(d))
        pers = (personality(d) or "?").lower()
        if at is None or pers not in REACTIVE_PERSONALITIES or not resets:
            bad.append((cfg, h, pers, resets))
    total = sum(hits.values())
    L.append("\n## 3. `maxSpeedScale` engagements\n")
    L.append("Session 64 ruling (b): the criterion is no longer \"zero\". Clamping must belong to a "
             "reaction — it may only occur on a reactive personality (`curious`, `scared`), and "
             "every episode must reset, because a compounding speed loop cannot reset. Neither "
             "`maxSpeedScale` (3.0) nor the reset requirement was relaxed to fit the data.\n")
    L.append("**%d engagements across %d of %d trials, %d of them outside the criterion — %s.**\n"
             % (total, len(hits), len(rows), len(bad), "PASS" if not bad else "FAIL"))
    if bad:
        for c, h, pers, resets in bad:
            L.append("- `%s` — %d engagements, personality `%s`, episodes reset: %s" %
                     (c, h, pers, resets))
    if hits:
        L.append("\n| trial | engagements | personality | episodes | longest | all reset |")
        L.append("|---|---|---|---|---|---|")
        for c, h in sorted(hits.items(), key=lambda kv: -kv[1]):
            at, n_eps, longest, resets = clamp_episodes(batch, c, clamp_ceiling(os.path.join(batch, c)))
            L.append("| `%s` | %d | %s | %s | %.2f s | %s |" % (
                c, h, (personality(os.path.join(batch, c)) or "?").lower(),
                n_eps, longest or 0.0, resets))
        L.append("\nThe `animator_speed` column the episode shape is read from is the clamp's own "
                 "OUTPUT, so it agrees with the `[S44Clamp]` summary by construction — that "
                 "agreement is a consistency check, not independent corroboration.\n")

    out = os.path.join(batch, "CHECKS.md")
    open(out, "w").write("\n".join(L) + "\n")
    print("\n".join(L))
    print("\nwrote %s" % out)


if __name__ == "__main__":
    main()
