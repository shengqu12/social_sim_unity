#!/usr/bin/env bash
# Dataset generation, plan D. headon only. 155 trials, roughly 6.2 h.
#
#   A1  6 appearances x 5 personalities x N=3  =  90
#   A2  7 special characters x N=5             =  35
#   A3  6 Mixamo behaviours x N=5              =  30
#
# DO NOT RUN THIS UNTIL THE FORMAT SAMPLE HAS BEEN REVIEWED BY A HUMAN. Everything here writes the
# same output format; if the format is wrong, all 155 trials are wrong, and that is the one failure
# this batch cannot absorb. The 8-trial verification batch and FORMAT_SAMPLE.md are the gate.
#
# Operational rules, from the work order:
#   - every trial gets AUTOTRIAL_S54_PROBE; s62_make_index's freeze-drift column depends on it
#   - a trial still running after 10 minutes is killed, recorded and skipped -- one stuck trial must
#     not take the batch down with it
#   - integrity and disk are checked every 40 trials, not at the end, so a full disk surfaces after
#     40 wasted trials rather than after 155
#   - seeds are deterministic and derived from the trial name, so any single trial can be reproduced
#     from this file alone without re-running the batch
set -u

OUT=${OUT:-/mnt/ssd/Social_Navigation/trial_outputs/dataset_planD}
mkdir -p "$OUT"
R="$OUT/results.tsv"
[ -f "$R" ] || echo -e "config\texit\tdist0\tmin_dist\tsafety_label\tblock" > "$R"
SKIPPED="$OUT/skipped.tsv"
[ -f "$SKIPPED" ] || echo -e "config\treason" > "$SKIPPED"

TRIAL_TIMEOUT=${TRIAL_TIMEOUT:-600}
MIN_FREE_GB=${MIN_FREE_GB:-40}
COUNT=0

seed_of() { printf '%d' "$(( 0x$(echo -n "$1" | md5sum | cut -c1-6) % 100000 ))"; }

checkpoint() {
  local free
  free=$(df -BG --output=avail "$OUT" | tail -1 | tr -dc '0-9')
  echo "[checkpoint] $COUNT trials done, ${free} GB free"
  if [ "$free" -lt "$MIN_FREE_GB" ]; then
    echo "[checkpoint] ABORT: only ${free} GB free (floor ${MIN_FREE_GB})" | tee -a "$OUT/ABORTED.txt"
    exit 2
  fi
  # Integrity: every completed trial must have its four deliverables. Catching this at 40 rather
  # than at 155 is the whole point of checking here.
  local bad=0
  while IFS=$'\t' read -r cfg _; do
    [ "$cfg" = "config" ] && continue
    for f in frames.csv meta.json vlm_eval/states.csv pov_full.mp4; do
      [ -e "$OUT/$cfg/$f" ] || { echo "[checkpoint] MISSING $cfg/$f"; bad=$((bad+1)); }
    done
  done < "$R"
  [ "$bad" -eq 0 ] && echo "[checkpoint] integrity OK" || echo "[checkpoint] $bad missing deliverable(s)"
}

run() {
  local name="$1"; shift
  local block="$1"; shift
  if grep -q "^${name}	" "$R" 2>/dev/null; then echo "[skip] $name already done"; return; fi
  local d="$OUT/$name"
  rm -rf "$d" "$OUT/${name}_params.csv"
  AUTOTRIAL_S54_PROBE="$OUT/${name}_params.csv" \
    timeout "$TRIAL_TIMEOUT" python3 tools/run_trial.py --out "$d" \
      --profile scoring --duration 60 --reused-ros --dense-encounter \
      --seed "$(seed_of "$name")" "$@" > "$OUT/${name}.log" 2>&1
  local ec=$?
  if [ "$ec" -eq 124 ]; then
    echo -e "${name}\ttimeout after ${TRIAL_TIMEOUT}s" >> "$SKIPPED"
    echo "[timeout] $name -- recorded and skipped"
  else
    local md sl
    md=$(grep -oP 'min_dist reached: \K[0-9.]+' "$OUT/${name}.log" | head -1)
    sl=$(grep -oP 'safety_label=\K[a-z]+' "$OUT/${name}.log" | head -1)
    local d0
    d0=$(grep -oP 'dist0=\K[0-9.]+' "$OUT/${name}.log" | head -1)
    echo -e "${name}\t${ec}\t${d0:-NA}\t${md:-NA}\t${sl:-NA}\t${block}" >> "$R"
    echo "[$block] $name exit=$ec min_dist=${md:-NA}"
  fi
  COUNT=$((COUNT+1))
  [ $((COUNT % 40)) -eq 0 ] && checkpoint
}

# --- A1: 6 appearances x 5 personalities x 3 ------------------------------------------------
APPEARANCES="male_adult_01 female_adult_07 business_male_04 chef_female_01 construction_male_03 medical_female_02"
PERSONALITIES="indifferent scared curious surprised assertive"
for a in $APPEARANCES; do
  for p in $PERSONALITIES; do
    for i in 1 2 3; do
      run "A1_${a}_${p}_r${i}" A1 --appearance "$a" --personality "$p"
    done
  done
done

# --- A2: 7 special characters x 5 -----------------------------------------------------------
# phone_user is deliberately absent -- removed from the roster, see known_issues/phone_user.md.
# male_child and female_child have no walking animation and run as static obstacles.
for i in 1 2 3 4 5; do
  run "A2_wheelchair_user_r${i}" A2 --appearance wheelchair_user --personality indifferent
  run "A2_scooter_user_r${i}"    A2 --appearance scooter_user    --personality indifferent
  run "A2_cyclist_r${i}"         A2 --appearance cyclist         --personality indifferent
  run "A2_white_cane_user_r${i}" A2 --appearance white_cane_user --personality indifferent
  run "A2_dog_walker_r${i}"      A2 --appearance dog_walker      --personality indifferent
  run "A2_male_child_r${i}"      A2 --appearance male_child      --personality indifferent --ped-motion standing
  run "A2_female_child_r${i}"    A2 --appearance female_child    --personality indifferent --ped-motion standing
done

# --- A3: 6 Mixamo behaviours x 5 ------------------------------------------------------------
for i in 1 2 3 4 5; do
  run "A3_old_man_r${i}"          A3 --appearance business_male_01 --personality indifferent --mixamo-clip Old_Man_Walk
  run "A3_Drunk_Walk_r${i}"       A3 --appearance business_male_01 --personality indifferent --mixamo-clip Drunk_Walk
  run "A3_carry_and_walk_r${i}"   A3 --appearance business_male_01 --personality indifferent --mixamo-clip carry_and_walk --carried-box
  run "A3_Pacing_Phone_r${i}"     A3 --appearance business_male_01 --personality indifferent --mixamo-clip Pacing_And_Talking_On_A_Phone
  run "A3_Sitting_r${i}"          A3 --appearance business_male_01 --personality indifferent --mixamo-clip Sitting --ped-motion standing
  run "A3_standing_arguing_r${i}" A3 --appearance business_male_01 --personality indifferent --mixamo-clip Standing_Arguing --ped-motion standing
done

checkpoint
echo "DATASET_PLAND COMPLETE" >> "$R"
echo
echo "Now: python3 tools/s62_make_index.py $OUT      # exit code is the verdict"
echo "Then the three post-run questions -- min_dist tail, R1-R4 for a never-moving configuration,"
echo "maxSpeedScale engagements -- are answered from INDEX.md and reported separately."
