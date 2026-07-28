#!/usr/bin/env bash
# Session 46 (S46-B section 1): appearance survival screen, 24 appearances, N=1, open-field headon.
#
# 112 Rocketbox prefabs are on disk and exactly ONE (business_male_01) has ever been through this
# pipeline. Putting unvalidated appearances straight into a multi-hour matrix risks a large crop of
# unusable footage; every failure mode below has occurred in this project before:
#   - GetLocomotionAnimator() picking the wrong Animator (a prop/animal Animator taking the
#     locomotion controller)
#   - foot slide
#   - floating / sunk into the ground
#   - facing not matching direction of travel
#   - Animator on a nested child leaving the character motionless
#
# Selection spans sex, age band, dress type and build rather than 24 near-identical business males:
# the screen exists to protect the dataset's DIVERSITY, so screening a monoculture would defeat it.
# Indices are spread within each family rather than taken adjacent.
set -u
OUT=/mnt/ssd/Social_Navigation/trial_outputs/s46_appearance_screen
mkdir -p "$OUT"
RESULTS="$OUT/results.tsv"
echo -e "appearance\tfamily\texit\tmin_dist\tout_dir" > "$RESULTS"

APPEARANCES=(
  # generic adults, both sexes, spread indices
  "male_adult_01:generic_male"      "male_adult_08:generic_male"
  "male_adult_15:generic_male"      "male_adult_21:generic_male"
  "female_adult_02:generic_female"  "female_adult_07:generic_female"
  "female_adult_12:generic_female"  "female_adult_17:generic_female"
  # occupational / distinct silhouettes
  "business_female_02:business_f"   "business_male_04:business_m"
  "construction_male_03:construction" "construction_female_01:construction"
  "police_male_05:police"           "police_female_01:police"
  "fire_male_02:fire"               "fire_female_01:fire"
  "medical_male_03:medical"         "medical_female_02:medical"
  "sports_male_02:sports"           "sports_female_01:sports"
  "military_male_04:military"       "chef_female_01:service"
  "delivery_male_01:service"        "gardener_male_01:service"
)

for entry in "${APPEARANCES[@]}"; do
  name="${entry%%:*}"; fam="${entry##*:}"
  d="$OUT/$name"
  rm -rf "$d" "$OUT/${name}_probe.csv"
  AUTOTRIAL_S44_PROBE="$OUT/${name}_probe.csv" \
    python3 tools/run_trial.py --out "$d" --profile scoring --duration 45 --reused-ros \
      --appearance "$name" --personality indifferent > "$OUT/${name}.log" 2>&1
  ec=$?
  md=$(grep -oP 'min_dist reached: \K[0-9.]+' "$OUT/${name}.log" | head -1)
  echo -e "${name}\t${fam}\t${ec}\t${md:-NA}\t${d}" >> "$RESULTS"
  echo "[s46screen] ${name} (${fam}) exit=${ec}"
done
echo "SCREEN COMPLETE" >> "$RESULTS"
