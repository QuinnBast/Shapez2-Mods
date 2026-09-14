using System.Collections.Generic;
using UnityEngine;
using static QuinnBast.Shapez2.ExtraShapeParts.ShapeGeometry;
using Rarity = MetaShapesConfiguration.PartGenerationRarity;

namespace QuinnBast.Shapez2.ExtraShapeParts
{
    /// The parts this mod adds, and the only file that needs editing to change them.
    ///
    /// **Codes are a flat namespace spanning every shape configuration, not just the one being
    /// played.** The game ships three: `DefaultShapesQuadConfiguration` and
    /// `OnboardingShapesQuadConfiguration` at `PartCount = 4`, and
    /// `DefaultShapesHexagonalConfiguration` at `PartCount = 6`. Between them they use
    ///
    ///     C R W S   quad - circle, square, windmill, star
    ///     X Y       quad - the two ConverterQuad parts, rarity NotSpawned
    ///     G H F     hexagonal - RectHex, CubeHex, FlowerHex
    ///     P c       pin and crystal, in all three
    ///
    /// which leaves `A J N Q U V` free after the ten below, plus the lower case letters other
    /// than `c`.
    ///
    /// That list is authored ScriptableObject data and is invisible to a decompiler - it was read
    /// out of `shapez 2_Data/resources.assets`. Four of these parts have already been renamed
    /// because of it: Cross lost `X` to the quad configuration, and Gear, Dome and Flower lost
    /// `G`, `H` and `F` to the hexagonal one, which had registered them into two configurations
    /// out of three and is the kind of half-failure nobody notices.
    ///
    /// So **do not pick a code by reasoning about it - run `esp.report`,** which prints every
    /// configuration's parts as they actually are, and is the only thing that survives a game
    /// update. `ShapePartInjector` also logs a warning when a code it wanted is already present,
    /// rather than skipping in silence.
    ///
    /// **Every outline is a function of its sector angle.** The same part is built once per
    /// distinct `PartCount`, because `ShapeItemRenderer` rotates a part but never squashes one
    /// angularly - see <see cref="ShapeGeometry"/>. Three shapes of that are worth knowing before
    /// writing an eleventh part:
    ///
    /// - A swept outline takes the sector as an angle and needs nothing else.
    /// - An outline authored in the quadrant's *cell* goes through <see cref="Cell"/>, or its
    ///   straight edges bend.
    /// - A part that is placed rather than swept keeps its own shape, moves to the new bisector,
    ///   and scales its clearance by <see cref="Clearance"/>.
    ///
    /// **Design rule: the difference has to live in the outer quarter of the radius.** Shapes stack,
    /// and `ShapeItemRenderer.GetShapeLayerScale` scales each layer by `(1 - 0.24)^layer` about the
    /// shape's centre while leaving the inner gap offset alone. So a second layer covers the inner
    /// ~76% of the first, and from above only the outer rim of the layer below survives. Anything
    /// distinguished by what happens near the centre - a hole, a chamfered inner corner - reads as a
    /// circle the moment something is stacked on it.
    public static class ExtraShapePartCatalog
    {
        private static IReadOnlyList<ShapePartProfile> Built;

        /// Built on first access rather than in a field initialiser, and that is not a style choice.
        ///
        /// Static field initialisers run in declaration order, so a field initialiser here would run
        /// *before* any `static readonly` outline declared below it, and would read a null. That is
        /// exactly what happened when the arrow pair shared one `ArrowHalf` array: this list called
        /// `ArrowRight()`, which passed a null outline, `ToArray` threw `ArgumentNullException`
        /// inside a type initialiser, and the resulting `TypeInitializationException` in the mod
        /// constructor took the game's whole mod loading step down with it.
        ///
        /// Deferring to first access means every field is initialised by the time any profile is
        /// built, whatever order they are declared in.
        public static IReadOnlyList<ShapePartProfile> All => Built ??= new[]
        {
            Gear(),
            Cross(),
            Bar(),
            Diamond(),
            Dot(),
            Dome(),
            Wedge(),
            Sawblade(),
            Flower(),
            Leaf(),
        };

        /// Two square teeth per part, eight round a quad shape and twelve round a hexagonal one.
        ///
        /// The tooth *count* is what stays fixed and the tooth *width* is what shrinks, because the
        /// arc bounds are fractions of the sector rather than angles. Keeping the width instead
        /// would give a hexagonal gear thirteen and a half teeth, and the half is a seam.
        private static ShapePartProfile Gear()
        {
            const float root = Radius * 0.74f;

            return ShapePartProfile.Solid('E', "Gear", sector =>
            {
                List<Vector2> points = new List<Vector2>();
                points.AddRange(Arc(0.0f, 0.125f * sector, root, 2));
                points.AddRange(Arc(0.125f * sector, 0.375f * sector, Radius, 3));
                points.AddRange(Arc(0.375f * sector, 0.625f * sector, root, 3));
                points.AddRange(Arc(0.625f * sector, 0.875f * sector, Radius, 3));
                points.AddRange(Arc(0.875f * sector, sector, root, 2));
                return points;
            }, Rarity.Rare, "Teeth sit at the rim, so they survive being stacked on.");
        }

        /// A cell square with a bite out of its outer corner. Four of them make a plus; six make a
        /// six-pointed asterisk.
        ///
        /// **The arm fraction has to shrink with the sector, and this is the one part where the
        /// correct transform was not enough.** Mapping the outline through <see cref="Cell"/> keeps
        /// the geometry faithful, but the *gestalt* does not survive: a square minus its outer
        /// corner is obviously not a square, while a 60 degree rhombus minus its outer point is
        /// still obviously a rhombus - and six rhombi are what vanilla's hexagonal `RectHex` (`G`)
        /// already is. Reported from a real hexagonal save, where Cross and `G` read as the same
        /// shape.
        ///
        /// Scaling the arm by `sector / 90` holds its *angular* width constant instead of its
        /// fraction of the cell, so the notch grows as the sector narrows and the spokes stay
        /// legible. At 90 degrees it is exactly `0.42f`, so the quad shape is unchanged to the bit.
        private static ShapePartProfile Cross()
        {
            return ShapePartProfile.Solid('K', "Cross", sector =>
            {
                float arm = 0.42f * sector / 90.0f;

                return new[]
                {
                    Cell(sector, 0.0f, 1.0f),
                    Cell(sector, arm, 1.0f),
                    Cell(sector, arm, arm),
                    Cell(sector, 1.0f, arm),
                    Cell(sector, 1.0f, 0.0f),
                };
            }, Rarity.Rare, "Arm tips reach the rim; the notch at the bisector stays visible stacked.");
        }

        /// One arm of a cross on its own, laid along the sector's leading edge rather than centred
        /// on it, so the parts chase each other round instead of meeting in the middle as a plus.
        private static ShapePartProfile Bar()
        {
            const float left = 0.16f;
            const float right = 0.50f;
            const float tip = 1.06f;

            return new ShapePartProfile('I', "Bar", sector => new[]
            {
                Cell(sector, left, 0.0f),
                Cell(sector, left, tip),
                Cell(sector, right, tip),
                Cell(sector, right, 0.0f),
            }, Rarity.VeryRare, "Makes a pinwheel. Watch it against the vanilla windmill.");
        }

        /// A whole diamond inscribed in the part's own cell - the square from the origin out to
        /// `(R, R)` that the vanilla square part fills - so its vertices sit at the midpoint of
        /// each side and four of them make a checkerboard.
        ///
        /// Like the square, it relies on `ShapeInnerGap` alone to hold it off its neighbours.
        private static ShapePartProfile Diamond()
        {
            const float half = 0.5f;

            return new ShapePartProfile('D', "Diamond", sector => new[]
            {
                Cell(sector, 1.0f, half),
                Cell(sector, half, 1.0f),
                Cell(sector, 0.0f, half),
                Cell(sector, half, 0.0f),
            }, Rarity.Rare, "Fills the cell; straight-edged partner to Dot.");
        }

        /// A whole circle sitting out in the sector rather than a sector of one, so the parts read
        /// as dots in a ring. The only part that touches neither the centre nor an edge.
        ///
        /// It stays a true circle at every part count - squashing it would make it a lens, which is
        /// Leaf - and only shrinks by <see cref="Clearance"/>, which is what a narrower sector
        /// actually takes away.
        private static ShapePartProfile Dot()
        {
            return new ShapePartProfile('O', "Dot",
                sector => Circle(Polar(sector * 0.5f, Radius * 0.72f),
                    Radius * 0.34f * Clearance(sector), 22),
                Rarity.VeryRare, "Detached from the centre, which no vanilla part is.");
        }

        /// A half disc with its flat edge lying along one radial edge, so the parts read as a
        /// pinwheel. The only chiral part here apart from vanilla's windmill.
        ///
        /// The one outline that has to be <see cref="Compress"/>ed rather than rebuilt: a half disc
        /// through the origin spans exactly 90 degrees by construction, so there is no parameter to
        /// narrow it with. Squashing is the shape, not an approximation of it.
        private static ShapePartProfile Dome()
        {
            const float span = Radius * 0.52f;
            Vector2 centre = new Vector2(0.0f, span);

            // From the top of the flat edge, round through the bisector, back to the origin - which
            // the arc already lands on, so this is not `Solid`: that would append a second copy of
            // it. The flat edge up the +Z axis is the loop's closing segment.
            return new ShapePartProfile('M', "Dome",
                sector => Compress(CircleArc(centre, span, 90.0f, -90.0f, 20), sector),
                Rarity.Rare, "The bulge is off-centre, so a stack cannot hide which way it faces.");
        }

        /// The same idea as <see cref="Dome"/> with a straight edge instead of an arc, and mirrored,
        /// so the two pinwheels turn opposite ways.
        ///
        /// Mirroring about the sector's bisector is what reverses the handedness. The renderer only
        /// ever *rotates* a part, and a rotation cannot flip a pinwheel; the flip has to be built
        /// into the outline. Dome lies along the +Z edge and bulges towards the far edge, so Wedge
        /// lies along the far edge and tapers back towards +Z - which in cell coordinates is the
        /// same two points with `u` and `v` swapped, and stays a mirror at any sector.
        ///
        /// Long and thin on purpose: a chunky triangle in each sector is what the vanilla windmill
        /// already is.
        private static ShapePartProfile Wedge()
        {
            return ShapePartProfile.Solid('T', "Wedge", sector => new[]
            {
                Cell(sector, 0.0f, 0.46f),
                Cell(sector, 1.10f, 0.0f),
            }, Rarity.Rare, "Dome's mirror image, so it turns the other way. Watch it against the windmill.");
        }

        /// Three raked teeth with a near-vertical drop behind each, so the whole shape spins one
        /// way. A star has one point per part; this has three, which keeps them apart.
        private static ShapePartProfile Sawblade()
        {
            const float root = Radius * 0.70f;

            return ShapePartProfile.Solid('Z', "Sawblade", sector =>
            {
                float scale = sector / 90.0f;
                return new[]
                {
                    Polar(0.0f * scale, root),
                    Polar(28.0f * scale, Radius),
                    Polar(30.0f * scale, root),
                    Polar(58.0f * scale, Radius),
                    Polar(60.0f * scale, root),
                    Polar(88.0f * scale, Radius),
                    Polar(90.0f * scale, root),
                };
            }, Rarity.VeryRare, "Teeth at the rim, and three of them, so it does not read as a star.");
        }

        /// A rounded lobe that bulges at the bisector and pinches on the edges, giving the whole
        /// shape a petal per part with a visible waist between them - and, below 90 degrees, two
        /// notches cut into each petal that leave it with three teeth.
        ///
        /// **The teeth are there to keep it out of vanilla's way, not for their own sake.**
        /// `FlowerHex` (`F`) is the hexagonal configuration's own part and is exactly this idea:
        /// one round lobe per sector. At six parts the two were all but the same shape, which was
        /// reported from a real save and is visible in `Screenshots/flower-tune.png` against the
        /// dumped vanilla mesh. A lobe is a lobe at any width, so no amount of re-parametrising the
        /// sweep separates them - waist, width and radius each only produce a slightly different
        /// round petal. The petal needed a feature vanilla's does not have.
        ///
        /// They scale in from nothing at 90 degrees, so the quad shape is untouched - the collision
        /// only exists in configurations where vanilla spends its own petals, and the quad set was
        /// already settled. The notches sit near the tip, which is the outer rim, so they survive
        /// having a layer stacked on them.
        private static ShapePartProfile Flower()
        {
            return ShapePartProfile.Solid('B', "Flower", sector =>
            {
                float tooth = 0.16f * Mathf.Clamp01((90.0f - sector) / 30.0f);

                // Narrow Gaussians rather than wedges: straight-sided notches read as damage, and
                // rounded ones read as a leaf's teeth. Placed either side of the bisector, so the
                // middle tooth keeps the petal's full reach and the two outer ones step down.
                float Radius01(float fraction)
                {
                    float lobe = 0.58f + 0.42f * Mathf.Sin(Mathf.PI * fraction);
                    float notch = tooth * (Notch(fraction, 0.34f) + Notch(fraction, 0.66f));
                    return lobe - notch;
                }

                // Each notch is under a tenth of the sweep wide and there are two of them, so this
                // needs sampling fine enough to resolve both as teeth rather than as dimples.
                // Without them the coarser sweep is kept, which is what keeps the quad mesh
                // identical to the one that shipped rather than merely close to it.
                int steps = tooth > 0.0f ? 64 : 28;

                return Radial(sector, fraction => Radius * Radius01(fraction), steps);
            }, Rarity.VeryRare, "The waist is on the edges at the rim; below 90 degrees the petal has three teeth.");
        }

        /// One notch in a radial sweep: a Gaussian centred at <paramref name="centre"/>, narrow
        /// enough that two of them leave three teeth rather than one wavy edge.
        private static float Notch(float fraction, float centre)
        {
            return Mathf.Exp(-Mathf.Pow((fraction - centre) / 0.07f, 2.0f));
        }

        /// A pointed lens lying along the bisector, from the shape's centre out past the cell's
        /// far corner. A whole shape of them has no straight edge anywhere.
        private static ShapePartProfile Leaf()
        {
            return new ShapePartProfile('L', "Leaf",
                sector => Lens(Vector2.zero, Polar(sector * 0.5f, Radius * 1.26f),
                    Radius * 0.30f * Clearance(sector), 12),
                Rarity.VeryRare, "Tip on the bisector past the cell's corner - the last thing a stack covers.");
        }
    }
}
