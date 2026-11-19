using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.ExtraBuildLayers;

/// <summary>
/// Reports what the layer overlays are actually made of, because it cannot be read statically.
///
/// The layer numbers a player sees - "02" on a platform's floor plane, the digit on the space
/// grid - are not driven by any uniform. <c>GlobalShaderInputs</c> has no layer-number entry,
/// so the digits are **painted into the authored textures**, one material per layer. That
/// makes the fix a question of what those textures look like, and a ScriptableObject's texture
/// assets are exactly the thing the decompiled assemblies do not contain.
///
/// So: dump them. <c>extrabuildlayers.dump-layer-visuals</c> writes every layer material's
/// shader, properties and textures to <c>dump/</c> beside the mod, via a RenderTexture blit so
/// it works on textures that are not CPU-readable (which most shipped ones are not -
/// <c>Texture2D.GetPixels</c> on them throws rather than returning anything).
/// </summary>
internal sealed class LayerVisualsDump : IConsoleRewirer
{
    private readonly ILogger Logger;

    /// <summary>
    /// Captured by <see cref="LayerVisualResourcesExtender"/>, which is handed the theme by
    /// Shifter. There is no static route to <c>VisualThemeBaseResources</c> - it reaches a
    /// drawer through <c>FrameDrawOptionsNoLOD.Theme</c> and nowhere else - so the rewirer that
    /// already receives it is the only place to take a reference.
    /// </summary>
    internal static VisualThemeBaseResources Theme;

    public LayerVisualsDump(ILogger logger)
    {
        Logger = logger;
    }

    public void RegisterCommands(IDebugConsole console)
    {
        try
        {
            console.Register("extrabuildlayers.dump-layer-visuals", context =>
            {
                string report = Dump();
                context.Output?.Invoke(report);
                Logger.Info?.Log(report);
            });

            // For the "only visible from straight above at high layers" report: the camera and
            // what the last frame's culling kept on the viewed layer. Run it at layer 0 and at a
            // high layer with the same camera angle, and compare.
            console.Register("extrabuildlayers.dump-view", context =>
            {
                string report = DumpView();
                context.Output?.Invoke(report);
                Logger.Info?.Log(report);
            });
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }

    private static string DumpView()
    {
#pragma warning disable CS0618 // A console command has nothing to inject.
        Viewport viewport = StaticGameCoreAccessor.HasInstance() ? StaticGameCoreAccessor.G.Viewport : null;
#pragma warning restore CS0618
        if (viewport == null)
        {
            return "Extra Build Layers: no session.";
        }

        Camera camera = viewport.MainCamera;
        return "Extra Build Layers view: island layer " + viewport.IslandLayer + ", building layer " +
               viewport.BuildingLayer + ", zoom " + viewport.Zoom + ", height " + viewport.Height +
               ", camera at " + (camera != null ? camera.transform.position + " pitch " + camera.transform.eulerAngles.x +
                                                  " near " + camera.nearClipPlane + " far " + camera.farClipPlane : "none") +
               "\n  last cull: " + LiftShaftRenderer.LastCullSummary;
    }

    private string Dump()
    {
        StringBuilder report = new StringBuilder();

        try
        {
            string directory = ModDirectoryLocator.CreateLocator<ExtraBuildLayersMod>().SubPath("dump");
            Directory.CreateDirectory(directory);

            report.AppendLine("Extra Build Layers: layer visual dump -> " + directory);

#pragma warning disable 0618   // Globals.Resources is [Obsolete]; there is no injection point for a mod here.
            GameResources resources = Globals.Resources;
#pragma warning restore 0618
            // Printed entry by entry, not just counted. A ramp of the right length can still be
            // the wrong colours: cycling an authored ramp whose last entry is dark puts that dark
            // entry on every nth floor, which looks like a threshold rather than a pattern.
            AppendRamp(report, "LayerColors", resources?.LayerColors);
            AppendRamp(report, "BeltArrowColors", resources?.BeltArrowColors);

            VisualThemeBaseResources theme = Theme;
            if (theme == null)
            {
                report.AppendLine("Theme not captured - load into a save first.");
                return report.ToString();
            }

            HashSet<string> written = new HashSet<string>();

            MaterialReference[] planes = theme.PlayingfieldCurrentLayerPlaneMaterialPerLayer;
            report.AppendLine("PlayingfieldCurrentLayerPlaneMaterialPerLayer: " +
                              (planes?.Length.ToString() ?? "null") + " entries");
            if (planes != null)
            {
                for (int i = 0; i < planes.Length; i++)
                {
                    Describe(report, written, directory, "playingfield-layer-" + i, planes[i]);
                }
            }

            Describe(report, written, directory, "island-grid", theme.IslandGridHelperMaterial);
            Describe(report, written, directory, "island-grid-cursor", theme.IslandGridHelperMaterialCursor);
            Describe(report, written, directory, "island-grid-preview", theme.IslandGridHelperMaterialPreview);
            Describe(report, written, directory, "island-grid-resource", theme.IslandGridHelperMaterialResourcePlacement);

            // The placement preview. A belt being planned on layers 10-12 of 13 renders solid
            // black and the same belt placed renders correctly, so the difference is in what
            // SmartBuildingBlueprintRenderer draws with: UXBuildingBlueprintMaterial (plus a
            // depth pre-pass) and a fixed-colour property block. The property block holds a
            // single _BaseColor and nothing per layer, so whatever is layer-dependent lives in
            // the shader - hence every property, not just its textures.
            Describe(report, written, directory, "blueprint", theme.UXBuildingBlueprintMaterial, allProperties: true);
            Describe(report, written, directory, "blueprint-prewrite-depth", theme.UXBuildingBlueprintPreWriteDepthMaterial, allProperties: true);

            AppendLayerGlobals(report);
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            report.AppendLine("dump failed: " + exception.Message);
        }

        return report.ToString();
    }

    private void Describe(
        StringBuilder report,
        HashSet<string> written,
        string directory,
        string label,
        MaterialReference reference,
        bool allProperties = false)
    {
        Material material = reference?.GetMaterialInternal();
        if (material == null)
        {
            report.AppendLine(label + ": <null material>");
            return;
        }

        report.AppendLine(label + ": material='" + material.name + "' shader='" + material.shader?.name + "'");

        if (allProperties)
        {
            AppendAllProperties(report, material);
        }

        // GetTexturePropertyNames returns Unity's own built-ins first (unity_Lightmaps and
        // friends), so every name is reported rather than assuming the first is the interesting
        // one - the same trap that writes a mod's texture into the lightmap slot.
        string[] names;
        try
        {
            names = material.GetTexturePropertyNames();
        }
        catch (Exception exception)
        {
            report.AppendLine("  (could not list texture properties: " + exception.Message + ")");
            return;
        }

        foreach (string name in names)
        {
            Texture texture = material.GetTexture(name);
            if (texture == null)
            {
                continue;
            }

            // A Texture2DArray's slice count is the thing that matters for the layer digits: the
            // grid indicator is one, and a layer past its last slice samples the last slice. The
            // PNG below is a blit, which only ever reads slice 0.
            string depth = texture is Texture2DArray array ? " x" + array.depth + " slices" : "";
            report.AppendLine("  " + name + " = '" + texture.name + "' " + texture.width + "x" + texture.height + depth);

            string file = label + "__" + Sanitize(name) + "__" + Sanitize(texture.name) + ".png";
            if (!written.Add(file))
            {
                continue;
            }

            if (TryWritePng(texture, Path.Combine(directory, file), out string error))
            {
                report.AppendLine("    -> " + file);
            }
            else
            {
                report.AppendLine("    !! " + error);
            }
        }
    }

    /// <summary>
    /// Every property the material's shader declares, with the material's value for it, plus its
    /// enabled keywords. Shader Graph names are generated (<c>_SampleTexture2D_&lt;guid&gt;…</c>),
    /// so a property whose name means nothing still gets its value printed - a float of 10 or a
    /// vector with a 3 in it is the kind of thing that would put a ceiling at layer 10.
    /// </summary>
    private static void AppendAllProperties(StringBuilder report, Material material)
    {
        Shader shader = material.shader;
        if (shader == null)
        {
            return;
        }

        try
        {
            report.AppendLine("  keywords: " + string.Join(" ", material.shaderKeywords ?? Array.Empty<string>()));
            report.AppendLine("  renderQueue: " + material.renderQueue + ", passes: " + material.passCount);

            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                UnityEngine.Rendering.ShaderPropertyType type = shader.GetPropertyType(i);
                string value;
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        value = material.GetColor(name).ToString("F3");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        value = material.GetVector(name).ToString("F3");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        value = material.GetFloat(name).ToString("R");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        Texture texture = material.GetTexture(name);
                        value = texture == null ? "<none>" : "'" + texture.name + "' " + texture.width + "x" + texture.height;
                        break;
                    default:
                        value = "?";
                        break;
                }

                report.AppendLine("  [" + type + "] " + name + " = " + value);
            }
        }
        catch (Exception exception)
        {
            report.AppendLine("  (could not list properties: " + exception.Message + ")");
        }
    }

    /// <summary>
    /// The globals <c>LayerRenderingController</c> publishes, read back from the GPU side rather
    /// than recomputed, so the dump shows what the shaders actually received. The two ramp
    /// textures' heights should equal <c>_G_LayerCount</c>; a mismatch is the known way to turn a
    /// belt black.
    /// </summary>
    private static void AppendLayerGlobals(StringBuilder report)
    {
        report.AppendLine("Shader globals:");
        try
        {
            foreach (string name in new[] { "_G_LayerCount", "_G_TilesPerIslandLayer", "_G_HideIslandLayerMaximumVisiblity" })
            {
                report.AppendLine("  " + name + " = " + Shader.GetGlobalFloat(name).ToString("R"));
            }

            foreach (string name in new[] { "_G_LayersParams", "_G_IslandLayersParams", "_G_CurrentLayerColor" })
            {
                report.AppendLine("  " + name + " = " + Shader.GetGlobalVector(name).ToString("F3"));
            }

            foreach (string name in new[] { "_G_LayerColorTexture", "_G_BeltArrowColorTexture" })
            {
                Texture texture = Shader.GetGlobalTexture(name);
                report.AppendLine("  " + name + " = " +
                                  (texture == null ? "<none>" : "'" + texture.name + "' " + texture.width + "x" + texture.height));
            }
        }
        catch (Exception exception)
        {
            report.AppendLine("  (could not read globals: " + exception.Message + ")");
        }
    }

    /// <summary>
    /// Copies through a RenderTexture rather than calling <c>GetPixels</c> on the source.
    /// A shipped texture almost always has Read/Write disabled, and reading it directly throws;
    /// blitting to a temporary RenderTexture and reading that back is the standard way round it
    /// and works regardless of the source's format.
    /// </summary>
    private static bool TryWritePng(Texture texture, string path, out string error)
    {
        RenderTexture temporary = null;
        RenderTexture previous = RenderTexture.active;
        Texture2D readable = null;

        try
        {
            temporary = RenderTexture.GetTemporary(
                texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(texture, temporary);

            RenderTexture.active = temporary;
            readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, mipChain: false);
            readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            readable.Apply();

            File.WriteAllBytes(path, readable.EncodeToPNG());
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
        finally
        {
            RenderTexture.active = previous;
            if (temporary != null)
            {
                RenderTexture.ReleaseTemporary(temporary);
            }

            if (readable != null)
            {
                UnityEngine.Object.Destroy(readable);
            }
        }
    }

    private static void AppendRamp(StringBuilder report, string label, Color[] ramp)
    {
        if (ramp == null)
        {
            report.AppendLine(label + ": null");
            return;
        }

        report.AppendLine(label + ": " + ramp.Length + " entries");
        for (int i = 0; i < ramp.Length; i++)
        {
            Color c = ramp[i];
            report.AppendLine(
                "  [" + i + "] r=" + c.r.ToString("0.###") +
                " g=" + c.g.ToString("0.###") +
                " b=" + c.b.ToString("0.###") +
                " a=" + c.a.ToString("0.###"));
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "unnamed";
        }

        StringBuilder builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        }

        return builder.ToString();
    }
}
