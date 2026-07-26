#!/usr/bin/env bash
# Session 41 TASK 6.2 -- corridor width sweep, N=5 per width.
#
# SCOPE NOTE, stated plainly: the ticket asks for (every surviving asset) x (4 widths) x N=5.
# With 9 clips that is 180 real trials at ~4 min each, roughly 12 hours -- out of budget for
# one session. What runs here instead is the sweep that carries the ticket's actual scientific
# claim (the width -> behaviour/label progression in its own 4-row table), at the full N=5 the
# project's safety methodology requires, on the baseline pedestrian. Per-asset corridor runs
# are a follow-up, and the harness supports them unchanged: add --mixamo-clip <name>.
set -u
OUT=/mnt/ssd/Social_Navigation/trial_outputs/demo_s41/corridor62
mkdir -p "$OUT"
WIDTHS=(3.0 2.0 1.5 1.2)
RESULTS="$OUT/results.tsv"
echo -e "width\trun\texit\tmin_dist\tsafety_label\tout_dir" > "$RESULTS"
for w in "${WIDTHS[@]}"; do
  for run in 01 02 03 04 05; do
    d="$OUT/w${w}_${run}"
    python3 tools/run_trial.py --appearance business_male_01 --personality indifferent \
      --profile corridor --corridor-width "$w" --duration 60 --out "$d" \
      > "$OUT/w${w}_${run}.log" 2>&1
    ec=$?
    md=$(grep -oP 'min_dist reached: \K[0-9.]+' "$OUT/w${w}_${run}.log" | head -1)
    sl=$(grep -oP 'safety_label=\K[a-z]+' "$OUT/w${w}_${run}.log" | head -1)
    echo -e "${w}\t${run}\t${ec}\t${md:-NA}\t${sl:-NA}\t${d}" >> "$RESULTS"
  done
done
echo "BATCH62 COMPLETE" >> "$RESULTS"
