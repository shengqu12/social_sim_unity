#!/usr/bin/env bash
# Session 43 TASK 6 -- demo batch, N=1.
#
# THIS BATCH IS FORMAT VALIDATION, NOT A SAFETY CENSUS. N=1 per configuration. Its min_dist values
# are recorded because they fall out of the pipeline, and they must never be quoted as a safety
# result: this project's standing rule is worst-of-N>=5, and one sample cannot support any claim
# about worst-case clearance. s43_make_index.py prints that warning at the top of INDEX.md.
#
# The point is to get one trial of every shape through the new video/ + vlm_eval/ output so a human
# can watch the _ov clips and confirm the format is right before hundreds of trials are produced in
# it. Sequential by design -- run_trial.py holds the Unity editor lock, so parallel runs collide.
set -u
OUT=/mnt/ssd/Social_Navigation/trial_outputs/demo_s43
mkdir -p "$OUT"
RESULTS="$OUT/results.tsv"
echo -e "config\tkind\texit\tmin_dist\tsafety_label\tout_dir" > "$RESULTS"

run() {  # run <name> <kind> <args...>
  local name="$1"; shift
  local kind="$1"; shift
  local d="$OUT/$name"
  rm -rf "$d"
  python3 tools/run_trial.py --out "$d" "$@" > "$OUT/${name}.log" 2>&1
  local ec=$?
  local md sl
  md=$(grep -oP 'min_dist reached: \K[0-9.]+' "$OUT/${name}.log" | head -1)
  sl=$(grep -oP 'safety_label=\K[a-z]+' "$OUT/${name}.log" | head -1)
  echo -e "${name}\t${kind}\t${ec}\t${md:-NA}\t${sl:-NA}\t${d}" >> "$RESULTS"
  echo "[s43] ${name} exit=${ec} min_dist=${md:-NA}"
}

COMMON="--profile scoring --duration 60 --reused-ros"

# --- the eight vlm_batch_v6 configurations ---
run indifferent    v6config $COMMON --appearance business_male_01 --personality indifferent
run assertive      v6config $COMMON --appearance business_male_01 --personality assertive
run scared         v6config $COMMON --appearance business_male_01 --personality scared
run dyad           v6config $COMMON --appearance business_male_01 --personality indifferent --dyad
run ped_count_3    v6config $COMMON --appearance business_male_01 --personality indifferent --ped-count 3
run scooter_user   v6config $COMMON --appearance scooter_user    --personality indifferent
run wheelchair_user v6config $COMMON --appearance wheelchair_user --personality indifferent
run cyclist        v6config $COMMON --appearance cyclist         --personality indifferent

# --- the five Mixamo clips that survived Session 41's screen ---
run mixamo_carry_and_walk mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip carry_and_walk --carried-box
run mixamo_Drunk_Walk     mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Drunk_Walk
run mixamo_Old_Man_Walk   mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Old_Man_Walk
run mixamo_Pacing_Phone   mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Pacing_And_Talking_On_A_Phone
run mixamo_Running        mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Running

# --- the four clips Session 41 failed, re-run with --ped-motion standing ---
# S42 TASK A, folded in here. These are stationary animations, and the bug was that SFAgent walked
# the character ~14m anyway while the clip played in place. --ped-motion standing pins the release
# destination to the spawn point so the agent never navigates. Measured net displacement, S41 -> now:
#   Sitting 14.04 -> 0.211m   Standing_Arguing -> 0.012m   Talking_standing -> 0.012m
#   Stroke_Shaking_Head -> 0.012m
#
# Stroke_Shaking_Head had a SECOND and more severe defect: it is not grounded. That is a vertical
# problem and this flag only addresses horizontal translation. frames.csv logs no pedestrian_y, so
# grounding cannot be checked from the data at all -- it needs the eyeball pass, and until then this
# clip is NOT fixed, only half-fixed. See known_issues/S41_mixamo_screen61.md.
run mixamo_Sitting_standing      mixamo_recovered $COMMON --appearance business_male_01 \
    --personality indifferent --mixamo-clip Sitting --ped-motion standing
run mixamo_Standing_Arguing_standing mixamo_recovered $COMMON --appearance business_male_01 \
    --personality indifferent --mixamo-clip Standing_Arguing --ped-motion standing
run mixamo_Talking_standing_standing mixamo_recovered $COMMON --appearance business_male_01 \
    --personality indifferent --mixamo-clip Talking_standing --ped-motion standing
run mixamo_Stroke_Shaking_Head_standing mixamo_recovered $COMMON --appearance business_male_01 \
    --personality indifferent --mixamo-clip Stroke_Shaking_Head --ped-motion standing

# --- one corridor, to exercise event frames on a genuine close pass ---
# 1.5m was chosen because Session 41 measured worst-of-5 = 0.539m there: close enough that the
# min_dist instant lands between two 1 Hz samples, which is exactly what the forced event frame
# exists to catch.
run corridor_w1.5 corridor --profile corridor --corridor-width 1.5 --duration 60 --reused-ros \
    --appearance business_male_01 --personality indifferent

echo "BATCH_S43_DEMO COMPLETE" >> "$RESULTS"
echo "[s43] batch complete -- $RESULTS"
