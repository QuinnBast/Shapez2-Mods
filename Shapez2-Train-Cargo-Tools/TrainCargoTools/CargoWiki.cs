using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Core.Localization;
using Game.Core.Research;
using Game.Core.Research.Content;
using MonoMod.RuntimeDetour;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// Wiki entries for the cargo machines, in the same knowledge panel every vanilla machine
    /// has one in.
    ///
    /// **There is no Shifter API for this**, so it is assembled from the two halves the game
    /// keeps apart:
    ///
    ///   - `ResearchProgression.WikiConfiguration` holds the *references* - which entry ids
    ///     exist, what category each sits in and what research each waits for. That object is
    ///     handed to every `IIslandResearchProgressionExtender`, so the mod already has it.
    ///   - `GameData` holds the *entries* themselves, keyed by id, and `WikiDatabase` looks each
    ///     reference up through `IGameData.GetWikiEntry`.
    ///
    /// A reference with no entry behind it throws out of `WikiDatabase`'s constructor, so both
    /// halves have to land. The references are simply added to the list. The entries are served
    /// by a detour on `GameData.GetWikiEntry` rather than by inserting into its dictionary,
    /// which matters: `GameData` outlives a session while a `ResearchProgression` does not, so
    /// inserting would have to be guarded against running twice, and a second `Dictionary.Add`
    /// on the same id throws - the same trap the pipette map sets.
    ///
    /// Titles are not ours to name. `WikiDatabase.Convert` builds one as
    /// `("wiki." + entry.Id.Id + ".title").T()`, so an entry called `CargoTools_CargoBelt` needs
    /// `wiki.CargoTools_CargoBelt.title` in translations.json and nothing else will do.
    internal sealed class CargoWiki : IDisposable
    {
        /// `WK<Category>_<Thing>` is vanilla's own shape - `WKTrains_TrainStations`,
        /// `WKIslands_SpaceBelts` - and the ids double as translation keys through the fixed
        /// `wiki.<id>.title` above, so the whole set reads like part of the game's wiki rather
        /// than like a mod bolted on beside it.
        private const string Intro = "WKCargo_Intro";

        private const string Belt = "WKCargo_Belt";

        private const string Packager = "WKCargo_Packager";

        private const string Unpackager = "WKCargo_Unpackager";

        private const string Store = "WKCargo_Store";

        /// Ours, created when no vanilla category obviously suits. A category is a tab in the
        /// wiki, so five entries scattered into somebody else's tabs would be harder to find
        /// than five in one clearly-labelled one.
        private const string CategoryId = "CargoTools_Category";

        private readonly Dictionary<WikiEntryId, MetaWikiEntry> Entries = new();

        private readonly ILogger Log;

        /// Loads a PNG from the mod's Resources folder as a sprite. An image block holds a
        /// `Sprite` directly rather than a `GameImageId`, so a wiki picture needs no registration
        /// - unlike a research node's, which CargoImages has to serve by id.
        private readonly Func<string, Sprite> LoadImage;

        private readonly Hook LookupHook;

        /// False when the lookup could not be detoured. Then `Register` must add nothing:
        /// a reference whose entry cannot be served throws out of `WikiDatabase`'s constructor
        /// and takes the whole session with it, so no wiki is very much better than half of one.
        private readonly bool Installed;

        private delegate MetaWikiEntry GetEntryOrig(GameData self, WikiEntryId id);

        private delegate MetaWikiEntry GetEntryHook(GetEntryOrig orig, GameData self, WikiEntryId id);

        /// True when CargoImages serves the category's own crate icon. When it does not, the
        /// category borrows one that already resolves - see EnsureCategory.
        private readonly bool OwnIcon;

        public CargoWiki(Func<string, Sprite> loadImage, bool ownIcon, ILogger log)
        {
            OwnIcon = ownIcon;
            LoadImage = loadImage;
            Log = log;

            Build();

            // Degrades rather than throws, unlike the station detours. Those are the mod, and a
            // mod that half-works there loses a player's cargo; this is documentation, and a
            // throw in a mod constructor is not contained by ModLoader - it comes out through
            // ModLoadingStep.LoadMods and kills the game's whole mod loading step.
            MethodBase target = typeof(GameData).GetMethod(nameof(GameData.GetWikiEntry));
            if (target == null)
            {
                Log.Error?.Log(
                    "Wiki: GameData.GetWikiEntry was not found, so the cargo entries cannot be "
                    + "served and none will be registered. Everything else is unaffected.");
                return;
            }

            LookupHook = new Hook(target, new GetEntryHook(Lookup));
            Installed = true;
        }

        public void Dispose()
        {
            LookupHook?.Dispose();
        }

        /// Adds the references, and the category they live in, to one scenario's research.
        ///
        /// Guarded rather than assumed-once: this is driven from the research extenders, which
        /// run per island group, and adding the same reference five times would list every entry
        /// five times in the wiki.
        public void Register(ResearchProgression research)
        {
            if (!Installed)
            {
                return;
            }

            WikiConfiguration wiki = research?.WikiConfiguration;
            if (wiki?.WikiReferences == null || wiki.Categories == null)
            {
                Log.Error?.Log("Wiki: this scenario has no wiki configuration; entries skipped.");
                return;
            }

            if (wiki.WikiReferences.Any(r => r.EntryId.Id == Intro))
            {
                return;
            }

            WikiCategoryId category = EnsureCategory(wiki);

            // Gated on the same two nodes the machines are, so an entry appears exactly when the
            // thing it describes becomes buildable - which is what every vanilla entry does, and
            // why `UnlockableWikiReference` carries requirements at all. The overview rides with
            // the machines, there being nothing to read about before then.
            Add(wiki, category, Intro, CargoResearch.MachinesUpgradeId);
            Add(wiki, category, Belt, CargoResearch.MachinesUpgradeId);
            Add(wiki, category, Packager, CargoResearch.MachinesUpgradeId);
            Add(wiki, category, Unpackager, CargoResearch.MachinesUpgradeId);
            Add(wiki, category, Store, CargoResearch.StoresUpgradeId);

            Log.Info?.Log($"Wiki: added {Entries.Count} entries in category '{category.Id}'.");
        }

        private static void Add(
            WikiConfiguration wiki, WikiCategoryId category, string entryId, string requiredUpgrade)
        {
            wiki.WikiReferences.Add(new UnlockableWikiReference(
                new SerializedUnlockableWikiReference
                {
                    EntryId = entryId,
                    CategoryId = category.Id,
                    RequiredUpgradeIds = new[] { requiredUpgrade },
                    RequiredMechanicIds = Array.Empty<string>(),

                    // Shown but greyed while locked, as a machine you have not researched is.
                    // Hiding it entirely would make the wiki disagree with the research tree,
                    // which already lists the node.
                    UnavailableIfLocked = false,
                },
                Array.Empty<ResearchMechanicId>(),
                Array.Empty<ResearchUpgradeId>()));
        }

        /// Our own category, borrowing an existing icon.
        ///
        /// A `GameIconId` that resolves is not optional - the nav bar asks for one per category -
        /// and icon ids live in Unity assets, so the only id certain to resolve is one the game
        /// is already drawing. Same reasoning as the research node's preview image.
        private WikiCategoryId EnsureCategory(WikiConfiguration wiki)
        {
            WikiCategory existing = wiki.Categories.FirstOrDefault(c => c.Id.Id == CategoryId);
            if (existing != null)
            {
                return existing.Id;
            }

            // The mod's own crate, drawn by Tools/generate_icons.py from the same glyph the
            // nine toolbar icons use. Borrowing was the first version and it took whichever
            // icon happened to be first in the list - which turned out to be a camera.
            string icon = OwnIcon ? CargoImages.CategoryIcon : BorrowIcon(wiki);

            WikiCategory category = new WikiCategory(new SerializedWikiCategory
            {
                Id = CategoryId,
                IconId = icon,
                TitleId = "cargo-tools.wiki.category.title",
                RequiredUpgradeIds = Array.Empty<string>(),
                RequiredMechanicIds = Array.Empty<string>(),
            });

            wiki.Categories.Add(category);
            return category.Id;
        }

        /// Any icon the game is already drawing, for when the mod's own cannot be served.
        /// `GameIconCollection.GetIcon` throws on an id it does not hold, so an unresolvable
        /// one would break the wiki's category bar rather than render blank.
        private string BorrowIcon(WikiConfiguration wiki)
        {
            string borrowed = wiki.Categories
               .Select(c => c.IconId.Id)
               .FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? string.Empty;

            if (borrowed.Length == 0)
            {
                Log.Error?.Log(
                    "Wiki: no existing category has an icon to borrow, so the cargo category will "
                    + "have none. If the wiki's category bar fails to build, this is why.");
            }

            return borrowed;
        }

        private MetaWikiEntry Lookup(GetEntryOrig orig, GameData self, WikiEntryId id)
        {
            return Entries.TryGetValue(id, out MetaWikiEntry entry) ? entry : orig(self, id);
        }

        /// The entries themselves, built once.
        ///
        /// `MetaWikiEntry` is a `ScriptableObject` whose `Id` is its `name`, so naming the object
        /// is what gives the entry its id - there is no id field to set.
        private void Build()
        {
            Entry(Intro, new[] { Belt, Packager, Unpackager, Store },
                Text("wiki.WKCargo_Intro.text-intro-1"),
                Picture("WikiCargoLine.png"),
                Text("wiki.WKCargo_Intro.text-intro-2"),
                Robot("wiki.WKCargo_Intro.text-robot"));

            Entry(Belt, new[] { Packager, Store, Intro },
                Text("wiki.WKCargo_Belt.text-intro-1"),
                // No robot line on this page: the string is not in translations.json, and a
                // key with no entry renders as "?wiki.WKCargo_Belt.text-robot". 89 of the 149
                // vanilla entries close without one, so this is a legitimate shape - add the
                // string and put the Robot(...) block back if you want one here.
                Text("wiki.WKCargo_Belt.text-intro-2"));

            Entry(Packager, new[] { Belt, Unpackager, Intro },
                Text("wiki.WKCargo_Packager.text-intro-1"),
                Robot("wiki.WKCargo_Packager.text-robot"));

            Entry(Unpackager, new[] { Packager, Belt, Intro },
                Text("wiki.WKCargo_Unpackager.text-intro-1"),
                Robot("wiki.WKCargo_Unpackager.text-robot"));

            Entry(Store, new[] { Belt, Intro },
                Text("wiki.WKCargo_Store.text-intro-1"),
                Picture("WikiCargoStore.png"),
                Robot("wiki.WKCargo_Store.text-robot"));
        }

        private void Entry(string id, string[] related, params MetaWikiEntryContentData[] contents)
        {
            MetaWikiEntry entry = ScriptableObject.CreateInstance<MetaWikiEntry>();
            entry.name = id;
            entry.ShowInKnowledgePanel = true;
            entry.RelatedEntries = related.Select(r => new WikiEntryId(r)).ToArray();
            // Nulls are dropped rather than passed on - a picture that would not load leaves a
            // page with one fewer block, not a page that throws while rendering.
            entry.Contents = contents.Where(c => c != null).ToArray();

            Entries.Add(new WikiEntryId(id), entry);
        }

        /// A picture of the machines actually running. Null when the file will not load, and
        /// the block is dropped rather than added: `WikiEntryContentImage` holds the sprite
        /// directly and the renderer does not expect a null one.
        private MetaWikiEntryContentData Picture(string file)
        {
            Sprite sprite = null;
            try
            {
                sprite = LoadImage(file);
            }
            catch (Exception exception)
            {
                Log.Exception?.LogException(exception);
            }

            if (sprite == null)
            {
                Log.Error?.Log($"Wiki: '{file}' could not be loaded; that page has no picture.");
                return null;
            }

            return new MetaWikiEntryContentImageData { Image = sprite };
        }

        /// The dry aside every vanilla entry closes on - `text-robot` in the exported
        /// translations, rendered by `HUDWikiComponentNarrativeText` in its own style.
        ///
        /// Not decoration: 60 of the 149 vanilla entries have one, and an entry without it
        /// reads as documentation rather than as part of this game. There are no headings here
        /// for the same reason - not one vanilla entry uses `MetaWikiEntryContentHeadingData`;
        /// they break a page up with paragraphs and images instead.
        private static MetaWikiEntryContentNarrativeTextData Robot(string key)
        {
            return new MetaWikiEntryContentNarrativeTextData { NarrativeText = Translation(key) };
        }

        private static MetaWikiEntryContentTextData Text(string key)
        {
            return new MetaWikiEntryContentTextData { Text = Translation(key) };
        }

        /// `SerializedTranslationId.T()` returns **null** for an unset id, not an empty text, and
        /// the content renderers do not expect a null - so every block gets a real key.
        private static SerializedTranslationId Translation(string key)
        {
            SerializedTranslationId id = default;
            id.Id = new TranslationId(key);
            return id;
        }
    }
}
