#!/usr/bin/env python3
"""Session 67, experiment A: is the S65 collapse a resolution problem?

    python3 tools/s67_vlm_judge.py <out_dir> [--runs 3] [--limit N] [--out NAME.jsonl]

S65 asked twelve trials, three groups, and got NEUTRAL twelve times out of twelve on the greedy
run. Four explanations were left standing: resolution, frame count, model/quantisation, prompt.
This script kills or spares the first two together by spending the same visual token budget
differently.

**The single change from S65 is 8 frames at 250880 px -> 4 frames at 451584 px.** Everything else is
imported from `s65_vlm_judge` rather than copied, so the prompt, the trial list, the label parser
and the dataset path are the same objects, not lookalikes:

  * prompt: `s65_vlm_judge.PROMPT`, verbatim, no prompt engineering (that is explanation 4, frozen)
  * trials: `s65_vlm_judge.TRIALS`, the set fixed in `s65_vlm/SELECTION.md`
  * model: Qwen2.5-VL-7B-Instruct, bitsandbytes NF4, vision tower left in bf16
  * decoding: run 0 greedy and primary, runs 1-2 at T=0.7

The four frames are the 1st, 3rd, 5th and 7th of the very same 8-frame sequence S65 showed, so no
second frame-selection rule enters the experiment. That drops the 8th frame, which is always the
robot receding: across all twelve trials the closest approach sits at index 3 or 4 of the eight, so
the frames that carry the encounter survive the halving. It also drops index 3 itself in the seven
trials where the minimum lands there; every negative trial still keeps a frame under 0.5 m.

Per-frame resolution really does rise: the source frames are 1280x720, so both budgets bind, and
smart_resize takes them to 668x376 under S65 and 896x504 here.

If this OOMs, the run stops and reports the measured peak. It does not fall back to a smaller
budget -- a silent downgrade is how S65 ended up at 250880 in the first place, and repeating it
here would destroy the one variable being tested.

The dataset is read-only; nothing under dataset_planD/ is written.
"""
import argparse, csv, json, os, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from s65_vlm_judge import DATASET, PROMPT, TRIALS, parse_label  # noqa: E402
from s65_vlm_judge import N_FRAMES as S65_N_FRAMES  # noqa: E402

MAX_PIXELS = 451584          # ~896x504, the budget S65 wanted and could not fit at 8 frames
KEEP = (0, 2, 4, 6)          # the 1st/3rd/5th/7th of the S65 eight
VRAM_BUDGET_GIB = 7.4        # 7.53 GiB is all this GPU exposes; above this the probe stops


def encounter_frames(trial):
    """S65's 8-frame encounter subsample, then every other frame. The 8-frame step is reproduced
    exactly as `s65_vlm_judge.encounter_frames` computes it, so KEEP indexes into the same list."""
    d = os.path.join(DATASET, trial)
    rows = list(csv.DictReader(open(os.path.join(d, "vlm_eval", "states.csv"))))
    enc = [r for r in rows if r.get("phase") == "encounter"] or rows
    if len(enc) > S65_N_FRAMES:
        step = (len(enc) - 1) / (S65_N_FRAMES - 1)
        enc = [enc[round(i * step)] for i in range(S65_N_FRAMES)]
    eight = enc
    enc = [eight[i] for i in KEEP if i < len(eight)]
    paths = [os.path.join(d, "vlm_eval", "frames", r["Image_name"]) for r in enc]
    tel = ["  t=%6.2fs  speed=%5.3f m/s  heading=%6.2f deg" %
           (float(r["time"]), float(r["robot_velocity"]), float(r["robot_heading"])) for r in enc]
    span = float(enc[-1]["time"]) - float(enc[0]["time"])
    return paths, "\n".join(tel), span


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out_dir")
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--limit", type=int, default=0, help="score only the first N trials (probe)")
    ap.add_argument("--out", default="RESULTS_A.jsonl")
    ap.add_argument("--model", default="Qwen/Qwen2.5-VL-7B-Instruct")
    args = ap.parse_args()
    os.makedirs(args.out_dir, exist_ok=True)

    import torch
    from transformers import (AutoProcessor, BitsAndBytesConfig,
                              Qwen2_5_VLForConditionalGeneration)
    from qwen_vl_utils import process_vision_info

    qcfg = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4",
                              bnb_4bit_compute_dtype=torch.bfloat16,
                              bnb_4bit_use_double_quant=True,
                              llm_int8_skip_modules=["visual", "lm_head"])
    t0 = time.time()
    model = Qwen2_5_VLForConditionalGeneration.from_pretrained(
        args.model, quantization_config=qcfg, dtype=torch.bfloat16, device_map="cuda:0")
    model.eval()
    processor = AutoProcessor.from_pretrained(args.model, max_pixels=MAX_PIXELS)
    load_s = time.time() - t0
    torch.cuda.reset_peak_memory_stats()
    print("[s67A] model loaded in %.1f s, %.2f GiB allocated"
          % (load_s, torch.cuda.memory_allocated() / 2**30), flush=True)

    out_path = os.path.join(args.out_dir, args.out)
    done = set()
    if os.path.exists(out_path):
        for line in open(out_path):
            try:
                r = json.loads(line)
                done.add((r["trial"], r["run"]))
            except ValueError:
                pass
    trials = TRIALS[:args.limit] if args.limit else TRIALS
    fout = open(out_path, "a")
    for trial, expected in trials:
        if all((trial, r) in done for r in range(args.runs)):
            print("[s67A] %-40s already scored, skipping" % trial, flush=True)
            continue
        paths, telemetry, span = encounter_frames(trial)
        prompt = PROMPT.format(n=len(paths), span=span, telemetry=telemetry)
        messages = [{"role": "user", "content":
                     [{"type": "image", "image": "file://" + p} for p in paths]
                     + [{"type": "text", "text": prompt}]}]
        text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
        images, videos = process_vision_info(messages)
        inputs = processor(text=[text], images=images, videos=videos,
                           padding=True, return_tensors="pt").to("cuda:0")
        # The size that matters is the one AFTER the processor's smart_resize, not the PIL size
        # qwen_vl_utils hands over. image_grid_thw is in 14 px patches; 2x2 of them make one token.
        grid = [(int(h) * 14, int(w) * 14) for _, h, w in inputs["image_grid_thw"].tolist()]
        vis_tokens = sum(int(h) * int(w) // 4 for _, h, w in inputs["image_grid_thw"].tolist())
        print("[s67A] %-40s %d frames at %s, %d visual tokens" %
              (trial, len(grid), " ".join("%dx%d" % g for g in grid), vis_tokens), flush=True)
        for run in range(args.runs):
            if (trial, run) in done:
                continue
            greedy = (run == 0)
            t1 = time.time()
            try:
                with torch.inference_mode():
                    gen = model.generate(**inputs, max_new_tokens=320,
                                         do_sample=not greedy,
                                         **({} if greedy else {"temperature": 0.7, "top_p": 0.9}))
            except torch.cuda.OutOfMemoryError:
                # Report and stop. Do NOT retry at a smaller max_pixels: that would silently turn
                # this back into S65 and the experiment would be void.
                print("[s67A] OOM on %s run%d at MAX_PIXELS=%d, %d frames; peak %.2f GiB"
                      % (trial, run, MAX_PIXELS, len(paths),
                         torch.cuda.max_memory_allocated() / 2**30), flush=True)
                fout.close()
                sys.exit(2)
            trimmed = gen[0][inputs.input_ids.shape[1]:]
            answer = processor.decode(trimmed, skip_special_tokens=True).strip()
            rec = {"trial": trial, "expected": expected, "run": run,
                   "decoding": "greedy" if greedy else "sample_t0.7",
                   "label": parse_label(answer), "answer": answer,
                   "latency_s": round(time.time() - t1, 2),
                   "n_frames": len(paths), "max_pixels": MAX_PIXELS,
                   "frame_wh": ["%dx%d" % g for g in grid], "visual_tokens": vis_tokens,
                   "input_tokens": int(inputs.input_ids.shape[1]),
                   "peak_gib": round(torch.cuda.max_memory_allocated() / 2**30, 2)}
            fout.write(json.dumps(rec) + "\n")
            fout.flush()
            print("[s67A] %-40s run%d %-9s %-9s %5.1fs  tok=%d peak=%.2f GiB" %
                  (trial, run, rec["decoding"], rec["label"], rec["latency_s"],
                   rec["input_tokens"], rec["peak_gib"]), flush=True)
            if rec["peak_gib"] > VRAM_BUDGET_GIB:
                print("[s67A] peak %.2f GiB exceeds the %.1f GiB budget -- stopping, not downgrading"
                      % (rec["peak_gib"], VRAM_BUDGET_GIB), flush=True)
                fout.close()
                sys.exit(3)
    fout.close()
    print("[s67A] peak VRAM %.2f GiB, load %.1f s, wrote %s"
          % (torch.cuda.max_memory_allocated() / 2**30, load_s, out_path))


if __name__ == "__main__":
    main()
