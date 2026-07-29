#!/usr/bin/env python3
"""Session 64: FORMAT_SAMPLE.md -- the document a human reads before 155 dataset trials are run.

    python3 tools/s64_format_sample.py <batch_dir> [trial_name]

Writes <batch_dir>/FORMAT_SAMPLE.md from one real trial on disk. Every number in it is read from
that trial at generation time; nothing here is copied forward from an earlier session's document,
because the point of the gate is to show what THIS pipeline just produced.

The three deliberate deviations from the literal spec are stated in full, since an unflagged
deviation that survives review is indistinguishable from a bug that survived review.
"""
import csv, os, re, subprocess, sys


def sh(cmd):
    r = subprocess.run(cmd, shell=True, capture_output=True, text=True)
    return r.stdout.strip()


def human(n):
    for unit in ("B", "K", "M", "G"):
        if n < 1024 or unit == "G":
            return "%.0f%s" % (n, unit) if unit == "B" else "%.1f%s" % (n, unit)
        n /= 1024.0


def tree(root):
    """Directory tree with file sizes. frames/ is summarised -- it has hundreds of entries and the
    frame listing section below shows the actual names."""
    lines = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames.sort()
        rel = os.path.relpath(dirpath, root)
        depth = 0 if rel == "." else rel.count(os.sep) + 1
        pad = "  " * depth
        if rel != ".":
            lines.append("%s%s/" % (pad, os.path.basename(dirpath)))
        if os.path.basename(dirpath) == "frames":
            tot = sum(os.path.getsize(os.path.join(dirpath, f)) for f in filenames)
            lines.append("%s  (%d files, %s total -- listed below)" % (pad, len(filenames), human(tot)))
            dirnames[:] = []
            continue
        for f in sorted(filenames):
            p = os.path.join(dirpath, f)
            lines.append("%s  %-28s %8s" % (pad, f, human(os.path.getsize(p))))
    return "\n".join(lines)


def main():
    batch = sys.argv[1]
    name = sys.argv[2] if len(sys.argv) > 2 else "old_man"
    d = os.path.join(batch, name)
    if not os.path.isdir(d):
        sys.exit("no such trial: %s" % d)

    states_path = os.path.join(d, "vlm_eval", "states.csv")
    frames_dir = os.path.join(d, "vlm_eval", "frames")
    readme_path = os.path.join(d, "vlm_eval", "README.md")

    raw = open(states_path).read().splitlines()
    rows = list(csv.DictReader(open(states_path)))
    header = raw[0].split(",")
    frame_files = sorted(os.listdir(frames_dir))

    first_png = os.path.join(frames_dir, frame_files[0])
    file_out = sh("file -b '%s'" % first_png)

    dense = [r for r in rows if r.get("event") == "dense"]
    base = [r for r in rows if re.fullmatch(r"frame_\d+\.png", r["Image_name"])]
    markers = [r for r in rows if r not in dense and r not in base]
    marker_events = sorted({r.get("event") or "(blank)" for r in markers})
    paired = sum(1 for r in rows if r["Image_name"] in set(frame_files))
    AGREED = ["time", "Image_name", "robot_velocity", "robot_heading"]
    prefix_ok = header[:4] == AGREED

    L = []
    L.append("# FORMAT_SAMPLE — `%s` from `%s`\n" % (name, os.path.basename(os.path.normpath(batch))))
    L.append("This is the exact output format all 155 plan D trials will be written in. It is "
             "generated from one real trial on disk; every figure below was read out of that trial "
             "when this file was written.\n")
    L.append("**Reviewing this is the gate.** The dataset batch writes one format 155 times; a "
             "format error found afterwards is 155 wrong trials, and that is the one failure the "
             "batch cannot absorb.\n")

    L.append("\n## 1. Directory tree\n")
    L.append("```")
    L.append("%s/" % name)
    L.append(tree(d))
    L.append("```")
    L.append("\n`video/` and `vlm_eval/` are the two deliverables the spec calls \"Unity output 1\" "
             "and \"Unity output 2\"; each README states which it is on its first line. The "
             "top-level `pov_*.mp4` / `contact_sheet_*.png` are the pre-existing per-trial review "
             "artefacts and are unchanged.\n")

    L.append("\n## 2. `vlm_eval/states.csv` — first 20 lines, verbatim\n")
    L.append("```")
    L.extend(raw[:20])
    L.append("```")
    L.append("")
    L.append("- **%d columns, %d data rows.**" % (len(header), len(rows)))
    L.append("- First four columns are `%s` — the agreed interface, in the agreed order and "
             "capitalisation (`Image_name` with a capital I): **%s**."
             % (", ".join(header[:4]), "as specified" if prefix_ok else "MISMATCH"))
    L.append("- Every row's `Image_name` resolves to a file in `frames/`: **%d/%d**." % (paired, len(rows)))
    L.append("- `robot_heading` is degrees in `[0,360)`, Unity top-down **clockwise-positive**. "
             "Verified in Session 62 against `robot_yaw_ros_rad` on 2706/2706 frames including two "
             "wrap-around frames, worst residual 0.008°. (Prior result, cited — not re-measured here.)")
    L.append("- `robot_velocity` is the unsigned ground speed; `robot_speed_ground` is the same "
             "quantity from the physics body and is the column to use for any speed statistic. "
             "Position differencing is never used for speed anywhere in this pipeline.")

    L.append("\n## 3. `vlm_eval/frames/` — first 15 and last 5, in capture order\n")
    L.append("Listed in `states.csv` row order, which is capture order. **Sorting the filenames "
             "does not reproduce it**: the densified frames are suffixed onto the base frame they "
             "follow (`frame_0007.png` → `_e` at the encounter boundary, then `_d2`, `_d3`, …), and "
             "`_d2` sorts before `_e` while arriving after it. Order by the `time` column, never by "
             "filename.\n")
    ordered = [r["Image_name"] for r in rows]
    L.append("```")
    L.extend(ordered[:15])
    L.append("...")
    L.extend(ordered[-5:])
    L.append("```")
    L.append("")
    L.append("- **%d frames**, one per `states.csv` row." % len(frame_files))
    L.append("- `file` on `%s` reports: `%s`" % (frame_files[0], file_out))
    L.append("- Frames are exported from the raw pre-overlay JPGs, so a frame **cannot** contain "
             "burned-in telemetry; the overlay exists only in `*_ov.mp4`, produced later.")

    L.append("\n## 4. Three deliberate deviations from the literal spec\n")
    L.append("Each is a departure a reviewer should either accept or reject now, not discover in "
             "the dataset.\n")
    L.append("**(a) Directory names are ASCII paths, not the prose labels.** The spec names the two "
             "deliverables \"Unity output 1\" and \"Unity output 2\". On disk they are `video/` and "
             "`vlm_eval/` — names that survive a tarball, a URL and a shell without quoting. The "
             "mapping is stated in the first line of `vlm_eval/README.md`, so the prose label is "
             "still recoverable from the data.\n")
    L.append("**(b) The encounter segment is sampled at 5 Hz, not 1 Hz** (`--dense-encounter`, on "
             "for every dataset trial). The agreed sequence is 1 Hz; that is preserved everywhere "
             "outside the encounter. Rows added by the densification carry `event=dense`, so a "
             "reader that wants the agreed sequence back drops them. This trial: **%d of %d rows "
             "are `event=dense`**. The %d that remain are the %d plain 1 Hz frames plus %d "
             "event-boundary frame(s) (%s) — those are extra rows too, but they mark the encounter "
             "boundary rather than densify it. Rationale: the encounter is the part a VLM is asked "
             "about, and 1 Hz at ~1.8 m/s closing speed steps ~1.8 m between frames.\n"
             % (len(dense), len(rows), len(rows) - len(dense), len(base), len(markers),
                ", ".join("`%s`" % e for e in marker_events) or "none"))
    L.append("**(c) `states.csv` appends columns after the agreed four.** The agreed four are first, "
             "in order and capitalisation; the other %d are appended after them, so a reader that "
             "selects by name is unaffected. Position columns are `robot_x` / `robot_z`, not "
             "`robot_x` / `robot_y`: in Unity the ground plane is (x, **z**) and y is height, so the "
             "literal spec names would pun the vertical axis onto a ground axis.\n"
             % (len(header) - 4))

    L.append("\n## 5. `vlm_eval/README.md` — full text\n")
    L.append("The per-trial README below ships inside every trial directory.\n")
    L.append("---\n")
    L.append(open(readme_path).read().rstrip())
    L.append("\n---\n")

    out = os.path.join(batch, "FORMAT_SAMPLE.md")
    open(out, "w").write("\n".join(L) + "\n")
    print("wrote %s (%d bytes)" % (out, os.path.getsize(out)))
    if not prefix_ok:
        sys.exit("FAIL: agreed column prefix is %s, not %s" % (header[:4], AGREED))
    if paired != len(rows):
        sys.exit("FAIL: %d/%d states.csv rows resolve to a frame file" % (paired, len(rows)))


if __name__ == "__main__":
    main()
