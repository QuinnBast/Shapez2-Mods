using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core.Research;
using MonoMod.RuntimeDetour;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Serves the mod's own pictures under `GameImageId`s the game will resolve.
    ///
    /// A research node's preview image is looked up by id through `IGameData.GetImage`, which
    /// throws on anything it does not know - including the empty id - and that throw comes out
    /// through the whole `HUDResearchTree` construction and leaves the research screen
    /// half-built and unclosable. So the cargo nodes used to borrow a vanilla node's picture,
    /// which resolved but showed the wrong machine.
    ///
    /// A detour on `GetImage`, exactly as CargoWiki detours `GetWikiEntry`, and for the same
    /// reason: `GameData._Images` is publicized and could be added to, but `GameData` outlives a
    /// session while the thing prompting the insert does not, so an insert would need guarding
    /// against a second scenario load and `Dictionary.Add` throws on a duplicate key.
    ///
    /// Sprites are loaded on first request rather than at mod load. These are screenshots, a few
    /// hundred KB apiece, and nothing asks for one until the research screen or a wiki page is
    /// opened - which many sessions never do.
    internal sealed class CargoImages : IDisposable
    {
        /// Ids are the mod's own and need only be unique against the game's. They are also the
        /// ids the research nodes ask for - see CargoResearch.
        public const string MachinesImage = "CargoTools_CargoMachines";

        public const string StoresImage = "CargoTools_CargoStores";

        /// The wiki category's icon. A `GameIconId`, not a `GameImageId` - the two are
        /// separate registries with separate lookups, so this needs its own detour.
        public const string CategoryIcon = "CargoTools_Cargo";

        private static readonly Dictionary<string, string> Files = new()
        {
            { MachinesImage, "ResearchCargoMachines.png" },
            { StoresImage, "ResearchCargoStores.png" },
        };

        private static readonly Dictionary<string, string> IconFiles = new()
        {
            { CategoryIcon, "WikiCategory.png" },
        };

        private readonly Dictionary<string, Sprite> Loaded = new();

        private readonly Func<string, Sprite> Load;

        private readonly ILogger Log;

        private readonly Hook LookupHook;

        private readonly Hook IconHook;

        /// False when the lookup could not be detoured, which the research nodes have to know:
        /// pointing one at an id that will not resolve is worse than pointing it at a borrowed
        /// picture of the wrong machine.
        public bool Installed { get; }

        /// Tracked apart from `Installed` because the two lookups are independent: the
        /// research nodes can show their own pictures while the wiki category falls back to a
        /// borrowed icon, or the other way round.
        public bool IconsInstalled { get; }

        public CargoImages(Func<string, Sprite> load, ILogger log)
        {
            Load = load;
            Log = log;

            // Degrades rather than throws. A throw in a mod constructor is not contained by
            // ModLoader - it comes out through ModLoadingStep.LoadMods and kills the game's
            // whole mod loading step - and a node with a borrowed picture still works.
            MethodBase target = typeof(GameData).GetMethod(nameof(GameData.GetImage));
            if (target == null)
            {
                Log.Error?.Log(
                    "Images: GameData.GetImage was not found, so the research nodes will borrow a "
                    + "vanilla picture instead of showing the cargo machines.");
                return;
            }

            LookupHook = new Hook(target, new GetImageHook(Lookup));
            Installed = true;

            // `GameIconCollection.GetIcon` throws on an id it does not hold, exactly as
            // GetImage does, so a category pointed at an unserved icon would break the wiki's
            // category bar rather than render blank.
            MethodBase icons = typeof(GameData).GetMethod(nameof(GameData.GetIcon));
            if (icons == null)
            {
                Log.Error?.Log(
                    "Images: GameData.GetIcon was not found, so the wiki category will borrow a "
                    + "vanilla icon instead of the cargo crate.");
                return;
            }

            IconHook = new Hook(icons, new GetIconHook(LookupIcon));
            IconsInstalled = true;
        }

        public void Dispose()
        {
            LookupHook?.Dispose();
            IconHook?.Dispose();
        }

        private delegate Sprite GetImageOrig(GameData self, GameImageId id);

        private delegate Sprite GetImageHook(GetImageOrig orig, GameData self, GameImageId id);

        private delegate Sprite GetIconOrig(GameData self, GameIconId id);

        private delegate Sprite GetIconHook(GetIconOrig orig, GameData self, GameIconId id);

        private Sprite LookupIcon(GetIconOrig orig, GameData self, GameIconId id)
        {
            string key = id.Id;
            if (key == null || !IconFiles.TryGetValue(key, out string file))
            {
                return orig(self, id);
            }

            Sprite sprite = Resolve(key, file);
            return sprite != null ? sprite : orig(self, id);
        }

        private Sprite Lookup(GetImageOrig orig, GameData self, GameImageId id)
        {
            string key = id.Id;
            if (key == null || !Files.TryGetValue(key, out string file))
            {
                return orig(self, id);
            }

            Sprite loaded = Resolve(key, file);
            return loaded != null ? loaded : orig(self, id);
        }

        /// Loads once and remembers the answer, including a failure.
        ///
        /// A null result means the caller must fall through to the original lookup, which
        /// throws a legible "not found" for the id rather than handing the UI a null sprite
        /// to dereference - a broken research or wiki screen is much harder to diagnose.
        private Sprite Resolve(string key, string file)
        {
            if (Loaded.TryGetValue(key, out Sprite cached))
            {
                return cached;
            }

            Sprite sprite = null;
            try
            {
                sprite = Load(file);
            }
            catch (Exception exception)
            {
                Log.Exception?.LogException(exception);
            }

            Loaded[key] = sprite;

            if (sprite == null)
            {
                Log.Error?.Log($"Images: '{file}' could not be loaded, so '{key}' has no picture.");
            }

            return sprite;
        }
    }
}
