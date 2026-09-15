#!/usr/bin/env bash

# Calls that compile cleanly, pass review, and then remove the mod for some players.
#
# Both of the entries below shipped at least once. Neither produces a compiler error, a
# runtime exception, or a line in anyone's log - the mod simply is not there, and the only
# way anyone finds out is a bug report. That is what this script exists for: they are
# trivial to grep for and impossible to notice by reading.
#
# Run it by hand, or let CI do it - see .github/workflows/lint-mods.yml
#
#     bash .github/scripts/check-traps.sh
#
# Each pattern requires a leading dot and an opening parenthesis, so the many comments in
# these repos that name these methods in prose do not match. Keep it that way when adding
# to the list: a check that cries wolf gets ignored, and then it is worth nothing.
#
# Scope is Shapez2-*/ only. shapez2-mod-samples/ is the official samples fork, which uses
# WithBoundingCollider legitimately as far as it is concerned, and decompiled/ is the game.

set -uo pipefail

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
STATUS=0

check() {
    pattern="$1"
    title="$2"
    why="$3"
    page="$4"

    hits=$(grep -rnE "$pattern" --include='*.cs' "$ROOT"/Shapez2-*/ 2>/dev/null \
           | grep -v '/obj/' | grep -v '/bin/')

    if [ -n "$hits" ]; then
        echo "error: $title"
        echo
        echo "$hits" | sed "s|^$ROOT/|    |"
        echo
        echo "$why" | sed 's/^/    /'
        echo "    See shapez2-modding-docs/docs/$page"
        echo
        STATUS=1
    fi
}

check '\.WithPrediction\s*\(' \
    'prediction registered on an atomic extender chain' \
    'AtomicIslandExtender.Build and AtomicBuildingExtender.Build re-arm their chain only once
WaitAllRewirers has seen every branch clear its link. The prediction branch runs from a
postfix on BuiltinPredictionSimulationSystems.CreateSimulationSystems, whose sole caller is
skipped outright when the game setting "prediction" is off. For a player who turns it off
the branch never fires, the chain never re-arms, and the islands, toolbar entries and
unlocks are spent on the main menu'"'"'s background game rather than their save - with no
error anywhere. Register the prediction extender by hand, with a rewirer that re-arms per
scenario load, and leave the chain waiting only on branches that do fire.' \
    'howto/add-an-island.md'

check '\.WithBoundingCollider\s*\(' \
    'bounding collider instead of per-chunk colliders' \
    'WithBoundingCollider() sizes one box as (max - min) * 20 over the chunk positions, which
is one chunk short on every axis - and since every island is a single layer deep,
min.z == max.z, so the box is always zero height whatever the footprint. A single-chunk
island gets a box of zero size in all three axes and cannot be hovered, selected, pipetted
or deleted. Use WithPerChunkColliders(). The official BiggerPlatforms and SandboxIslands
samples both call WithBoundingCollider(), so do not read those as evidence it works.' \
    'howto/add-an-island.md'

if [ "$STATUS" -eq 0 ]; then
    echo "check-traps: clean"
fi

exit "$STATUS"
