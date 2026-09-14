using UnityEngine;

namespace QuinnBast.Shapez2.ModProfiler;

/// <summary>
/// The top bar button's icon: a flame graph, drawn rather than loaded.
///
/// **Why generated and not a PNG.** Shipping an image means finding it at runtime, and
/// <c>ModDirectoryLocator</c> throws on a hot reload - a byte-loaded assembly has an empty
/// <c>Location</c>, which is the same trap that stops the panel itself being rebuilt in place.
/// A glyph that is three rounded rectangles does not need a file, a loader or an import
/// pipeline, and the page it opens is already built entirely out of generated textures.
///
/// The shape is a call tree seen the way the CPU tab draws one: a wide root, a narrower child
/// under it, and a grandchild indented under that. It reads as this mod rather than as a
/// borrowed Statistics icon, which is what it replaced.
/// </summary>
internal static class PanelIcon
{
    private const int Size = 128;

    private static Sprite Glyph;

    /// <summary>
    /// Builds the icon once and hands out the same sprite afterwards.
    ///
    /// **Never destroyed**, deliberately. A hot reload disposes this mod and the next generation
    /// re-points the button that is already in the bar; destroying the sprite in between would
    /// leave that button showing nothing until the bar next rebuilt its state, and leave a mod
    /// that was simply switched off showing a hole. One 128x128 texture is 64 KiB, which is the
    /// cheaper of the two mistakes.
    /// </summary>
    public static Sprite FlameGraph()
    {
        if (Glyph != null)
        {
            return Glyph;
        }

        Texture2D texture = new Texture2D(Size, Size, TextureFormat.ARGB32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };

        Color[] pixels = new Color[Size * Size];

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Color(1f, 1f, 1f, 0f);
        }

        // Top down, widest first, the third indented under the second - a root, a child and a
        // grandchild. White, because the game tints its own bar icons through Image.color.
        Bar(pixels, 16f, 18f, 112f, 42f);
        Bar(pixels, 16f, 52f, 80f, 76f);
        Bar(pixels, 32f, 86f, 76f, 110f);

        texture.SetPixels(pixels);
        texture.Apply();

        Glyph = Sprite.Create(texture, new Rect(0f, 0f, Size, Size), new Vector2(0.5f, 0.5f));
        Glyph.name = "ModProfilerIcon";
        Glyph.hideFlags = HideFlags.HideAndDontSave;

        return Glyph;
    }

    /// <summary>
    /// One rounded bar, in top-down coordinates, antialiased from a distance field the same way
    /// the panel's own surfaces are.
    /// </summary>
    private static void Bar(Color[] pixels, float left, float top, float right, float bottom)
    {
        float radius = Mathf.Min(5f, (bottom - top) / 2f);

        float centreX = (left + right) / 2f;
        float centreY = (top + bottom) / 2f;
        float extentX = (right - left) / 2f - radius;
        float extentY = (bottom - top) / 2f - radius;

        for (int y = (int)top - 2; y <= (int)bottom + 2; y++)
        {
            if (y < 0 || y >= Size)
            {
                continue;
            }

            for (int x = (int)left - 2; x <= (int)right + 2; x++)
            {
                if (x < 0 || x >= Size)
                {
                    continue;
                }

                float dx = Mathf.Max(Mathf.Abs(x + 0.5f - centreX) - extentX, 0f);
                float dy = Mathf.Max(Mathf.Abs(y + 0.5f - centreY) - extentY, 0f);

                float alpha = Mathf.Clamp01(0.5f - (Mathf.Sqrt(dx * dx + dy * dy) - radius));

                if (alpha <= 0f)
                {
                    continue;
                }

                // Row zero is the bottom of a Unity texture, so the top-down y is flipped here
                // rather than every rectangle above being written upside down.
                int index = (Size - 1 - y) * Size + x;

                pixels[index] = new Color(1f, 1f, 1f, Mathf.Max(pixels[index].a, alpha));
            }
        }
    }
}
