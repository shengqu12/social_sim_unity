#!/usr/bin/env python3
"""Session 67, experiment B: is the S65 collapse the local model, or the task?

    python3 tools/s67_vlm_api.py <out_dir> [--arm control|legible|both] [--model claude-opus-5]

Experiments A and the frame audit retired resolution and frame count and turned up a fifth
explanation the work order did not list -- the POV camera cannot image the encounter (see
`VERDICT_A.md` §4). Experiment B is the remaining discriminator on the model axis: same twelve
trials, same prompt, a frontier model instead of NF4 Qwen2.5-VL-7B.

Two arms, and they answer different questions:

**`control`** -- S65's exact eight frames, downsampled to 644x364, the resolution the local model
actually received. Everything except the model is held fixed, so this is the clean model swap and
the arm the work order pre-registered. If this passes, the bottleneck was the local model.

**`legible`** -- the eight frames, chosen from the whole trial, in which the pedestrian is most
legibly rendered, at native 1280x720. This is not a controlled contrast (frames and resolution both
move); it is a ceiling test. It asks whether a frontier model can grade these encounters from the
best input this dataset can supply. `FRAME_AUDIT.md` says that best is a headless body at ~1.0 m,
so a failure here indicts the capture rig rather than any model.

The prompt is imported from `s65_vlm_judge`, verbatim -- explanation 4 stays frozen in both arms.
`min_dist` and pedestrian positions remain withheld, as in S65.

Cost: roughly 33k input tokens for `control` and 121k for `legible` across the twelve trials, so
well under $2 at Claude Opus 5 rates. Reads the dataset only.
"""
import argparse, base64, csv, io, json, os, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from s65_vlm_judge import DATASET, PROMPT, TRIALS, parse_label, N_FRAMES  # noqa: E402
from s67_frame_audit import classify  # noqa: E402

CONTROL_WH = (644, 364)     # exactly what Qwen received under S65's max_pixels=250880
N_SHOW = 8


def telemetry(rows):
    return "\n".join("  t=%6.2fs  speed=%5.3f m/s  heading=%6.2f deg" %
                     (float(r["time"]), float(r["robot_velocity"]), float(r["robot_heading"]))
                     for r in rows)


def control_frames(trial):
    """S65's eight, unchanged."""
    rows = list(csv.DictReader(open(os.path.join(DATASET, trial, "vlm_eval", "states.csv"))))
    enc = [r for r in rows if r.get("phase") == "encounter"] or rows
    if len(enc) > N_FRAMES:
        step = (len(enc) - 1) / (N_FRAMES - 1)
        enc = [enc[round(i * step)] for i in range(N_FRAMES)]
    return enc


def legible_frames(trial):
    """The eight most legible frames of the whole trial, in time order.

    Keeps only frames where the pedestrian is inside the FOV and at a range that renders a
    recognisable body ('full' or 'partial' in the audit's terms), then subsamples evenly so the
    approach is covered rather than eight consecutive frames of the same instant. Falls back to the
    control selection if a trial somehow has none -- none do, but a silent empty list would be worse
    than a logged fallback."""
    rows = list(csv.DictReader(open(os.path.join(DATASET, trial, "vlm_eval", "states.csv"))))
    keep = [r for r in rows if classify(r)[0] in ("full", "partial")]
    if not keep:
        return control_frames(trial), True
    if len(keep) > N_SHOW:
        step = (len(keep) - 1) / (N_SHOW - 1)
        keep = [keep[round(i * step)] for i in range(N_SHOW)]
    return keep, False


def encode(path, resize_to):
    from PIL import Image
    im = Image.open(path).convert("RGB")
    if resize_to:
        im = im.resize(resize_to, Image.LANCZOS)
    buf = io.BytesIO()
    im.save(buf, format="PNG")
    return base64.standard_b64encode(buf.getvalue()).decode("ascii"), im.size


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out_dir")
    ap.add_argument("--arm", choices=["control", "legible", "both"], default="both")
    ap.add_argument("--model", default="claude-opus-5")
    ap.add_argument("--limit", type=int, default=0, help="score only the first N trials (probe)")
    ap.add_argument("--out", default="RESULTS_B.jsonl")
    args = ap.parse_args()
    os.makedirs(args.out_dir, exist_ok=True)

    import anthropic
    client = anthropic.Anthropic()

    out_path = os.path.join(args.out_dir, args.out)
    done = set()
    if os.path.exists(out_path):
        for line in open(out_path):
            try:
                r = json.loads(line)
                done.add((r["trial"], r["arm"]))
            except ValueError:
                pass

    arms = ["control", "legible"] if args.arm == "both" else [args.arm]
    trials = TRIALS[:args.limit] if args.limit else TRIALS
    fout = open(out_path, "a")
    for arm in arms:
        for trial, expected in trials:
            if (trial, arm) in done:
                print("[s67B] %-40s %-8s already scored, skipping" % (trial, arm), flush=True)
                continue
            if arm == "control":
                rows, fell_back, resize = control_frames(trial), False, CONTROL_WH
            else:
                rows, fell_back = legible_frames(trial)
                resize = None
            span = float(rows[-1]["time"]) - float(rows[0]["time"])
            prompt = PROMPT.format(n=len(rows), span=span, telemetry=telemetry(rows))
            blocks, sizes = [], []
            for r in rows:
                data, wh = encode(os.path.join(DATASET, trial, "vlm_eval", "frames",
                                               r["Image_name"]), resize)
                blocks.append({"type": "image", "source": {"type": "base64",
                                                           "media_type": "image/png",
                                                           "data": data}})
                sizes.append("%dx%d" % wh)
            t0 = time.time()
            resp = client.messages.create(
                model=args.model, max_tokens=8000,
                messages=[{"role": "user", "content": blocks + [{"type": "text", "text": prompt}]}])
            # A safety decline is a valid outcome, not a crash: check stop_reason before content.
            if resp.stop_reason == "refusal":
                answer, label = "", "REFUSAL"
            else:
                answer = "".join(b.text for b in resp.content if b.type == "text").strip()
                label = parse_label(answer)
            rec = {"trial": trial, "expected": expected, "arm": arm, "model": args.model,
                   "label": label, "answer": answer, "stop_reason": resp.stop_reason,
                   "latency_s": round(time.time() - t0, 2), "n_frames": len(rows),
                   "frame_wh": sizes, "frames": [r["Image_name"] for r in rows],
                   "min_dist_shown": [float(r["min_dist"]) for r in rows],
                   "fell_back_to_control": fell_back,
                   "input_tokens": resp.usage.input_tokens,
                   "output_tokens": resp.usage.output_tokens}
            fout.write(json.dumps(rec) + "\n")
            fout.flush()
            print("[s67B] %-40s %-8s %-9s %5.1fs  in=%d out=%d" %
                  (trial, arm, label, rec["latency_s"], rec["input_tokens"],
                   rec["output_tokens"]), flush=True)
    fout.close()
    print("[s67B] wrote %s" % out_path)


if __name__ == "__main__":
    main()
