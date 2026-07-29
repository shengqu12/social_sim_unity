#!/usr/bin/env bash
# Session 64: the 8-trial verification batch that gates the plan D dataset. headon, N=1 each.
#
# EYEBALL / FORMAT VALIDATION BATCH, NOT A SAFETY CENSUS. N=1 per configuration; min_dist is
# recorded because the pipeline emits it and must never be quoted as a safety result.
#
# Why these eight:
#   old_man, Drunk_Walk, carry_and_walk, Pacing_Phone   the Zone B freeze gate fix (S61) stops these
#       four from walking 2.87-7.73 m away during the freeze window, so the encounter geometry they
#       are captured with has changed and no human has looked at the new geometry yet
#   white_cane, dog_walker                              calibration changes that are NOT supposed to
#       change the picture -- they are here to prove that
#   Sitting                                             the freeze clear must not lock a static
#       configuration into place permanently
#   indifferent                                         Zone A spot check
#
# scooter_user is deliberately absent: untouched this round, and already in the dataset roster.
#
# Every invocation below is the same shape s63_dataset_planD.sh will use -- same profile, duration,
# --dense-encounter, and the same deterministic seed function -- because the point of this batch is
# to validate the format and geometry that all 155 dataset trials will be written in.
set -u

cd "$(dirname "$0")/.." || exit 1

OUT=${OUT:-/mnt/ssd/Social_Navigation/trial_outputs/s64_verify8}
mkdir -p "$OUT"
R="$OUT/results.tsv"
[ -f "$R" ] || echo -e "config\texit\tdist0\tmin_dist\tsafety_label" > "$R"
SKIPPED="$OUT/skipped.tsv"
[ -f "$SKIPPED" ] || echo -e "config\treason" > "$SKIPPED"

TRIAL_TIMEOUT=${TRIAL_TIMEOUT:-600}

# Identical to s63_dataset_planD.sh, so a verification trial and its dataset counterpart are the
# same run and can be compared directly.
seed_of() { printf '%d' "$(( 0x$(echo -n "$1" | md5sum | cut -c1-6) % 100000 ))"; }

run() {
  local name="$1"; shift
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
    return
  fi
  local md sl d0
  md=$(grep -oP 'min_dist reached: \K[0-9.]+' "$OUT/${name}.log" | head -1)
  sl=$(grep -oP 'safety_label=\K[a-z]+' "$OUT/${name}.log" | head -1)
  d0=$(grep -oP 'dist0=\K[0-9.]+' "$OUT/${name}.log" | head -1)
  echo -e "${name}\t${ec}\t${d0:-NA}\t${md:-NA}\t${sl:-NA}" >> "$R"
  echo "[s64] $name exit=$ec dist0=${d0:-NA} min_dist=${md:-NA}"
}

M="--appearance business_male_01 --personality indifferent"

# --- the four Mixamo clips whose encounter geometry changed with the S61 freeze gate fix ---
run old_man        $M --mixamo-clip Old_Man_Walk
run Drunk_Walk     $M --mixamo-clip Drunk_Walk
run carry_and_walk $M --mixamo-clip carry_and_walk --carried-box
run Pacing_Phone   $M --mixamo-clip Pacing_And_Talking_On_A_Phone

# --- calibration changes that must NOT change the picture ---
run white_cane  --appearance white_cane_user --personality indifferent
run dog_walker  --appearance dog_walker      --personality indifferent

# --- freeze clear must not lock a static configuration; Zone A spot check ---
run Sitting      $M --mixamo-clip Sitting --ped-motion standing
run indifferent  $M

echo "BATCH_S64 COMPLETE" >> "$R"
echo
echo "Now: python3 tools/s62_make_index.py $OUT      # exit code is the verdict"
