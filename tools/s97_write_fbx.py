"""S97: write the baked clip.

Replaces the Lcl Rotation curves of LeftArm / LeftForeArm / LeftHand, on the chosen frames only,
with values taken from the bake CSV. Every other byte of the file -- every other bone, every other
channel, and these three channels OUTSIDE the chosen frames -- is copied verbatim from the source.

EULER CONTINUITY. A quaternion has infinitely many Euler representatives and the curve is
interpolated linearly BETWEEN KEYS, so a representative that jumps by ~360 (or through the
x+180 / 180-y / z+180 flip) between adjacent frames makes the bone spin between them even though
both endpoints are correct. Each emitted key is therefore the representative closest to the
previously emitted one, and the first baked key is chosen closest to the untouched key before it.
"""
import sys, csv, math, argparse
import numpy as np
import s97_fbxbin as fbxbin, s97_fbxanim as fbxanim

BONES = [("LeftArm", "bSh"), ("LeftForeArm", "bEl"), ("LeftHand", "bHa")]
CH = [b'd|X', b'd|Y', b'd|Z']

def candidates(e):
    x, y, z = e
    out = []
    for base in ((x, y, z), (x + 180.0, 180.0 - y, z + 180.0)):
        for dx in (-360, 0, 360):
            for dy in (-360, 0, 360):
                for dz in (-360, 0, 360):
                    out.append((base[0] + dx, base[1] + dy, base[2] + dz))
    return out

def pick(e, prev):
    if prev is None:
        return min(candidates(e), key=lambda c: abs(c[0]) + abs(c[1]) + abs(c[2]))
    return min(candidates(e), key=lambda c: max(abs(c[i] - prev[i]) for i in range(3)))

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--csv", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--col", default="b", help="'b' = baked/corrected, 'i' = identity round-trip")
    ap.add_argument("--frames", default="all", help="'all' or 'lo-hi' inclusive")
    ap.add_argument("--nframes", type=int, default=0, help="key count; 0 = take it from the file")
    a = ap.parse_args()

    rows = {int(r["frame"]): r for r in csv.DictReader(open(a.csv))}
    f0 = fbxanim.Fbx(a.src)
    n = a.nframes or len(f0.rot_values("LeftArm"))
    if a.frames == "all":
        sel = set(range(n))
    else:
        lo, hi = (int(v) for v in a.frames.split("-"))
        sel = set(range(lo, hi + 1))

    f = f0
    report = []
    for bone, pre in BONES:
        pre = a.col + pre[1:]
        curves = f.rot_curves(bone)
        arrs = [curves[c].find(b'KeyValueFloat').props[0] for c in CH]
        vals = [list(p[1]) for p in arrs]
        assert len(vals[0]) == n, "%d keys, %d csv rows" % (len(vals[0]), n)
        prev = None
        worst = 0.0
        for fr in range(n):
            if fr not in sel or fr not in rows:
                prev = (vals[0][fr], vals[1][fr], vals[2][fr])
                continue
            r = rows[fr]
            qu = np.array([float(r[pre + "X"]), float(r[pre + "Y"]),
                           float(r[pre + "Z"]), float(r[pre + "W"])])
            e = fbxanim.fbxq_to_euler(fbxanim.unity_to_fbxq(qu))
            e = pick(e, prev)
            # the written key must encode the SAME rotation it came from
            back = fbxanim.euler_to_fbxq([float(np.float32(v)) for v in e])
            worst = max(worst, fbxanim.qang(back, fbxanim.unity_to_fbxq(qu)))
            for i in range(3):
                vals[i][fr] = float(np.float32(e[i]))
            prev = e
        for i, p in enumerate(arrs):
            # clear the cached compressed bytes so the array is re-deflated from the new values
            curves[CH[i]].find(b'KeyValueFloat').props[0] = (p[0], vals[i], (p[2][0], None))
        step = max(max(abs(vals[i][fr] - vals[i][fr - 1]) for i in range(3)) for fr in range(1, n))
        report.append((bone, worst, step))

    out = fbxbin.serialize(f.tree)
    open(a.out, "wb").write(out)
    for bone, worst, step in report:
        # a key that is a full turn away from its neighbour is a correct rotation and a visible spin;
        # the emitted representatives are chained, so this is the check that the chain held.
        flag = "" if step < 60.0 else "   <-- CHECK: large inter-key step"
        print("  %-12s max euler-encode error %.6f deg   max inter-key step %7.2f deg%s"
              % (bone, worst, step, flag))
    print("wrote %s (%d bytes, source %d)" % (a.out, len(out), len(f.raw)))

main()
