#!/usr/bin/env python3
"""Session 64: proof that the two relaxed gates in s62_make_index.py can still fail.

    python3 tools/s64_gate_selftest.py

Session 64 widened two hard stops:

  * the freeze gate gained an N/A state for pedSpeedMultiplier == 1.0 agents
  * maxSpeedScale stopped being "must be zero" and became "must belong to a reaction": only on a
    reactive personality, and every episode must reset

A widened criterion is worth exactly as much as its ability to still catch the case it was written
for, so this builds synthetic trials on disk and asserts both directions: the case the ruling
excuses must pass, and the case the ruling was careful NOT to excuse must still be a hard stop.

  A  0/0 freeze, multiplier 1.0            -> N/A, no problem        (the ruling)
  B  0/0 freeze, multiplier 1.21           -> hard stop              (the Session 61 failure mode)
  C  clamped, reactive personality, resets -> no problem             (the ruling)
  D  clamped, non-reactive personality     -> hard stop              (no reaction to belong to)
  E  clamped and pinned at the ceiling     -> hard stop              (the compounding signature)
  F  N/A freeze with no probe drift        -> hard stop              ("record the drift" enforced)

Run from the repo root. Writes only under a temporary directory.
"""
import json, os, shutil, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))

FRAMES = ("t,robot_x,robot_z,robot_speed_ground,dist_to_pedestrian\n"
          + "".join("%.3f,%.3f,-109.0,0.500,%.3f\n" % (i * 0.1, -5.0 - i * 0.05, 8.0 - i * 0.05)
                    for i in range(120)))


def probe(n_ceiling, resets, ceiling=3.0):
    """A probe whose animator_speed reaches `ceiling` on n_ceiling samples in the middle of the run.
    With resets=True it falls back to 1.0 afterwards; with resets=False it stays pinned at the
    ceiling to the end -- the compounding signature a broken speed loop would leave."""
    out = ["t,animator_speed,close_enough_latched,pos_x,pos_z"]
    first = 200
    for i in range(400):
        if i < first:
            v = 1.0
        elif i < first + n_ceiling:
            v = ceiling
        else:
            v = 1.0 if resets else ceiling
        out.append("%.3f,%.3f,0,-8.777,-109.339" % (i * 0.05, v))
    return "\n".join(out) + "\n"


def make_trial(batch, name, frozen, mult, hi_hits, n_ceiling, resets, personality="Curious",
               with_probe=True):
    d = os.path.join(batch, name)
    os.makedirs(d, exist_ok=True)
    log = []
    if frozen:
        log.append("[S59Freeze] root-motion translation FROZEN on 'X(Clone)'")
        log.append("[S59Freeze] root-motion translation RELEASED on 'X(Clone)'")
    else:
        log.append("[S59Freeze] no PedestrianModulator on 'X(Clone)' -- nothing to release "
                   "(expected for pedSpeedMultiplier == 1.0 agents)")
    if hi_hits:
        log.append("[S44Clamp] agent=X(Clone) ref=1.3000 frames=400 loHits=0 (0.0 %%) "
                   "hiHits=%d (1.0 %%) range=[0.05,3.00]" % hi_hits)
    open(os.path.join(d, "unity.log"), "w").write("\n".join(log) + "\n")
    open(os.path.join(d, "frames.csv"), "w").write(FRAMES)
    open(os.path.join(d, "meta.json"), "w").write(json.dumps(
        {"terminationReason": "duration",
         "config": {"pedSpeedMultiplier": mult, "personality": personality}}))
    if with_probe:
        open(os.path.join(batch, name + "_params.csv"), "w").write(probe(n_ceiling, resets))


def run_case(label, expect_problem, **kw):
    batch = tempfile.mkdtemp(prefix="s64gate_")
    try:
        open(os.path.join(batch, "results.tsv"), "w").write(
            "config\texit\tdist0\tmin_dist\nT\t0\t7.998\t0.700\n")
        make_trial(batch, "T", **kw)
        p = subprocess.run([sys.executable, os.path.join(HERE, "s62_make_index.py"), batch],
                           capture_output=True, text=True)
        got_problem = p.returncode != 0
        ok = got_problem == expect_problem
        print("%-58s expected %-11s got %-11s %s" % (
            label, "HARD STOP" if expect_problem else "pass",
            "HARD STOP" if got_problem else "pass", "OK" if ok else "*** FAIL ***"))
        if not ok:
            tail = [l for l in p.stdout.splitlines() if l.startswith("- ")]
            print("    problems reported:", tail or "(none)")
        return ok
    finally:
        shutil.rmtree(batch, ignore_errors=True)


def main():
    results = [
        run_case("A  0/0 freeze, multiplier 1.0 -> N/A", False,
                 frozen=False, mult=1.0, hi_hits=0, n_ceiling=0, resets=True),
        run_case("B  0/0 freeze, multiplier 1.21 -> still the S61 hard stop", True,
                 frozen=False, mult=1.21, hi_hits=0, n_ceiling=0, resets=True),
        run_case("C  clamped on a reactive personality, episode resets", False,
                 frozen=True, mult=1.21, hi_hits=6, n_ceiling=6, resets=True,
                 personality="Curious"),
        run_case("D  clamped on a NON-reactive personality", True,
                 frozen=True, mult=1.21, hi_hits=6, n_ceiling=6, resets=True,
                 personality="Indifferent"),
        run_case("E  clamped and PINNED at the ceiling -- compounding signature", True,
                 frozen=True, mult=1.21, hi_hits=6, n_ceiling=6, resets=False,
                 personality="Curious"),
        run_case("F  0/0 freeze, multiplier 1.0, no probe -> drift unrecordable", True,
                 frozen=False, mult=1.0, hi_hits=0, n_ceiling=0, resets=True, with_probe=False),
    ]
    print()
    if all(results):
        print("ALL %d CASES OK -- both widened gates still fail on what they were written for."
              % len(results))
    else:
        print("*** %d of %d CASES FAILED ***" % (sum(1 for r in results if not r), len(results)))
        sys.exit(1)


if __name__ == "__main__":
    main()
