using System.Collections.Generic;
using System.IO;
using JetBrains.Annotations;
using QuinnBast.Shapez2.ToolbarKit;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// Decorative blocks for the machine layer. They connect to nothing, do nothing, and exist so a
/// platform can be made to look like something.
[UsedImplicitly]
public class DecorationBlocksMod : IMod
{
    public DecorationBlocksMod(ILogger logger)
    {
        ToolbarKit.ToolbarKit.Log = logger;
        BlockRenderer.Log = logger;

        string textures = FindResources().SubPath("Textures");

        List<string> wanted = new List<string>(BlockCatalog.DistinctTextures());
        foreach (string texture in RedstoneCatalog.Textures())
        {
            if (!wanted.Contains(texture))
            {
                wanted.Add(texture);
            }
        }

        BlockAtlas atlas = BlockAtlas.Build(textures, wanted, logger);
        BlockMaterials.Atlas = atlas.Texture;

        DecorationUnlock unlock = new DecorationUnlock(logger);
        DecorationRegistrar.RegisterAll(atlas, unlock, logger);
        RedstoneRegistrar.RegisterAll(atlas, unlock, logger);

        // The redstone tick and the map binding. A rewirer rather than a hook of our own:
        // ITickRewirer is postfixed onto GameSessionOrchestrator.Tick, which is the main thread.
        GameRewirers.AddRewirer(new RedstoneTicker(logger));

        Preview = new PlacementPreview(logger);

        GameRewirers.AddRewirer(new BlockConsole(logger));
    }

    /// Held so the detour can be released. A mod reloaded without unhooking leaves the old
    /// assembly's detour in place, and the next reload stacks a second one on top of it.
    private readonly PlacementPreview Preview;

    public void Dispose()
    {
        Preview?.Dispose();
    }

    /// Where the mod's own files are, without throwing under a hot reloader.
    ///
    /// ModDirectoryLocator.CreateLocator&lt;T&gt; is Directory.GetParent(typeof(T).Assembly.Location),
    /// and a reloader has to load with Assembly.Load(byte[]) - LoadFrom binds by assembly
    /// identity and would hand back the copy already loaded, which is the whole reason the byte
    /// array overload is needed. An assembly with no file behind it reports Location == "", and
    /// GetParent("") throws ArgumentException. The throw lands in this constructor, so the
    /// reload reports the mod as dead with no obvious cause.
    ///
    /// Application.persistentDataPath is the same folder the build-time SPZ2_PERSISTENT points
    /// at and is safe to read at runtime. mods-dev rather than mods, because that is where a
    /// reloader stages from.
    private static ModFolderLocator FindResources()
    {
        string location = typeof(DecorationBlocksMod).Assembly.Location;

        return !string.IsNullOrEmpty(location)
            ? ModDirectoryLocator.CreateLocator<DecorationBlocksMod>().SubLocator("Resources")
            : new ModFolderLocator(Path.Combine(
                Application.persistentDataPath, "mods-dev", "DecorationBlocks")).SubLocator("Resources");
    }
}
