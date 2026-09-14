# Set up a shapez 2 mod project

`scripts/new-mod.sh MyMod` produces everything on this page. Read it when the scaffold
needs changing, or when a build behaves strangely.

## Three environment variables drive everything

| Variable | Points at |
| --- | --- |
| `SPZ2_PATH` | the game's managed assemblies directory (`shapez 2_Data/Managed`) |
| `SPZ2_PERSISTENT` | `~/AppData/LocalLow/tobspr Games/shapez 2` |
| `SPZ2_SHIFTER` | `ShapezShifter.dll` — **the file**, not its folder |

On Windows the game writes them for you:

```
"shapez 2.exe" --set-modding-env-vars
```

They must be visible to the process running `dotnet build`. A terminal opened before the
variables were set does not have them, and the failure is a csproj with empty `HintPath`s
producing hundreds of unrelated "type or namespace not found" errors.
`scripts/check-env.sh` separates that from a real compile error in one line.

## The project

Target **`netstandard2.1`**, matching the game's Unity runtime.

```xml
<PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>10</LangVersion>
    <RootNamespace>YourName.Shapez2.MyMod</RootNamespace>
    <!-- MonoMod.Utils ships its own copy of the publicizer attribute -->
    <NoWarn>$(NoWarn);CS0436</NoWarn>
</PropertyGroup>
```

Every game reference must be `<Private>False</Private>`:

```xml
<Reference Include="ShapezShifter">
    <HintPath>$(SPZ2_SHIFTER)</HintPath>
    <Private>False</Private>
</Reference>
<Reference Include="SPZGameAssembly">
    <HintPath>$(SPZ2_PATH)\SPZGameAssembly.dll</HintPath>
    <Private>False</Private>
</Reference>
```

**`Private=False` is not a tidiness preference.** Without it the publicized copy of a game
assembly is written into your mod folder, where it shadows the real one at load. The
symptom is `FieldAccessException` or `MethodAccessException` at runtime from code that
compiled cleanly.

`Game.Core.Modding.dll` supplies `IMod`. Add `Core.dll`, `Core.Localization.dll` and the
`UnityEngine.*Module` assemblies only as you need them.

## Building is installing

```xml
<PropertyGroup>
    <OutputPath>$(SPZ2_PERSISTENT)\mods\MyMod</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
</PropertyGroup>
```

`dotnet build` now writes straight into the game's mod folder. Two consequences:

- **The game must be closed.** A running game holds the DLL memory-mapped and the build
  fails with `MSB3021` / `MSB3027 … user-mapped section open`. That is the copy failing —
  the compile already succeeded.
- To compile-check without closing the game, redirect the output:
  `dotnet build -p:OutputPath=/tmp/verify/`.

`AppendTargetFrameworkToOutputPath` matters. Without it the DLL lands in a
`netstandard2.1/` subfolder, and mod discovery — which scans only immediate subdirectories
of `mods/` — never sees it.

### Dev staging

A `Directory.Build.props` beside the solution adds a second mode, for use with Mod
Reloader:

```xml
<Project>
  <PropertyGroup Condition="'$(Dev)' == 'true' And '$(SPZ2_PERSISTENT)' != ''">
    <OutputPath>$(SPZ2_PERSISTENT)\mods-dev\$(MSBuildProjectName)\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
</Project>
```

`Directory.Build.props` is imported **before** the project body, so an unconditional
`OutputPath` in the csproj overrides it. Repeat the `Dev` condition in the csproj itself.

`mods-dev/` sits deliberately outside `mods/`, so a staged build is never discovered as a
second copy of the mod.

## manifest.json

Sits beside the DLL, and must be copied on every build:

```xml
<None Update="manifest.json">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

```json
{
  "Version": "1.0.0",
  "Title": "My Mod",
  "Description": "What it does",
  "Author": "You",
  "SavedModVersionCompabilityRangeWithSelf": "<1.1.0",
  "GameVersionSupportRange": "*",
  "AffectsSaveGames": true,
  "DisablesAchievements": false,
  "Conflicts": [],
  "Assemblies": [ "MyMod.dll" ],
  "Dependencies": [
    { "ModId": "steam:3542611357", "ModTitle": "Shapez Shifter", "Version": "1.0.*" }
  ]
}
```

- `Assemblies` is optional to the loader — it reads
  `serializableModManifest.Assemblies ?? Array.Empty<string>()` — so a typo here produces a
  mod that loads with no code and no error. If your constructor never logs, check this
  before anything else.
- **`AffectsSaveGames: true` means the mod cannot be added to or removed from an existing
  save.** Set it honestly: content mods need it, a pure HUD overlay does not.
- `GameVersionSupportRange: "*"` lets the game load the mod into a version you have never
  tested. Narrow it once you have hooks, because the shipped assemblies carry no
  compatibility promise.
- `steam:3542611357` is ShapezShifter's workshop id.

## Publicizer

Private game members are unreachable without it:

```xml
<PropertyGroup>
    <PublicizeAll>true</PublicizeAll>
    <PublicizerClearCacheOnClean>true</PublicizerClearCacheOnClean>
</PropertyGroup>
<PackageReference Include="Krafs.Publicizer" Version="2.3.0">
    <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

**The warning `Assembly is marked for publicization, but no members were publicized`
appears on builds where publicization worked.** It is not a diagnosis. The discriminating
test is a throwaway file that touches one private member; if it compiles, publicization
applied at compile time.

## NuGet packages must not ship

MonoMod and Cecil are already loaded in the process — ShapezShifter depends on them as
their own workshop mod. Two copies of one assembly in one AppDomain is a real problem:

```xml
<PackageReference Include="MonoMod.RuntimeDetour" Version="25.3.4">
    <ExcludeAssets>runtime</ExcludeAssets>
</PackageReference>
```

The same reasoning applies to sharing code between your own mods: distribute it as
**source** (a shared `.props` that imports `.cs` files), not as an assembly. A mod that
hard-depends on another mod is worth avoiding for a few hundred lines.

## The entry point

```csharp
namespace YourName.Shapez2.MyMod;

public class MyMod : IMod
{
    public void Dispose() { }
}
```

The class must be `public` and implement `IMod`, and the assembly must be listed in
`Assemblies`. Log one line from the constructor, prefixed with the mod name — `Player.log`
is busy, and that line is how you know the class was constructed at all.

**There is no map at mod-load time.** The constructor runs at the main menu; touching the
map there is a `NullReferenceException`. See `docs/howto/run-code-when-game-loads.md` for
the session hooks.

Never let the constructor throw — see rule 3 in `SKILL.md`.

## Hot reload

With Mod Reloader installed, `mrl.reload <modname>` picks up `mods-dev/`. It reloads
**code only**:

- logic, detours, HUD providers → works
- new islands, new toolbar entries, collider or definition changes → **restart required**

Definitions and the toolbar are built per session, and `AtomicIslands…Build()` returns no
handle, so a mod cannot unregister them in `Dispose`. Reloading and then re-entering a
session double-registers and crashes on a duplicate key.

## Publishing

`steamcmd` plus a `.vdf` manifest; the appid is `2162800`. Start at `visibility "2"`
(private). Copying a sample's `Steam/` folder brings a tobspr developer login *and* that
sample's real `publishedfileid` — set the id to `0` and replace the login before the first
run. Full procedure in `docs/howto/publish-to-workshop.md`.

## Deeper reading

Long-form pages in the community docs repo (`shapez2-modding-docs`):

`docs/getting-started.md` ·
`docs/publicizer.md` ·
`docs/mod-lifecycle.md` ·
`docs/howto/run-code-when-game-loads.md` ·
`docs/howto/publish-to-workshop.md`
