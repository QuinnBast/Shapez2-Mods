using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using ShapezShifter.Hijack;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// `esp.report` and `esp.dump`, registered without the assembly-name prefix so they are short
    /// enough to type while iterating.
    ///
    /// `esp.dump` exists because the shape sub-part mesh convention cannot be read out of the
    /// decompiled assemblies - the vanilla meshes are authored Unity assets. It writes every
    /// registered part's mesh out so the mod's generated geometry can be compared against a circle
    /// rather than guessed at. The meshes are readable at runtime because the renderer itself reads
    /// `sourceMesh.vertices` and `.colors` on every cache miss.
    public class ShapePartConsole : IConsoleRewirer
    {
        private readonly ILogger Logger;

        /// Set by the injection hook. There is no static accessor for `IGameData`, and the commands
        /// are only useful once a session has loaded anyway.
        public static IGameData GameData;

        public ShapePartConsole(ILogger logger)
        {
            Logger = logger;
        }

        public void RegisterCommands(IDebugConsole console)
        {
            // Each registration is wrapped separately: ShapezShifter catches per rewirer, so one
            // failure would otherwise take the rest of the commands with it.
            try
            {
                console.Register("esp.report", context => Report(context.Output));
            }
            catch (Exception exception)
            {
                Logger.Error?.Log("esp.report could not be registered: " + exception.Message);
            }

            try
            {
                console.Register("esp.dump", context => Dump(context.Output));
            }
            catch (Exception exception)
            {
                Logger.Error?.Log("esp.dump could not be registered: " + exception.Message);
            }
        }

        private void Report(Action<string> output)
        {
            if (GameData == null)
            {
                output?.Invoke("No game data yet - load a save first.");
                return;
            }

            output?.Invoke(DescribeRendererData() + "\n" + DescribeColors(GameData) + "\n" + ShapePartInjector.Describe(GameData));
        }

        /// Which colours exist, which tier each is in, and - the one that matters - which of them a
        /// player can actually obtain.
        ///
        /// A quest asking for a colour the player cannot make is an impossible goal, and the tiers
        /// are the only *mechanical* answer to when a colour arrives: `SecondaryColors` are one
        /// mixer away from the primaries, `TertiaryColors` are all three mixed. Inferring it from
        /// where vanilla happens to ask for a colour is a different thing and a weaker one - it put
        /// the magenta in Fine Detail a whole milestone late, gated behind a space floor that has
        /// nothing to do with paint.
        ///
        /// Black is the open question this exists to settle. It is the only colour this mod asks
        /// for that a hexagonal scenario never mentions, so if `k` is missing from
        /// `PlayerObtainableColors` there, Widow's Web is unbuildable and wants recolouring rather
        /// than re-gating.
        private static string DescribeColors(IGameData gameData)
        {
            StringBuilder colours = new StringBuilder();

            foreach (IShapeColorScheme scheme in gameData.ColorSchemes)
            {
                colours.AppendLine(
                    $"colours all={Codes(scheme.Colors)} obtainable={Codes(scheme.PlayerObtainableColors)}");
                colours.AppendLine(
                    $"  primary={Codes(scheme.PrimaryColors)} secondary={Codes(scheme.SecondaryColors)} " +
                    $"tertiary={Codes(scheme.TertiaryColors)} default={scheme.DefaultShapeColor?.Code}");
            }

            return colours.Length == 0 ? "no colour schemes" : colours.ToString().TrimEnd();
        }

        private static string Codes(IReadOnlyList<IShapeColor> colours)
        {
            return colours == null ? "-" : new string(colours.Select(c => c.Code).ToArray());
        }

        /// The renderer's authored numbers, which decide both how big a generated mesh comes out and
        /// how much of a layer survives having another stacked on it.
        ///
        /// `ShapeItemRendererData` is a serialized class, so its field initialisers are only the
        /// Unity defaults - the values that actually ship are authored into the asset and cannot be
        /// read out of the assemblies. Print them rather than trusting the defaults.
        private static string DescribeRendererData()
        {
            // Globals.Resources is marked obsolete in favour of injection, but a console command has
            // nothing to inject into and this is exactly what HUDShapeViewer reads.
#pragma warning disable 618
            ShapeItemRendererData data = Globals.Resources?.BeltItemRendererData?.ShapeItemRendererData;
#pragma warning restore 618
            if (data == null)
            {
                return "renderer data unavailable";
            }

            float covered = Mathf.Pow(1.0f - data.ShapeLayerScaleReduction, 1.0f);

            return $"dimensions2D={data.ShapeDimensions2D} innerGap={data.ShapeInnerGap} " +
                   $"layerHeight={data.ShapeLayerHeight} layerScaleReduction={data.ShapeLayerScaleReduction}\n" +
                   $"  a stacked layer covers the inner {covered * 100.0f:0.#}% of the one below\n" +
                   $"  mesh authoring space: radius {ShapeGeometry.Radius}, height {ShapeGeometry.Height}, " +
                   $"outline {ShapeGeometry.OutlineWidth}";
        }

        private void Dump(Action<string> output)
        {
            if (GameData == null)
            {
                output?.Invoke("No game data yet - load a save first.");
                return;
            }

            string folder = Path.Combine(Application.persistentDataPath, "extra-shape-parts");
            Directory.CreateDirectory(folder);

            StringBuilder summary = new StringBuilder();

            // Two configurations of the same part count share their part instances, so without this
            // every quad part would be written twice - and a part that appears in two configurations
            // at *different* counts is a different mesh under the same code, which is the case the
            // file name has to keep apart.
            HashSet<MetaShapeSubPart> seen = new HashSet<MetaShapeSubPart>();

            foreach (IShapesConfiguration configuration in GameData.ShapesConfigurations)
            {
                foreach (IShapeSubPart subPart in configuration.Parts)
                {
                    if (!(subPart is MetaShapeSubPart part) || part.Mesh == null)
                    {
                        continue;
                    }

                    if (!part.Mesh.TryGet(0, out IMeshReference handle) || !seen.Add(part))
                    {
                        continue;
                    }

                    try
                    {
                        summary.AppendLine(WriteMesh(folder, part, configuration.PartCount,
                            handle.GetMeshInternal()));
                    }
                    catch (Exception exception)
                    {
                        summary.AppendLine($"'{part.Code}': {exception.GetType().Name}: {exception.Message}");
                    }
                }
            }

            File.WriteAllText(Path.Combine(folder, "summary.txt"), summary.ToString());
            output?.Invoke(summary + "\nWritten to " + folder);
        }

        /// Writes one mesh as a .obj, with the vertex colours as a trailing comment block - .obj has
        /// no colour channel, and the colours are the interesting part.
        private static string WriteMesh(string folder, MetaShapeSubPart part, int partCount, Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            Color[] colors = mesh.colors;
            int[] triangles = mesh.triangles;

            StringBuilder file = new StringBuilder();
            file.AppendLine($"# shape part '{part.Code}' ({part.name}), {partCount}-part configuration");
            file.AppendLine($"# overrideMaterial={part.OverrideMaterial} material={part.Material}");

            foreach (Vector3 vertex in vertices)
            {
                file.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0} {1} {2}",
                    vertex.x, vertex.y, vertex.z));
            }

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                file.AppendLine($"f {triangles[i] + 1} {triangles[i + 1] + 1} {triangles[i + 2] + 1}");
            }

            int outlineVertices = 0;
            for (int i = 0; i < colors.Length; i++)
            {
                // The same test the renderer applies to decide outline versus shape colour.
                if (colors[i].r < 0.05f)
                {
                    outlineVertices++;
                }

                file.AppendLine(string.Format(CultureInfo.InvariantCulture, "# color {0} {1} {2} {3} {4}",
                    i, colors[i].r, colors[i].g, colors[i].b, colors[i].a));
            }

            // Lower case codes get a marker rather than their own letter: shape codes are case
            // sensitive and Windows file names are not, so `C` (circle) and `c` (crystal) would
            // otherwise write to the same file and one would silently overwrite the other.
            string code = char.IsLetterOrDigit(part.Code)
                ? (char.IsLower(part.Code) ? part.Code + "_lower" : part.Code.ToString())
                : "code" + (int)part.Code;

            string name = "part_" + code + "_" + partCount;
            File.WriteAllText(Path.Combine(folder, name + ".obj"), file.ToString());

            Bounds bounds = mesh.bounds;
            return $"'{part.Code}' x{partCount} verts={vertices.Length} tris={triangles.Length / 3} " +
                   $"colors={colors.Length} outlineVerts={outlineVertices} " +
                   $"min={bounds.min} max={bounds.max}";
        }
    }
}
