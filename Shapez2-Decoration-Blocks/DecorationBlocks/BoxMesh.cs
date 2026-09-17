using System.Collections.Generic;
using UnityEngine;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// A mesh built from axis-aligned boxes, each face pointing at its own atlas tile.
///
/// The generalisation of `CubeMesh`, which is one box with three distinct faces. A redstone
/// torch is a thin post with a head on it, a repeater is a slab with two pillars, a lever is a
/// plate with a handle - all boxes, all authored in code for the same reason the cube is: the
/// UVs address an atlas that does not exist until the mod has read the player's texture folder.
///
/// Coordinates are in tile space: X and Z run -0.5 to 0.5 across the tile, Y runs 0 upward from
/// the deck, and one building layer is one unit (see CubeMesh).
internal static class BoxMesh
{
    /// One box, and which texture goes on which face. A null texture leaves that face out
    /// entirely - worth doing for the underside of anything flat, which is never seen and
    /// otherwise z-fights with the platform deck.
    internal sealed class Box
    {
        public Box(
            Vector3 min, Vector3 max, string side, string top = null, string bottom = null,
            Rect? uv = null, int topTurns = 0, bool topMirror = false)
        {
            Min = min;
            Max = max;
            Side = side;
            Top = top ?? side;
            Bottom = bottom;
            Uv = uv ?? new Rect(0.0f, 0.0f, 1.0f, 1.0f);
            TopTurns = topTurns;
            TopMirror = topMirror;
        }

        public Vector3 Min { get; }

        public Vector3 Max { get; }

        public string Side { get; }

        public string Top { get; }

        public string Bottom { get; }

        /// Quarter turns, clockwise seen from above, applied to the **top and bottom** faces.
        ///
        /// A block's top texture is authored with its own up pointing north, which is the default
        /// here and needs no turn. A component's top texture is authored pointing along the way
        /// the component faces - a repeater's groove runs from its input to its output, and a
        /// comparator's back slot sits at the end its input arrives from - so those want one turn,
        /// which lands the texture's up on +X, the direction `RedstoneWorld.Facing` returns.
        public int TopTurns { get; }

        /// Flips a top or bottom face's **up** axis, after any turns.
        ///
        /// Kept because the eight combinations of four turns and one flip are what it takes to
        /// reach every orientation a Minecraft top texture might be authored in - Minecraft rotates
        /// a block's top face inside each model rather than authoring every texture to one
        /// convention, so the right setting is a fact about each texture.
        ///
        /// It flips `up` rather than `right` because that is the axis the repeater and comparator
        /// slabs needed once the sign bug below was fixed, and those two are confirmed correct
        /// on screen.
        public bool TopMirror { get; }

        /// Which part of the tile this box samples, in 0-1 tile space.
        ///
        /// The whole tile by default, which is right for a block. It is **wrong for anything
        /// Minecraft draws as a small model**: a torch texture is a torch shape on a transparent
        /// field, so a box mapped to the whole tile shows the transparent field on its faces and
        /// depends on the shader clipping it away. Minecraft's own torch model does not do that -
        /// it maps the box to the stick's pixels. So does this.
        public Rect Uv { get; }
    }

    /// A flat quad lying just above the deck, which is what dust is.
    ///
    /// The height is not zero. A quad coplanar with the platform deck z-fights with it - the two
    /// surfaces are the same distance from the camera and which one wins is decided by floating
    /// point noise, so the dust flickers as the camera moves. A hundredth of a unit is under a
    /// pixel at any sane zoom and settles it.
    public static Mesh Plane(string texture, BlockAtlas atlas, string name, float height = 0.01f)
    {
        return Build(new[]
        {
            new Box(new Vector3(-0.5f, height, -0.5f), new Vector3(0.5f, height, 0.5f), texture),
        }, atlas, name);
    }

    /// A flat quad covering part of a tile, for building a shape out of geometry rather than out
    /// of a texture's transparency.
    public static Box Quad(
        float minX, float minZ, float maxX, float maxZ, string texture, float height = 0.01f,
        Rect? uv = null)
    {
        return new Box(
            new Vector3(minX, height, minZ), new Vector3(maxX, height, maxZ), texture, uv: uv);
    }

    /// Twelve bars along the edges of a box: four uprights and eight rails.
    ///
    /// Used for two different things that want the same shape. A see-through block *is* this (see
    /// CubeMesh.Frame), and every one of the mod's buildings uses it as its **blueprint mesh** -
    /// the shape the game's placement ghost draws in flat blue. A solid ghost of a one by one by
    /// one cube covers the thing being placed completely; a wireframe shows the footprint and
    /// leaves the middle open.
    public static IEnumerable<Box> Edges(
        Vector3 min, Vector3 max, string texture, Rect? uv = null, float thickness = 0.06f)
    {
        float t = Mathf.Min(thickness, Mathf.Min(max.x - min.x, max.z - min.z) * 0.4f);

        foreach (float x in new[] { min.x, max.x - t })
        {
            foreach (float z in new[] { min.z, max.z - t })
            {
                yield return new Box(
                    new Vector3(x, min.y, z), new Vector3(x + t, max.y, z + t), texture, uv: uv);
            }
        }

        foreach (float y in new[] { min.y, max.y - t })
        {
            foreach (float z in new[] { min.z, max.z - t })
            {
                yield return new Box(
                    new Vector3(min.x, y, z), new Vector3(max.x, y + t, z + t), texture, uv: uv);
            }

            foreach (float x in new[] { min.x, max.x - t })
            {
                yield return new Box(
                    new Vector3(x, y, min.z), new Vector3(x + t, y + t, max.z), texture, uv: uv);
            }
        }
    }

    /// The wireframe of whatever a mesh occupies, for use as a placement ghost.
    public static Mesh EdgesOf(Mesh source, string texture, BlockAtlas atlas, string name)
    {
        Bounds bounds = source.bounds;
        return Build(
            new List<Box>(Edges(bounds.min, bounds.max, texture)), atlas, name);
    }

    public static Mesh Build(IReadOnlyList<Box> boxes, BlockAtlas atlas, string name)
    {
        List<Vector3> vertices = new List<Vector3>();
        List<Vector3> normals = new List<Vector3>();
        List<Vector2> uvs = new List<Vector2>();
        List<int> triangles = new List<int>();

        foreach (Box box in boxes)
        {
            AddFace(box, new Vector3(0, 1, 0), box.Top);
            AddFace(box, new Vector3(0, -1, 0), box.Bottom);
            AddFace(box, new Vector3(0, 0, 1), box.Side);
            AddFace(box, new Vector3(0, 0, -1), box.Side);
            AddFace(box, new Vector3(1, 0, 0), box.Side);
            AddFace(box, new Vector3(-1, 0, 0), box.Side);
        }

        Mesh mesh = new Mesh { name = name };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        return mesh;

        void AddFace(Box box, Vector3 normal, string texture)
        {
            if (texture == null)
            {
                return;
            }

            // The tile, then the box's window within it.
            Rect tile = atlas.UvFor(texture);
            Rect rect = new Rect(
                tile.x + tile.width * box.Uv.x,
                tile.y + tile.height * box.Uv.y,
                tile.width * box.Uv.width,
                tile.height * box.Uv.height);

            // The two axes of the face. v runs upward where there is an up to run along; on a
            // horizontal face it runs north, which is the convention a block's top texture is
            // authored in.
            //
            // The horizontal case is **not** `Cross(up, normal)`. That returns -X for the top
            // face, which mirrors every texture put on it - and mirrored is not something you
            // notice on gravel or grass, so it survived until a texture with a direction in it
            // went on a box. CubeMesh always hardcoded +X here; this now agrees with it, which is
            // the point: two mesh builders in one mod disagreeing about which way is east is a bug
            // waiting for the first asymmetric texture.
            Vector3 up;
            Vector3 right;

            if (Mathf.Abs(normal.y) > 0.5f)
            {
                right = new Vector3(1, 0, 0);
                up = new Vector3(0, 0, 1);

                for (int turn = 0; turn < ((box.TopTurns % 4) + 4) % 4; turn++)
                {
                    right = QuarterTurn(right);
                    up = QuarterTurn(up);
                }

                if (box.TopMirror)
                {
                    up = -up;
                }
            }
            else
            {
                up = new Vector3(0, 1, 0);
                right = Vector3.Cross(up, normal);

                if (right.sqrMagnitude < 0.5f)
                {
                    right = new Vector3(1, 0, 0);
                }
            }

            Vector3 centre = (box.Min + box.Max) * 0.5f;
            Vector3 extent = (box.Max - box.Min) * 0.5f;

            // Offset from the box centre to the face centre, then half-spans along the face's
            // own axes.
            //
            // These used to be `Scale(Abs(right), extent)`, and the `Abs` was the bug behind four
            // rounds of chasing the observer's top texture. `extent` is a half-size and already
            // non-negative, so the absolute value did nothing except **throw away the axis's
            // sign** - which is the only thing that distinguishes one quarter turn from the turn
            // opposite it. Turns 1 and 3 produced byte-identical meshes, as did 0 and 2, so a top
            // face had two reachable orientations rather than four and half of every rotation
            // request was silently ignored.
            Vector3 origin = centre + Scale(normal, extent);
            Vector3 halfRight = Scale(right, extent);
            Vector3 halfUp = Scale(up, extent);

            int baseIndex = vertices.Count;

            Corner(-1, -1, 0.0f, 0.0f);
            Corner(+1, -1, 1.0f, 0.0f);
            Corner(+1, +1, 1.0f, 1.0f);
            Corner(-1, +1, 0.0f, 1.0f);

            // Winding derived from the geometry rather than asserted, as in CubeMesh: getting it
            // wrong by hand yields a shape that is invisible from outside and solid from inside,
            // which reads as "the mesh did not load".
            Vector3 edge1 = vertices[baseIndex + 1] - vertices[baseIndex];
            Vector3 edge2 = vertices[baseIndex + 2] - vertices[baseIndex];
            bool outward = Vector3.Dot(Vector3.Cross(edge1, edge2), normal) > 0.0f;

            int[] order = outward
                ? new[] { 0, 1, 2, 0, 2, 3 }
                : new[] { 2, 1, 0, 3, 2, 0 };

            foreach (int offset in order)
            {
                triangles.Add(baseIndex + offset);
            }

            void Corner(int u, int v, float texU, float texV)
            {
                vertices.Add(origin + Scale(halfRight, new Vector3(u, u, u)) + Scale(halfUp, new Vector3(v, v, v)));
                normals.Add(normal);
                uvs.Add(new Vector2(rect.x + rect.width * texU, rect.y + rect.height * texV));
            }
        }
    }

    /// One quarter turn clockwise about Y, seen from above: east becomes south, north becomes
    /// east. One turn therefore takes a top texture's up from north to east, which is where a
    /// component at rotation zero is pointing.
    private static Vector3 QuarterTurn(Vector3 v)
    {
        return new Vector3(v.z, v.y, -v.x);
    }

    private static Vector3 Scale(Vector3 a, Vector3 b)
    {
        return new Vector3(a.x * b.x, a.y * b.y, a.z * b.z);
    }

    private static Vector3 Abs(Vector3 v)
    {
        return new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
    }
}
