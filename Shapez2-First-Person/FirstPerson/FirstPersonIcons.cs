using System;
using System.Collections.Generic;
using System.IO;
using Game.Core.Research;
using ShapezShifter.Kit;
using ShapezShifter.Textures;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.FirstPerson;

/// <summary>
/// The mod's own shop icons, served straight out of <c>GameData.GetImage</c>.
///
/// A research node's image is a <c>GameImageId</c>, and <c>GameData</c> resolves those
/// against a dictionary built from sprites in the game's own asset bundles - so an id of
/// ours is not in it and <c>GetImage</c> throws. That throw is not survivable: it comes out
/// of <c>HUDResearchSideUpgradeDisplay.RebuildView</c> through the whole
/// <c>HUDResearchTree</c> construction, leaving a research screen half-built and unclosable.
///
/// Two ways to fix that. Adding to <c>GameData._Images</c> works - the dictionary is
/// private but reachable - but needs the instance, and needs it before the screen is ever
/// opened. Hooking <c>GetImage</c> needs neither: our ids are answered from here, everything
/// else falls through to the original, and the sprite can be loaded the first time it is
/// actually asked for rather than at some carefully chosen moment.
///
/// If an icon file is missing the id is refused, and the caller falls back to borrowing an
/// existing upgrade's image. A missing PNG should cost the node its artwork, not the screen.
/// </summary>
public static class FirstPersonIcons
{
    public const string Prefix = "first-person.icon.";

    private static readonly Dictionary<string, string> Files = new Dictionary<string, string>
    {
        { "flight", "Icon_JetPack.png" },
        { "travel", "Icon_WaypointTravel.png" },
        { "train-riding", "Icon_TrainRiding.png" },
    };

    private static readonly Dictionary<string, Sprite> Loaded = new Dictionary<string, Sprite>();

    private static ILogger Logger;

    public static void Bind(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// The image id for an unlock, or none when its file cannot be loaded - in which case
    /// the caller should fall back to an image the game already has.
    /// </summary>
    public static bool TryGetImageId(string key, out GameImageId imageId)
    {
        if (TryGetSprite(Prefix + key, out Sprite _))
        {
            imageId = new GameImageId(Prefix + key);
            return true;
        }

        imageId = GameImageId.Empty;
        return false;
    }

    /// <summary>
    /// Answers for our ids only. Loads on first ask and remembers the answer, including a
    /// failure - a missing file should be one log line, not one per frame the research
    /// screen is open.
    /// </summary>
    public static bool TryGetSprite(string id, out Sprite sprite)
    {
        sprite = null;

        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (Loaded.TryGetValue(id, out sprite))
        {
            return sprite != null;
        }

        string key = id.Substring(Prefix.Length);

        if (!Files.TryGetValue(key, out string file))
        {
            Loaded[id] = null;
            return false;
        }

        try
        {
            sprite = FileTextureLoader.LoadTextureAsSprite(Path.Combine(ResourcesFolder(), file), out Texture2D _);
        }
        catch (Exception exception)
        {
            Logger?.Exception?.LogException(exception);
            sprite = null;
        }

        if (sprite == null)
        {
            Logger?.Error?.Log("First Person: could not load icon '" + file + "', falling back to a borrowed image.");
        }

        Loaded[id] = sprite;
        return sprite != null;
    }

    public static void Forget()
    {
        Loaded.Clear();
    }

    /// <summary>
    /// Where our files are.
    ///
    /// <c>ModDirectoryLocator.CreateLocator</c> is <c>Directory.GetParent(assembly.Location)</c>,
    /// and a hot reloader loads from a byte array - an assembly with no file behind it
    /// reports an empty <c>Location</c>, so that throws `Path cannot be the empty string`
    /// from inside the mod. Guarded here rather than at the call site because every icon
    /// load goes through it.
    ///
    /// <c>Application.persistentDataPath</c> is the folder `SPZ2_PERSISTENT` points at and
    /// is safe to read at runtime, unlike the build-time variable. The fallback prefers
    /// `mods-dev` because that is where a reloader stages from.
    /// </summary>
    private static string ResourcesFolder()
    {
        string location = typeof(FirstPersonIcons).Assembly.Location;

        if (!string.IsNullOrEmpty(location))
        {
            return ModDirectoryLocator.CreateLocator<FirstPersonMod>().SubLocator("Resources").SubPath(string.Empty);
        }

        return Path.Combine(Application.persistentDataPath, "mods-dev", "FirstPerson", "Resources");
    }
}
