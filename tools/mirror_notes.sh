#!/bin/bash
# Round 3 (output-root health guard companion); repointed Session 30 (T7 retired -> /mnt/ssd).
# trial_outputs resolves through a symlink onto the dedicated output-root drive -- this mirrors
# just the paper trail (REPORT.md, HOWARD_HANDOFF.md, PROJECT_HANDOFF.md, *.diff, index*.html)
# onto the internal, always-mounted disk after every trial, so that record survives even if the
# output-root drive is ever disconnected/unavailable again. PROJECT_HANDOFF.md added to this list
# Session 30 -- its prior absence here (T7-only) is exactly why it had to be reconstructed from
# memory after T7 went offline; don't let that gap repeat for the doc going forward.
# Never mirrors trial video/CSV payloads -- those live on the output-root drive only, this is the
# narrative record.
#
# Usage: mirror_notes.sh [trial_outputs_path]
set -u

SRC="${1:-$HOME/Desktop/research/social_navigation/trial_outputs}"
DST="$HOME/trial_notes_mirror"
mkdir -p "$DST"

shopt -s nullglob
rsync -t "$SRC"/REPORT.md "$SRC"/HOWARD_HANDOFF.md "$SRC"/PROJECT_HANDOFF.md "$DST"/ 2>/dev/null
diffs=("$SRC"/*.diff)
if [ ${#diffs[@]} -gt 0 ]; then
    rsync -t "${diffs[@]}" "$DST"/ 2>/dev/null
fi
indexes=("$SRC"/index*.html)
if [ ${#indexes[@]} -gt 0 ]; then
    rsync -t "${indexes[@]}" "$DST"/ 2>/dev/null
fi

exit 0
