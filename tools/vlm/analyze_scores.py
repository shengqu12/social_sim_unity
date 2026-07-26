#!/usr/bin/env python3
"""Analyze a scores.csv produced by score_batch.py and write a markdown report.

This is the file the human actually reads. It reports, in order:
  1. Label distribution + PARSE_FAIL count
  2. An explicit discrimination verdict (does the model differentiate at all?)
  3. Direction-only correlation between label and objective metrics
  4. Per-trial table with reasoning, so the human can sanity-check the model's justifications
"""
import argparse
import csv
import statistics
from collections import Counter
from pathlib import Path

LABEL_ORDER = ["NEGATIVE_SOCIAL", "NEUTRAL", "POSITIVE_SOCIAL", "PARSE_FAIL"]
LABEL_SCORE = {"NEGATIVE_SOCIAL": -1, "NEUTRAL": 0, "POSITIVE_SOCIAL": 1}


def load_scores(path: Path):
    with open(path) as f:
        return list(csv.DictReader(f))


def discrimination_verdict(rows):
    labels = [r["label"] for r in rows if r["label"] != "PARSE_FAIL"]
    if not labels:
        return "NO DATA -- every trial was PARSE_FAIL, discrimination cannot be assessed.", Counter()
    counts = Counter(labels)
    n_distinct = len(counts)
    if n_distinct <= 1:
        only = next(iter(counts)) if counts else "?"
        return (f"**NO DISCRIMINATION** -- every scored trial collapsed to a single label "
                f"({only}). The rubric or the visual signal (montage frames, prompt wording) "
                f"needs work before this pipeline is useful for differentiating configs."), counts
    return (f"Some discrimination observed: {n_distinct} distinct labels used across "
            f"{len(labels)} scored trials ({dict(counts)})."), counts


def correlation_notes(rows):
    notes = []
    scored = [r for r in rows if r["label"] in LABEL_SCORE]
    if len(scored) < 2:
        return ["Not enough non-PARSE_FAIL trials to say anything about correlation."]

    # min_dist vs label direction
    pairs = []
    for r in scored:
        try:
            md = float(r["worst_of_N_min_dist"])
        except (ValueError, KeyError):
            continue
        pairs.append((md, LABEL_SCORE[r["label"]]))
    if len(pairs) >= 2:
        pairs.sort()
        low_half = pairs[: len(pairs) // 2] or pairs[:1]
        high_half = pairs[len(pairs) // 2:] or pairs[-1:]
        low_mean = statistics.mean(s for _, s in low_half)
        high_mean = statistics.mean(s for _, s in high_half)
        direction = "lower-clearance trials scored MORE negative on average" if low_mean < high_mean else (
            "lower-clearance trials did NOT score more negative on average (no clear direction, or reversed)"
        )
        notes.append(
            f"min_dist vs. label: N={len(pairs)}. {direction} "
            f"(low-clearance half mean label-score={low_mean:.2f}, high-clearance half mean={high_mean:.2f}). "
            f"**Weak evidence at this N -- direction only, not a claim of significance.**"
        )
    else:
        notes.append("Not enough valid min_dist values to compare against label.")

    # personality breakdown
    by_personality = {}
    for r in scored:
        by_personality.setdefault(r.get("personality", "?"), []).append(r["label"])
    notes.append("Label by personality (raw counts, N too small per-group for any statistical claim):")
    for pers, labs in sorted(by_personality.items()):
        notes.append(f"  - {pers}: {dict(Counter(labs))}")

    return notes


def write_report(rows, out_path: Path, scores_csv_path: Path):
    verdict_text, label_counts = discrimination_verdict(rows)
    parse_fail_count = sum(1 for r in rows if r["label"] == "PARSE_FAIL")
    corr_notes = correlation_notes(rows)

    lines = []
    lines.append("# VLM Scoring Analysis -- vlm_eval_v1")
    lines.append("")
    lines.append(f"**N = {len(rows)}. This is a PLUMBING TEST, not a statistically meaningful "
                 "evaluation.** The rubric (`rubric.yaml`) is an explicit placeholder pending "
                 "the human's own rewrite -- read any finding below as \"does the pipeline "
                 "work end-to-end and produce something legible,\" not as a real behavioral "
                 "conclusion about the dataset.")
    lines.append("")
    lines.append("## Top-line findings")
    lines.append("")
    lines.append(f"- **Label distribution**: {dict(sorted(label_counts.items()))}")
    lines.append(f"- **PARSE_FAIL count**: {parse_fail_count} / {len(rows)}")
    lines.append(f"- **Discrimination verdict**: {verdict_text}")
    lines.append("")
    lines.append("## Correlation with objective metrics (direction only, N is small -- weak evidence)")
    lines.append("")
    for note in corr_notes:
        lines.append(f"- {note}" if not note.startswith("  ") else note)
    lines.append("")
    lines.append("## Per-trial table")
    lines.append("")
    lines.append("| Config | Personality | min_dist (m) | Label | Confidence | Reasoning |")
    lines.append("|---|---|---|---|---|---|")
    for r in rows:
        reasoning = (r.get("reasoning") or "").replace("|", "/").replace("\n", " ").strip()
        if len(reasoning) > 160:
            reasoning = reasoning[:157] + "..."
        lines.append(
            f"| {r['config']} | {r.get('personality','')} | {r.get('worst_of_N_min_dist','')} "
            f"| {r['label']} | {r.get('confidence','')} | {reasoning} |"
        )
    lines.append("")
    lines.append(f"Full raw model responses (including any PARSE_FAIL raw text) are in `{scores_csv_path.name}`'s "
                 "`raw_model_response` column.")
    lines.append("")

    out_path.write_text("\n".join(lines))
    return out_path


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--scores-csv", default="../../../trial_outputs/vlm_eval_v1/scores.csv")
    ap.add_argument("--out", default="../../../trial_outputs/vlm_eval_v1/analysis_report.md")
    args = ap.parse_args()

    script_dir = Path(__file__).parent
    scores_csv_path = (script_dir / args.scores_csv).resolve()
    out_path = (script_dir / args.out).resolve()

    rows = load_scores(scores_csv_path)
    if not rows:
        raise SystemExit(f"no rows found in {scores_csv_path}")

    written = write_report(rows, out_path, scores_csv_path)
    print(f"[analyze_scores] wrote {written}")

    verdict_text, label_counts = discrimination_verdict(rows)
    print(f"[analyze_scores] label distribution: {dict(label_counts)}")
    print(f"[analyze_scores] discrimination verdict: {verdict_text}")


if __name__ == "__main__":
    main()
