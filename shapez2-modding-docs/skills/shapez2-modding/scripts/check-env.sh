#!/usr/bin/env bash
#
# Verify the shapez 2 modding environment, and report what is actually installed.
#
# Run this BEFORE a build (the game must be closed, or the copy fails) and AFTER one
# (to confirm the build you are about to test is the build you just made).
#
# Exit codes:  0 ok   1 environment problem   2 game is running

set -uo pipefail

usage() {
    cat <<'EOF'
check-env.sh [--quiet]

Checks SPZ2_PATH / SPZ2_PERSISTENT / SPZ2_SHIFTER, reports whether shapez 2 is
running, and lists every installed mod with its version and DLL timestamp.

  --quiet   only print problems

Exit: 0 ok, 1 environment problem, 2 game running.
EOF
}

QUIET=0
case "${1:-}" in
    -h|--help) usage; exit 0 ;;
    --quiet)   QUIET=1 ;;
    "")        ;;
    *)         echo "unknown argument: $1" >&2; usage >&2; exit 1 ;;
esac

say()  { [ "$QUIET" -eq 1 ] || printf '%s\n' "$*"; }
warn() { printf '%s\n' "$*" >&2; }

status=0

# ---------------------------------------------------------------- environment

check_var() {
    local name="$1" kind="$2" value="${!1:-}"
    if [ -z "$value" ]; then
        warn "MISSING  $name is not set"
        warn "         On Windows:  \"shapez 2.exe\" --set-modding-env-vars"
        warn "         The variables must be set before the terminal running dotnet was opened."
        return 1
    fi
    if [ "$kind" = file ] && [ ! -f "$value" ]; then
        warn "BAD      $name does not point at a file: $value"
        [ -d "$value" ] && warn "         It is a directory. SPZ2_SHIFTER must be ShapezShifter.dll itself."
        return 1
    fi
    if [ "$kind" = dir ] && [ ! -d "$value" ]; then
        warn "BAD      $name does not point at a directory: $value"
        return 1
    fi
    say "ok       $name = $value"
    return 0
}

say "Environment"
check_var SPZ2_PATH       dir  || status=1
check_var SPZ2_PERSISTENT dir  || status=1
check_var SPZ2_SHIFTER    file || status=1

if [ -n "${SPZ2_PATH:-}" ] && [ -d "${SPZ2_PATH:-}" ] && [ ! -f "$SPZ2_PATH/SPZGameAssembly.dll" ]; then
    warn "BAD      SPZ2_PATH has no SPZGameAssembly.dll — expected the Managed directory"
    status=1
fi

[ "$status" -ne 0 ] && say ""

# ------------------------------------------------------------- game running?

game_running() {
    if command -v tasklist >/dev/null 2>&1; then
        tasklist 2>/dev/null | grep -qi 'shapez'
    elif command -v pgrep >/dev/null 2>&1; then
        pgrep -i 'shapez' >/dev/null 2>&1
    else
        return 3
    fi
}

say ""
game_running
case $? in
    0) warn "RUNNING  shapez 2 is open."
       warn "         A plain 'dotnet build' will fail on the copy, not the compile:"
       warn "           MSB3021 / MSB3027 ... user-mapped section open"
       warn "         Close the game, or compile-check with -p:OutputPath=/tmp/verify/,"
       warn "         or stage with -p:Dev=true and reload in-game."
       status=2 ;;
    3) say "?        cannot detect processes on this system — check manually" ;;
    *) say "ok       shapez 2 is not running" ;;
esac

# --------------------------------------------------------------- what is installed

mtime() {
    stat -c '%y' "$1" 2>/dev/null | cut -c1-16 && return 0
    stat -f '%Sm' -t '%Y-%m-%d %H:%M' "$1" 2>/dev/null || echo '?'
}

json_field() {
    grep -o "\"$2\"[[:space:]]*:[[:space:]]*\"[^\"]*\"" "$1" 2>/dev/null \
        | head -1 | sed 's/.*"\([^"]*\)"[[:space:]]*$/\1/'
}

list_folder() {
    local root="$1" label="$2"
    [ -d "$root" ] || { say "  ($label does not exist)"; return; }
    local found=0
    for dir in "$root"/*/; do
        [ -d "$dir" ] || continue
        found=1
        local name version newest ts
        name=$(basename "$dir")
        version=$(json_field "$dir/manifest.json" Version)
        [ -z "$version" ] && version='no manifest'
        newest=$(find "$dir" -maxdepth 1 -name '*.dll' -print 2>/dev/null | head -1)
        if [ -n "$newest" ]; then ts=$(mtime "$newest"); else ts='no dll'; fi
        # An empty folder under mods/ is a leftover, not a mod. Worth naming: it looks
        # installed and loads nothing, which reads exactly like a mod that failed.
        if [ -z "$(ls -A "$dir" 2>/dev/null)" ]; then
            version='EMPTY'; ts='nothing to load'
        fi
        printf '  %-34s %-14s %s\n' "$name" "$version" "$ts"
    done
    [ "$found" -eq 0 ] && say "  (empty)"
    return 0
}

if [ -n "${SPZ2_PERSISTENT:-}" ] && [ -d "${SPZ2_PERSISTENT:-}" ]; then
    say ""
    say "Installed — $SPZ2_PERSISTENT/mods  (the game loads these)"
    [ "$QUIET" -eq 1 ] || list_folder "$SPZ2_PERSISTENT/mods" "mods"
    say ""
    say "Staged — $SPZ2_PERSISTENT/mods-dev  (only Mod Reloader reads these)"
    [ "$QUIET" -eq 1 ] || list_folder "$SPZ2_PERSISTENT/mods-dev" "mods-dev"
    say ""
    say "If a version or timestamp here predates your last build, the install did not happen"
    say "and you are about to test the previous build."
fi

exit $status
