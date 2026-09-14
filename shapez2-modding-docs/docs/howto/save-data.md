# Store data in the save

**Problem.** Your mod has settings or state that should travel with the player's save.

**Solution.** `ModSaveDataExtensions` attaches a serialisable class of yours to the
savegame. You never touch serialisation code.

## Attach it

```csharp
using ShapezShifter.Flow;

public class MySaveData
{
    public bool OverlayEnabled = true;
    public int ColorScheme;
}

public MyMod(ILogger logger)
{
    this.AttachSaveData<MySaveData>();   // needs a parameterless constructor
}
```

With non-default initial values:

```csharp
this.AttachSaveData(() => new MySaveData { OverlayEnabled = false });
```

Attach in the constructor. It has to be registered before a save is loaded, or there is
nothing to deserialise into.

## Read and write it

```csharp
MySaveData data = this.GetSaveData<MySaveData>();

data.OverlayEnabled = !data.OverlayEnabled;   // mutate in place; it is your object
```

The full surface:

```csharp
void AttachSaveData<T>()                                   // and Func<T> / IFactory<T> overloads
T    GetSaveData<T>()
void ResetSaveDataToDefault<T>()
void DetachSaveData<T>()
void RegisterToBeforeSaveDataSerialized<T>(Action<T>)
void RegisterToAfterSaveDataDeserialized<T>(Action<T>)
void UnregisterToBeforeSaveDataSerialized<T>(Action<T>)
void UnregisterToAfterSaveDataDeserialized<T>(Action<T>)
```

## The two lifecycle hooks

```csharp
// Flush live state into the object just before it is written:
this.RegisterToBeforeSaveDataSerialized<MySaveData>(data => data.OverlayEnabled = Enabled);

// Apply loaded values once the save has been read:
this.RegisterToAfterSaveDataDeserialized<MySaveData>(data => Enabled = data.OverlayEnabled);
```

`AfterSaveDataDeserialized` is also a clean answer to "when has a game loaded?" for
anything that only depends on your own data — it fires with the values already
populated. For anything touching the *map*, still use the map check in
[Run code when a game loads](run-code-when-game-loads.md).

## Say so in the manifest

```json
"AffectsSaveGames": true,
"SavedModVersionCompabilityRangeWithSelf": "<1.1.0"
```

`AffectsSaveGames` marks saves as depending on your mod. `SavedModVersionCompabilityRangeWithSelf`
(the typo is the game's) declares which versions of *your own* mod can read data written
by this one — tighten it whenever you change your data class incompatibly.

An overlay that only reads and draws needs none of this; it should set
`AffectsSaveGames: false` and keep settings in a config file instead, so players can
share saves freely.

## The key is your type's full name — moving it orphans the data

`ModSaveDataExtensions.ResolveId<T>()` is:

```csharp
mod.GetType().Assembly.GetName().Name + "-" + typeof(T).FullName
```

`Type.FullName` **includes the namespace**. So renaming the namespace your save-data class
lives in, moving it to another namespace, or renaming the class, changes the key — and the
data already in players' saves is silently orphaned. No error, no warning: the mod simply
comes up with defaults, as if the player had never set anything.

Changing the **assembly name** does the same, and that one also breaks `manifest.json`'s
`Assemblies` entry, so it fails more loudly.

If you have shipped a mod that uses `AttachSaveData<T>`, treat that type's full name as
part of its public contract. When you must move it, keep the old class where it was as a
thin shim, read from it once, and write into the new one.

This is unlike the `[SyncableIdentifier("…")]` attribute used for simulation state, whose
key is an explicit string the compiler never sees — `PolymorphicSerializer` does
`TypesById.Add(attribute.Id, type)` with no type-name fallback. Namespaces are free to
move around those.

## Gotchas

- Data is stored per save. Global preferences belong in your own file under the mod
  folder (`ModDirectoryLocator` / `ModFolderLocator` find it).
- Adding or removing fields between versions is a compatibility decision. Old saves
  deserialise into the new shape, so give new fields sensible defaults and never assume
  a field was written.
- `GetSaveData<T>` before anything is attached or loaded will not give you a useful
  object — attach in the constructor and read after load.
