#!/bin/bash
# S97 Phase 1: fixed-point inversion of the FBX -> Unity humanoid import.
set -e
PROJ=/home/sheng/Desktop/research/social_navigation/social_sim_unity
UNITY=/home/sheng/Unity/Hub/Editor/2022.3.40f1/Editor/Unity
S97=/mnt/ssd/Social_Navigation/sandbox_s72_nextgen/s97
DATA=$S97/data; LOGS=$S97/logs
SCR=/tmp/claude-1000/-mnt-ssd-Social-Navigation/b059a264-ff7e-4b95-92b9-16b06860aacb/scratchpad/s97
BAKED=Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised_baked.fbx
SRC=$PROJ/Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx
WINDOW=7-113

run() { ( cd $PROJ && env "$@" timeout 900 $UNITY -batchmode -nographics -quit -projectPath "$PROJ" \
        -executeMethod "$METHOD" -logFile "$LOG" >/dev/null 2>&1 ); }

# Phase 1 target: the ON-MANIFOLD solve. 6 alternation passes, 12 ROM-settle iterations, and the
# ramp built in muscle space (see S97BakeBuild). These are solution-path settings; pole 2.6, roll
# target 20 and the standoff are untouched.
( cd $PROJ && env AUTOTRIAL_S97_OUT=$DATA AUTOTRIAL_S97_TAG=target AUTOTRIAL_S97_MANIFOLD=1 \
    AUTOTRIAL_S97_PASSES=6 AUTOTRIAL_S97_SETTLE=12 timeout 900 $UNITY -batchmode -nographics -quit \
    -projectPath "$PROJ" -executeMethod SEAN.AutoTrial.S97BakeBuild.Capture \
    -logFile $LOGS/target.log >/dev/null 2>&1 )

for K in 0 1 2 3 4; do
  echo "===== round $K ====="
  if [ $K -eq 0 ]; then
    python3 - <<PY
import csv
rows=list(csv.DictReader(open("$DATA/bake_target.csv")))
lo,hi=[int(v) for v in "$WINDOW".split("-")]
with open("$DATA/mu_r0.csv","w") as f:
    f.write("frame,m0,m1,m2,m3,m4,m5,m6\n")
    for r in rows:
        fr=int(r["frame"])
        if lo<=fr<=hi:
            f.write(fr.__str__()+","+",".join(r["m%d"%k] for k in range(7))+"\n")
    # the target file itself, same window
with open("$DATA/target_mu.csv","w") as f:
    f.write("frame,m0,m1,m2,m3,m4,m5,m6\n")
    for r in rows:
        fr=int(r["frame"])
        if lo<=fr<=hi:
            f.write(fr.__str__()+","+",".join(r["m%d"%k] for k in range(7))+"\n")
PY
    MU=$DATA/mu_r0.csv
  fi
  # decode mu -> source-rig rotations (measuring the CURRENT candidate closes the loop)
  METHOD=SEAN.AutoTrial.S97BakeBuild.Iterate LOG=$LOGS/iter_r$K.log
  CAND=$SRC_ASSET
  if [ $K -eq 0 ]; then MEASURE=Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx; else MEASURE=$BAKED; fi
  run AUTOTRIAL_S97_OUT=$DATA AUTOTRIAL_S97_TAG=r$K AUTOTRIAL_S97_SRC=$MEASURE \
      AUTOTRIAL_S97_MU=$MU AUTOTRIAL_S97_TARGET=$DATA/target_mu.csv
  grep -aE "residual|ITERATE OK|FAIL|error CS" $LOGS/iter_r$K.log | head -3
  # write the candidate FBX from the decoded rotations, window only
  python3 $SCR/write_fbx.py --src $SRC --csv $DATA/decoded_r$K.csv --col b --frames $WINDOW \
      --out $SCR/baked_r$K.fbx --nframes 180
  cp $SCR/baked_r$K.fbx $PROJ/$BAKED
  METHOD=SEAN.AutoTrial.S86KimodoAvatarRefPose.Apply LOG=$LOGS/imp_r$K.log
  run AUTOTRIAL_S86_TARGETS=$BAKED
  grep -aE "S86gate|FAIL" $LOGS/imp_r$K.log | grep -av "^ #" | head -3
  MU=$DATA/mu_r$K.csv
done
