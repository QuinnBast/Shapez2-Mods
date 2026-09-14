using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// One part of a shape, described as a flat outline that
    /// <see cref="ShapePartMeshBuilder"/> extrudes into the mesh the game draws.
    ///
    /// The game's own parts are authored meshes, so there is nothing to subclass or copy: a new
    /// part has to arrive as geometry. Every vanilla part is a flat extruded 2D polygon, so a
    /// polygon is all this needs to carry - which also means a new shape is a few lines here
    /// rather than a modelling session.
    ///
    /// **The outline is a function of the sector angle, not a fixed array,** because the same part
    /// has to exist at 90 degrees for the quad configurations and 60 for the hexagonal one. A
    /// profile is one part; a <see cref="ShapePartFactory"/> entry is one part at one width.
    public sealed class ShapePartProfile
    {
        /// The character the shape appears as in a shape code (`EuEuEuEu`). Case sensitive, one
        /// byte, and shared across every mod, every shape configuration and the base game - see
        /// <see cref="ExtraShapePartCatalog"/> for what is already taken.
        public readonly char Code;

        /// Only used for logging and the console report; the game never shows a part name.
        public readonly string Name;

        /// Which map generation bucket the part lands in. `NotSpawned` registers the part - so its
        /// code parses and it can be built - without putting it on the map.
        public readonly MetaShapesConfiguration.PartGenerationRarity Rarity;

        /// One line on what the shape is and what it survives, printed by `esp.report`.
        public readonly string Note;

        private readonly Func<float, IEnumerable<Vector2>> Build;

        public ShapePartProfile(char code, string name, Func<float, IEnumerable<Vector2>> outline,
            MetaShapesConfiguration.PartGenerationRarity rarity, string note = null)
        {
            Code = code;
            Name = name;
            Build = outline ?? throw new ArgumentNullException(nameof(outline), "Shape part: " + name);
            Rarity = rarity;
            Note = note;
        }

        /// The closed boundary at a given sector width, in the local space described by
        /// <see cref="ShapeGeometry"/>. Any simple polygon: convex, concave, or not reaching the
        /// shape's centre at all. Winding does not matter, because the builder normalises it.
        ///
        /// A part meant to join the one beside it starts on the +Z axis and reaches the sector's
        /// far edge. One meant to float free - a dot, a leaf - simply does not.
        public Vector2[] OutlineFor(float sector)
        {
            // Naming the part matters more than it looks. This used to run inside a static
            // initialiser, where the exception arrived as a TypeInitializationException whose own
            // message said nothing about which profile was being built; it now runs at mod load
            // and at session load, where the same is true of the surrounding log line.
            IEnumerable<Vector2> outline = Build(sector);
            if (outline == null)
            {
                throw new InvalidOperationException(
                    $"Shape part '{Name}' built a null outline at {sector} degrees. If it reads a " +
                    "static field, check that field is initialised before the list that builds the " +
                    "profiles - static field initialisers run in declaration order.");
            }

            Vector2[] points = outline as Vector2[] ?? outline.ToArray();
            if (points.Length < 3)
            {
                throw new InvalidOperationException(
                    $"Shape part '{Name}' needs at least three points, got {points.Length} at " +
                    $"{sector} degrees.");
            }

            return points;
        }

        /// A part that runs in to the shape's centre: a boundary walked from the +Z axis round to
        /// the sector's far edge, closed back through the origin.
        public static ShapePartProfile Solid(char code, string name,
            Func<float, IEnumerable<Vector2>> outer,
            MetaShapesConfiguration.PartGenerationRarity rarity, string note = null)
        {
            return new ShapePartProfile(code, name,
                sector => outer(sector).Concat(new[] { Vector2.zero }), rarity, note);
        }

        /// A part with a far and a near boundary walked over the same range - a crescent, a lens, a
        /// sector of an annulus. The near boundary is reversed to close the loop.
        public static ShapePartProfile Banded(char code, string name,
            Func<float, IEnumerable<Vector2>> far, Func<float, IEnumerable<Vector2>> near,
            MetaShapesConfiguration.PartGenerationRarity rarity, string note = null)
        {
            return new ShapePartProfile(code, name,
                sector => far(sector).Concat(near(sector).Reverse()), rarity, note);
        }
    }
}
