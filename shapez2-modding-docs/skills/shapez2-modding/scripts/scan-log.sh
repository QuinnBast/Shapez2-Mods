#!/usr/bin/env bash
#
# Triage Player.log.
#
# The error in the crash dialog is usually not the error. A mod that throws while
# registering content produces a cascade: the throw propagates out of savegame loading,
# the game falls back to starting a new savegame, that fallback re-runs registration
# against an already-populated registry, and vanilla's own code throws on the first
# group it touches. The visible exception names no mod at all.
#
# So this script finds the FIRST failure, not the loudest one.

set -uo pipefail

usage() {
    cat <<'EOF'
scan-log.sh [--prev] [--mod NAME] [--context N] [--full]

  --prev         read Player-prev.log — what you want after a crash, because
                 restarting has already overwritten Player.log
  --mod NAME     also show every line mentioning NAME
  --context N    lines of context around the first failure (default 25)
  --full         print every match, not just the first failure

Reads $SPZ2_PERSISTENT/Player.log.
EOF
}

PREV=0; MOD=""; CONTEXT=25; FULL=0
while [ $# -gt 0 ]; do
    case "$1" in
        -h|--help)  usage; exit 0 ;;
        --prev)     PREV=1; shift ;;
        --full)     FULL=1; shift ;;
        --mod)      MOD="${2:-}"; shift 2 ;;
        --context)  CONTEXT="${2:-25}"; shift 2 ;;
        *)          echo "unknown argument: $1" >&2; usage >&2; exit 1 ;;
    esac
done

if [ -z "${SPZ2_PERSISTENT:-}" ]; then
    echo "SPZ2_PERSISTENT is not set. Run check-env.sh." >&2
    exit 1
fi

LOG="$SPZ2_PERSISTENT/Player.log"
[ "$PREV" -eq 1 ] && LOG="$SPZ2_PERSISTENT/Player-prev.log"

if [ ! -f "$LOG" ]; then
    echo "No log at $LOG" >&2
    [ "$PREV" -eq 0 ] && echo "After a crash, try --prev." >&2
    exit 1
fi

echo "=== $LOG"
echo "    $(wc -l < "$LOG" | tr -d ' ') lines"
[ "$PREV" -eq 0 ] && echo "    (after a crash you probably want --prev — this file is from the run AFTER it)"
echo

# ---------------------------------------------------------------- known signatures
#
# pattern <TAB> what it actually means

SIGNATURES=$(cat <<'EOF'
Source method is generic	MonoMod cannot hook a method on a generic type, struct instantiations included. Relocate to a non-generic choke point.
Target method is not compatible with source method	CreatePrefixHook was pointed at a method that does not return void. Use a raw Hook with hand-written delegate types.
user-mapped section open	The COPY failed, not the compile — the game was running and held the installed DLL. Close it and build again.
An item with the same key has already been added	A duplicate registration — read the Key. A definition or console command registered twice (often by a hot reload re-entering a session), or a second placer over definitions the pipette map already claimed, which throws before the main menu.
island group with id	Duplicate island group. If the id is vanilla (HUB), this is the SECOND init pass — the real failure is far above. Grep for 'Failed to load' and read upward.
KeyNotFoundException	Often an island missing IslandFrameDrawData. It repeats every frame and aborts MapDrawer.Draw partway, silently killing whatever drew after it.
FieldAccessException	Publicization is not reaching the runtime. Check every game <Reference> has <Private>False</Private> — a publicized copy in the mod folder shadows the real assembly.
MethodAccessException	Publicization is not reaching the runtime. Check every game <Reference> has <Private>False</Private>.
NoDataFitDataTypeQueryException	Detach<T>() resolves with Get<T>() first and throws when there is nothing attached.
ModLoadingStep	A mod constructor threw. This is not contained — it kills the game's entire mod loading step, taking every other mod with it.
ReflectionTypeLoadException	A referenced assembly is missing at runtime, or a NuGet package was shipped that should have had <ExcludeAssets>runtime</ExcludeAssets>.
EOF
)

echo "=== Known signatures"
hits=0
while IFS=$'\t' read -r pattern meaning; do
    [ -z "$pattern" ] && continue
    # grep -c already prints 0 and exits 1 when there is no match; a '|| echo 0'
    # here appends a SECOND zero and breaks the numeric test below.
    n=$(grep -c -- "$pattern" "$LOG" 2>/dev/null) || true
    n=${n:-0}
    if [ "$n" -gt 0 ]; then
        hits=$((hits + 1))
        printf '  [%sx] %s\n' "$n" "$pattern"
        printf '        %s\n' "$meaning"
        grep -n -- "$pattern" "$LOG" | head -1 | sed 's/^/        first at line /'
        echo
    fi
done <<< "$SIGNATURES"
[ "$hits" -eq 0 ] && echo "  none" && echo

# ---------------------------------------------------------------- the cascade marker

echo "=== Cascade check"
cascade=$(grep -n "Failed to load" "$LOG" | head -1 | cut -d: -f1)
if [ -n "$cascade" ]; then
    echo "  'Failed to load' at line $cascade."
    echo "  The real stack is ABOVE this line, with a whole second init pass in between."
    echo "  Anything after it names vanilla code and leads nowhere."
    start=$(( cascade - 120 )); [ "$start" -lt 1 ] && start=1
    echo
    echo "  --- first exception before line $cascade ---"
    sed -n "${start},${cascade}p" "$LOG" \
        | grep -n -E 'Exception|  at [A-Z]' | head -20 | sed 's/^/  /'
else
    echo "  no 'Failed to load' — the visible error is probably the real one"
fi
echo

# ---------------------------------------------------------------- first failure

echo "=== First failure, with context"
# 'Error' and 'Failed' alone match hundreds of analytics and tweening warnings that have
# nothing to do with mods, and they come early enough to bury the real first failure.
# Bare 'MonoMod' is not usable here either — it matches every "Loading MonoMod.Utils.dll"
# line at startup. MonoMod's actual hook failures have distinct text, already in the
# signature table above.
ERRPAT='Exception|error CS|FATAL|Unhandled|ModLoadingStep'
NOISE='GameAnalytics|DOTWEEN|Analytics|Failed to send events|Database too large'
first=$(grep -n -E "$ERRPAT" "$LOG" | grep -v -E "$NOISE" | head -1 | cut -d: -f1)
if [ -z "$first" ]; then
    echo "  nothing matched — the mod may not be loading at all."
    echo "  Check: the DLL is in mods/<Mod>/, manifest.json lists it in Assemblies,"
    echo "  dependencies are installed, the mod is enabled, and the class is public."
else
    start=$(( first - 3 )); [ "$start" -lt 1 ] && start=1
    sed -n "${start},$(( first + CONTEXT ))p" "$LOG" | sed 's/^/  /'
fi
echo

if [ "$FULL" -eq 1 ]; then
    echo "=== All failures"
    grep -n -E "$ERRPAT" "$LOG" | grep -v -E "$NOISE" | sed 's/^/  /'
    echo
fi

# ---------------------------------------------------------------- mod-specific

if [ -n "$MOD" ]; then
    echo "=== Lines mentioning $MOD"
    if grep -q -- "$MOD" "$LOG"; then
        grep -n -- "$MOD" "$LOG" | sed 's/^/  /'
    else
        echo "  none."
        echo "  If your IMod constructor logs a line and it is not here, the class was never"
        echo "  constructed — which is a different problem from anything in your code."
    fi
fi
