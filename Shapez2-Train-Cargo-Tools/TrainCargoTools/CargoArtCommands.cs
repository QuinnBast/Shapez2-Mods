using System;
using System.Collections.Generic;
using System.IO;
using ShapezShifter.Hijack;
using UnityEngine;
using Game.Content.Features.SpacePaths.IslandIO;
using Game.Core.Content.Islands;
using Game.Core.Coordinates;
using Game.Core.Rendering;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.TrainCargoTools
{
    /// The art-tuning commands: recolour the machine meshes, and dump the game's own textures
    /// and icons to disk so this mod's art can be matched to them rather than guessed at.
    ///
    /// The machines are one flat colour each because colour in this game is a texture lookup,
    /// not a material property. The shipped sample proves it: DiagonalCutter.fbx carries a
    /// `base_color_texture` and its UV0 sits inside a tight sub-rectangle - U[0.095, 0.476],
    /// V[0.587, 0.919] across 319 distinct pairs. The mesh is picking colours out of one shared
    /// atlas by pointing at spots in it.
    ///
    /// That atlas is authored Unity asset data. It is not in the decompiled assemblies and
    /// cannot be read statically, so every UV in Tools/generate_meshes.py is a placeholder. This
    /// command is how that stops being true: dump the image, read off the coordinates of the
    /// colours you want, and fill in that script's PALETTE table.
    ///
    /// Registered unprefixed, because `traincargotools.dumpatlas` is a lot to type. The shared
    /// `cargotools.` prefix keeps it out of other mods' way.
    ///
    /// Worth having at all because a mod DLL is memory-mapped once loaded, so new code needs a
    /// game restart. Anything that has to be judged by eye - and a colour is the definition of
    /// that - is far quicker to tune in place than to rebuild.
    internal sealed class CargoArtCommands : IConsoleRewirer
    {
        private readonly CargoAppearance Appearance;
        private readonly ILogger Log;

        public CargoArtCommands(CargoAppearance appearance, ILogger logger)
        {
            Appearance = appearance;
            Log = logger;
        }

        public void RegisterCommands(IDebugConsole console)
        {
            // One try/catch per registration. The Shifter catches per rewirer, so a single
            // failure would otherwise take every later command with it.
            Register(() => console.Register(
                "cargotools.dumpatlas", context => Dump(context.Output)));

            Register(() => console.Register(
                "cargotools.dumpicons", context => DumpIcons(context.Output)));

            Register(() => console.Register(
                "cargotools.palette", context => ReportPalette(context.Output)));

            Register(() => console.Register(
                "cargotools.dumplift", context => DumpLifts(context.Output)));

            // The tint and wash commands are gone. They existed to make a cargo belt
            // distinguishable while it still drew as vanilla track; the belt now has its own
            // channel-section model, which does the job at every zoom, so the knobs for tuning a
            // colour nobody could see are dead weight. See CargoTrackDrawer for the whole
            // sequence of attempts.

            // One command per role rather than one command taking a role name:
            // IDebugConsole.Register tops out at two options, and u and v use both.
            foreach (string role in CargoPalette.Roles)
            {
                string captured = role;
                Register(() => console.Register(
                    "cargotools.uv." + captured,
                    new DebugConsole.FloatOption("u", 0f, 1f),
                    new DebugConsole.FloatOption("v", 0f, 1f),
                    context => SetUv(context.Output, captured,
                        new Vector2(context.GetFloat(0), context.GetFloat(1)))));
            }
        }

        private void Register(Action register)
        {
            try
            {
                register();
            }
            catch (Exception exception)
            {
                Log.Exception?.LogException(exception);
            }
        }

        /// Prints the chunk layout and connectors of vanilla's own space belt lifts.
        ///
        /// A lift is the one piece of this family whose shape cannot be read out of
        /// `decompiled/`: the island definitions are authored, so how many chunks one occupies
        /// and where its connectors sit are not knowable statically. `PathLiftingProcessor`
        /// finds a lift through the same `MatchingDefinitionFinder` the corners and junctions
        /// go through, matching on connector pivots - so getting those pivots wrong means the
        /// processor silently never picks the cargo version, which is indistinguishable from
        /// lifts simply not working.
        ///
        /// Prints vanilla's instead of guessing. Temporary: it can go once cargo lifts exist.
        private void DumpLifts(Action<string> output)
        {
            GameIslands islands = Appearance.Islands;
            if (islands == null)
            {
                Report(output, "No islands yet - run this inside a game, not the main menu.");
                return;
            }

            int found = 0;

            foreach (IIslandDefinition definition in islands.AllDefinitions)
            {
                string id = definition.Id.Name;
                if (id.IndexOf("Lift", StringComparison.OrdinalIgnoreCase) < 0
                    || id.IndexOf("SpaceBelt", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                found++;
                Report(output, id);

                foreach (ChunkVector chunk in definition.Layout.GetChunkPositions())
                {
                    Report(output, $"    chunk ({chunk.x},{chunk.y},{chunk.z})");
                }

                if (!definition.CustomData.TryGet(out IIslandConnectorData connectors))
                {
                    continue;
                }

                foreach (EntityIO<LocalChunkPivot, ISpacePathInputConnector> io
                         in connectors.ConnectorsOfType<ISpacePathInputConnector>())
                {
                    LocalChunkPivot p = io.Location;
                    Report(output, $"    in   ({p.Position.x},{p.Position.y},{p.Position.z}) {p.Direction}"
                        + $" {io.Connector.GetType().Name}");
                }

                foreach (EntityIO<LocalChunkPivot, ISpacePathOutputConnector> io
                         in connectors.ConnectorsOfType<ISpacePathOutputConnector>())
                {
                    LocalChunkPivot p = io.Location;
                    Report(output, $"    out  ({p.Position.x},{p.Position.y},{p.Position.z}) {p.Direction}"
                        + $" {io.Connector.GetType().Name}");
                }
            }

            Report(output, found == 0
                ? "No SpaceBelt lift definitions found."
                : $"{found} lift definition(s).");
        }

        private void ReportPalette(Action<string> output)
        {
            if (!Appearance.Palette.Ready)
            {
                Report(output, "Palette not resolved - load a save first.");
                return;
            }

            // Which path resolved it is the first thing worth knowing: the material palette
            // gives every role its own colour, and the vanilla-mesh fallback folds most of them
            // onto five. A machine that looks flat when it should not is usually this line.
            Report(output, $"resolved from {Appearance.Palette.Source}");

            HashSet<Vector2> seen = new HashSet<Vector2>();

            foreach (KeyValuePair<string, Vector2> entry in Appearance.Palette.All)
            {
                seen.Add(entry.Value);

                Report(output, string.Format(
                    "{0,-9} u={1:F4} v={2:F4}", entry.Key, entry.Value.x, entry.Value.y));
            }

            Report(output, $"{seen.Count} distinct coordinate(s) across "
                + $"{CargoPalette.Roles.Length} role(s).");
        }

        /// Moves one role's atlas coordinate and repaints the meshes immediately.
        private void SetUv(Action<string> output, string role, Vector2 uv)
        {
            Appearance.Palette.Override(role, uv);
            Appearance.RecolourMeshes();
            Report(output, string.Format("{0} -> u={1:F4} v={2:F4}", role, uv.x, uv.y));
        }

        /// Writes every island group icon in the session to disk, named by definition id.
        ///
        /// The point is the train loader and unloader: they already have package icons, and
        /// matching those beats inventing a crate glyph from scratch. Every island is dumped
        /// rather than a chosen few because the ids are not obvious from outside and the whole
        /// set is a few hundred kilobytes.
        private void DumpIcons(Action<string> output)
        {
            GameIslands islands = Appearance.Islands;
            if (islands == null)
            {
                Report(output, "No island catalogue yet - load a save first.");
                return;
            }

            string folder = Path.Combine(Application.persistentDataPath, "cargo-tools-icons");
            Directory.CreateDirectory(folder);

            int written = 0;
            foreach (IIslandDefinition definition in islands.AllDefinitions)
            {
                if (!definition.CustomData.TryGet(out IslandPresentationData presentation)
                    || presentation.Icon == null)
                {
                    continue;
                }

                string path = Path.Combine(folder, Sanitise(definition.Id.ToString()) + ".png");
                if (TryWriteSprite(presentation.Icon, path))
                {
                    written++;
                }
            }

            Report(output, "Wrote " + written + " island icon(s) to " + folder);
        }

        /// A sprite is a rectangle of a larger atlas, so its own rect is cut out rather than the
        /// whole sheet written - otherwise every icon dumps as the same page.
        private static bool TryWriteSprite(Sprite sprite, string path)
        {
            Rect rect = sprite.textureRect;
            int w = Mathf.Max(1, Mathf.RoundToInt(rect.width));
            int h = Mathf.Max(1, Mathf.RoundToInt(rect.height));

            RenderTexture target = RenderTexture.GetTemporary(
                sprite.texture.width, sprite.texture.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;

            try
            {
                Graphics.Blit(sprite.texture, target);
                RenderTexture.active = target;

                Texture2D readable = new(w, h, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(rect.x, rect.y, w, h), 0, 0);
                readable.Apply();

                File.WriteAllBytes(path, readable.EncodeToPNG());
                UnityEngine.Object.Destroy(readable);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private void Dump(Action<string> output)
        {
            try
            {
                // Populated when the session builds its platform drawers, so before any save
                // is loaded there is nothing to dump.
                VisualThemeBaseResources resources = Appearance.ThemeResources;
                if (resources?.IslandMaterial == null)
                {
                    Report(output, "No island material yet - load a save first.");
                    return;
                }

                if (!resources.IslandMaterial.TryGet(0, out IMaterialReference reference))
                {
                    Report(output, "Island material has no LOD 0 entry.");
                    return;
                }

                Material material = reference.GetMaterialInternal();
                if (material == null)
                {
                    Report(output, "Island material reference is empty.");
                    return;
                }

                string folder = Path.Combine(Application.persistentDataPath, "cargo-tools-atlas");
                Directory.CreateDirectory(folder);

                int written = 0;
                foreach (string name in material.GetTexturePropertyNames())
                {
                    if (!(material.GetTexture(name) is Texture texture))
                    {
                        continue;
                    }

                    string path = Path.Combine(folder, Sanitise(name) + ".png");
                    if (TryWrite(texture, path))
                    {
                        written++;
                        Report(output, $"{name}  {texture.width}x{texture.height}  -> {path}");
                    }
                }

                Report(output, written == 0
                    ? $"Shader {material.shader?.name} exposed no readable textures."
                    : $"Wrote {written} texture(s) to {folder}. "
                      + "UV origin is bottom-left; PNG row 0 is the top, so V = 1 - (row / height).");
            }
            catch (Exception exception)
            {
                Log.Exception?.LogException(exception);
                Report(output, "Dump failed; see Player.log.");
            }
        }

        /// Copies through a RenderTexture rather than calling GetPixels directly.
        ///
        /// A texture shipped in a build is almost never CPU-readable - Read/Write Enabled is off
        /// by default because it doubles the memory - and GetPixels on one throws. Blitting to a
        /// RenderTexture and using ReadPixels goes via the GPU instead, which works regardless,
        /// and is the only reason this command can see the atlas at all.
        private static bool TryWrite(Texture texture, string path)
        {
            RenderTexture target = RenderTexture.GetTemporary(
                texture.width, texture.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;

            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;

                Texture2D readable = new(texture.width, texture.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                readable.Apply();

                File.WriteAllBytes(path, readable.EncodeToPNG());
                UnityEngine.Object.Destroy(readable);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static string Sanitise(string name)
        {
            foreach (char bad in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(bad, '_');
            }

            return name;
        }

        /// context.Output is a field and can be null, so it is never called unconditionally.
        private void Report(Action<string> output, string message)
        {
            output?.Invoke(message);
            Log.Info?.Log(message);
        }
    }
}
