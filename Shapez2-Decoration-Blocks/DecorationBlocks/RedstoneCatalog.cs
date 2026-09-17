using System.Collections.Generic;
using UnityEngine;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// What kind of redstone component a definition is. The world switches on this rather than on
/// the definition id, so adding a second lever-shaped thing later costs nothing.
public enum RedstoneKind
{
    /// Carries power, losing one strength per tile, and is the only component whose appearance
    /// depends on its neighbours.
    Dust,

    /// A source at full strength when standing free; an inverter of the block it is mounted on
    /// when mounted on one.
    Torch,

    /// A one-way delay of one to four ticks, lockable from the side.
    Repeater,

    /// A latch. Click it in the side panel.
    Lever,

    /// A lever that lets go after ten ticks.
    Button,

    /// A solid block that lights when powered and carries nothing onward. The output half of the
    /// toolkit: without one, a circuit's only visible effect is the colour of its own dust.
    Lamp,

    /// Compare or subtract, with a strength on the output rather than a bare on/off.
    Comparator,

    /// Watches the tile it faces and pulses out of its back when that tile changes.
    Observer,

    /// Redstone on one side, a shapez wire on the other, both directions at once.
    Converter,
}

/// One redstone component: the definition side of it. State lives in RedstoneWorld.
public sealed class RedstoneDefinition
{
    public RedstoneDefinition(
        string id, string displayName, RedstoneKind kind, string texture,
        string activeTexture = null, string iconTexture = null)
    {
        Id = id;
        DisplayName = displayName;
        Kind = kind;
        Texture = texture;
        ActiveTexture = activeTexture ?? texture;
        IconTexture = iconTexture ?? ActiveTexture;
    }

    /// Ends up in the BuildingDefinitionId and therefore in save files.
    public string Id { get; }

    public string DisplayName { get; }

    public RedstoneKind Kind { get; }

    /// The unpowered look, and the toolbar icon.
    public string Texture { get; }

    /// The powered look. Several components have a distinct one shipped with the game -
    /// `redstone_torch` and `redstone_torch_off`, `repeater` and `repeater_on`.
    public string ActiveTexture { get; }

    /// What the toolbar entry shows, which is the powered texture unless a component says
    /// otherwise. Two do: a button has no texture of its own and would show a full square of
    /// stone, and dust is stored greyscale and would show grey. Both get an icon generated at
    /// import - see tools_import_minecraft.py.
    public string IconTexture { get; }

    public string TranslationKey => "decoration-blocks.redstone." + Id;
}

internal static class RedstoneCatalog
{
    /// In toolbar order: what you power with, what you carry power along, what you shape it
    /// with.
    public static IReadOnlyList<RedstoneDefinition> All { get; } = new[]
    {
        new RedstoneDefinition("SparkstoneTorch", "Sparkstone Torch", RedstoneKind.Torch,
            "redstone_torch_off", "redstone_torch"),
        new RedstoneDefinition("Lever", "Lever", RedstoneKind.Lever, "lever"),
        new RedstoneDefinition("Button", "Stone Button", RedstoneKind.Button, "smooth_stone",
            iconTexture: "button_icon"),
        new RedstoneDefinition("SparkstoneDust", "Sparkstone Dust", RedstoneKind.Dust, "redstone_dust_0",
            iconTexture: "redstone_dust_icon"),
        new RedstoneDefinition("Repeater", "Sparkstone Repeater", RedstoneKind.Repeater,
            "repeater", "repeater_on"),
        new RedstoneDefinition("Comparator", "Sparkstone Comparator", RedstoneKind.Comparator,
            "comparator", "comparator_on"),
        new RedstoneDefinition("Observer", "Observer", RedstoneKind.Observer,
            "observer_side", "observer_side", iconTexture: "observer_front"),
        new RedstoneDefinition("SparkstoneLamp", "Sparkstone Lamp", RedstoneKind.Lamp,
            "redstone_lamp", "redstone_lamp_on"),
        new RedstoneDefinition("SparkstoneConverter", "Sparkstone Converter", RedstoneKind.Converter,
            "iron_block", "redstone_block", iconTexture: "converter_icon"),
    };

    /// Every texture the components name, plus the sixteen dust connection shapes. The shapes
    /// are generated at import (see tools_import_minecraft.py) and are greyscale: signal
    /// strength is a tint applied per draw, not a tile.
    public static IEnumerable<string> Textures()
    {
        foreach (RedstoneDefinition component in All)
        {
            yield return component.Texture;
            yield return component.ActiveTexture;
            yield return component.IconTexture;
        }

        // Dust is drawn as geometry on a flat white tile rather than as a textured quad, so the
        // sixteen generated shape tiles are no longer packed. They are still written by the
        // importer, and going back to them is a one-line change if the flat look disappoints.
        yield return Solid;

        // The observer is the only component whose faces differ, so its extra tiles are named
        // here rather than on the definition.
        yield return "observer_front";
        yield return "observer_back";
        yield return "observer_back_on";
        yield return "observer_top";
    }

    /// A fully opaque white tile. Anything drawn as flat colour maps here and takes its colour
    /// from a material property block, which is what lets dust work without relying on the
    /// shader clipping transparent texels.
    public const string Solid = "solid_white";

    /// Minecraft's own colour ramp for dust, from `RedStoneWireBlock`: nearly black at rest,
    /// bright red at fifteen, with green and blue only entering at the top of the range so the
    /// strong end reads as orange rather than pink.
    ///
    /// Worth copying exactly rather than inventing a gradient. A player reads strength off the
    /// colour without counting tiles, and they already know this ramp.
    public static Color DustColour(int power)
    {
        float f = Mathf.Clamp01(power / 15.0f);
        float r = f * 0.6f + (power > 0 ? 0.4f : 0.3f);
        float g = Mathf.Clamp01(f * f * 0.7f - 0.5f);
        float b = Mathf.Clamp01(f * f * 0.6f - 0.7f);
        return new Color(r, g, b, 1.0f);
    }
}
