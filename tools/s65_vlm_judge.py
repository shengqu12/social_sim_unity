#!/usr/bin/env python3
"""Session 65: can Qwen2.5-VL tell good robot social behaviour from bad?

    python3 tools/s65_vlm_judge.py <out_dir> [--trials FILE] [--runs 3]

This is the discrimination verdict, not a scoring run. It reads the 12 trials named in
`s65_vlm/SELECTION.md`, shows the model the encounter segment, asks for a three-way classification
with reasons, and writes every answer verbatim to `<out_dir>/RESULTS.jsonl`.

Two decisions that shape what the verdict means, both deliberate:

**A naive prompt, once.** The work order forbids prompt engineering here -- the point is the model's
bare ability, and a tuned prompt would hide whether that ability exists. One plain prompt, no
few-shot examples, no chain-of-thought scaffolding.

**`min_dist` and the pedestrian positions are NOT shown to the model.** `min_dist` is the variable
the 12 trials were selected on, so handing it over would test whether the model can compare two
numbers, not whether it can see a social error. The model gets the agreed interface columns --
`time`, `robot_velocity`, `robot_heading` -- and the frames. This is a deviation from a literal
reading of "the corresponding states.csv rows" and is called out in the report.

The dataset is read-only here; nothing under dataset_planD/ is written.
"""
import argparse, csv, json, os, time

DATASET = "/mnt/ssd/Social_Navigation/trial_outputs/dataset_planD"

# Fixed in SELECTION.md before any model output existed. Do not edit to chase a verdict.
TRIALS = [
    ("A1_construction_male_03_indifferent_r1", "POSITIVE"),
    ("A1_medical_female_02_indifferent_r2", "POSITIVE"),
    ("A1_female_adult_07_indifferent_r3", "POSITIVE"),
    ("A1_chef_female_01_indifferent_r1", "POSITIVE"),
    ("A2_scooter_user_r3", "NEUTRAL"),
    ("A3_Drunk_Walk_r3", "NEUTRAL"),
    ("A3_Pacing_Phone_r5", "NEUTRAL"),
    ("A3_Drunk_Walk_r1", "NEUTRAL"),
    ("A2_white_cane_user_r2", "NEGATIVE"),
    ("A2_male_child_r2", "NEGATIVE"),
    ("A2_female_child_r2", "NEGATIVE"),
    ("A3_standing_arguing_r1", "NEGATIVE"),
]

N_FRAMES = 8
# 8 frames at 451584 OOMs: NF4 weights are 6.46 GiB of the 7.53 GiB this GPU exposes, leaving ~1 GiB
# for the vision tower and KV. 250880 (~668x376) is the highest per-frame budget that fits, measured
# at 7.00 GiB peak with 2441 input tokens. Chosen for perception, not speed.
MAX_PIXELS = 250880

PROMPT = (
    "These {n} images are consecutive views from a mobile robot's onboard forward camera as it "
    "drives down a walkway and passes a pedestrian. They are in time order, spanning {span:.1f} "
    "seconds of the encounter.\n\n"
    "The robot's own telemetry over those frames:\n{telemetry}\n\n"
    "Judge the robot's SOCIAL BEHAVIOUR toward the pedestrian -- whether it behaved considerately "
    "and safely around the person.\n\n"
    "Answer in exactly this format:\n"
    "LABEL: <NEGATIVE or NEUTRAL or POSITIVE>\n"
    "REASON: <your reasoning>"
)


def encounter_frames(trial):
    """The encounter segment, subsampled to N_FRAMES evenly. Frames come from the trial's own
    states.csv order (capture order), never from a filename sort."""
    d = os.path.join(DATASET, trial)
    rows = list(csv.DictReader(open(os.path.join(d, "vlm_eval", "states.csv"))))
    enc = [r for r in rows if r.get("phase") == "encounter"] or rows
    if len(enc) > N_FRAMES:
        step = (len(enc) - 1) / (N_FRAMES - 1)
        enc = [enc[round(i * step)] for i in range(N_FRAMES)]
    paths = [os.path.join(d, "vlm_eval", "frames", r["Image_name"]) for r in enc]
    tel = ["  t=%6.2fs  speed=%5.3f m/s  heading=%6.2f deg" %
           (float(r["time"]), float(r["robot_velocity"]), float(r["robot_heading"])) for r in enc]
    span = float(enc[-1]["time"]) - float(enc[0]["time"])
    return paths, "\n".join(tel), span


def parse_label(text):
    up = text.upper()
    for line in up.splitlines():
        if "LABEL:" in line:
            for lab in ("NEGATIVE", "NEUTRAL", "POSITIVE"):
                if lab in line:
                    return lab
    # Fall back to first mention anywhere, and record that the format was not followed.
    hits = [(up.find(l), l) for l in ("NEGATIVE", "NEUTRAL", "POSITIVE") if up.find(l) >= 0]
    return min(hits)[1] + "*" if hits else "UNPARSED"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("out_dir")
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--model", default="Qwen/Qwen2.5-VL-7B-Instruct")
    args = ap.parse_args()
    os.makedirs(args.out_dir, exist_ok=True)

    import torch
    from transformers import (AutoProcessor, BitsAndBytesConfig,
                              Qwen2_5_VLForConditionalGeneration)
    from qwen_vl_utils import process_vision_info

    # NF4 4-bit. The vision tower is left in bf16, matching the official AWQ build's
    # modules_to_not_convert=["visual"] -- quantising the encoder is where VLMs lose the most.
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
    print("[s65] model loaded in %.1f s, %.2f GiB allocated"
          % (load_s, torch.cuda.memory_allocated() / 2**30), flush=True)

    out_path = os.path.join(args.out_dir, "RESULTS.jsonl")
    done = set()
    if os.path.exists(out_path):
        for line in open(out_path):
            try:
                r = json.loads(line)
                done.add((r["trial"], r["run"]))
            except ValueError:
                pass
    fout = open(out_path, "a")
    for trial, expected in TRIALS:
        if all((trial, r) in done for r in range(args.runs)):
            print("[s65] %-40s already scored, skipping" % trial, flush=True)
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
        for run in range(args.runs):
            if (trial, run) in done:
                continue
            greedy = (run == 0)  # run 0 is deterministic and is the primary classification
            t1 = time.time()
            with torch.inference_mode():
                gen = model.generate(**inputs, max_new_tokens=320,
                                     do_sample=not greedy,
                                     **({} if greedy else {"temperature": 0.7, "top_p": 0.9}))
            trimmed = gen[0][inputs.input_ids.shape[1]:]
            answer = processor.decode(trimmed, skip_special_tokens=True).strip()
            rec = {"trial": trial, "expected": expected, "run": run,
                   "decoding": "greedy" if greedy else "sample_t0.7",
                   "label": parse_label(answer), "answer": answer,
                   "latency_s": round(time.time() - t1, 2),
                   "n_frames": len(paths), "input_tokens": int(inputs.input_ids.shape[1]),
                   "peak_gib": round(torch.cuda.max_memory_allocated() / 2**30, 2)}
            fout.write(json.dumps(rec) + "\n")
            fout.flush()
            print("[s65] %-40s run%d %-9s %-9s %5.1fs" %
                  (trial, run, rec["decoding"], rec["label"], rec["latency_s"]), flush=True)
    fout.close()
    print("[s65] peak VRAM %.2f GiB, load %.1f s, wrote %s"
          % (torch.cuda.max_memory_allocated() / 2**30, load_s, out_path))


if __name__ == "__main__":
    main()
