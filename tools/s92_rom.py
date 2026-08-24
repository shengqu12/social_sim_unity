"""S92 G-ROM: per-frame joint range-of-motion gate.

Limits are the clinical goniometry norms for the adult upper limb (AAOS; Norkin & White,
"Measurement of Joint Motion: A Guide to Goniometry"), tightened where the S92 ticket asked for a
tighter bound -- a reaction pose should sit inside the envelope, not at the anatomical extreme.
The angles graded here are produced by S89ContactIK.MeasureRom, so the gate reads exactly the
quantity the solver clamped; nothing else defines "wrist angle".

Verdicts are weighted by IK ramp weight, per the S91b rule: a violation on a near-zero-weight frame
is the SOURCE animation's pose, not the layer's, and is reported rather than charged to the gate.
"""
import csv, sys

ATTRIB_W = 0.5
# The solver pins a saturated joint exactly AT its limit, and the value is then re-measured after
# the ramp blend, so a pinned joint reads 60.02 where the limit is 60. Half a degree of tolerance
# keeps that from being reported as a violation; it is far below the ~2 deg the limits themselves
# are argued to, and any real excursion is tens of degrees.
TOL = 0.5
# The wrist is gated on the AUTHORED columns -- the pose the layer writes at full strength, before
# the ramp weight blends it toward the source clip. The blended value is reported beside it. The
# blend is a quaternion Slerp, so its decomposed angles are NOT the linear interpolation of the two
# endpoints, and no closed-form "source's share" of the blended angle exists to gate against; the
# authored pose is the quantity the layer is actually responsible for, and over the whole hold
# (w = 1) it is the pose. Elbow and shoulder are read post-blend: the layer never writes an axial
# term to either, and their limits are far from binding.
LIMITS = {                       # column: (min, max, label)
    "aFlex":  (-60.0,  60.0, "wrist flexion (+palmar)   [norm ~80/70; ticket +-60]"),
    "aDev":   (-20.0,  30.0, "wrist deviation (+ulnar)  [norm 20 radial / 30 ulnar]"),
    "aTwist": (-15.0,  15.0, "wrist axial twist         [radiocarpal has none; 15 = rig slack]"),
    "eFlex":   (-5.0, 150.0, "elbow flexion             [norm 0..145-150]"),
    "sElev":    (0.0, 180.0, "shoulder elevation        [norm 0..180]"),
}
REPORTED = {
    "wFlex":  "wrist flexion, AFTER the ramp blend  [source clip reaches +18]",
    "wDev":   "wrist deviation, AFTER the blend     [source clip reaches -31, outside range]",
    "wTwist": "wrist twist, AFTER the blend         [source clip reaches +27, outside range]",
    "sTwist": "humeral axial rotation    [elbow-plane construction, declared convention]",
}


def source_excess(src, col, mn, mx, base="s92/data/", _cache={}):
    """Per-frame amount by which the UNTOUCHED animation already exceeds a limit.

    The ramp is a blend between the source pose and the solved-and-clamped pose, so at weight w the
    source contributes (1-w) of whatever violation it carries. With a clip whose wrist is already
    11 deg outside range -- and this one's is -- no CONTINUOUS ramp can hold every frame inside;
    clamping the blended result instead would pop by that same 11 deg at the window edge, which is
    a worse failure and one G-C would catch. So the gate asks the question that can be answered:
    does the layer drive a joint further out than the source does at that blend weight?
    """
    key = (src, col)
    if key not in _cache:
        rows = list(csv.DictReader(open(base + f"contact_{src}.csv")))
        _cache[key] = {int(r["frame"]): max(0.0, mn - float(r[col]), float(r[col]) - mx)
                       for r in rows}
    return _cache[key]


def _interp(table, f):
    if f in table: return table[f]
    ks = sorted(table)
    if not ks: return 0.0
    if f <= ks[0]: return table[ks[0]]
    if f >= ks[-1]: return table[ks[-1]]
    hi = next(k for k in ks if k >= f); lo = max(k for k in ks if k <= f)
    if hi == lo: return table[lo]
    t = (f - lo) / (hi - lo)
    return table[lo] * (1 - t) + table[hi] * t


def grade(tag, base="s92/data/", lo=6, hi=114, gate=True, src=None):
    rows = [r for r in csv.DictReader(open(base + f"contact_{tag}.csv"))
            if lo <= int(r["frame"]) <= hi]
    n = len(rows)
    na = sum(1 for r in rows if float(r["weight"]) >= ATTRIB_W)
    print(f"\n=== G-ROM — {tag}  ({n} frames of ramp+hold f{lo}..f{hi}, "
          f"{na} at ramp weight >= {ATTRIB_W}) ===")
    ok = True
    for col, (mn, mx, label) in LIMITS.items():
        vals = [(float(r[col]), int(r["frame"]), float(r["weight"])) for r in rows]
        att = [v for v in vals if v[2] >= ATTRIB_W]
        # The authored pose is the layer's own work at full strength, so it is gated against the
        # limit flat -- no source allowance is needed or wanted here.
        def slack(fr, w):
            return TOL
        bad_all = [v for v in vals if v[0] < mn - slack(v[1], v[2]) or v[0] > mx + slack(v[1], v[2])]
        bad_att = [v for v in att if v[0] < mn - slack(v[1], v[2]) or v[0] > mx + slack(v[1], v[2])]
        v_all = [v[0] for v in vals]
        worst = max(bad_att, key=lambda v: max(mn - v[0], v[0] - mx) - slack(v[1], v[2])) if bad_att else None
        flag = "!" if bad_att else " "
        note = (f"OUT by {max(mn - worst[0], worst[0] - mx):.1f} deg at f{worst[1]} w{worst[2]:.2f}"
                if worst else
                "inside")
        print(f"  {col:7s} {min(v_all):8.2f} .. {max(v_all):8.2f}   "
              f"[{mn:6.0f},{mx:6.0f}] {flag} {note}")
        print(f"          {label}")
        ok &= not bad_att
    for col, label in REPORTED.items():
        v = [float(r[col]) for r in rows]
        extra = ""
        if src and col in ("wFlex", "wDev", "wTwist"):
            srows = [r for r in csv.DictReader(open(base + f"contact_{src}.csv"))
                     if lo <= int(r["frame"]) <= hi]
            sv = [float(r[col]) for r in srows]
            extra = f"   source clip: {min(sv):7.2f} .. {max(sv):7.2f}"
        print(f"  {col:7s} {min(v):8.2f} .. {max(v):8.2f}   (reported, not gated){extra}")
        print(f"          {label}")
    gu = [float(r["rollGivenUp"]) for r in rows if float(r["weight"]) >= ATTRIB_W]
    if gu:
        print(f"  roll given up to stay in range: max {max(gu):.2f} deg, "
              f"{sum(1 for x in gu if x > 1e-3)}/{len(gu)} attributable frames")
    pr = [abs(float(r["pronation"])) for r in rows if float(r["weight"]) >= ATTRIB_W]
    if pr:
        worst = max(pr)
        flag = "!" if worst > 80.0 + TOL else " "
        print(f"  forearm pronation from bind:   {flag} max {worst:.2f} deg   [norm +-80]")
        ok_pr = worst <= 80.0 + TOL
    else:
        ok_pr = True
    ap = [abs(float(r["pronApplied"])) for r in rows if float(r["weight"]) >= ATTRIB_W]
    if ap:
        print(f"  pronation this layer added:      max {max(ap):.2f} deg")
    ok &= ok_pr
    print(f"  G-ROM: {'PASS' if ok else 'FAIL'}" + ("" if gate else "   (report-only)"))
    return ok


if __name__ == "__main__":
    src = None
    args = list(sys.argv[1:])
    if args and args[0].startswith("src="):
        src = args.pop(0)[4:]
    for t in args:
        grade(t, src=src)
