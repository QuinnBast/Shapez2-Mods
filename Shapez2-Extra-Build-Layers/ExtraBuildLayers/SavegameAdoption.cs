using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Game.Core.Modding;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Lets an existing save be opened with this mod newly installed, while still refusing to open
/// one that used it without it.
///
/// The manifest has to say <c>AffectsSaveGames: true</c>, because removing the mod from a save
/// that used it is genuinely destructive: <c>IslandLayoutSerializer.DeserializeBuilding</c> drops
/// every tall lift ("Could not resolve building … it might have been removed?"), and a vanilla
/// building left on floor 3 or above meets a three-slot mesh array again and takes its chunk's
/// mesh down. But the game treats that flag symmetrically.
/// <c>SavegameModdingContextDivergenceBarrier.RequestConfirmationIfModContextDiverge</c> filters
/// both the removed and the **added** mods to the ones that affect saves, and any left over go to
/// <c>ShowLoadingError</c> - a dialog with only "OK" that does not load. So out of the box a player
/// could not try the mod on the save they already have, which is the main way it is meant to be
/// used.
///
/// The two directions are not alike, though. Adding is safe - a vanilla save holds nothing of
/// ours - and it is also the only direction this mod can do anything about, because when it is
/// being removed it is not loaded. So this drops exactly one entry, this mod, from the **added**
/// list that <c>ComputeModDiff</c> fills, and leaves removal and version checks alone. The save
/// then loads; the next save records this mod like any other; and removing it from that save is
/// refused by the unmodified vanilla path.
///
/// The mod is recognised by its manifest's assembly list rather than its title, which a Workshop
/// listing can rename.
/// </summary>
internal sealed class SavegameAdoption : IDisposable
{
    private const string OwnAssembly = "ExtraBuildLayers.dll";

    private delegate void ComputeModDiff(
        SavegameModdingContextDivergenceBarrier self, SerializedSavegameMetadata saveMetadata,
        ICollection<SavegameModSignature> removedMods, ICollection<SavegameModSignature> addedMods,
        ICollection<SavegameModMismatch> mismatchingMods);

    private delegate void ComputeModDiffHook(
        ComputeModDiff orig, SavegameModdingContextDivergenceBarrier self, SerializedSavegameMetadata saveMetadata,
        ICollection<SavegameModSignature> removedMods, ICollection<SavegameModSignature> addedMods,
        ICollection<SavegameModMismatch> mismatchingMods);

    private readonly ILogger Logger;
    private readonly Hook Hook;

    public SavegameAdoption(ILogger logger)
    {
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            MethodInfo target = typeof(SavegameModdingContextDivergenceBarrier).GetMethod(
                "ComputeModDiff", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (target == null)
            {
                logger.Error?.Log("Extra Build Layers: ComputeModDiff not found; adding the mod to an existing save will be refused.");
                return;
            }

            Hook = new Hook(target, new ComputeModDiffHook(Detour));
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
        }
    }

    private void Detour(
        ComputeModDiff orig, SavegameModdingContextDivergenceBarrier self, SerializedSavegameMetadata saveMetadata,
        ICollection<SavegameModSignature> removedMods, ICollection<SavegameModSignature> addedMods,
        ICollection<SavegameModMismatch> mismatchingMods)
    {
        orig(self, saveMetadata, removedMods, addedMods, mismatchingMods);

        try
        {
            IModId own = OwnModId(self);
            if (own == null)
            {
                return;
            }

            foreach (SavegameModSignature added in addedMods.Where(added => own.Equals(added.ModId)).ToList())
            {
                addedMods.Remove(added);
                Logger.Info?.Log("Extra Build Layers: opening a save made without this mod; it will be required by that save from its next save on.");
            }
        }
        catch (Exception exception)
        {
            // Falls back to vanilla's behaviour: the save is refused, which is safe.
            Logger.Exception?.LogException(exception);
        }
    }

    private static IModId OwnModId(SavegameModdingContextDivergenceBarrier barrier)
    {
        foreach (ResolvedMod mod in barrier.ModdingEnvironment.Context.ResolvedMods)
        {
            string[] assemblies = mod.Metadata?.Assemblies;
            if (assemblies != null &&
                assemblies.Any(name => string.Equals(name, OwnAssembly, StringComparison.OrdinalIgnoreCase)))
            {
                return mod.Descriptor?.ModId;
            }
        }

        return null;
    }

    public void Dispose()
    {
        Hook?.Dispose();
    }
}
