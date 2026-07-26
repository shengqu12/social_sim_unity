#!/usr/bin/env python3
"""VLM social-navigation scorer for an AutoTrial vlm_batch_* package.

Reads rubric.yaml + prompt_template.txt (external, editable -- see those files)
and manifest.csv from the batch directory, scores each trial's CLEAN
(non-overlay) scoring clip with a vision-language model, and writes
scores.csv joined with the manifest's objective columns.

Model backend: local Ollama (default model: llava:7b, already resident on
this machine -- see STEP 1 findings in the Loop 2 session's REPORT.md entry
for why Qwen2.5-VL/API access were not used). Pass --mock to use a
structured-random mock scorer instead (for pipeline testing without a model).
"""
import argparse
import base64
import csv
import json
import random
import re
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

import yaml
from PIL import Image

OLLAMA_URL = "http://localhost:11434/api/chat"
OVERLAY_MARKER = "_ov"


def reject_overlay(path: Path):
    """Hard assertion: never let an overlay (burned-in telemetry) file reach the model."""
    if path.stem.endswith("_ov"):
        raise AssertionError(
            f"REFUSING to score an overlay file (burned-in telemetry would leak the answer): {path}"
        )


def load_rubric(rubric_path: Path):
    with open(rubric_path) as f:
        return yaml.safe_load(f)


def build_prompt(template_path: Path, rubric: dict, num_frames: int) -> str:
    template = template_path.read_text()
    label_names = list(rubric["labels"].keys())
    label_list = "\n".join(f"- {name}" for name in label_names)
    label_definitions = "\n".join(f"- {name}: {desc.strip()}" for name, desc in rubric["labels"].items())
    return template.format(
        viewpoint=rubric["viewpoint"].strip(),
        task=rubric["task"].strip(),
        num_frames=num_frames,
        label_list=label_list,
        label_definitions=label_definitions,
        label_names=", ".join(label_names),
    )


def probe_duration_seconds(video_path: Path) -> float:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "default=noprint_wrappers=1:nokey=1", str(video_path)],
        capture_output=True, text=True, check=True,
    )
    return float(out.stdout.strip())


def extract_montage(video_path: Path, num_frames: int, work_dir: Path) -> Path:
    """Sample num_frames evenly-spaced frames and arrange them into one grid image
    (chronological, left-to-right then top-to-bottom). A single montage image is used
    instead of a true multi-image call because a quick A/B against this machine's local
    model (llava:7b) showed multi-image messages produce incoherent/hallucinated output,
    while a single montage image produces a coherent, on-topic description -- see the
    Loop 2 session REPORT.md entry for the actual test transcript."""
    duration = probe_duration_seconds(video_path)
    work_dir.mkdir(parents=True, exist_ok=True)
    frame_paths = []
    for i in range(num_frames):
        # evenly spaced, inset slightly from the very first/last frame
        t = duration * (i + 0.5) / num_frames
        frame_path = work_dir / f"frame_{i:02d}.jpg"
        subprocess.run(
            ["ffmpeg", "-y", "-ss", f"{t:.3f}", "-i", str(video_path),
             "-frames:v", "1", "-q:v", "3", str(frame_path)],
            capture_output=True, check=True,
        )
        frame_paths.append(frame_path)

    imgs = [Image.open(p).convert("RGB") for p in frame_paths]
    w, h = imgs[0].size
    scale = 480 / w
    tw, th = int(w * scale), int(h * scale)
    imgs = [im.resize((tw, th)) for im in imgs]

    cols = 4
    rows = (num_frames + cols - 1) // cols
    grid = Image.new("RGB", (tw * cols, th * rows), color=(0, 0, 0))
    for i, im in enumerate(imgs):
        x = (i % cols) * tw
        y = (i // cols) * th
        grid.paste(im, (x, y))

    montage_path = work_dir / "montage.jpg"
    grid.save(montage_path, quality=85)
    return montage_path


def call_ollama(model: str, prompt: str, image_path: Path, timeout: int = 180) -> str:
    b64 = base64.b64encode(image_path.read_bytes()).decode()
    payload = {
        "model": model,
        "messages": [{"role": "user", "content": prompt, "images": [b64]}],
        "stream": False,
        "options": {"temperature": 0.2},
    }
    req = urllib.request.Request(
        OLLAMA_URL, method="POST",
        data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = json.loads(resp.read())
    return data["message"]["content"]


def parse_structured_response(raw: str, valid_labels: list):
    """Defensive parse: find a JSON object anywhere in the text, validate required
    keys and label membership. Returns (label, confidence, reasoning) or None on failure."""
    match = re.search(r"\{.*\}", raw, re.DOTALL)
    if not match:
        return None
    try:
        obj = json.loads(match.group(0))
    except json.JSONDecodeError:
        return None
    label = obj.get("label")
    reasoning = obj.get("reasoning")
    confidence = obj.get("confidence")
    if label not in valid_labels:
        return None
    if reasoning is None:
        return None
    try:
        confidence = float(confidence)
    except (TypeError, ValueError):
        confidence = None
    if confidence is None or not (0.0 <= confidence <= 1.0):
        confidence = 0.5  # model returned a label+reasoning but a bad/missing confidence; don't discard the sample for that alone
    return label, confidence, str(reasoning)


def score_trial_real(model: str, prompt: str, montage_path: Path, valid_labels: list):
    last_raw = ""
    for attempt in range(2):
        try:
            last_raw = call_ollama(model, prompt, montage_path)
        except Exception as e:
            if attempt == 1:
                return "PARSE_FAIL", 0.0, f"model call failed: {e}", last_raw
            continue
        parsed = parse_structured_response(last_raw, valid_labels)
        if parsed is not None:
            label, confidence, reasoning = parsed
            return label, confidence, reasoning, last_raw
        if attempt == 0:
            prompt = prompt + "\n\nYour previous response was not valid JSON. Respond with ONLY the JSON object, nothing else."
    return "PARSE_FAIL", 0.0, "model output could not be parsed as valid structured JSON after retry", last_raw


def score_trial_mock(valid_labels: list, rng: random.Random):
    label = rng.choice(valid_labels)
    confidence = round(rng.uniform(0.3, 0.99), 2)
    reasoning = f"[MOCK] structured-random placeholder score for plumbing test (label={label})"
    return label, confidence, reasoning, "[MOCK RESPONSE -- no real model was called]"


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--batch-dir", default="../../../trial_outputs/vlm_batch_v6",
                     help="Path to a vlm_batch_* directory (must contain manifest.csv)")
    ap.add_argument("--out-dir", default="../../../trial_outputs/vlm_eval_v1",
                     help="Output directory for scores.csv and provenance copies")
    ap.add_argument("--model", default="llava:7b", help="Ollama model name")
    ap.add_argument("--num-frames", type=int, default=8)
    ap.add_argument("--mock", action="store_true",
                     help="Use a structured-random mock scorer instead of a real model call")
    ap.add_argument("--rubric", default=str(Path(__file__).parent / "rubric.yaml"))
    ap.add_argument("--prompt-template", default=str(Path(__file__).parent / "prompt_template.txt"))
    args = ap.parse_args()

    script_dir = Path(__file__).parent
    batch_dir = (script_dir / args.batch_dir).resolve()
    out_dir = (script_dir / args.out_dir).resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    work_dir = out_dir / "_frames_work"

    rubric = load_rubric(Path(args.rubric))
    valid_labels = list(rubric["labels"].keys())
    prompt_base = build_prompt(Path(args.prompt_template), rubric, args.num_frames)

    manifest_path = batch_dir / "manifest.csv"
    with open(manifest_path) as f:
        manifest_rows = list(csv.DictReader(f))

    print(f"[score_batch] batch_dir={batch_dir}")
    print(f"[score_batch] {len(manifest_rows)} trials, model={'MOCK' if args.mock else args.model}, num_frames={args.num_frames}")

    rng = random.Random(1234)
    results = []
    parse_fail_count = 0

    for row in manifest_rows:
        config = row["config"]
        trial_dir = batch_dir / config
        clip_path = trial_dir / "pov_near_00_scoring_clip.mp4"
        reject_overlay(clip_path)  # hard guard, even though the filename is already clean
        if not clip_path.exists():
            raise FileNotFoundError(f"expected scoring clip not found: {clip_path}")

        print(f"[score_batch] {config}: extracting {args.num_frames} frames + montage...")
        montage_path = extract_montage(clip_path, args.num_frames, work_dir / config)

        t0 = time.time()
        if args.mock:
            label, confidence, reasoning, raw = score_trial_mock(valid_labels, rng)
        else:
            label, confidence, reasoning, raw = score_trial_real(args.model, prompt_base, montage_path, valid_labels)
        elapsed = time.time() - t0

        if label == "PARSE_FAIL":
            parse_fail_count += 1

        print(f"[score_batch] {config}: label={label} confidence={confidence} ({elapsed:.1f}s)")

        results.append({
            "trial_id": config,
            "config": config,
            "appearance": row.get("appearance", ""),
            "personality": row.get("personality", ""),
            "scenario": row.get("scenario", ""),
            "measured_speed_mps": row.get("measured_speed_mps", ""),
            "worst_of_N_min_dist": row.get("worst_of_N_min_dist", ""),
            "heading_angle_deg": row.get("heading_angle_deg", ""),
            "approach_seconds": row.get("approach_seconds", ""),
            "label": label,
            "confidence": confidence,
            "reasoning": reasoning,
            "raw_model_response": raw,
        })

    scores_path = out_dir / "scores.csv"
    fieldnames = list(results[0].keys()) if results else []
    with open(scores_path, "w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(results)

    print(f"[score_batch] wrote {len(results)} rows ({parse_fail_count} PARSE_FAIL) to {scores_path}")

    # Provenance copies: exact rubric/prompt used, for anyone re-reading scores.csv later.
    (out_dir / "rubric_used.yaml").write_text(Path(args.rubric).read_text())
    (out_dir / "prompt_template_used.txt").write_text(Path(args.prompt_template).read_text())

    return 0


if __name__ == "__main__":
    sys.exit(main())
