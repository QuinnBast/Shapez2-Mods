using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// Turns a <see cref="ShapePartProfile"/> outline into the mesh a shape sub-part needs.
    ///
    /// It takes an outline, not a profile, because one profile produces one mesh per distinct
    /// `PartCount` - a 90 degree sector for the quad configurations and a 60 degree one for the
    /// hexagonal configuration. Nothing in here cares how wide the sector is; it extrudes whatever
    /// simple polygon it is handed.
    ///
    /// The mesh has to be built in code rather than loaded from a model file, and that is not a
    /// preference. <c>ShapeItemRenderer.GenerateShapeSubPartMesh</c> reads <c>sourceMesh.colors</c>
    /// per vertex and treats red below 0.05 as the outline material and anything else as the
    /// shape's colour - so the mesh's vertex colours *are* the material assignment. ShapezShifter's
    /// model importer (<c>AssimpToUnityMeshConverter</c>) keeps positions, normals and UVs and drops
    /// the colour channel entirely, so a part loaded from an .fbx or .obj arrives with an empty
    /// colour array and the renderer indexes off the end of it.
    public static class ShapePartMeshBuilder
    {
        /// Triangles are emitted un-indexed so every face gets its own normal. A part is a few
        /// hundred triangles and there are only a handful of parts, so the duplication costs
        /// nothing and it avoids smoothing a square's corner into a curve.
        private sealed class Buffer
        {
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector3> Normals = new List<Vector3>();
            public readonly List<Color32> Colors = new List<Color32>();
            public readonly List<int> Triangles = new List<int>();

            public void Add(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Color32 color)
            {
                if ((b - a).sqrMagnitude < 1e-12f || (c - a).sqrMagnitude < 1e-12f ||
                    (c - b).sqrMagnitude < 1e-12f)
                {
                    return;
                }

                int start = Vertices.Count;
                Vertices.Add(a);
                Vertices.Add(b);
                Vertices.Add(c);
                for (int i = 0; i < 3; i++)
                {
                    Normals.Add(normal);
                    Colors.Add(color);
                    Triangles.Add(start + i);
                }
            }

            /// For side walls, where the normal is whatever the triangle's own plane says.
            public void AddFacetted(Vector3 a, Vector3 b, Vector3 c, Color32 color)
            {
                Vector3 normal = Vector3.Cross(b - a, c - a);
                if (normal.sqrMagnitude < 1e-12f)
                {
                    return;
                }

                Add(a, b, c, normal.normalized, color);
            }
        }

        /// Anything with red at or above the renderer's 0.05 threshold takes the shape's colour.
        private static readonly Color32 Body = new Color32(255, 255, 255, 255);

        /// Red at zero is what marks a vertex as outline.
        private static readonly Color32 Outline = new Color32(0, 0, 0, 255);

        public static Mesh Build(ShapePartProfile profile, float sector)
        {
            // Clockwise in (x, z) is the winding that faces up, so normalising here means a profile
            // can be written in whichever direction reads best.
            Vector2[] loop = AsClockwise(profile.OutlineFor(sector));
            RejectRepeatedPoints(profile, sector, loop);

            // The outline is grown outward from the designed shape, which is what vanilla does and
            // is the opposite of what this used to do. See ShapeGeometry.OutlineWidth.
            Outset(loop, ShapeGeometry.OutlineWidth, out Vector2[] border, out int[] source);

            Buffer buffer = new Buffer();

            AddCap(buffer, loop, ShapeGeometry.Height, Vector3.up, Body);
            AddBevel(buffer, loop, border, source);
            AddSideWalls(buffer, border, ShapeGeometry.Height - ShapeGeometry.BorderDrop);
            AddCap(buffer, border, 0.0f, Vector3.down, Outline);

            Mesh mesh = new Mesh { name = "ShapePart_" + profile.Name };
            mesh.SetVertices(buffer.Vertices);
            mesh.SetNormals(buffer.Normals);
            mesh.SetColors(buffer.Colors);
            mesh.SetTriangles(buffer.Triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// Fills a closed outline at one height. Ear clipping rather than a fan from the centre,
        /// because a shape need not contain its own centre - a dot sitting out in the quadrant, a
        /// leaf, a crescent - and a fan would cover the gap with stray triangles.
        private static void AddCap(Buffer buffer, Vector2[] loop, float height, Vector3 normal, Color32 color)
        {
            List<int> triangles = Triangulate(loop);
            bool upwards = normal.y > 0.0f;

            for (int i = 0; i + 2 < triangles.Count; i += 3)
            {
                Vector3 a = At(loop[triangles[i]], height);
                Vector3 b = At(loop[triangles[i + 1]], height);
                Vector3 c = At(loop[triangles[i + 2]], height);

                // The clockwise winding faces up, so the underside takes the same triangles reversed.
                if (upwards)
                {
                    buffer.Add(a, b, c, normal, color);
                }
                else
                {
                    buffer.Add(a, c, b, normal, color);
                }
            }
        }

        /// The dark border, as the ring between the silhouette and the inset top face.
        /// The black bevel between the designed outline and the grown one: it starts at the top
        /// face's edge and runs outward *and downward*, which is how vanilla builds it.
        ///
        /// The drop is the whole point - see <see cref="ShapeGeometry.BorderDrop"/>. A flat band at
        /// the face's own height looks right in isolation and z-fights every neighbour it reaches
        /// over, and it reaches over all of them.
        ///
        /// Not a quad strip: a rounded corner emits several border points for one source point, so
        /// each step walks the *border* and fans back to whichever input point it came from. Where
        /// two consecutive border points share a source the second triangle collapses, and the
        /// buffer drops it - which is cheaper than branching on it here.
        ///
        /// Facetted normals rather than a flat `up`, because the surface is now sloped. Getting
        /// them backwards would be nearly invisible anyway: every triangle here is outline black.
        private static void AddBevel(Buffer buffer, Vector2[] loop, Vector2[] border, int[] source)
        {
            float outerHeight = ShapeGeometry.Height - ShapeGeometry.BorderDrop;

            for (int j = 0; j < border.Length; j++)
            {
                int next = (j + 1) % border.Length;

                Vector3 p = At(border[j], outerHeight);
                Vector3 q = At(border[next], outerHeight);

                int inner = source[j];
                buffer.AddFacetted(p, q, At(loop[inner], ShapeGeometry.Height), Outline);

                // Walk *every* outline vertex this border step passed over, not just its two ends.
                //
                // Where `DropFoldedPoints` removed points the step spans several outline vertices,
                // and joining only the ends leaves the bevel's inner edge as a chord across the
                // notch while the coloured cap still follows the notch itself. The sliver between
                // the two belongs to no triangle at all, so the shape is open there and you can see
                // straight through the layer. Reported on Gear and Flower, which are the only two
                // parts that lose points.
                int guard = 0;
                while (inner != source[next] && guard++ <= loop.Length)
                {
                    int following = (inner + 1) % loop.Length;

                    buffer.AddFacetted(q, At(loop[following], ShapeGeometry.Height),
                        At(loop[inner], ShapeGeometry.Height), Outline);

                    inner = following;
                }
            }
        }

        private static void AddSideWalls(Buffer buffer, Vector2[] loop, float topHeight)
        {
            for (int k = 0; k < loop.Length; k++)
            {
                int next = (k + 1) % loop.Length;

                Vector3 topP = At(loop[k], topHeight);
                Vector3 topQ = At(loop[next], topHeight);
                Vector3 botP = At(loop[k], 0.0f);
                Vector3 botQ = At(loop[next], 0.0f);

                buffer.AddFacetted(topP, botP, botQ, Outline);
                buffer.AddFacetted(topP, botQ, topQ, Outline);
            }
        }

        private static Vector3 At(Vector2 point, float height)
        {
            return new Vector3(point.x, height, point.y);
        }

        /// Ear clipping over a simple polygon wound clockwise in (x, z).
        ///
        /// Returns indices into <paramref name="loop"/>. If it gets numerically stuck it stops
        /// clipping and fans whatever is left, which draws something slightly wrong rather than
        /// looping forever inside a mod constructor.
        private static List<int> Triangulate(Vector2[] loop)
        {
            List<int> remaining = new List<int>(loop.Length);
            for (int i = 0; i < loop.Length; i++)
            {
                remaining.Add(i);
            }

            List<int> triangles = new List<int>();

            while (remaining.Count > 3)
            {
                int clipped = -1;

                for (int i = 0; i < remaining.Count; i++)
                {
                    int a = remaining[(i - 1 + remaining.Count) % remaining.Count];
                    int b = remaining[i];
                    int c = remaining[(i + 1) % remaining.Count];

                    if (Cross(loop[a], loop[b], loop[c]) >= 0.0f)
                    {
                        continue;   // reflex corner, or collinear: not an ear
                    }

                    if (ContainsAnother(loop, remaining, a, b, c))
                    {
                        continue;
                    }

                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                    clipped = i;
                    break;
                }

                if (clipped < 0)
                {
                    break;
                }

                remaining.RemoveAt(clipped);
            }

            for (int i = 1; i + 1 < remaining.Count; i++)
            {
                triangles.Add(remaining[0]);
                triangles.Add(remaining[i]);
                triangles.Add(remaining[i + 1]);
            }

            return triangles;
        }

        private static bool ContainsAnother(Vector2[] loop, List<int> remaining, int a, int b, int c)
        {
            foreach (int index in remaining)
            {
                if (index == a || index == b || index == c)
                {
                    continue;
                }

                Vector2 point = loop[index];
                if (Cross(loop[a], loop[b], point) < 0.0f &&
                    Cross(loop[b], loop[c], point) < 0.0f &&
                    Cross(loop[c], loop[a], point) < 0.0f)
                {
                    return true;
                }
            }

            return false;
        }

        /// Negative when the corner turns clockwise in (x, z), which is the interior side here.
        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        private static Vector2[] AsClockwise(Vector2[] outline)
        {
            float twiceArea = 0.0f;
            for (int i = 0; i < outline.Length; i++)
            {
                Vector2 current = outline[i];
                Vector2 next = outline[(i + 1) % outline.Length];
                twiceArea += current.x * next.y - next.x * current.y;
            }

            if (twiceArea <= 0.0f)
            {
                return outline;
            }

            Vector2[] reversed = new Vector2[outline.Length];
            for (int i = 0; i < outline.Length; i++)
            {
                reversed[i] = outline[outline.Length - 1 - i];
            }

            return reversed;
        }

        /// Moves every point of a closed loop inwards by <paramref name="width"/> along the bisector
        /// of its two edges, which keeps the border an even width around corners.
        ///
        /// The loop is walked clockwise in (x, z), so the inward normal of an edge running (ex, ez)
        /// is (ez, -ex). The miter length is clamped: at a sharp spike such as a sawblade tooth the
        /// bisector scale runs away, and an unclamped miter turns the point inside out.
        /// The designed outline grown outward by <paramref name="width"/>, with **rounded**
        /// corners, plus the index each generated point came from.
        ///
        /// Rounded rather than mitred, because vanilla is: a mitre would push a 90 degree corner
        /// out by `width * sqrt(2)`, and vanilla's square corner grows by 0.042 against a width of
        /// 0.043 - which is a circular offset, not a mitre. At the old 0.013 the difference was
        /// invisible; at 0.043 a mitred leaf tip would be a visible thorn three times the width of
        /// the border it belongs to.
        ///
        /// Concave corners still take a single mitred point, clamped the same way the old inset
        /// was: there is no arc to draw on the inside of a turn, and a corner that nearly doubles
        /// back would otherwise shoot off.
        ///
        /// The source indices exist because the offset is no longer one point per input point, so
        /// the band between the two loops cannot be a simple quad strip.
        private static void Outset(Vector2[] loop, float width, out Vector2[] points, out int[] source)
        {
            List<Vector2> outPoints = new List<Vector2>(loop.Length * 2);
            List<int> outSource = new List<int>(loop.Length * 2);

            for (int k = 0; k < loop.Length; k++)
            {
                Vector2 previous = loop[(k - 1 + loop.Length) % loop.Length];
                Vector2 next = loop[(k + 1) % loop.Length];

                Vector2 intoHere = (loop[k] - previous).normalized;
                Vector2 outOfHere = (next - loop[k]).normalized;

                Vector2 normalIn = Outward(intoHere);
                Vector2 normalOut = Outward(outOfHere);

                // Clockwise in (x, z) means a right turn is convex, and a right turn is a negative
                // cross product.
                float turn = intoHere.x * outOfHere.y - intoHere.y * outOfHere.x;

                if (turn < -1e-6f)
                {
                    float angle = Vector2.SignedAngle(normalIn, normalOut);
                    int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Abs(angle) / 18.0f), 1, 16);

                    for (int step = 0; step <= steps; step++)
                    {
                        Vector2 direction = Rotate(normalIn, angle * step / steps);
                        outPoints.Add(loop[k] + direction * width);
                        outSource.Add(k);
                    }

                    continue;
                }

                Vector2 bisector = normalIn + normalOut;
                if (bisector.sqrMagnitude < 1e-8f)
                {
                    // The loop doubles back on itself; there is no meaningful bisector.
                    outPoints.Add(loop[k] + normalOut * width);
                    outSource.Add(k);
                    continue;
                }

                bisector = bisector.normalized;
                float projection = Mathf.Max(Vector2.Dot(bisector, normalOut), 0.34f);
                outPoints.Add(loop[k] + bisector * (width / projection));
                outSource.Add(k);
            }

            DropFoldedPoints(loop, width, outPoints, outSource);

            points = outPoints.ToArray();
            source = outSource.ToArray();
        }

        /// How close to the source outline an offset point may sit before it counts as folded.
        ///
        /// A valid outward offset point is exactly `width` away from the outline it came from.
        /// Anything materially closer has crossed some *other* part of the same outline, which is
        /// what happens when a notch is narrower than twice the border width. Empirically every
        /// part is clean at 0.95 and above; 0.97 leaves margin without discarding good points.
        private const float FoldTolerance = 0.97f;

        /// Removes offset points that folded over another part of the outline.
        ///
        /// **Without this the border self-intersects**, and at 60 degrees Gear and Flower both do:
        /// their notches are finer than `2 * OutlineWidth`, so the two walls of a notch each offset
        /// outward past one another. The resulting mesh has overlapping bevel triangles with
        /// *different* facetted normals, which is why it shows up as z-fighting rather than as
        /// harmless black-on-black - reported in hexagonal mode after the bevel fix, and invisible
        /// in quad, where none of the parts have a notch that tight.
        ///
        /// Dropping the folded points is the whole fix: what remains bridges the notch, which is
        /// what a true offset does anyway. A notch narrower than the border simply is not
        /// representable in the border, and closing over it is the honest answer.
        private static void DropFoldedPoints(Vector2[] loop, float width, List<Vector2> points,
            List<int> source)
        {
            float minimum = width * FoldTolerance;

            for (int j = points.Count - 1; j >= 0; j--)
            {
                if (DistanceToLoop(points[j], loop) < minimum)
                {
                    points.RemoveAt(j);
                    source.RemoveAt(j);
                }
            }

            // Cannot happen for anything in the catalogue, and a two point border would throw
            // further down rather than draw badly.
            if (points.Count < 3)
            {
                throw new InvalidOperationException(
                    $"The border collapsed to {points.Count} points. The outline has a feature " +
                    $"finer than the {width} border everywhere it was measured.");
            }
        }

        private static float DistanceToLoop(Vector2 point, Vector2[] loop)
        {
            float best = float.MaxValue;

            for (int k = 0; k < loop.Length; k++)
            {
                best = Mathf.Min(best, DistanceToSegment(point, loop[k], loop[(k + 1) % loop.Length]));
            }

            return best;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 edge = b - a;
            float lengthSquared = edge.sqrMagnitude;
            float t = lengthSquared < 1e-18f
                ? 0.0f
                : Mathf.Clamp01(Vector2.Dot(point - a, edge) / lengthSquared);

            return Vector2.Distance(point, a + edge * t);
        }

        /// The outward normal of an edge. The inward one is `(z, -x)` for a clockwise loop, so the
        /// outward one is its negation.
        private static Vector2 Outward(Vector2 edge)
        {
            return new Vector2(-edge.y, edge.x);
        }

        private static Vector2 Rotate(Vector2 vector, float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);
            return new Vector2(vector.x * cos - vector.y * sin, vector.x * sin + vector.y * cos);
        }

        /// A repeated point gives a zero-length edge, which has no direction to build a normal or a
        /// bisector from. Throwing at mod load beats a part that renders as a smear.
        private static void RejectRepeatedPoints(ShapePartProfile profile, float sector, Vector2[] loop)
        {
            for (int k = 0; k < loop.Length; k++)
            {
                Vector2 next = loop[(k + 1) % loop.Length];
                if ((next - loop[k]).sqrMagnitude < 1e-10f)
                {
                    throw new ArgumentException(
                        $"Shape part '{profile.Name}' repeats the point {loop[k]} at index {k}. " +
                        "Outline points must all be distinct, including where two boundaries meet.");
                }
            }
        }
    }
}
