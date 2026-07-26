#!/usr/bin/env bash
# Session 41 TASK 6.2 CONTROL -- isolates "walls" from "head-on yielding".
#
# Why this exists: the 3.0m width, which the ticket treats as the control condition, produced
# a 0.319m breach. Trajectory inspection (tools/s41_wall_clearance.py) shows neither agent got
# within 10% of a wall in ANY of the five 3.0m runs -- peak lateral use was 0.61-0.84 of the
# available half-width -- and min_dist tracks lateral separation at the pass (0.118m on the
# breach vs 0.790m on the safest run), not wall proximity. So the walls were never the binding
# constraint at 3.0m and the sweep has no clean control.
#
# This runs the same geometry at 6.0m, where half-width (3.0m) is ~2.4x the largest lateral
# excursion ever observed (1.254m). Walls are provably non-binding, so any breach here is
# attributable to head-on yielding alone. Waits for the main sweep first -- run_trial.py holds
# the Unity editor lock.
set -u
while pgrep -f s41_batch62.sh >/dev/null; do sleep 30; done
OUT=/mnt/ssd/Social_Navigation/trial_outputs/demo_s41/corridor62_control
mkdir -p "$OUT"
RESULTS="$OUT/results.tsv"
echo -e "width\trun\texit\tmin_dist\tsafety_label\tout_dir" > "$RESULTS"
for run in 01 02 03 04 05; do
  d="$OUT/w6.0_${run}"
  python3 tools/run_trial.py --appearance business_male_01 --personality indifferent \
    --profile corridor --corridor-width 6.0 --duration 60 --out "$d" \
    > "$OUT/w6.0_${run}.log" 2>&1
  ec=$?
  md=$(grep -oP 'min_dist reached: \K[0-9.]+' "$OUT/w6.0_${run}.log" | head -1)
  sl=$(grep -oP 'safety_label=\K[a-z]+' "$OUT/w6.0_${run}.log" | head -1)
  echo -e "6.0\t${run}\t${ec}\t${md:-NA}\t${sl:-NA}\t${d}" >> "$RESULTS"
done
echo "CONTROL COMPLETE" >> "$RESULTS"
