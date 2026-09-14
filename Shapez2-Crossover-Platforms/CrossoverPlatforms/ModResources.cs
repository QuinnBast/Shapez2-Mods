using System.IO;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.CrossoverPlatforms
{
    /// Finds this mod's Resources folder, including after a hot reload.
    ///
    /// <see cref="ModDirectoryLocator"/> resolves a mod's folder as
    /// <c>Directory.GetParent(typeof(TMod).Assembly.Location)</c>. That works for a mod the game
    /// loaded itself, and not at all for a reloaded one: Mod Reloader has to use
    /// <c>Assembly.Load(byte[])</c> - <c>LoadFrom</c> binds by assembly identity and would just
    /// hand back the copy already loaded, which is the whole reason reloading needs the byte[]
    /// overload - and an assembly loaded from a byte array has **no file backing it**, so
    /// <c>Location</c> is the empty string and <c>GetParent</c> throws
    /// <c>ArgumentException: Path cannot be the empty string</c>.
    ///
    /// The throw lands in the mod's constructor, so the reload reports the mod as dead. That is
    /// not specific to crossings - it hits every mod that loads an icon off disk, which is every
    /// mod in this folder that has a toolbar entry.
    ///
    /// So: use the locator when there is an assembly location, and fall back to the two places the
    /// mod can be installed when there is not. Preferring mods-dev matches Mod Reloader, which
    /// reloads from the staged build when one is there.
    internal static class ModResources
    {
        /// Matches the project name, which is what both output paths are named after.
        private const string ModFolderName = "CrossoverPlatforms";

        private const string ResourcesFolder = "Resources";

        public static ModFolderLocator Locate(ILogger logger)
        {
            string assemblyLocation = typeof(CrossoverPlatformsMod).Assembly.Location;
            if (!string.IsNullOrEmpty(assemblyLocation))
            {
                return ModDirectoryLocator.CreateLocator<CrossoverPlatformsMod>()
                   .SubLocator(ResourcesFolder);
            }

            foreach (string installRoot in new[] { "mods-dev", "mods" })
            {
                string candidate = Path.Combine(
                    Application.persistentDataPath, installRoot, ModFolderName);
                if (Directory.Exists(Path.Combine(candidate, ResourcesFolder)))
                {
                    return new ModFolderLocator(candidate).SubLocator(ResourcesFolder);
                }
            }

            // Let the icon load fail loudly rather than silently handing back a wrong path: an
            // empty preview image is its own class of problem in this game.
            logger.Warning?.Log(
                $"No Resources folder found for {ModFolderName}; icons will not load");
            return new ModFolderLocator(ResourcesFolder);
        }
    }
}
