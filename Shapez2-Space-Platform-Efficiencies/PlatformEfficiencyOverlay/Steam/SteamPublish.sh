#!/usr/bin/env bash

# Publishes the built mod folder to the workshop item named in base.vdf.
#
#   dotnet build -t:SteamPublish          from the project folder, the normal way
#   bash Steam/SteamPublish.sh <folder>   by hand, folder being what to upload
#
# With no folder it takes the installed copy under SPZ2_PERSISTENT, and refuses to run if
# that does not look like a built mod. It used to accept an empty content path, which
# steamcmd is perfectly happy with: it uploads the preview image, reports "Success", and
# leaves the item's files exactly as they were.

set -u

CONTENT_PATH=${1:-}

# Everything is resolved from where this script lives, not from the working directory.
# Composing paths out of $PWD meant running it from inside Steam/ produced Steam\Steam\.
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
BASE_VDF="$SCRIPT_DIR/base.vdf"
TMP_VDF_POSIX="$SCRIPT_DIR/base.tmp.vdf"
LOG="$SCRIPT_DIR/publish.log"

# Everything from here is written to publish.log as well as to the console. Run from MSBuild or
# an IDE, the console this gets is often a window that closes the moment the script ends, and
# the one run that mattered was the one nobody could read.
exec > >(tee "$LOG") 2>&1

echo "transcript: $LOG"

# --- refuse to hand steamcmd a file it cannot parse -------------------------------------
#
# A VDF value is a quoted string and Valve's KeyValues parser has escape sequences OFF, so one
# double quote inside the description ends the value early. steamcmd then prints an assertion
# into its own stderr.txt, exits zero, creates nothing, and this script used to say "published
# file ID:" with an empty id and carry on.
validate_vdf() {
  file="$1"

  # Read the value exactly as the parser will: opening quote, then everything up to the very
  # next quote. If that lands in the middle of the description, the character after it is prose
  # rather than the start of another key or the closing brace - which is the whole tell.
  #
  # Counting quotes in a line range was the first attempt and it was wrong: a vdf may carry keys
  # after the description (changenote does), and their quotes counted as damage.
  if ! awk 'BEGIN { RS = "\x00" }
    {
      i = index($0, "\"description\"")
      if (i == 0) { exit 2 }

      rest = substr($0, i + 13)
      j = index(rest, "\"")
      if (j == 0) { exit 2 }

      body = substr(rest, j + 1)
      k = index(body, "\"")
      if (k == 0) { exit 2 }

      after = substr(body, k + 1)
      sub(/^[ \t\r\n]+/, "", after)
      head = substr(after, 1, 1)

      if (head != "\"" && head != "}") { exit 1 }
    }' "$file"; then
    echo "error: the description value in base.vdf ends early - it almost certainly has a" >&2
    echo "       double quote in it." >&2
    echo "       Valve's KeyValues parser does not honour \\\" escapes, so the first one ends" >&2
    echo "       the value and steamcmd fails with:" >&2
    echo "         KeyValues.cpp : Assertion Failed: Error while parsing text KeyValues" >&2
    echo "       Use apostrophes in description.bbcode, then: python Steam/build-vdf.py" >&2
    return 1
  fi

  if LC_ALL=C grep -n '[^[:print:][:space:]]' "$file" >/dev/null 2>&1; then
    echo "error: base.vdf contains non-ASCII bytes. Every vdf that has published from this" >&2
    echo "       repo is plain ASCII - use - for a dash and ... for an ellipsis:" >&2
    LC_ALL=C grep -n '[^[:print:][:space:]]' "$file" | head -5 >&2
    return 1
  fi

  return 0
}

# Checks the vdf and stops, so the thing that broke the last publish can be caught without
# uploading anything.
if [ "${1:-}" = "--check" ]; then
  validate_vdf "$BASE_VDF" || exit 1
  echo "base.vdf parses as far as this can tell: no quotes in the description, plain ASCII."
  exit 0
fi


# --- what to upload ---------------------------------------------------------

if [ -z "$CONTENT_PATH" ]; then
  INSTALLED="${SPZ2_PERSISTENT:-}/mods/PlatformEfficiencyOverlay"

  if [ -n "${SPZ2_PERSISTENT:-}" ] && [ -d "$INSTALLED" ]; then
    CONTENT_PATH="$INSTALLED"
    echo "no content folder given; using the installed mod: $CONTENT_PATH"
  else
    echo "error: no content folder given and none found under SPZ2_PERSISTENT." >&2
    echo "       Pass the folder to upload, or publish with:" >&2
    echo "           dotnet build -t:SteamPublish" >&2
    exit 1
  fi
fi

# Refuse to publish something that is not a built mod. An empty or wrong folder is the one
# mistake here that looks like success.
for required in manifest.json PlatformEfficiencyOverlay.dll; do
  if [ ! -f "$CONTENT_PATH/$required" ]; then
    echo "error: $CONTENT_PATH has no $required, so it is not a built mod folder." >&2
    echo "       Build first, then publish." >&2
    exit 1
  fi
done

VERSION=$(grep -m1 '"Version"' "$CONTENT_PATH/manifest.json" | sed 's/.*"Version"[^"]*"\([^"]*\)".*/\1/')
echo "publishing version ${VERSION:-unknown} from $CONTENT_PATH"

# --- the paths steamcmd wants, which are Windows ones with escaped separators -----------

CONTENT_PATH=$(cygpath -w "$CONTENT_PATH")
PREVIEW_IMG=$(cygpath -w "$SCRIPT_DIR/preview.png")

CONTENT_PATH="${CONTENT_PATH//\\/\\\\}"
PREVIEW_IMG="${PREVIEW_IMG//\\/\\\\}"

echo "CONTENT_PATH: $CONTENT_PATH"
echo "PREVIEW_IMG: $PREVIEW_IMG"

export CONTENT_PATH
export PREVIEW_IMG

validate_vdf "$BASE_VDF" || exit 1

# Fill the absolute paths into a copy, leaving base.vdf as the checked-in template.
envsubst < "$BASE_VDF" > "$TMP_VDF_POSIX"

cat "$TMP_VDF_POSIX"

TMP_VDF=$(cygpath -w "$TMP_VDF_POSIX")

# --- steamcmd ---------------------------------------------------------------

# It is usually not on PATH, and it has to live somewhere writable because it self-updates
# into its own folder - which rules out Program Files.
find_steamcmd() {
  if [ -n "${STEAMCMD:-}" ] && [ -x "$STEAMCMD" ]; then
    printf '%s' "$STEAMCMD"
    return 0
  fi

  if command -v steamcmd >/dev/null 2>&1; then
    command -v steamcmd
    return 0
  fi

  for candidate in "$HOME/steamcmd/steamcmd.exe" "C:/steamcmd/steamcmd.exe" "${PROGRAMFILES:-C:/Program Files}/SteamCMD/steamcmd.exe" "${LOCALAPPDATA:-}/SteamCMD/steamcmd.exe"
  do
    if [ -x "$candidate" ]; then
      printf '%s' "$candidate"
      return 0
    fi
  done

  return 1
}

STEAMCMD_BIN=$(find_steamcmd || true)

if [ -z "$STEAMCMD_BIN" ]; then
  echo "error: steamcmd not found. Install it somewhere writable (not Program Files -" >&2
  echo "       it self-updates into its own folder) and either put it on PATH or set" >&2
  echo "       STEAMCMD to the full path of steamcmd.exe." >&2
  exit 1
fi

echo "STEAMCMD: $STEAMCMD_BIN"

# Which account to publish as. steamcmd remembers the accounts it has logged in with, so
# after the first interactive login there is nothing to set: the name comes out of its own
# config. STEAM_LOGIN overrides it, and is only needed when more than one is cached.
cached_account() {
  local config
  config="$(dirname "$STEAMCMD_BIN")/config/config.vdf"

  [ -f "$config" ] || return 1

  # Inside the Accounts block, an account name is the only thing on its line - the keys
  # underneath it all carry a value on the same line. The tail drops the block header,
  # which is alone on its line too.
  sed -n '/"Accounts"/,/^\t\}/p' "$config" \
    | tail -n +2 \
    | grep -oE '^[[:space:]]*"[^"]+"[[:space:]]*$' \
    | tr -d ' \t"'
}

if [ -z "${STEAM_LOGIN:-}" ]; then
  ACCOUNTS=$(cached_account || true)
  COUNT=$(printf '%s' "$ACCOUNTS" | grep -c . || true)

  if [ "$COUNT" = "1" ]; then
    STEAM_LOGIN="$ACCOUNTS"
    echo "STEAM_LOGIN not set; using the account steamcmd has cached: $STEAM_LOGIN"
  elif [ "$COUNT" -gt 1 ] 2>/dev/null; then
    echo "error: steamcmd has more than one account cached. Set STEAM_LOGIN to the one to" >&2
    echo "       publish as. Cached:" >&2
    printf '         %s\n' $ACCOUNTS >&2
    exit 1
  else
    echo "error: no cached steamcmd account and STEAM_LOGIN is not set. Log in once with" >&2
    echo "         \"$STEAMCMD_BIN\" +login <account> +quit" >&2
    exit 1
  fi
fi

# steamcmd can only ask for a password when it owns the terminal. Run from MSBuild - which
# captures stdin - the prompt reads EOF, it submits an empty password, and the login fails
# with "Invalid Password" without ever pausing. Logging in once by hand caches the session
# and every later run is non-interactive.
#
# What is cached is a refresh token, not the password, and it expires - and is invalidated
# by signing in elsewhere, or by a password or Steam Guard change. So expect to repeat that
# interactive login every few days; it is not a sign anything is set up wrongly.
if [ ! -f "$(dirname "$STEAMCMD_BIN")/config/config.vdf" ] || ! grep -qi "ConnectCache\|WebToken" "$(dirname "$STEAMCMD_BIN")/config/config.vdf" 2>/dev/null; then
  echo
  echo "note: steamcmd has no cached login. If this fails with 'Invalid Password' without"
  echo "      prompting, run this once in a normal terminal window and then retry:"
  echo
  echo "          \"$STEAMCMD_BIN\" +login \"$STEAM_LOGIN\" +quit"
  echo
fi

BEFORE_ID=$(sed -n 's/.*"publishedfileid"[ 	]*"\([0-9]*\)".*//p' "$BASE_VDF" | head -1)
STEAM_OUT="$SCRIPT_DIR/.steamcmd.out"

"$STEAMCMD_BIN" +login "$STEAM_LOGIN" +workshop_build_item "$TMP_VDF" +quit 2>&1 | tee "$STEAM_OUT"
STEAM_STATUS=${PIPESTATUS[0]}

# --- did it actually work? --------------------------------------------------
#
# steamcmd is cheerful about failure: a vdf it cannot parse gets an assertion in its own
# stderr.txt, an exit code of zero, and no item. Nothing downstream noticed, so the build went
# green and the mod was not published. Three things have to agree before this reports success.

FILE_ID=$(sed -n 's/.*"publishedfileid"[ \t]*"\([0-9]*\)".*/\1/p' "$TMP_VDF_POSIX" | head -1)

FAILED=""

if [ "$STEAM_STATUS" != "0" ]; then
  FAILED="steamcmd exited with status $STEAM_STATUS"
elif ! grep -qi "success" "$STEAM_OUT"; then
  FAILED="steamcmd never reported success"
elif [ "${BEFORE_ID:-0}" = "0" ] && [ "${FILE_ID:-0}" = "0" ]; then
  FAILED="no workshop item was created - the id is still 0"
fi

rm -f "$TMP_VDF_POSIX" "$STEAM_OUT"

if [ -n "$FAILED" ]; then
  echo
  echo "PUBLISH FAILED: $FAILED" >&2
  echo >&2
  echo "  Full output:   $LOG" >&2
  echo "  steamcmd logs: $(dirname "$STEAMCMD_BIN")/logs/" >&2
  echo "                 stderr.txt there names a parse error in the vdf." >&2
  echo >&2
  echo "  base.vdf was left untouched, so nothing is half-published." >&2
  exit 1
fi

# A first publish is given a fresh id, so copy it back into the template. On an update this
# writes the same id it already had.
echo "published file ID: $FILE_ID"
sed -i 's/\("publishedfileid"[ \t]*"\)[0-9]\+"/\1'"$FILE_ID"'"/' "$BASE_VDF"

echo
echo "Check $HOME/steamcmd/logs/workshop_log.txt for what was actually uploaded."
echo "A run that changed the files says 'Uploaded new content ( ManifestID ... )'."
echo "Without that line only the preview and the text changed."

# The category checkboxes are Workshop tags, and workshop_build_item has no key for them -
# it reads appid, publishedfileid, filetype, title, description, visibility, previewfile,
# contentfolder, kvtags and changenote, and nothing else. A "tags" block in base.vdf is an
# unknown key that the KeyValues parser silently drops, and kvtags is AddItemKeyValueTag -
# API metadata, not the categories. Set them on the page below; because nothing here calls
# SetItemTags, later publishes leave them alone.
echo
echo "Item page: https://steamcommunity.com/sharedfiles/filedetails/?id=$FILE_ID"
echo "Categories and extra screenshots are set there, not from this script."
