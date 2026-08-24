"""S91 GATE A: full self-intersection audit.

Every IK-modified segment (upper arm, forearm, hand) against every body volume (head, neck, torso),
as capsules, every frame of the hold and both ramps. Clearance = segment-segment distance minus the
two radii; negative is penetration. This replaces the per-symptom rulers of S87-S90, each of which
checked one named pair and missed the one that mattered.
"""
import csv, sys
import numpy as np

ARMS = ["upper_arm", "forearm", "hand"]
BODY = ["head", "neck", "torso"]
PEN_CAP = 0.005
# S91b. A per-frame verdict must be weighted by the IK ramp weight before blame is assigned. At
# w ~ 0 the pose on screen IS the source animation -- the layer has written essentially nothing --
# so a negative cell there indicts the clip, not the layer. This is not hypothetical: S91's first
# final_bm01 capture scored forearm-vs-torso -4.3 mm and the sample sat at f7, w = 0.0146; the
# re-run, whose grid happened to skip the near-zero-weight frames, scored +55.2 mm for the same
# frozen constants. Same layer, same pose, opposite-looking verdict. Frames at or above ATTRIB_W are
# the ones the layer actually owns; everything below is reported, never gated.
ATTRIB_W = 0.5


def seg_seg(p1, q1, p2, q2):
    """Closest distance between two segments (Ericson, Real-Time Collision Detection)."""
    d1, d2, r = q1 - p1, q2 - p2, p1 - p2
    a, e, f = d1 @ d1, d2 @ d2, d2 @ r
    if a < 1e-12 and e < 1e-12: return np.linalg.norm(r)
    if a < 1e-12: s, t = 0.0, np.clip(f / e, 0, 1)
    else:
        c = d1 @ r
        if e < 1e-12: t, s = 0.0, np.clip(-c / a, 0, 1)
        else:
            b = d1 @ d2; den = a * e - b * b
            s = np.clip((b * f - c * e) / den, 0, 1) if den > 1e-12 else 0.0
            t = (b * s + f) / e
            if t < 0: t, s = 0.0, np.clip(-c / a, 0, 1)
            elif t > 1: t, s = 1.0, np.clip((b - c) / a, 0, 1)
    return np.linalg.norm((p1 + d1 * s) - (p2 + d2 * t))


def ellipse_clear(P, Q, hip, neck, right, a, b, nsamp=24):
    """Clearance from a segment to an ELLIPTICAL-cylinder torso: half-width a (lateral), half-depth
    b (anterior). A circular capsule of chest half-width fills the whole space in front of the
    sternum, so a hand held in front of the chest reads as 5 cm inside the body without touching it
    -- which is why forearm-vs-torso was invariant to the elbow pole across the whole sweep."""
    # Stop the torso axis below the collarbones. Carrying a mid-chest ellipse all the way to the
    # Neck joint balloons it around the throat, so anything near the chin reads as inside the torso
    # -- that is how the UNMODIFIED animation, whose hand rests at the jaw, scored -202 mm.
    axis = neck - hip; L = np.linalg.norm(axis)
    if L < 1e-9: return 9e9
    axis = axis / L
    # Build an ORTHONORMAL basis of the plane perpendicular to the spine axis. Decomposing `perp`
    # onto raw right/forward was wrong: the spine is not vertical, so perp carries a component along
    # neither of them, u^2+v^2 < 1, and the ellipse radius 1/sqrt((u/a)^2+(v/b)^2) blew up -- which
    # is how the audit came to report the UNMODIFIED animation 429 mm inside its own torso.
    e1 = right - axis * (right @ axis)
    n1 = np.linalg.norm(e1)
    if n1 < 1e-9:
        e1 = np.cross(axis, np.array([0.0, 1.0, 0.0])); n1 = np.linalg.norm(e1)
        if n1 < 1e-9: return 9e9
    e1 = e1 / n1
    e2 = np.cross(axis, e1)
    best = 9e9
    for i in range(nsamp + 1):
        p = P + (Q - P) * (i / nsamp)
        d0 = p - hip
        t = np.clip(d0 @ axis, 0.0, L)
        perp = d0 - axis * t
        m = np.linalg.norm(perp)
        if m < 1e-9: return -min(a, b)
        u = abs(perp @ e1) / m
        v = abs(perp @ e2) / m
        # TAPER. A constant chest cross-section carried to the top of the Hips->Neck axis puts a
        # 17.6 x 14.9 cm cap around the throat, so any hand at the mouth is "inside the torso" -- and
        # that cap, not the forearm, was setting the forearm-vs-torso minimum, which is why the
        # number did not move when the elbow moved 505 mm between pole settings. Narrow the section
        # above 60% of the axis to a neck-sized 35% at the top.
        f = t / L
        k = 1.0 if f <= 0.6 else 1.0 - 0.65 * (f - 0.6) / 0.4
        r = k / np.sqrt((u / a) ** 2 + (v / b) ** 2)
        best = min(best, m - r)
    return best


def audit(tag, base="s91/data/", lo=6, hi=114):
    R = {}
    for ln in open(base + f"radii_{tag}.txt"):
        k, v = ln.split(); R[k] = float(v)
    rad = {"upper_arm": R["upper"], "forearm": R["fore"], "hand": R["hand"],
           "head": R["head"],
           # The measured 0.182 m "neck" is collar and shoulder geometry, which would make every
           # arm-vs-neck pair vacuous. Audit with an anatomical 0.06 m and report both numbers.
           "neck": 0.06,
           "torso": R["torso"]}
    ell = ("torsoHalfW" in R and "torsoHalfD" in R)
    rows = list(csv.DictReader(open(base + f"contact_{tag}.csv")))
    V = lambda r, a, b, c: np.array([float(r[a]), float(r[b]), float(r[c])])
    worst = {(x, y): (9e9, -1, 0.0) for x in ARMS for y in BODY}      # overall
    attrib = {(x, y): (9e9, -1, 0.0) for x in ARMS for y in BODY}     # w >= ATTRIB_W only
    n = na = 0
    for r in rows:
        f = int(r["frame"])
        if f < lo or f > hi: continue
        w = float(r.get("weight", 1.0))
        n += 1
        na += (w >= ATTRIB_W)
        segs = {"upper_arm": (V(r, "shX", "shY", "shZ"), V(r, "elX", "elY", "elZ")),
                "forearm": (V(r, "elX", "elY", "elZ"), V(r, "wrX", "wrY", "wrZ")),
                "hand": (V(r, "wrX", "wrY", "wrZ"), V(r, "tipX", "tipY", "tipZ")),
                "head": (V(r, "nkX", "nkY", "nkZ"), V(r, "hdX", "hdY", "hdZ")),
                "neck": (V(r, "nkX", "nkY", "nkZ"), V(r, "nkX", "nkY", "nkZ")),
                "torso": (V(r, "hipX", "hipY", "hipZ"), V(r, "nkX", "nkY", "nkZ"))}
        rg = np.array([float(r.get("rgX", 1)), 0.0, float(r.get("rgZ", 0))])
        if np.linalg.norm(rg) < 1e-6: rg = np.array([1.0, 0.0, 0.0])
        rg /= np.linalg.norm(rg)
        for x in ARMS:
            for y in BODY:
                if y == "torso" and ell:
                    d = ellipse_clear(segs[x][0], segs[x][1], segs["torso"][0], segs["torso"][1],
                                      rg, R["torsoHalfW"], R["torsoHalfD"]) - rad[x]
                else:
                    d = seg_seg(*segs[x], *segs[y]) - rad[x] - rad[y]
                if d < worst[(x, y)][0]: worst[(x, y)] = (d, f, w)
                if w >= ATTRIB_W and d < attrib[(x, y)][0]: attrib[(x, y)] = (d, f, w)
    return rad, worst, attrib, n, na


def report(tag, base="s91/data/", gate=True, ref=None):
    """GATE A. Absolute: no pair may penetrate deeper than PEN_CAP on a frame the layer owns.

    Two numbers per cell. The OVERALL worst is every sampled frame of ramp+hold; the ATTRIBUTABLE
    worst is restricted to frames at or above ATTRIB_W, which are the ones the IK layer actually
    authored. The gate reads the attributable number. When the two disagree, the overall worst is
    sitting on a near-zero-weight frame and is a property of the source clip -- which is exactly the
    case the S91 re-run exposed, and the reason this split exists at all.

    Clearance cannot be gated at zero in the absolute: the limb radii and the torso ellipse are
    distances from bone axis to CLOTHED surface, so two capsules overlap in the model whenever the
    real meshes merely touch. On the IK-OFF baseline, arms hanging at the sides, upper_arm-vs-torso
    is already -46 mm under a circular torso."""
    rad, worst, attrib, n, na = audit(tag, base)
    rref = None
    if ref is not None:
        _, rref, _, _, _ = audit(ref, base)
    print(f"\n=== GATE A full clearance matrix — {tag}  ({n} frames of ramp+hold f6..f114, "
          f"{na} of them at ramp weight >= {ATTRIB_W}) ===")
    print("radii (m): " + "  ".join(f"{k}={v:.4f}" for k, v in rad.items())
          + "   [neck audited at an anatomical 0.06; the mesh partition measured 0.182 = collar]")
    print(f"\n{'':11s}" + "".join(f"{y:>25s}" for y in BODY))
    ok = True
    for x in ARMS:
        line = f"{x:11s}"
        for y in BODY:
            d, f, w = worst[(x, y)]
            a, af, aw = attrib[(x, y)]
            # A run with the layer OFF has no attributable frame at all. Fall back to the overall
            # worst so the baseline still prints a number, and gate it the same way.
            if af < 0: a, af = d, f
            bad = a < -PEN_CAP                      # gate on the attributable number only
            flag = "!" if bad else (" " if a <= d + 1e-9 else " ")
            if rref is None:
                line += f"{a*1000:+8.1f} f{af:<4d}[{d*1000:+7.1f} w{w:.2f}]{flag}"
            else:
                b = rref[(x, y)][0]
                line += f"{a*1000:+8.1f}({a*1000-b*1000:+6.1f})[{d*1000:+7.1f} w{w:.2f}]{flag}"
            ok &= not bad
        print(line)
    print("\n  cell = ATTRIBUTABLE worst mm"
          + (" (delta vs the IK-OFF baseline mm)" if rref is not None else " at frame f")
          + "  [overall worst mm, and the ramp weight at that frame].")
    print(f"  The gate reads the attributable number. A bracketed number far below it means the")
    print(f"  overall worst sits at low ramp weight -- the source animation's pose, not the layer's.")
    print(f"  negative = overlap; ! = penetration deeper than {PEN_CAP*1000:.0f} mm")
    print(f"  GATE A: {'PASS' if ok else 'FAIL'}" + ("" if gate else "   (report-only)"))
    return ok, worst, attrib


if __name__ == "__main__":
    for t in sys.argv[1:]:
        report(t)
