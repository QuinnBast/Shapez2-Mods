using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Game.Core.Coordinates;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// Console commands for the things that cannot be settled from the decompiled assemblies.
///
/// Materials and shaders are authored Unity data. The building shader turned out to be
/// `Shader Graphs/UberBuildingShader` with no albedo map at all - colour is a lookup into a
/// 256x256 `_MaterialLUT` - which is not something any amount of reading C# would have shown.
/// These four commands are what turned that from a guess into a fact, and `db.slot` is what
/// stops the next guess costing a rebuild and a reload.
///
/// Registered through IConsoleRewirer rather than AddModCommands so the prefix is `db.` rather
/// than `decorationblocks.`, which matters when the point is to type it while the game runs.
internal sealed class BlockConsole : IConsoleRewirer
{
    private readonly ILogger Log;

    public BlockConsole(ILogger log)
    {
        Log = log;
    }

    public void RegisterCommands(IDebugConsole console)
    {
        // One try/catch each: the Shifter catches per rewirer, not per command, so a single
        // failed Register would otherwise take the rest with it.
        Add(console, "db.material", context => Report(context.Output));
        Add(console, "db.atlas", context => DumpAtlas(context.Output));
        Add(console, "db.shaders", context => ListShaders(context.Output));

        Add(console, "db.redstone", context =>
        {
            Viewport viewport = GameHelper.Core?.Viewport;

            if (viewport == null
                || !ScreenUtils.TryGetTileCoordinateAtCursor(viewport, out GlobalTileCoordinate tile))
            {
                Say(context.Output, "Decoration blocks: no cursor tile - load a save and point at something.");
                return;
            }

            Say(context.Output, RedstoneWorld.Instance.Describe(tile));
        });

        Add(console, "db.redstone", context =>
        {
            Viewport viewport = GameHelper.Core?.Viewport;

            if (viewport == null
                || !ScreenUtils.TryGetTileCoordinateAtCursor(viewport, out GlobalTileCoordinate tile))
            {
                Say(context.Output, "Decoration blocks: no cursor tile - load a save and point at something.");
                return;
            }

            Say(context.Output, RedstoneWorld.Instance.Describe(tile));
        });

        // The commands that take arguments. CommandContext has no raw argument array - it
        // carries parsed ConsoleOptions - so each option has to be declared up front and read
        // back by index. `db.material` is where you go to see the current settings; these only
        // set them.
        Add(console, "db.slot", new DebugConsole.StringOption("property"),
            context => Rebind(context.Output, context.GetString(0)));

        Add(console, "db.shader", new DebugConsole.StringOption("name"),
            context => UseShader(context.Output, context.GetString(0)));

        // Smoothness, metallic and the alpha cutoff, tuned against the real lighting instead of
        // guessed. URP Lit defaults to 0.5 smoothness, which puts a wet sheen on cobblestone.
        Add(console, "db.set",
            new DebugConsole.StringOption("property"),
            new DebugConsole.FloatOption("value", 0.0f, 1.0f),
            context => Tune(context.Output, context.GetString(0), context.GetFloat(1)));
    }

    private void Add(
        IDebugConsole console, string name, DebugConsole.ConsoleOption option,
        Action<DebugConsole.CommandContext> body)
    {
        try
        {
            console.Register(name, option, body);
        }
        catch (Exception exception)
        {
            Log?.Exception?.LogException(exception);
        }
    }

    private void Add(
        IDebugConsole console, string name, DebugConsole.ConsoleOption first,
        DebugConsole.ConsoleOption second, Action<DebugConsole.CommandContext> body)
    {
        try
        {
            console.Register(name, first, second, body);
        }
        catch (Exception exception)
        {
            Log?.Exception?.LogException(exception);
        }
    }

    private void UseShader(Action<string> output, string name)
    {
        if (Shader.Find(name) == null)
        {
            Say(output,
                "Decoration blocks: no shader called '" + name + "' in this build. `db.shaders` "
                + "lists the ones that are loaded - an unreferenced URP shader is stripped from "
                + "a player build and resolves to null rather than failing loudly.");
            return;
        }

        if (BlockMaterials.UseShader(name, Log))
        {
            Say(output, "Decoration blocks: rebuilt on '" + name + "'. Blocks redraw this frame.");
        }
    }

    private void Tune(Action<string> output, string property, float value)
    {
        switch (property.ToLowerInvariant())
        {
            case "smoothness":
                BlockMaterials.Smoothness = value;
                break;
            case "metallic":
                BlockMaterials.Metallic = value;
                break;
            case "cutoff":
                BlockMaterials.Cutoff = value;
                break;
            default:
                Say(output, "Decoration blocks: db.set takes smoothness, metallic or cutoff.");
                return;
        }

        if (BlockMaterials.Rebuild(Log))
        {
            Say(output, "Decoration blocks: " + property + " = " + value + ".");
        }
    }

    private void Add(IDebugConsole console, string name, Action<DebugConsole.CommandContext> body)
    {
        try
        {
            console.Register(name, body);
        }
        catch (Exception exception)
        {
            Log?.Exception?.LogException(exception);
        }
    }

    private void Report(Action<string> output)
    {
        BlockMaterials materials = BlockMaterials.Current;

        if (materials == null)
        {
            Say(output,
                "Decoration blocks: no material built yet. Look at a placed block first - the "
                + "material is cloned from the visual theme on the first frame that draws one.");
            return;
        }

        Material material = materials.MaterialFor(translucent: false);

        StringBuilder report = new StringBuilder();
        report.AppendLine("shader: " + (material == null ? "<null>" : material.shader.name));
        report.AppendLine(
            "smoothness " + BlockMaterials.Smoothness + ", metallic " + BlockMaterials.Metallic
            + ", cutoff " + BlockMaterials.Cutoff + "   (db.set <name> <0..1>)");
        report.AppendLine("atlas written to: " + (materials.ChosenProperty ?? "<none>"));
        report.AppendLine("texture properties (unity_* built-ins excluded, they are not ours to write):");

        foreach (string name in materials.AvailableProperties)
        {
            Texture current = material != null ? material.GetTexture(name) : null;
            report.AppendLine(
                "  " + name + " = "
                + (current == null
                    ? "<none>"
                    : current.name + " " + current.width + "x" + current.height)
                + (name == materials.ChosenProperty ? "   <-- atlas here" : string.Empty));
        }

        report.Append("`db.slot <name>` moves the atlas; `db.shader <name>` rebuilds on a different shader.");
        Say(output, report.ToString());
    }

    private void Rebind(Action<string> output, string property)
    {
        BlockMaterials materials = BlockMaterials.Current;

        if (materials == null)
        {
            Say(output, "Decoration blocks: no material built yet. Look at a placed block first.");
            return;
        }

        if (string.IsNullOrEmpty(property))
        {
            Say(output,
                "Decoration blocks: atlas is on '" + (materials.ChosenProperty ?? "<none>")
                + "'. Pass a property name from `db.material` to move it.");
            return;
        }

        bool known = false;
        foreach (string name in materials.AvailableProperties)
        {
            known |= name == property;
        }

        if (!known)
        {
            Say(output,
                "Decoration blocks: '" + property + "' is not a writable texture property of this "
                + "material. `db.material` lists the ones that are.");
            return;
        }

        if (BlockMaterials.Rebind(property, Log))
        {
            Say(output, "Decoration blocks: atlas moved to '" + property + "'. Blocks redraw this frame.");
        }
    }

    /// Every shader the build actually loaded.
    ///
    /// Worth having because the usual advice - `Shader.Find("Universal Render Pipeline/Lit")` -
    /// is a coin flip in a shipped game: URP shaders nothing references are stripped, and a
    /// stripped shader resolves to null rather than failing loudly. This says which names are
    /// really there, so picking a different shader is a decision rather than a hope.
    ///
    /// FindObjectsOfTypeAll rather than FindObjectsOfType: shaders are assets, not scene
    /// objects, so the scene-only version returns nothing.
    private void ListShaders(Action<string> output)
    {
        Shader[] shaders = Resources.FindObjectsOfTypeAll<Shader>();
        List<string> names = new List<string>(shaders.Length);

        foreach (Shader shader in shaders)
        {
            if (shader != null && !string.IsNullOrEmpty(shader.name))
            {
                names.Add(shader.name);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);

        StringBuilder report = new StringBuilder();
        report.AppendLine(names.Count + " shaders loaded:");
        foreach (string name in names)
        {
            report.AppendLine("  " + name);
        }

        Say(output, report.ToString());
    }

    private void DumpAtlas(Action<string> output)
    {
        Texture2D atlas = BlockMaterials.Atlas;

        if (atlas == null)
        {
            Say(output, "Decoration blocks: no atlas. The mod failed to load its textures.");
            return;
        }

        // The atlas is built in code with makeNoLongerReadable: false, so EncodeToPNG works on
        // it directly. A texture shipped inside a Unity build would need a RenderTexture blit
        // first; this one does not, which is a side benefit of packing at runtime.
        string path = Path.Combine(Application.persistentDataPath, "DecorationBlocksAtlas.png");
        File.WriteAllBytes(path, atlas.EncodeToPNG());
        Say(output, "Decoration blocks: atlas written to " + path);
    }

    private void Say(Action<string> output, string message)
    {
        // Output is a field and can be null - a command invoked from somewhere other than the
        // console window has nowhere to print.
        output?.Invoke(message);
        Log?.Info?.Log(message);
    }
}
