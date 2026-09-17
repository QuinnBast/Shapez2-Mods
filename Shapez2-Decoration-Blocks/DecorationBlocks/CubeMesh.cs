using System.Collections.Generic;
using UnityEngine;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// The cube, built in code rather than imported from a file.
///
/// Three reasons it is not an .fbx. The importer keeps one mesh per file, so 21 blocks
/// would be 21 files; it negates X and reverses winding, so an authored cube arrives
/// mirrored and every UV has to be authored backwards to compensate; and the UVs are the whole
/// problem here - each face has to point at one cell of an atlas that does not exist until the
/// mod has read the player's texture folder, so they cannot be baked into a file at all.
///
/// Scale comes from the coordinate system and is not a choice. GlobalTileCoordinate.ToCenter_W
/// is (x, y, z + heightOffset) with z the building layer, so one building layer is exactly one
/// world unit and a unit cube fills the gap to the layer above it. Blocks therefore stack the
/// way a player expects, three layers to a platform, with no gap and no overlap.
internal static class CubeMesh
{
    /// Which way each face points, and the two axes its texture runs along. The v axis is the
    /// one that appears upward to a player, so side textures are not upside down.
    private static readonly Face[] Faces =
    {
        //          outward normal            u axis (right)            v axis (up)
        new Face(new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1), FaceKind.Top),
        new Face(new Vector3(0, -1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1), FaceKind.Bottom),
        new Face(new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 1, 0), FaceKind.Side),
        new Face(new Vector3(0, 0, -1), new Vector3(1, 0, 0), new Vector3(0, 1, 0), FaceKind.Side),
        new Face(new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 0), FaceKind.Side),
        new Face(new Vector3(-1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 0), FaceKind.Side),
    };

    private enum FaceKind
    {
        Top,
        Bottom,
        Side,
    }

    /// A unit cube sitting on the tile it occupies, with each face sampling its own atlas cell.
    ///
    /// The cube is centred on X and Z and rises from Y = 0, matching how the game places a
    /// building mesh: the transform it is drawn at is the tile centre on the deck, not the
    /// centre of the tile's volume.
    /// How thick the bars of a see-through block are, and where on its texture they sample.
    ///
    /// Minecraft's glass texture is a **one pixel opaque border around a transparent middle**, so
    /// a glass block drawn as a frame of twelve edge bars is not an approximation of it - it is
    /// what the texture depicts. The bars sample the top row, which is solid all the way across.
    private const float BarThickness = 0.07f;

    private static readonly Rect BorderWindow = new Rect(0.0f, 15.0f / 16.0f, 1.0f, 1.0f / 16.0f);

    public static Mesh Build(BlockDefinition block, BlockAtlas atlas)
    {
        if (block.Translucent)
        {
            return Frame(block, atlas);
        }

        Rect top = atlas.UvFor(block.TopTexture);
        Rect bottom = atlas.UvFor(block.BottomTexture);
        Rect side = atlas.UvFor(block.SideTexture);

        List<Vector3> vertices = new List<Vector3>(24);
        List<Vector3> normals = new List<Vector3>(24);
        List<Vector2> uvs = new List<Vector2>(24);
        List<int> triangles = new List<int>(36);

        Vector3 centre = new Vector3(0.0f, 0.5f, 0.0f);

        foreach (Face face in Faces)
        {
            Rect rect = face.Kind switch
            {
                FaceKind.Top => top,
                FaceKind.Bottom => bottom,
                _ => side,
            };

            Vector3 origin = centre + face.Normal * 0.5f;
            int baseIndex = vertices.Count;

            // Corners anticlockwise in the face's own (u, v) frame. Whether that comes out
            // anticlockwise on screen depends on handedness, which the winding fix below
            // settles without anyone having to be sure about it.
            AddCorner(-1, -1, 0.0f, 0.0f);
            AddCorner(+1, -1, 1.0f, 0.0f);
            AddCorner(+1, +1, 1.0f, 1.0f);
            AddCorner(-1, +1, 0.0f, 1.0f);

            // Emit the two triangles with whichever winding actually faces outward. Getting
            // this wrong by hand produces a cube that is invisible from outside and solid from
            // inside, which reads as "the mesh did not load" and sends you looking in the wrong
            // place. Deriving it from the geometry costs one cross product per face, once.
            Vector3 edge1 = vertices[baseIndex + 1] - vertices[baseIndex];
            Vector3 edge2 = vertices[baseIndex + 2] - vertices[baseIndex];
            bool outward = Vector3.Dot(Vector3.Cross(edge1, edge2), face.Normal) > 0.0f;

            if (outward)
            {
                triangles.Add(baseIndex + 0);
                triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 0);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 3);
            }
            else
            {
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 1);
                triangles.Add(baseIndex + 0);
                triangles.Add(baseIndex + 3);
                triangles.Add(baseIndex + 2);
                triangles.Add(baseIndex + 0);
            }

            void AddCorner(int u, int v, float texU, float texV)
            {
                vertices.Add(origin + face.U * (u * 0.5f) + face.V * (v * 0.5f));
                normals.Add(face.Normal);
                uvs.Add(new Vector2(
                    rect.x + rect.width * texU,
                    rect.y + rect.height * texV));
            }
        }

        Mesh mesh = new Mesh { name = "DecorationBlock_" + block.Id };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    /// A hollow cube: four posts and eight rails, with nothing in between.
    ///
    /// This exists because **transparency does not work**. Alpha clipping on a runtime-built URP
    /// Lit material has no effect in the shipped game - almost certainly shader variant stripping,
    /// where `_ALPHATEST_ON` is dropped as unused and the material falls back to a variant that
    /// renders transparent texels instead of discarding them. Glass came out as a solid grey cube
    /// and redstone dust as a black square.
    ///
    /// So nothing in this mod relies on it any more. Dust became geometry, small models sample the
    /// solid part of their texture, and a see-through block becomes the frame its texture already
    /// describes. The happy accident is that all three are what Minecraft's own models do.
    private static Mesh Frame(BlockDefinition block, BlockAtlas atlas)
    {
        const float Low = -0.5f;
        const float High = 0.5f;
        float inner = High - BarThickness;

        List<BoxMesh.Box> bars = new List<BoxMesh.Box>();

        // Four uprights at the corners.
        foreach (float x in new[] { Low, inner })
        {
            foreach (float z in new[] { Low, inner })
            {
                bars.Add(Bar(x, 0.0f, z, x + BarThickness, 1.0f, z + BarThickness, block));
            }
        }

        // Eight rails: two horizontal runs at each of the top and bottom edges.
        foreach (float y in new[] { 0.0f, 1.0f - BarThickness })
        {
            foreach (float z in new[] { Low, inner })
            {
                bars.Add(Bar(Low, y, z, High, y + BarThickness, z + BarThickness, block));
            }

            foreach (float x in new[] { Low, inner })
            {
                bars.Add(Bar(x, y, Low, x + BarThickness, y + BarThickness, High, block));
            }
        }

        return BoxMesh.Build(bars, atlas, "DecorationBlockFrame_" + block.Id);
    }

    private static BoxMesh.Box Bar(
        float minX, float minY, float minZ, float maxX, float maxY, float maxZ, BlockDefinition block)
    {
        return new BoxMesh.Box(
            new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ),
            block.SideTexture, uv: BorderWindow);
    }

    private readonly struct Face
    {
        public Face(Vector3 normal, Vector3 u, Vector3 v, FaceKind kind)
        {
            Normal = normal;
            U = u;
            V = v;
            Kind = kind;
        }

        public Vector3 Normal { get; }

        public Vector3 U { get; }

        public Vector3 V { get; }

        public FaceKind Kind { get; }
    }
}
