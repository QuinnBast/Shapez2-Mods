using UnityEngine;
using UnityEngine.UI;

namespace QuinnBast.Shapez2.ModProfiler;

/// <summary>
/// Draws the game's own full-screen page background behind this panel, using the game's own art.
///
/// **What the background actually is.** Every full-screen page in shapez - Statistics, Research,
/// the Wiki, the blueprint library - is a `HUDPart` holding one
/// <c>HUDFullscreenDialogBackground</c>, and that component is five stacked images: a vignette, a
/// glow at the top, a glow at the bottom, and two big faint line layers that rotate slowly in
/// opposite directions (see its <c>OnUpdate</c>, which turns them at 2 and -1.333 degrees a
/// second). The blue-over-warm wash those produce is most of what a shapez page *looks* like,
/// and a flat gradient is never going to be mistaken for it.
///
/// **Why the sprites and not the component.** Cloning the component would be the obvious move
/// and it is the wrong one: <c>HUDFullscreenDialogBackground</c> is a <c>HUDComponent</c> whose
/// <c>[Construct]</c> never runs on a runtime clone, and whose <c>OnUpdate</c> ends with
/// <c>ConsumeAll()</c> - so a clone would be an uninitialised object fighting this panel for the
/// input it already consumes for itself. Reading five sprites off the live instance and drawing
/// them in IMGUI keeps the art and none of the lifecycle.
///
/// **What is approximated.** The sprites, their tint colours and the two rotation rates are the
/// game's. Where each layer sits is not: the prefab's rectangles are authored, and an authored
/// value cannot be read from an assembly - the live instance's own transforms are mid-animation
/// (<c>Construct</c> parks the vignette at half height and the glows at three times width until
/// <c>Show</c> runs), so reading them back would capture a pose rather than a layout. The
/// proportions below were matched against a screenshot of the Statistics page.
/// </summary>
internal static class GameBackdrop
{
    private struct Layer
    {
        public Texture Texture;

        /// <summary>Where in its atlas the sprite lives, normalised - sprites here are packed.</summary>
        public Rect Coordinates;

        public Color Colour;

        public bool Valid => Texture != null;
    }

    private static Layer Vignette;
    private static Layer GlowTop;
    private static Layer GlowBottom;
    private static Layer Lines;
    private static Layer LinesTwo;

    private static bool Resolved;

    /// <summary>
    /// Whether there is still a background to draw - asked of the layers every time, not cached.
    ///
    /// **Cached, this was a bug.** The sprites belong to the session's HUD, so quitting to the
    /// main menu destroys them. Every layer then reports itself invalid and draws nothing, but a
    /// bool set once at resolve stayed true - so the panel's "retry until it works" never fired
    /// again and the next save ran on the bare gradient for the rest of the process.
    ///
    /// Deriving it costs five null checks a frame and cannot go stale: a destroyed
    /// <c>UnityEngine.Object</c> compares equal to null, which is exactly the signal needed.
    /// </summary>
    public static bool Available => Vignette.Valid || GlowTop.Valid || GlowBottom.Valid
                                    || Lines.Valid || LinesTwo.Valid;

    /// <summary>What the last resolve found, for <c>prof.probe</c> and the log.</summary>
    public static string Status { get; private set; } = "Not looked for yet.";

    /// <summary>
    /// Finds the live background once.
    ///
    /// <c>FindObjectsOfTypeAll</c> rather than <c>FindObjectsOfType</c> because every one of
    /// these is inactive until its page opens - <c>HUDStatistics.Construct</c> ends with
    /// <c>gameObject.SetActive(false)</c> - and the active-only search returns nothing.
    /// </summary>
    public static void Resolve()
    {
        if (Resolved)
        {
            return;
        }

        Resolved = true;

        try
        {
            HUDFullscreenDialogBackground[] found =
                Resources.FindObjectsOfTypeAll<HUDFullscreenDialogBackground>();

            if (found.Length == 0)
            {
                Status = "No HUDFullscreenDialogBackground in the scene - not in a session yet.";
                return;
            }

            HUDFullscreenDialogBackground source = found[0];

            Vignette = Read(source.UIVignetteTransform);
            GlowTop = Read(source.UIGlowTopTransform);
            GlowBottom = Read(source.UIGlowBottomTransform);
            Lines = Read(source.UIBackgroundLinesTransform);
            LinesTwo = Read(source.UIBackgroundLines2Transform);

            int count = (Vignette.Valid ? 1 : 0) + (GlowTop.Valid ? 1 : 0) + (GlowBottom.Valid ? 1 : 0)
                        + (Lines.Valid ? 1 : 0) + (LinesTwo.Valid ? 1 : 0);

            Status = count > 0
                ? count + " of 5 background layers read from " + source.name + "."
                : "Found " + source.name + ", but none of its layers carry a sprite.";
        }
        catch (System.Exception exception)
        {
            // A background this could not read is a page drawn on the generated gradient, which
            // is what it looked like before any of this existed.
            Forget();
            Status = "Could not read the game's background (" + exception.GetType().Name + ").";
        }
    }

    /// <summary>
    /// Resolution is cheap but only works inside a session, so it is retried each time the panel
    /// is opened while there is nothing to draw - before the first session, and again after a
    /// quit to the menu has taken the sprites with it.
    /// </summary>
    public static void Invalidate()
    {
        Resolved = false;
        Forget();
    }

    /// <summary>Drops the cached layers, which is what makes <see cref="Available"/> false.</summary>
    private static void Forget()
    {
        Vignette = default;
        GlowTop = default;
        GlowBottom = default;
        Lines = default;
        LinesTwo = default;
    }

    private static Layer Read(RectTransform transform)
    {
        Image image = transform == null ? null : transform.GetComponent<Image>();
        Sprite sprite = image == null ? null : image.sprite;

        if (sprite == null || sprite.texture == null)
        {
            return default;
        }

        Rect box = sprite.textureRect;
        Texture texture = sprite.texture;

        return new Layer
        {
            Texture = texture,
            Coordinates = new Rect(box.x / texture.width, box.y / texture.height,
                box.width / texture.width, box.height / texture.height),
            Colour = image.color,
        };
    }

    /// <summary>
    /// Paints the layers in the order the prefab stacks them: lines furthest back, then the two
    /// glows, then the vignette over everything.
    /// </summary>
    public static void Draw(float dim)
    {
        if (!Available)
        {
            return;
        }

        float width = Screen.width;
        float height = Screen.height;
        float span = Mathf.Max(width, height) * 1.9f;

        Rect square = new Rect((width - span) / 2f, (height - span) / 2f, span, span);

        // The same two rates the game turns them at, so the drift matches a page opened beside
        // this one. Unscaled, because this panel is meant to work with the game paused.
        Spin(Lines, square, Time.unscaledTime * 2f, dim * 0.8f);
        Spin(LinesTwo, square, 233f - Time.unscaledTime * 1.333f, dim * 0.8f);

        Blit(GlowTop, new Rect(-width * 0.3f, -height * 0.45f, width * 1.6f, height * 0.95f), dim);
        Blit(GlowBottom, new Rect(-width * 0.3f, height * 0.5f, width * 1.6f, height * 0.95f), dim);

        Blit(Vignette, new Rect(-width * 0.02f, -height * 0.02f, width * 1.04f, height * 1.04f), dim);
    }

    private static void Spin(Layer layer, Rect area, float degrees, float dim)
    {
        if (!layer.Valid)
        {
            return;
        }

        Matrix4x4 matrix = GUI.matrix;

        GUIUtility.RotateAroundPivot(degrees, area.center);
        Blit(layer, area, dim);

        GUI.matrix = matrix;
    }

    private static void Blit(Layer layer, Rect area, float dim)
    {
        if (!layer.Valid)
        {
            return;
        }

        Color colour = layer.Colour;
        colour.a *= dim;

        GUI.color = colour;

        // The sprites are atlas-packed, so the whole texture is the wrong thing to draw; the
        // sub-rect has to be given explicitly.
        GUI.DrawTextureWithTexCoords(area, layer.Texture, layer.Coordinates, true);

        GUI.color = Color.white;
    }
}
