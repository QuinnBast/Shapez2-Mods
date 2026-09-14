#!/usr/bin/env bash
#
# Scaffold a buildable shapez 2 mod project.
#
# Produces a project that compiles and loads and does nothing else — the point is that
# the build/install/log loop is known-good before you write any behaviour, so the first
# failure you see belongs to your code.

set -euo pipefail

usage() {
    cat <<'EOF'
new-mod.sh NAME [options]

  --author NAME      author string for manifest.json (default: git user.name)
  --namespace NS     root namespace (default: <Author>.Shapez2.<Name>)
  --dir PATH         where to create it (default: current directory)
  --no-props         do not write Directory.Build.props

NAME must be a valid C# identifier — it becomes the project, the assembly, the mod
folder under mods/, and the class name.

Creates NAME/ containing the csproj, manifest.json, translations.json, an IMod entry
point, and .gitignore; plus Directory.Build.props alongside it.
EOF
}

[ $# -eq 0 ] && { usage >&2; exit 1; }

NAME=""; AUTHOR=""; NS=""; DEST="."; WRITE_PROPS=1
while [ $# -gt 0 ]; do
    case "$1" in
        -h|--help)   usage; exit 0 ;;
        --author)    AUTHOR="${2:-}"; shift 2 ;;
        --namespace) NS="${2:-}"; shift 2 ;;
        --dir)       DEST="${2:-}"; shift 2 ;;
        --no-props)  WRITE_PROPS=0; shift ;;
        -*)          echo "unknown option: $1" >&2; exit 1 ;;
        *)           [ -n "$NAME" ] && { echo "unexpected argument: $1" >&2; exit 1; }
                     NAME="$1"; shift ;;
    esac
done

[ -z "$NAME" ] && { echo "NAME is required" >&2; exit 1; }
if ! printf '%s' "$NAME" | grep -qE '^[A-Za-z_][A-Za-z0-9_]*$'; then
    echo "NAME must be a valid C# identifier (letters, digits, underscore; not starting with a digit)" >&2
    exit 1
fi

[ -z "$AUTHOR" ] && AUTHOR=$(git config user.name 2>/dev/null || echo "Author")
if [ -z "$NS" ]; then
    SAFE_AUTHOR=$(printf '%s' "$AUTHOR" | tr -cd '[:alnum:]')
    [ -z "$SAFE_AUTHOR" ] && SAFE_AUTHOR="Author"
    NS="${SAFE_AUTHOR}.Shapez2.${NAME}"
fi

ROOT="$DEST/$NAME"
[ -e "$ROOT" ] && { echo "$ROOT already exists — refusing to overwrite" >&2; exit 1; }
mkdir -p "$ROOT"

subst() { sed -e "s|@@NAME@@|$NAME|g" -e "s|@@NS@@|$NS|g" -e "s|@@AUTHOR@@|$AUTHOR|g"; }

# --------------------------------------------------------------------- csproj

cat <<'XML' | subst > "$ROOT/$NAME.csproj"
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <!-- netstandard2.1 matches the game's Unity runtime. -->
        <TargetFramework>netstandard2.1</TargetFramework>
        <LangVersion>10</LangVersion>
        <Nullable>disable</Nullable>
        <!--
            Every type goes in our own namespace. The game has ~2,800 types in the global
            namespace, so Data, Entry and Configuration are all collisions waiting.
        -->
        <RootNamespace>@@NS@@</RootNamespace>
        <!-- MonoMod.Utils ships its own copy of the publicizer attribute. -->
        <NoWarn>$(NoWarn);CS0436</NoWarn>
    </PropertyGroup>

    <!--
        A build IS an install: this writes straight into the game's mod folder, so the
        game must be CLOSED or the copy fails with MSB3027 "user-mapped section open".
        Without AppendTargetFrameworkToOutputPath the dll lands in a netstandard2.1/
        subfolder, and mod discovery only scans immediate subdirectories of mods/.
    -->
    <PropertyGroup>
        <OutputPath>$(SPZ2_PERSISTENT)\mods\@@NAME@@</OutputPath>
        <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    </PropertyGroup>

    <!--
        Repeated from Directory.Build.props on purpose: that file is imported BEFORE the
        project body, so the unconditional OutputPath above would otherwise win.
    -->
    <PropertyGroup Condition="'$(Dev)' == 'true'">
        <OutputPath>$(SPZ2_PERSISTENT)\mods-dev\@@NAME@@\</OutputPath>
    </PropertyGroup>

    <!--
        Private=False on every game reference is not tidiness. Without it a publicized
        copy of the assembly is written into the mod folder, where it shadows the real
        one at load and produces FieldAccessException at runtime from code that compiled.
    -->
    <ItemGroup>
        <Reference Include="ShapezShifter">
            <HintPath>$(SPZ2_SHIFTER)</HintPath>
            <Private>False</Private>
        </Reference>
        <!-- IMod, the entry point the loader constructs. -->
        <Reference Include="Game.Core.Modding">
            <HintPath>$(SPZ2_PATH)\Game.Core.Modding.dll</HintPath>
            <Private>False</Private>
        </Reference>
        <Reference Include="SPZGameAssembly">
            <HintPath>$(SPZ2_PATH)\SPZGameAssembly.dll</HintPath>
            <Private>False</Private>
        </Reference>
        <Reference Include="Core">
            <HintPath>$(SPZ2_PATH)\Core.dll</HintPath>
            <Private>False</Private>
        </Reference>
    </ItemGroup>

    <!--
        The warning "Assembly is marked for publicization, but no members were
        publicized" appears on builds where this DID work. Ignore it; verify with a
        throwaway file that touches one private member.
    -->
    <PropertyGroup>
        <PublicizeAll>true</PublicizeAll>
        <PublicizerClearCacheOnClean>true</PublicizerClearCacheOnClean>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Krafs.Publicizer" Version="2.3.0">
            <PrivateAssets>all</PrivateAssets>
        </PackageReference>

        <!--
            ExcludeAssets=runtime is required. MonoMod and Cecil are already loaded in
            the process - ShapezShifter depends on them as their own workshop mod - and
            two copies of one assembly in an AppDomain is a real problem.
        -->
        <PackageReference Include="MonoMod.RuntimeDetour" Version="25.3.4">
            <ExcludeAssets>runtime</ExcludeAssets>
        </PackageReference>

        <None Update="manifest.json">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
        <None Update="translations.json">
            <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
        </None>
    </ItemGroup>

</Project>
XML

# ------------------------------------------------------------------- manifest

cat <<'JSON' | subst > "$ROOT/manifest.json"
{
  "Version": "0.1.0",
  "Title": "@@NAME@@",
  "Description": "TODO",
  "Author": "@@AUTHOR@@",
  "SavedModVersionCompabilityRangeWithSelf": "<1.0.0",
  "GameVersionSupportRange": "*",
  "AffectsSaveGames": false,
  "DisablesAchievements": false,
  "Conflicts": [],
  "Assemblies": [
    "@@NAME@@.dll"
  ],
  "Dependencies": [
    {
      "ModId": "steam:3542611357",
      "ModTitle": "Shapez Shifter",
      "Version": "1.0.*"
    }
  ]
}
JSON

# --------------------------------------------------------------- translations

cat <<'JSON' | subst > "$ROOT/translations.json"
{
  "en-US": {
    "@@NAME@@.hello": "Hello from @@NAME@@"
  }
}
JSON

# --------------------------------------------------------------- entry point

cat <<'CS' | subst > "$ROOT/$NAME.cs"
using System;
using Game.Core.Modding;

namespace @@NS@@;

/// <summary>
/// Entry point. The loader constructs this once, at mod load, while the main menu is up.
/// </summary>
/// <remarks>
/// A throw from here is NOT contained by ModLoader — it comes out through
/// ModLoadingStep.LoadMods and kills the game's entire mod loading step, taking every
/// other installed mod with it. So nothing here may throw, and that includes static
/// field initialisers, which run in declaration order before the constructor body.
///
/// There is also no map at this point. Anything that touches the world belongs on a
/// session hook, not here.
/// </remarks>
public class @@NAME@@ : IMod
{
    public @@NAME@@()
    {
        // Prefix every log line with the mod name. Player.log is busy, and this line is
        // how you learn whether the class was constructed at all — which is a different
        // problem from anything in your own code.
        Console.WriteLine("@@NAME@@: loaded");
    }

    public void Dispose()
    {
        // Global hooks come off here. Anything installed per-session comes off when the
        // map goes away, or it leaks into the next save holding a dead map reference.
    }
}
CS

# ---------------------------------------------------------------- .gitignore

cat <<'IGNORE' > "$ROOT/.gitignore"
bin/
obj/
logs/
IGNORE

# ------------------------------------------------------------------- props

if [ "$WRITE_PROPS" -eq 1 ] && [ ! -f "$DEST/Directory.Build.props" ]; then
    cat <<'XML' > "$DEST/Directory.Build.props"
<!--
    Imported automatically by MSBuild for every project beneath it.

    Adds one build mode:

        dotnet build -p:Dev=true

    which stages into <persistent>/mods-dev/<Project>/ instead of installing into mods/.
    The installed copy is memory-mapped by a running game and cannot be overwritten, so
    an ordinary build produces nothing new while the game is open. Mod discovery only
    scans immediate subdirectories of mods/, so a staged build is never picked up as a
    second mod.

    Reload it in-game with Mod Reloader:  mrl.reload <modname>
-->
<Project>

    <PropertyGroup Condition="'$(Dev)' == 'true' And '$(SPZ2_PERSISTENT)' != ''">
        <OutputPath>$(SPZ2_PERSISTENT)\mods-dev\$(MSBuildProjectName)\</OutputPath>
        <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    </PropertyGroup>

</Project>
XML
fi

# ---------------------------------------------------------------------- done

HERE=$(dirname "$0")
W=$(( ${#NAME} + 10 )); [ "$W" -lt 18 ] && W=18

printf '\nCreated %s\n\n' "$ROOT"
printf "  %-${W}s %s\n" "$NAME.csproj"      "references, publicizer, install path"
printf "  %-${W}s %s\n" "$NAME.cs"          "IMod entry point, logs one line on load"
printf "  %-${W}s %s\n" "manifest.json"     "Assemblies must list $NAME.dll or the mod loads with no code"
printf "  %-${W}s %s\n" "translations.json" "language code first, then flat key -> text"
printf "  %s\n" ".gitignore"

cat <<EOF

Next:

  1. bash $HERE/check-env.sh
     env vars set, game closed

  2. dotnet build
     compiles AND installs into mods/$NAME

  3. start the game, enable $NAME in the mods menu

  4. bash $HERE/scan-log.sh --mod $NAME

Step 4 must show "$NAME: loaded". If it does not, the class was never constructed and
nothing you write will run — fix that before adding any behaviour.

AffectsSaveGames is false. Set it true before shipping anything that adds content, or
saves made with the mod will break when it is removed.
EOF
