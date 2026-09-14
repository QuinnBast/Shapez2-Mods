using System;
using System.Collections.Generic;

namespace QuinnBast.Shapez2.ToolbarKit
{
    /// The game's own top-level toolbar categories.
    ///
    /// Read off a live toolbar dump rather than guessed. Two things about these ids are easy to
    /// get wrong by hand, and are most of the reason this enum exists:
    ///
    /// - **Every id ends in `.title`.** The element's title is a translation id in its own
    ///   right, so the category `island-toolbar.category-Rail` is addressed as
    ///   `island-toolbar.category-Rail.title`. Omitting the suffix silently matches nothing.
    /// - **Casing is not consistent.** `RegularPlatform` and `Rail` are capitalised,
    ///   `converters` is not. That is the game's data, not a typo here.
    ///
    /// Which toolbar a category belongs to is a naming convention and nothing more - the
    /// `building-toolbar.` / `island-toolbar.` prefix - so Toolbar() reads the prefix rather
    /// than claiming to know something the data does not say.
    public enum ToolbarCategory
    {
        /// Belts, rotators, cutters, stackers.
        Shapes,

        /// Pipes and fluid processing.
        Fluids,

        /// Wires, signals, logic gates, virtual processors.
        Logic,

        /// Hidden unless sandbox rules are on.
        Sandbox,

        /// Platforms, miners, extractors, layouts.
        RegularPlatform,

        /// Rails, trains, stations, docks and train cargo.
        Rail,

        /// Shape and colour converters.
        Converters
    }

    /// Which of the two toolbars a category appears on.
    public enum ToolbarKind
    {
        Unknown,
        Building,
        Island
    }

    /// Named groups inside the categories, as of the 1.2.0-rc3 tree.
    ///
    /// Less stable than the categories, and not unique: BeltBuilding exists under Shapes,
    /// Fluids, Logic and Sandbox alike, so looking one up by id alone finds the
    /// shallowest-leftmost - the Shapes copy. Where that matters, query the live tree instead
    /// (see ToolbarNode) or fall back to an index path.
    public enum ToolbarGroup
    {
        // RegularPlatform
        ShapeMinerExtractors,
        FluidMinerExtractors,
        LinePlatforms,
        RectangularPlatforms,
        OtherPlatforms,

        // Rail
        Rails,
        TrainProducerCargo,
        GenericTrainProducer,
        TrainStations,
        TrainDockLoading,
        TrainDockUnloading,
        TrainDockTransfer,
        RailRouting,
        RailDecorations,

        // Converters
        TierRgb,
        TierCmy,
        TierWk,

        // Building toolbar - these repeat across categories.
        BeltBuilding,
        PipeBuilding,
        WireBuilding,
        RotatorBuilding,
        CutterBuilding,
        StackerBuilding,
        LogicGateBuilding,
        VirtualProcessorBuilding,
        SignalSource,
        WireDisplayBuilding,
        WireBeltInteractionBuilding
    }

    public static class ToolbarIds
    {
        private static readonly Dictionary<ToolbarCategory, string> Categories =
            new Dictionary<ToolbarCategory, string>
            {
                { ToolbarCategory.Shapes, "building-toolbar.category-Shapes.title" },
                { ToolbarCategory.Fluids, "building-toolbar.category-Fluids.title" },
                { ToolbarCategory.Logic, "building-toolbar.category-Logic.title" },
                { ToolbarCategory.Sandbox, "building-toolbar.category-Sandbox.title" },
                { ToolbarCategory.RegularPlatform, "island-toolbar.category-RegularPlatform.title" },
                { ToolbarCategory.Rail, "island-toolbar.category-Rail.title" },

                // Lowercase in the game's data, unlike every other category.
                { ToolbarCategory.Converters, "island-toolbar.category-converters.title" }
            };

        private static readonly Dictionary<ToolbarGroup, string> Groups =
            new Dictionary<ToolbarGroup, string>
            {
                { ToolbarGroup.ShapeMinerExtractors, "island-group.ShapeMinerExtractorsGroup.title" },
                { ToolbarGroup.FluidMinerExtractors, "island-group.FluidMinerExtractorsGroup.title" },
                { ToolbarGroup.LinePlatforms, "island-group.LinePlatformIslandsGroup.title" },
                { ToolbarGroup.RectangularPlatforms, "island-group.RectangularPlatformIslandsGroup.title" },
                { ToolbarGroup.OtherPlatforms, "island-group.OtherPlatformIslandsGroup.title" },

                { ToolbarGroup.Rails, "island-toolbar.RailsCategory.title" },
                { ToolbarGroup.TrainProducerCargo, "island-toolbar.TrainProducerCargoGroup.title" },
                { ToolbarGroup.GenericTrainProducer, "island-toolbar.GenericTrainProducerCategory.title" },
                { ToolbarGroup.TrainStations, "island-toolbar.TrainStationGroup.title" },
                { ToolbarGroup.TrainDockLoading, "island-toolbar.TrainDockLoadingGroup.title" },
                { ToolbarGroup.TrainDockUnloading, "island-toolbar.TrainDockUnloadingGroup.title" },
                { ToolbarGroup.TrainDockTransfer, "island-toolbar.TrainDockTransferGroup.title" },
                { ToolbarGroup.RailRouting, "island-toolbar.RailRoutingCategory.title" },
                { ToolbarGroup.RailDecorations, "island-toolbar.RailsDecorations.title" },

                { ToolbarGroup.TierRgb, "island-group.TierRGBGroup.title" },
                { ToolbarGroup.TierCmy, "island-group.TierCMYGroup.title" },
                { ToolbarGroup.TierWk, "island-group.TierWKGroup.title" },

                { ToolbarGroup.BeltBuilding, "building.BeltBuilding.title" },
                { ToolbarGroup.PipeBuilding, "building.PipeBuilding.title" },
                { ToolbarGroup.WireBuilding, "building.WireBuilding.title" },
                { ToolbarGroup.RotatorBuilding, "building.RotatorBuilding.title" },
                { ToolbarGroup.CutterBuilding, "building.CutterBuilding.title" },
                { ToolbarGroup.StackerBuilding, "building.StackerBuilding.title" },
                { ToolbarGroup.LogicGateBuilding, "building.LogicGateBuilding.title" },
                { ToolbarGroup.VirtualProcessorBuilding, "building.VirtualProcessorBuilding.title" },
                { ToolbarGroup.SignalSource, "building.SignalSource.title" },
                { ToolbarGroup.WireDisplayBuilding, "building.WireDisplayBuilding.title" },
                { ToolbarGroup.WireBeltInteractionBuilding, "building.WireBeltInteractionBuilding.title" }
            };

        /// Every category, for iterating or for printing a list of what is addressable.
        public static IEnumerable<ToolbarCategory> AllCategories => Categories.Keys;

        /// Every named group.
        public static IEnumerable<ToolbarGroup> AllGroups => Groups.Keys;

        public static string ToTranslationId(this ToolbarCategory category)
        {
            return Categories.TryGetValue(category, out string id) ? id : null;
        }

        public static string ToTranslationId(this ToolbarGroup group)
        {
            return Groups.TryGetValue(group, out string id) ? id : null;
        }

        /// Which toolbar a category sits on, read off its id prefix - the only thing that says.
        public static ToolbarKind Toolbar(this ToolbarCategory category)
        {
            string id = category.ToTranslationId();

            if (id == null)
            {
                return ToolbarKind.Unknown;
            }

            if (id.StartsWith("building-toolbar.", StringComparison.Ordinal))
            {
                return ToolbarKind.Building;
            }

            return id.StartsWith("island-toolbar.", StringComparison.Ordinal)
                ? ToolbarKind.Island
                : ToolbarKind.Unknown;
        }

        /// The enum value for a translation id, if it names one of the known categories.
        public static bool TryGetCategory(string translationId, out ToolbarCategory category)
        {
            foreach (KeyValuePair<ToolbarCategory, string> pair in Categories)
            {
                if (pair.Value == translationId)
                {
                    category = pair.Key;
                    return true;
                }
            }

            category = default;
            return false;
        }

        /// The enum value for a translation id, if it names one of the known groups.
        public static bool TryGetGroup(string translationId, out ToolbarGroup group)
        {
            foreach (KeyValuePair<ToolbarGroup, string> pair in Groups)
            {
                if (pair.Value == translationId)
                {
                    group = pair.Key;
                    return true;
                }
            }

            group = default;
            return false;
        }
    }
}
