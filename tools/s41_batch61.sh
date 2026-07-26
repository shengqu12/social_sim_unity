#!/usr/bin/env bash
# Session 41 TASK 6.1 -- survival screen, N=2 per Mixamo clip, open field (no corridor).
# Answers only the four screening questions: does the character move, does the clip play,
# does it foot-slide, is the facing right. Sequential by design: run_trial.py holds the
# Unity editor lock, so parallel runs would collide.
set -u
OUT=/mnt/ssd/Social_Navigation/trial_outputs/demo_s41/screen61
mkdir -p "$OUT"
CLIPS=(carry_and_walk Drunk_Walk Old_Man_Walk Pacing_And_Talking_On_A_Phone Running \
       Sitting Standing_Arguing Stroke_Shaking_Head Talking_standing)
RESULTS="$OUT/results.tsv"
echo -e "clip\trun\texit\tmin_dist\tsafety_label\tout_dir" > "$RESULTS"
for clip in "${CLIPS[@]}"; do
  for run in 01 02; do
    d="$OUT/${clip}_${run}"
    extra=""
    [ "$clip" = "carry_and_walk" ] && extra="--carried-box"
    python3 tools/run_trial.py --appearance business_male_01 --personality indifferent \
      --profile scoring --mixamo-clip "$clip" $extra --duration 45 --out "$d" \
      > "$OUT/${clip}_${run}.log" 2>&1
    ec=$?
    md=$(grep -oP 'min_dist reached: \K[0-9.]+' "$OUT/${clip}_${run}.log" | head -1)
    sl=$(grep -oP 'safety_label=\K[a-z]+' "$OUT/${clip}_${run}.log" | head -1)
    echo -e "${clip}\t${run}\t${ec}\t${md:-NA}\t${sl:-NA}\t${d}" >> "$RESULTS"
  done
done
echo "BATCH61 COMPLETE" >> "$RESULTS"
