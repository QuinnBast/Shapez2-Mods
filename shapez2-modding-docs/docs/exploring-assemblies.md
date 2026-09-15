# Exploring the Assemblies

The [API reference](../api/index.md) on this site tells you *what* exists. When you need to know
*how the game does something*, you want the method bodies — and for that you decompile
locally. This page is the workflow that produced most of this guide.

## Install ilspycmd

```bash
dotnet tool install -g ilspycmd
```

The published tool targets .NET Core 3.1, which you almost certainly do not have. Roll
it forward instead of installing an ancient runtime:

```bash
export DOTNET_ROLL_FORWARD=LatestMajor   # PowerShell: $env:DOTNET_ROLL_FORWARD="LatestMajor"
ilspycmd --version
```

## List the types in an assembly

```bash
M="$SPZ2_PATH"
ilspycmd -l c "$M/Game.Core.Map.Simulation.dll"   # classes
ilspycmd -l i "$M/Game.Core.Map.Simulation.dll"   # interfaces
ilspycmd -l s "$M/Game.Core.Map.Simulation.dll"   # structs
ilspycmd -l e "$M/Game.Core.Map.Simulation.dll"   # enums
```

Pass one kind per invocation — the comma-separated form documented in `--help` silently
produces nothing.

Building one grep-able index of every type across every assembly is the single most
useful thing you can do up front:

```bash
mkdir -p /tmp/spz2types
for f in "$M"/Game*.dll "$M"/Core*.dll "$M"/SPZGameAssembly.dll; do
  b=$(basename "$f" .dll)
  { ilspycmd -l c "$f"; ilspycmd -l i "$f"; ilspycmd -l s "$f"; ilspycmd -l e "$f"; } \
    2>/dev/null | grep -v '<' | sed "s|^|$b |" > "/tmp/spz2types/$b.txt"
done
cat /tmp/spz2types/*.txt > /tmp/allTypes.txt
```

That is about 5,200 types. Now "where does efficiency live?" is one command:

```bash
grep -iE "efficien|throughput|statistic" /tmp/allTypes.txt
```

## Decompile a single type

```bash
ilspycmd -t HUDSidePanelModuleBuildingEfficiency "$M/SPZGameAssembly.dll"
```

Two failure modes you will hit:

- *"Decompiling types that are not part of the main module is not supported"* — the type
  is forwarded; find its real assembly in your type index.
- *"Could not find type definition … in type system"* — you have the namespace wrong.
  Nested and namespaced types need the full name.

## Decompile everything into a source tree

The highest-leverage version. Output is a real project layout you can grep and open in
an IDE:

```bash
OUT="$HOME/spz2-decompiled"
for n in SPZGameAssembly Game.Content Game.Content.Features Game.Core \
         Game.Core.Map.Model Game.Core.Map.Simulation Game.Core.Coordinates \
         Game.Core.Rendering Game.Orchestration Game.Interaction Core; do
  mkdir -p "$OUT/$n"
  ilspycmd -o "$OUT/$n" -p -r "$M" "$M/$n.dll"
done
```

`-p` writes a project, `-r` supplies the reference path so types resolve. The output
directory **must already exist** — ilspycmd will not create it.

Roughly 4,000 files and 20 MB for the assemblies above. It does not compile, and it is
not meant to; it is a searchable answer key.

### That list is not the whole game

The eleven assemblies above cover the simulation, the map and the rendering, which is
most of what a mod touches. They do **not** cover the interface. Notably absent:

| Assembly | What is in it |
| --- | --- |
| `Toolbar` | `ToolbarModel`, `ToolbarQuery`, `IToolbar` — the *runtime* toolbar, as opposed to the `ToolbarData` authoring tree in `Game.Orchestration` |
| `Game.Hud.View` | `HUDToolbar`, `HUDToolbarView`, `HUDToolbarSlotView` — the bar the player actually clicks |
| `Game.Hud.View.Model` | `IPlacementToolbarElement`, `ToolbarSlotPresentationData` |
| `Game.Core.HUD`, `Game.Blueprints`, `Game.Core.Blueprint*`, `Game.Core.Simulation`, `Game.Core.Map`, `Game.Core.Map.Layout`, `Game.Logic`, `Game.Tutorial` | as named |

The failure mode is quiet and misleading: `grep -rn "class ToolbarModel"` returns
nothing, and the obvious conclusion — that the type does not exist — is wrong. It is used
all over `Game.Orchestration`, which *is* decompiled. **A type that appears in usages but
has no definition anywhere in the tree is a missing assembly, not a missing type.**

`DOTNET_ROLL_FORWARD` (above) matters here too, and the framework it rolls forward *from*
depends on the tool version: 3.1 for `ilspycmd` 7.x, 6.0 for 8.x. Updating to pick up a
newer target can fail with *"Settings file 'DotnetToolSettings.xml' was not found in the
package"* — that is a packaging problem in some published versions, not anything local, so
pin a version instead of taking the latest: `dotnet tool update -g ilspycmd --version 8.2.0.7535`.

Add what you need to the loop. If you only want to know what is on a type, metadata is
enough and faster than a decompile — `ReflectionOnlyLoadFrom` plus a resolver for the
Managed folder, then `GetMembers`:

```powershell
$dir = "$env:SPZ2_PATH"
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve([ResolveEventHandler]{
    param($s, $e)
    $f = Join-Path 'C:\full\path\to\Managed' (($e.Name -split ',')[0] + '.dll')
    if (Test-Path $f) { [Reflection.Assembly]::ReflectionOnlyLoadFrom($f) } else { $null }
})
$a = [Reflection.Assembly]::ReflectionOnlyLoadFrom("$dir\Game.Hud.View.dll")
$t = try { $a.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $_.Exception.Types | ? { $_ } }
($t | ? Name -eq 'HUDToolbarView').GetMembers('Public,NonPublic,Instance,DeclaredOnly') | % { $_.ToString() }
```

Two things will bite you. The resolver must have the directory **baked in** — `$using:`
and closure capture do not reach inside a `ResolveEventHandler`, and a resolver that
silently resolves nothing looks exactly like an assembly with two types in it. And
`GetTypes` throws `ReflectionTypeLoadException` when anything fails to load; the partial
list on `.Types` is usually all you need, so catch it rather than letting the count
mislead you.

## The questions it answers

Once the tree exists, most API questions become one `grep`:

```bash
# Who calls this, and what do they pass?
grep -rn "UXOverviewModeMapResourcePlaneMaterial" "$OUT"

# What implements this interface?
grep -rn ": IMapSubDrawer" "$OUT"

# How does the game itself do the thing I want to do?
grep -rn "DrawMesh(" "$OUT/SPZGameAssembly" | head -40
```

The third one is the important habit. Nearly every problem a mod faces — drawing a
coloured plane over a chunk, measuring a machine's throughput, caching a combined mesh —
is something the game already solves somewhere, and copying its approach is both faster
and likelier to keep working than inventing one.

## Regenerating this site's API reference

The reference here is generated by [DocFX](https://dotnet.github.io/docfx/) straight
from the assemblies — no decompilation involved:

```bash
dotnet tool install -g docfx

bash scripts/write-local-config.sh          # substitute your SPZ2_* paths
docfx metadata docfx.metadata.local.json    # assemblies → api/*.yml
docfx build docfx.ci.json                   # → _site/
docfx serve _site                           # http://localhost:8080
```

`docfx.metadata.json` lists the assemblies under `metadata.src` and points `references`
at the whole `Managed` folder so dependencies resolve; the paths are substitution tokens
that `write-local-config.sh` fills in for your machine. Add an assembly to that list and
rerun to extend the reference.

> [!NOTE]
> A word on redistribution: signatures are one thing, decompiled method bodies are
> another. Keep your local decompiled tree local, and out of any repository you publish.
