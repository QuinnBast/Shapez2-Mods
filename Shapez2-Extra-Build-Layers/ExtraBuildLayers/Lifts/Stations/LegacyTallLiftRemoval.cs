using System;
using MonoMod.RuntimeDetour;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Lets a save that holds the old per-height tall space lifts load, by dropping them.
///
/// Earlier versions made one island definition per lift height and variant, with ids like
/// <c>ExtraBuildLayers.SpaceBeltsLift27_19</c>. Those definitions are gone - lift stations
/// replaced them - and a missing island is **not** skipped on load: <c>IslandLayoutSerializer</c>
/// skips one quietly only if the game's catalog still knows its id, and otherwise throws
/// "Island definition was not migrated correctly", which fails the whole map. That is a save that
/// cannot be opened.
///
/// The game's own way out is an id migrator that maps the id to <c>IslandDefinitionId.Invalid</c>:
/// <c>Deserialize</c> checks for that straight after running the migrators and calls
/// <c>SkipBlob</c>. Vanilla ships exactly this as <c>IslandDefinitionDeletionMigrator</c>, one id at
/// a time; this is the same thing matching the old ids by prefix, since every height and variant
/// had its own.
///
/// The migrator list is read from the provider into a private list in the serializer's
/// constructor, so a postfix there appends to it. The lift's belts either side load as normal and
/// simply end where it was; dragging the climb again places stations.
/// </summary>
internal sealed class LegacyTallLiftRemoval : IDisposable
{
    private delegate void Ctor(IslandLayoutSerializer self, GameMode mode, IIslandSerializationMigratorsProvider provider, ILogger logger);

    private delegate void CtorHook(Ctor orig, IslandLayoutSerializer self, GameMode mode, IIslandSerializationMigratorsProvider provider, ILogger logger);

    private readonly ILogger Logger;
    private readonly Hook Hook;

    public LegacyTallLiftRemoval(ILogger logger)
    {
        Logger = logger;

        // Contained: a throw out of a mod constructor is not caught by ModLoader.
        try
        {
            Hook = new Hook(
                typeof(IslandLayoutSerializer).GetConstructor(new[]
                {
                    typeof(GameMode), typeof(IIslandSerializationMigratorsProvider), typeof(ILogger),
                }),
                new CtorHook(Detour));
        }
        catch (Exception exception)
        {
            logger.Exception?.LogException(exception);
            logger.Error?.Log("Extra Build Layers: could not hook island loading; a save with old tall space lifts will not open.");
        }
    }

    private void Detour(Ctor orig, IslandLayoutSerializer self, GameMode mode, IIslandSerializationMigratorsProvider provider, ILogger logger)
    {
        orig(self, mode, provider, logger);

        try
        {
            self.IslandDefinitionIdMigrators.Add(new Migrator(Logger));
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    public void Dispose()
    {
        Hook?.Dispose();
    }

    private sealed class Migrator : IIslandDefinitionIdMigrator
    {
        private static readonly string[] OldPrefixes =
        {
            "ExtraBuildLayers.SpaceBeltsLift",
            "ExtraBuildLayers.SpacePipesLift",
        };

        private readonly ILogger Logger;
        private int Dropped;

        public Migrator(ILogger logger)
        {
            Logger = logger;
        }

        public bool CanMigrate(GameVersion version, IslandDefinitionId id)
        {
            string name = id.Name;
            if (name == null)
            {
                return false;
            }

            foreach (string prefix in OldPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public IslandDefinitionId Migrate(IslandDefinitionId definitionId)
        {
            // Once, not per lift: a save can hold hundreds.
            if (Dropped++ == 0)
            {
                Logger.Warning?.Log(
                    "Extra Build Layers: this save holds tall space lifts from an earlier version (" + definitionId +
                    " and possibly more); they are removed on load. Drag the climb again to place lift stations.");
            }

            return IslandDefinitionId.Invalid;
        }
    }
}
