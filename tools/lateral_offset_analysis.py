"""Session 34 FIX 3: replaces the never-committed "detour-onset distance" metric (S32/S33, an
ephemeral /tmp script) with a DIFFERENTIAL lateral-offset measure -- the only thing Session 33's
own control trial (pedestrian parked 40m away, physically incapable of influencing the robot)
proved was real signal rather than TEB's own ambient path noise. That control measured ~0.3-0.45m
of ambient robot lateral drift and still fired the old onset metric at 37.7m on pure noise.

Usage (robot lateral offset from the straight corridor centerline, present vs. an absent control):
    python3 tools/lateral_offset_analysis.py robot-differential PRESENT_FRAMES_CSV ABSENT_FRAMES_CSV

Usage (pedestrian's own lateral deviation from ITS OWN straight-line bearing, vs. live distance to
the robot -- the FIX 1 verification curve, "flat until the gate, then bends"):
    python3 tools/lateral_offset_analysis.py ped-vs-distance FRAMES_CSV
"""
import csv
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from run_trial import ROBOT_START, DEFAULT_ROBOT_GOAL  # noqa: E402


def _corridor_unit():
    sx, sy, sz = ROBOT_START
    gx, gy, gz = DEFAULT_ROBOT_GOAL[0], DEFAULT_ROBOT_GOAL[1], DEFAULT_ROBOT_GOAL[2]
    dx, dz = gx - sx, gz - sz
    norm = math.hypot(dx, dz)
    return (dx / norm, dz / norm), (sx, sz)


def _lateral_offset(x, z, unit, origin):
    ux, uz = unit
    ox, oz = origin
    dx, dz = x - ox, z - oz
    # Perpendicular component of (x,z)-origin against the corridor's own unit bearing.
    along = dx * ux + dz * uz
    perp_x = dx - along * ux
    perp_z = dz - along * uz
    return math.hypot(perp_x, perp_z)


def read_frames(csv_path):
    rows = []
    with open(csv_path, newline="") as f:
        for row in csv.DictReader(f):
            rows.append(row)
    return rows


def robot_lateral_series(csv_path):
    unit, origin = _corridor_unit()
    rows = read_frames(csv_path)
    return [
        (float(r["t"]), _lateral_offset(float(r["robot_x"]), float(r["robot_z"]), unit, origin))
        for r in rows
    ]


def robot_differential(present_csv, absent_csv):
    present = robot_lateral_series(present_csv)
    absent = robot_lateral_series(absent_csv)
    n = min(len(present), len(absent))
    if n == 0:
        print("ERROR: empty frames.csv on one or both sides")
        sys.exit(1)
    deltas = [present[i][1] - absent[i][1] for i in range(n)]
    max_delta = max(deltas, key=abs)
    max_idx = deltas.index(max_delta)
    mean_present = sum(v for _, v in present[:n]) / n
    mean_absent = sum(v for _, v in absent[:n]) / n
    print("frames compared: {}".format(n))
    print("present mean lateral offset: {:.4f}m".format(mean_present))
    print("absent (control) mean lateral offset: {:.4f}m  <- ambient TEB noise floor".format(mean_absent))
    print("max |differential| (present - absent): {:.4f}m at t={:.2f}s (frame {})".format(
        max_delta, present[max_idx][0], max_idx))
    print("mean differential: {:.4f}m".format(sum(deltas) / n))
    return {
        "n": n, "mean_present": mean_present, "mean_absent": mean_absent,
        "max_abs_differential": max_delta, "max_abs_differential_t": present[max_idx][0],
        "mean_differential": sum(deltas) / n,
    }


def ped_vs_distance(csv_path, n_bins=8):
    rows = read_frames(csv_path)
    ped_rows = [r for r in rows if r.get("pedestrian_x") not in (None, "", "0")]
    if len(ped_rows) < 2:
        print("ERROR: not enough pedestrian-position rows in {}".format(csv_path))
        sys.exit(1)
    # Pedestrian's own straight-line bearing: first vs. last recorded pedestrian position
    # (release position -> final position), same "reference straight line" concept
    # S32AssertiveStraightLineGuardian already uses for its own trajectory checks.
    x0, z0 = float(ped_rows[0]["pedestrian_x"]), float(ped_rows[0]["pedestrian_z"])
    x1, z1 = float(ped_rows[-1]["pedestrian_x"]), float(ped_rows[-1]["pedestrian_z"])
    dx, dz = x1 - x0, z1 - z0
    norm = math.hypot(dx, dz)
    if norm < 1e-6:
        print("ERROR: pedestrian never moved (net displacement ~0) -- can't define a bearing")
        sys.exit(1)
    unit = (dx / norm, dz / norm)
    origin = (x0, z0)

    samples = []
    for r in ped_rows:
        px, pz = float(r["pedestrian_x"]), float(r["pedestrian_z"])
        dist = float(r["dist_to_pedestrian"])
        lat = _lateral_offset(px, pz, unit, origin)
        samples.append((dist, lat))

    max_dist = max(d for d, _ in samples)
    bin_w = max_dist / n_bins if max_dist > 0 else 1.0
    bins = [[] for _ in range(n_bins + 1)]
    for dist, lat in samples:
        idx = min(int(dist / bin_w), n_bins) if bin_w > 0 else 0
        bins[idx].append(lat)

    print("distance-bin -> mean lateral deviation (near to far, {} samples total):".format(len(samples)))
    for i in range(n_bins, -1, -1):
        if not bins[i]:
            continue
        lo, hi = i * bin_w, (i + 1) * bin_w
        mean_lat = sum(bins[i]) / len(bins[i])
        print("  [{:5.2f}-{:5.2f})m  n={:3d}  mean_lateral={:.4f}m".format(lo, hi, len(bins[i]), mean_lat))


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)
    mode = sys.argv[1]
    if mode == "robot-differential":
        robot_differential(sys.argv[2], sys.argv[3])
    elif mode == "ped-vs-distance":
        ped_vs_distance(sys.argv[2])
    else:
        print(__doc__)
        sys.exit(1)
