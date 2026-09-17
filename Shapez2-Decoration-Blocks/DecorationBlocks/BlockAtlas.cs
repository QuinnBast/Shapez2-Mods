using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// One texture holding every block face, built at load from the individual PNGs in
/// Resources/Textures.
///
/// Packing at runtime rather than shipping a pre-built atlas is what lets a player swap in a
/// resource pack by copying files: the catalog names files, not atlas coordinates, so nothing
/// has to be re-authored when a texture changes size or is replaced.
///
/// Every cell is the tile repeated three by three, with the mesh pointing at the middle copy.
/// That is the fix for two problems at once. Mip levels average across cell boundaries, so a
/// tightly packed atlas bleeds a neighbouring tile's colour into a block's edges as the camera
/// pulls back, and bilinear sampling does the same at the seam even at mip 0. Surrounding each
/// tile with copies of itself means whatever bleeds in is that same tile, at every mip level,
/// so there is nothing to see. It costs nine times the texture memory for an atlas a few
/// hundred pixels square, which is to say nothing.
public sealed class BlockAtlas
{
    /// How many copies of the tile go across each cell. Three is the smallest odd number that
    /// gives the centre copy a full ring of neighbours.
    private const int CellRepeat = 3;

    private readonly Dictionary<string, Rect> UvByTexture;

    private readonly Dictionary<string, Rect> PixelsByTexture;

    private BlockAtlas(
        Texture2D texture, Dictionary<string, Rect> uvByTexture,
        Dictionary<string, Rect> pixelsByTexture, int tileSize)
    {
        Texture = texture;
        UvByTexture = uvByTexture;
        PixelsByTexture = pixelsByTexture;
        TileSize = tileSize;
    }

    public Texture2D Texture { get; }

    /// The edge of one tile in pixels, after every source has been resampled to agree. Reported
    /// so the log can say what resolution the installed pack turned out to be.
    public int TileSize { get; }

    /// Where a texture sits in the atlas, in UV space. A missing name returns the whole atlas
    /// rather than throwing: a pack with one file absent should show one wrong block, not fail
    /// to load.
    public Rect UvFor(string textureName)
    {
        return UvByTexture.TryGetValue(textureName, out Rect rect)
            ? rect
            : new Rect(0.0f, 0.0f, 1.0f, 1.0f);
    }

    public bool Has(string textureName)
    {
        return UvByTexture.ContainsKey(textureName);
    }

    /// The same tile in pixels, for Sprite.Create.
    ///
    /// Toolbar icons are cut straight out of the atlas rather than drawn: a texture pack mod
    /// whose entry shows anything other than the texture it places would be lying about itself,
    /// and 21 hand-drawn icons would go stale the moment someone swapped their pack. The
    /// atlas is already FilterMode.Point, so a 16 pixel sprite blown up to toolbar size stays
    /// crisp instead of turning to soup.
    public Rect PixelRectFor(string textureName)
    {
        return PixelsByTexture.TryGetValue(textureName, out Rect rect)
            ? rect
            : new Rect(0.0f, 0.0f, Texture.width, Texture.height);
    }

    public static BlockAtlas Build(string textureDirectory, IReadOnlyList<string> names, ILogger log)
    {
        List<string> present = new List<string>();
        List<Color32[]> tiles = new List<Color32[]>();
        List<int> widths = new List<int>();
        List<int> heights = new List<int>();

        foreach (string name in names)
        {
            string path = Path.Combine(textureDirectory, name + ".png");
            if (!File.Exists(path))
            {
                log?.Error?.Log(
                    "Decoration blocks: no texture at " + path
                    + ". Blocks using it will sample the wrong part of the atlas.");
                continue;
            }

            // LoadImage marks the texture readable, which the GetPixels32 below needs. A texture
            // shipped inside a Unity build would not be, which is why this path loads from a
            // file rather than from an asset bundle.
            Texture2D source = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            source.LoadImage(File.ReadAllBytes(path));

            int width = source.width;
            int height = source.height;

            // An animated texture in a resource pack is a vertical strip of square frames. Take
            // the first frame rather than squashing the whole film onto one face.
            if (height > width && width > 0 && height % width == 0)
            {
                height = width;
            }

            present.Add(name);
            tiles.Add(source.GetPixels32());
            widths.Add(source.width);
            heights.Add(height);
            UnityEngine.Object.Destroy(source);
        }

        int tileSize = 16;
        for (int i = 0; i < widths.Count; i++)
        {
            tileSize = Math.Max(tileSize, widths[i]);
        }

        int cell = tileSize * CellRepeat;
        int columns = Math.Max(1, Mathf.CeilToInt(Mathf.Sqrt(Math.Max(1, present.Count))));
        int rows = Math.Max(1, Mathf.CeilToInt(present.Count / (float)columns));

        Texture2D atlas = new Texture2D(columns * cell, rows * cell, TextureFormat.RGBA32, mipChain: true)
        {
            // The whole point of the mod. Bilinear on a 16 pixel texture is a smear, and
            // bilinear is the default for a texture built in code.
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            anisoLevel = 0,
        };

        Color32[] canvas = new Color32[atlas.width * atlas.height];
        Dictionary<string, Rect> uvs = new Dictionary<string, Rect>();
        Dictionary<string, Rect> pixels = new Dictionary<string, Rect>();

        for (int i = 0; i < present.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;
            int originX = column * cell;

            // UV origin is bottom left and so is Color32 row 0, so rows are not inverted: the
            // atlas is written bottom up and addressed bottom up.
            int originY = row * cell;

            Color32[] resampled = Resample(tiles[i], widths[i], heights[i], tileSize);

            for (int copyY = 0; copyY < CellRepeat; copyY++)
            {
                for (int copyX = 0; copyX < CellRepeat; copyX++)
                {
                    Blit(canvas, atlas.width, resampled, tileSize,
                        originX + copyX * tileSize, originY + copyY * tileSize);
                }
            }

            // Half a texel inset on the addressed copy. Point filtering makes this unnecessary
            // in principle, but a UV landing exactly on a texel boundary is rounded in a
            // direction nothing in the API defines.
            float inset = 0.5f / atlas.width;
            float x = (originX + tileSize) / (float)atlas.width;
            float y = (originY + tileSize) / (float)atlas.height;
            float w = tileSize / (float)atlas.width;
            float h = tileSize / (float)atlas.height;

            uvs[present[i]] = new Rect(x + inset, y + inset, w - inset * 2.0f, h - inset * 2.0f);
            pixels[present[i]] = new Rect(originX + tileSize, originY + tileSize, tileSize, tileSize);
        }

        atlas.SetPixels32(canvas);
        atlas.Apply(updateMipmaps: true, makeNoLongerReadable: false);

        log?.Info?.Log(
            "Decoration blocks: atlas " + atlas.width + "x" + atlas.height + ", "
            + present.Count + "/" + names.Count + " textures at " + tileSize + "px.");

        return new BlockAtlas(atlas, uvs, pixels, tileSize);
    }

    /// Nearest neighbour, deliberately. A pack whose textures are a different resolution to the
    /// rest should stay crisp and the wrong size rather than become blurry and the right size,
    /// and at the integer ratios that are the usual case nearest neighbour is exact.
    private static Color32[] Resample(Color32[] source, int sourceWidth, int sourceHeight, int size)
    {
        Color32[] result = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            int sourceY = Math.Min(sourceHeight - 1, y * sourceHeight / size);

            for (int x = 0; x < size; x++)
            {
                int sourceX = Math.Min(sourceWidth - 1, x * sourceWidth / size);
                result[y * size + x] = source[sourceY * sourceWidth + sourceX];
            }
        }

        return result;
    }

    private static void Blit(Color32[] canvas, int canvasWidth, Color32[] tile, int size, int atX, int atY)
    {
        for (int y = 0; y < size; y++)
        {
            Array.Copy(tile, y * size, canvas, (atY + y) * canvasWidth + atX, size);
        }
    }
}
