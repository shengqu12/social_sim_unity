#!/usr/bin/env bash
# Session 44 TASK 6 -- final demo, N=1, 16 trials.
#
# FORMAT / EYEBALL VALIDATION BATCH, NOT A SAFETY CENSUS. N=1 per configuration; min_dist is
# recorded because the pipeline emits it and must never be quoted as a safety result. Session 43's
# accidental control measured up to 1.4 m of run-to-run spread on identical commands.
#
# Every trial runs with the S44 probe enabled so checks 3.1-3.3 have data; without it they report
# SKIP rather than PASS.
set -u
OUT=/mnt/ssd/Social_Navigation/trial_outputs/demo_s44
mkdir -p "$OUT"
RESULTS="$OUT/results.tsv"
echo -e "config\tkind\texit\tmin_dist\tsafety_label\tout_dir" > "$RESULTS"

run() {
  local name="$1"; shift
  local kind="$1"; shift
  local d="$OUT/$name"
  rm -rf "$d" "$OUT/${name}_probe.csv"
  AUTOTRIAL_S44_PROBE="$OUT/${name}_probe.csv" \
    python3 tools/run_trial.py --out "$d" "$@" > "$OUT/${name}.log" 2>&1
  local ec=$?
  local md sl
  md=$(grep -oP 'min_dist reached: \K[0-9.]+' "$OUT/${name}.log" | head -1)
  sl=$(grep -oP 'safety_label=\K[a-z]+' "$OUT/${name}.log" | head -1)
  echo -e "${name}\t${kind}\t${ec}\t${md:-NA}\t${sl:-NA}\t${d}" >> "$RESULTS"
  echo "[s44] ${name} exit=${ec} min_dist=${md:-NA}"
}

COMMON="--profile scoring --duration 60 --reused-ros"

# --- 8 personality / appearance configurations ---
run indifferent     v6config $COMMON --appearance business_male_01 --personality indifferent
run assertive       v6config $COMMON --appearance business_male_01 --personality assertive
run scared          v6config $COMMON --appearance business_male_01 --personality scared
run dyad            v6config $COMMON --appearance business_male_01 --personality indifferent --dyad
run ped_count_3     v6config $COMMON --appearance business_male_01 --personality indifferent --ped-count 3
run scooter_user    v6config $COMMON --appearance scooter_user     --personality indifferent
run wheelchair_user v6config $COMMON --appearance wheelchair_user  --personality indifferent
run cyclist         v6config $COMMON --appearance cyclist          --personality indifferent

# --- 7 Mixamo clips (9 minus talking_standing minus Stroke_Shaking_Head) ---
run mixamo_Pacing_Phone    mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Pacing_And_Talking_On_A_Phone
run mixamo_carry_and_walk  mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip carry_and_walk --carried-box
run mixamo_Old_Man_Walk    mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Old_Man_Walk
run mixamo_Drunk_Walk      mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Drunk_Walk
run mixamo_Running         mixamo $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Running
# Stationary pair: --ped-motion standing pins the release destination (S42 TASK A).
run mixamo_Standing_Arguing static $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Standing_Arguing --ped-motion standing
run mixamo_Sitting          static $COMMON --appearance business_male_01 --personality indifferent \
    --mixamo-clip Sitting --ped-motion standing

# --- corridor ---
run corridor_w1.5 corridor --profile corridor --corridor-width 1.5 --duration 60 --reused-ros \
    --appearance business_male_01 --personality indifferent

echo "BATCH_S44_DEMO COMPLETE" >> "$RESULTS"
