#!/bin/bash
# Round 3 (output-root health guard companion). trial_outputs now resolves through a symlink onto
# the T7 drive, which is unplugged sometimes and holds no git history of its own -- this mirrors
# just the paper trail (REPORT.md, HOWARD_HANDOFF.md, *.diff, index*.html) onto the internal,
# always-mounted disk after every trial, so that record survives even if T7 is disconnected.
# Never mirrors trial video/CSV payloads -- those live on T7 only, this is the narrative record.
#
# Usage: mirror_notes.sh [trial_outputs_path]
set -u

SRC="${1:-$HOME/Desktop/research/social_navigation/trial_outputs}"
DST="$HOME/trial_notes_mirror"
mkdir -p "$DST"

shopt -s nullglob
rsync -t "$SRC"/REPORT.md "$SRC"/HOWARD_HANDOFF.md "$DST"/ 2>/dev/null
diffs=("$SRC"/*.diff)
if [ ${#diffs[@]} -gt 0 ]; then
    rsync -t "${diffs[@]}" "$DST"/ 2>/dev/null
fi
indexes=("$SRC"/index*.html)
if [ ${#indexes[@]} -gt 0 ]; then
    rsync -t "${indexes[@]}" "$DST"/ 2>/dev/null
fi

exit 0
