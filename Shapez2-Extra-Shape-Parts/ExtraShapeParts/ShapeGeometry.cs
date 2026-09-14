using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// The local space a shape sub-part mesh is authored in, and the helpers for laying points out
    /// in it.
    ///
    /// Both figures are read off <see cref="ShapeItemRenderer"/> rather than guessed: it scales
    /// every sub-part mesh by `ShapeDimensions2D / 0.37f * 0.5f` horizontally and by
    /// `ShapeLayerHeight / 0.1f` vertically. Those two divisors are the dimensions the vanilla
    /// meshes are authored at, so a mesh built to them comes out the same size as a circle.
    ///
    /// **Every outline here is a function of its sector angle, never of 90 degrees.**
    /// `ShapeItemRenderer.GenerateShapeMesh` places part `j` with a Y rotation of
    /// `j / PartCount * 360` and a *uniform* horizontal scale - there is no angular squash anywhere
    /// in that matrix - so a mesh authored for a quadrant and dropped into the six-part hexagonal
    /// configuration overlaps both its neighbours by 30 degrees. The sector has to be built in, and
    /// the helpers below take it as their first argument so it cannot be forgotten.
    public static class ShapeGeometry
    {
        /// Horizontal reference radius. A part may reach past it - the square's outer corner
        /// does, at `R * sqrt(2)` - but this is the radius a circle part has.
        public const float Radius = 0.37f;

        /// The angular width of one part in a shape of <paramref name="partCount"/> parts: 90
        /// degrees for the two quad configurations, 60 for the hexagonal one.
        public static float Sector(int partCount)
        {
            return 360.0f / partCount;
        }

        /// The smallest part count the geometry here can be built for.
        ///
        /// At two parts the sector is 180 degrees, <see cref="Cell"/>'s far corner collapses onto
        /// the origin and its normalisation divides by zero. `MetaShapesConfiguration.OnValidate`
        /// only requires an even count of at least one, so this is a real bound rather than a
        /// theoretical one - see <see cref="ShapePartInjector"/>, which skips below it.
        public const int MinimumPartCount = 3;

        /// Extrusion height of one layer.
        public const float Height = 0.1f;

        /// How far below the top face the border's outer edge sits.
        ///
        /// **The border is a bevel, not a flat band, and that is not cosmetic.** Dumped and
        /// counted, a vanilla part has *no flat black triangles at all*: its top face is entirely
        /// coloured, and the black is the sloped surface running outward and down from the face's
        /// edge, plus the wall under it. The circle's face is at y 0.0954 and the widest point of
        /// its black at 0.0754, so the drop is 0.02.
        ///
        /// A flat band at the same height as the face renders identically on its own and then
        /// z-fights the moment it overlaps a *neighbour's* face - which it always does, because the
        /// border grows 0.043 across a gap of about 0.022. Sloping it down means a neighbour's
        /// black passes under your colour instead of fighting it for the same plane. Reported as
        /// z-clipping on `ZuZuZuZuZuZu`, where two border triangles landed on the next part's face.
        public const float BorderDrop = 0.02f;

        /// Width of the dark border, in the same units - and it is grown **outward** from the
        /// shape, not carved inward out of it.
        ///
        /// **Measured, not guessed.** `esp.dump` writes the vanilla meshes too, and every one of
        /// them puts the coloured face at the designed outline and the black past it:
        ///
        ///     circle    colour to 0.370   black to 0.413   +0.044
        ///     square    colour to 0.502   black to 0.544   +0.042
        ///     windmill  colour to 0.373   black to 0.414   +0.041
        ///     CubeHex   colour to 0.395   black to 0.440   +0.045
        ///     FlowerHex colour to 0.459   black to 0.503   +0.044
        ///
        /// The first version of this mod had it backwards - it inset the colour and left the black
        /// *inside* the nominal boundary, about 0.013 wide. That was wrong twice over: the border
        /// was a third of vanilla's width, and because each part stopped at its own outline the
        /// gaps between neighbours stayed background-coloured instead of merging into one thick
        /// line. Growing outward is what closes them, because two neighbours each grow 0.043 across
        /// a gap of roughly half that.
        public static float OutlineWidth = 0.043f;

        /// A point at `degrees` around from the +Z axis towards +X, `radius` out from the centre.
        ///
        /// That is the sector the renderer draws unrotated: for part index 0 it applies no
        /// rotation and offsets the mesh along `(sin, cos)` of half a sector, so the first part is
        /// the one between +Z and the edge at <see cref="Sector"/> degrees.
        public static Vector2 Polar(float degrees, float radius)
        {
            float radians = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians) * radius, Mathf.Cos(radians) * radius);
        }

        /// An arc at a constant radius, inclusive of both ends.
        public static IEnumerable<Vector2> Arc(float fromDegrees, float toDegrees, float radius, int steps)
        {
            for (int i = 0; i <= steps; i++)
            {
                yield return Polar(Mathf.Lerp(fromDegrees, toDegrees, (float)i / steps), radius);
            }
        }

        /// A boundary sampled from a radius function, swept across the whole sector.
        ///
        /// The function is given a *fraction* of the sector rather than an angle, which is the
        /// point: a lobe written as `t => f(t)` is the same lobe at any part count, where one
        /// written against degrees silently stays 90 degrees wide.
        public static IEnumerable<Vector2> Radial(float sector, Func<float, float> radiusAtFraction, int steps)
        {
            for (int i = 0; i <= steps; i++)
            {
                float fraction = (float)i / steps;
                yield return Polar(sector * fraction, radiusAtFraction(fraction));
            }
        }

        /// The sector's own oblique basis: `v` along the edge it starts on, `u` along the edge it
        /// ends on, both in units of <see cref="Radius"/>.
        ///
        /// This is what an outline authored in the quadrant's *cell* - the square from the origin
        /// out to `(R, R)` that the vanilla square fills - has to go through to survive a narrower
        /// sector. Mapping through the basis keeps straight edges straight, which angular
        /// compression does not: <see cref="Compress"/> would bow the square into a fan.
        ///
        /// At 90 degrees it is the identity, `Cell(90, u, v) == (u * R, v * R)`, so the quad
        /// geometry is unchanged to the bit.
        ///
        /// **The basis is normalised so the cell's far corner keeps the radius it has in a
        /// quadrant**, and that is not cosmetic. Two edges of length `R` at 60 degrees span a
        /// rhombus whose long diagonal is `1.73R` against a quadrant's `1.41R`, so the raw basis
        /// makes every cell-authored part a third too big and drives it into its neighbours.
        public static Vector2 Cell(float sector, float u, float v)
        {
            Vector2 edge = Polar(sector, Radius);
            Vector2 corner = new Vector2(edge.x, Radius + edge.y);
            float fit = new Vector2(Radius, Radius).magnitude / corner.magnitude;

            return new Vector2(u * edge.x, v * Radius + u * edge.y) * fit;
        }

        /// Remaps points authored over a quadrant onto <paramref name="sector"/> degrees, keeping
        /// each point's radius and scaling only its angle.
        ///
        /// For the outlines whose whole identity is that they exactly fill their sector, where the
        /// curve is the shape and its straightness is not. A half disc sitting on one radial edge
        /// is the case here: it spans precisely 0 to 90 degrees, and the only way to make it span
        /// 0 to 60 is to squash it.
        public static IEnumerable<Vector2> Compress(IEnumerable<Vector2> points, float sector)
        {
            foreach (Vector2 point in points)
            {
                float degrees = Mathf.Atan2(point.x, point.y) * Mathf.Rad2Deg;
                yield return Polar(degrees * sector / 90.0f, point.magnitude);
            }
        }

        /// How much room a narrower sector leaves at a given radius, relative to a quadrant.
        ///
        /// For the parts that are placed rather than swept - a dot, a leaf - which keep their own
        /// shape and only need to shrink enough to clear the neighbours they are now closer to.
        public static float Clearance(float sector)
        {
            return Mathf.Sin(sector * 0.5f * Mathf.Deg2Rad) / Mathf.Sin(45.0f * Mathf.Deg2Rad);
        }

        /// An arc of a circle centred anywhere, for the shapes that do not sit around the shape's
        /// own centre. Angles run from +X towards +Z, which is the ordinary convention and not the
        /// one <see cref="Polar"/> uses - that one is tied to how the renderer places quadrants.
        public static IEnumerable<Vector2> CircleArc(Vector2 centre, float radius,
            float fromDegrees, float toDegrees, int steps)
        {
            for (int i = 0; i <= steps; i++)
            {
                float radians = Mathf.Lerp(fromDegrees, toDegrees, (float)i / steps) * Mathf.Deg2Rad;
                yield return centre + new Vector2(Mathf.Cos(radians) * radius, Mathf.Sin(radians) * radius);
            }
        }

        /// A closed circle, with no repeated point where it meets itself.
        public static IEnumerable<Vector2> Circle(Vector2 centre, float radius, int steps)
        {
            for (int i = 0; i < steps; i++)
            {
                float radians = 360.0f * i / steps * Mathf.Deg2Rad;
                yield return centre + new Vector2(Mathf.Cos(radians) * radius, Mathf.Sin(radians) * radius);
            }
        }

        /// A closed lens - two arcs bowing out from the line between two points, meeting at a point
        /// on each end. Both tips are emitted exactly once, so the loop has no repeated point.
        public static IEnumerable<Vector2> Lens(Vector2 from, Vector2 to, float halfWidth, int steps)
        {
            Vector2 along = to - from;
            Vector2 across = new Vector2(along.y, -along.x).normalized;

            yield return from;

            for (int i = 1; i < steps; i++)
            {
                yield return Side(from, along, across, (float)i / steps, halfWidth);
            }

            yield return to;

            // Back down the other side, or the loop crosses itself.
            for (int i = steps - 1; i >= 1; i--)
            {
                yield return Side(from, along, across, (float)i / steps, -halfWidth);
            }
        }

        /// The lens boundary at <paramref name="t"/> along the axis. The exponent keeps the tips
        /// pointed rather than rounding them the way a plain sine would.
        private static Vector2 Side(Vector2 from, Vector2 along, Vector2 across, float t, float halfWidth)
        {
            float bulge = halfWidth * Mathf.Pow(Mathf.Sin(t * Mathf.PI), 0.75f);
            return from + along * t + across * bulge;
        }
    }
}
