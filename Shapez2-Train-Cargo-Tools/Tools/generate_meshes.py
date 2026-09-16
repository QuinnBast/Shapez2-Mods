#!/usr/bin/env python3
"""Generates the cargo machine meshes into TrainCargoTools/Resources/*.obj.

Run from anywhere:  python Tools/generate_meshes.py

Why a generator rather than checked-in art: these are hard-surface shapes made of boxes,
octagonal prisms and cylinders, and the proportions are the part that needs iterating. A
script keeps "make the tank a bit taller" a one-line edit instead of a modelling session,
and keeps the six machines consistent with each other by construction - they share the
same leg, plinth and pipe helpers, so the family reads as a family.

The output is deliberately plain .obj and deliberately dumb. Open one in Blender, change
whatever you like, export over the top, and nothing in the mod needs to know: the loader
goes by filename. This script is the starting point, not a dependency.


COORDINATE FRAME
----------------
Author in the frame the *renderer* sees, which is Unity's: Y up, and for a single-chunk
island the chunk is 20x20 centred on the origin with the platform deck top at Y = 0.

That falls out of three things in the decompiled game:

  GlobalChunkCoordinate.ToCenter_W(v)  ->  WorldCoordinate(x*20 + 9.5, y*20 + 9.5, z*20 + v)
  WorldCoordinate -> Vector3            ->  (x, z, -y)          [so game Z is Unity Y, up]
  DiagonalCutter.fbx                    ->  X[-0.5,0.5] Y[0,0.26] Z[-0.479,0.375]

The last one is the shipped sample building: a 1x1-tile building is one unit across and
sits on Y = 0. A chunk is 20 tiles, hence 20 units, hence +-10 about the centre.

  X  is the flow axis.  The islands are built West in, East out, and chunk East is +X.
  Z  is the cross axis.
  Y  is up.  Y = 0 is the deck; build upwards from there.

For reference, the vanilla space path track sits at Y = -2.07314 (SpacePathPlatformDrawer's
own constant), which is why the belts reuse that drawer instead of getting a mesh here.


THE X FLIP
----------
ShapezShifter's AssimpToUnityMeshConverter writes every vertex as float3(-x, y, z) and
reverses each triangle's winding. That is a consistent mirror, so normals stay correct -
but it does mean the model arrives mirrored along the flow axis, which for a packager
(hopper at one end, chute at the other) is exactly backwards.

So write_obj negates X on the way out. Everything in this file is in final in-game space;
the flip is applied once, at the last moment, and cancels the importer's.


COLOUR IS AN ATLAS LOOKUP, SO THE UVS HERE ARE MARKERS
------------------------------------------------------
Colour is not per-material, it is a texture atlas lookup through UV0. DiagonalCutter.fbx
carries a `base_color_texture` and its UV0 sits in a tight sub-rectangle (U[0.095,0.476],
V[0.587,0.919], 319 distinct pairs) - the mesh picks its colours by pointing at spots in
one shared image.

That image is authored Unity asset data and is not in the decompiled assemblies. The first
version of this script guessed (0.25, 0.75) for every vertex, and whatever sits there is
bright red, which is what the machines came out as.

The fix is not a better guess. Every vertex here carries a per-role *sentinel* UV, and at
load time CargoPalette rewrites each one to a coordinate it sampled off a vanilla mesh the
game draws with the very same IslandMaterial - the train cargo package and the space belt
structure. See PALETTE below and CargoPalette.cs.

To move a role by hand, `cargotools.uv <role> <u> <v>` on the debug console changes it live,
and `cargotools.dumpatlas` writes the atlas out so you can read coordinates off it.
"""

import math
import os
import sys

# ---------------------------------------------------------------------------
# Palette
# ---------------------------------------------------------------------------

# Sentinel UVs, keyed by role - NOT real atlas coordinates.
#
# Colour is an atlas lookup through UV0, and the atlas is authored Unity data that cannot be
# read out of the decompiled assemblies. The first version of this file guessed (0.25, 0.75)
# for everything, and whatever lives there turned out to be bright red.
#
# So these do not try to be colours at all. Each role gets a marker coordinate parked in the
# bottom-left corner of UV space at 1/100 intervals, and CargoPalette rewrites every vertex
# carrying a marker to a coordinate it sampled off a *vanilla* mesh at load time - the cargo
# package and the space belt structure, both of which the game draws with the same
# IslandMaterial these meshes use. That turns "what colour is this" from a guess into a
# measurement.
#
# Order matters: CargoPalette.Sentinel(n) is ((n + 1) / 100, 0.01) and CargoPalette.Roles
# lists the roles in this same order. Keep the two in step.
#
# The values are deliberately somewhere nothing real would land, so a mesh that somehow
# escapes remapping is visibly wrong in one corner of the atlas rather than subtly off.
SENTINEL_V = 0.01
# Keep in step with CargoPalette.Wanted, which names the colour each role asks the game's
# material palette for. Order is load-bearing on both sides - Sentinel(n) is ((n+1)/100, 0.01)
# - so append, never insert.
#
# Roles are named for the *job* a colour does, not for the colour itself. Nothing here can
# know what the atlas holds; the C# side asks it for the nearest thing to a target and takes
# what it gets. Naming them by job means reassigning a part stays a decision about the machine
# rather than about which grey was which.
ROLES = [
    "hull", "accent", "metal", "fluid", "cargo",
    "hullDark", "deck", "frame", "rail", "trim",
    "warn", "glass", "collar", "shadow", "pale",
]

# `rubber`, `light` and `scuff` were here and are gone: nothing in this file ever painted a
# triangle with one. A role that colours nothing is worse than no role - it is a knob on the
# console that appears to do nothing, which is exactly how it was found. Add one back at the
# END of the list when there is geometry for it; inserting shifts every later role's sentinel.

# One step lighter, for the chamfer a `block` or `tube` puts on its top edge. A shoulder that
# catches the light is most of what makes a shipped building read as machined rather than as
# a box, and it costs nothing here: the geometry is already two lofts, they simply shared a
# role until now.
LIGHTER = {
    "frame": "hullDark",
    "hullDark": "hull",
    "hull": "pale",
    "metal": "rail",
    "deck": "hull",
    "cargo": "trim",
    "fluid": "glass",
    "shadow": "frame",
}


def lighter(uv):
    """The role a chamfer on `uv` should take."""
    return LIGHTER.get(uv, uv)
PALETTE = {role: ((i + 1) / 100.0, SENTINEL_V) for i, role in enumerate(ROLES)}

ATLAS_UV = PALETTE["hull"]

# ---------------------------------------------------------------------------
# Mesh building
# ---------------------------------------------------------------------------


class Mesh:
    """A triangle soup with per-face normals.

    No vertex sharing and no smoothing: these are hard-surface shapes and every face wants
    its own flat normal. Assimp's TargetRealTimeMaximumQuality preset would generate smooth
    normals if the file had none, which on a box looks like a bad balloon, so the normals
    are always written explicitly.
    """

    def __init__(self):
        self.tris = []  # (v0, v1, v2, uv_key)

    def tri(self, a, b, c, uv="hull"):
        self.tris.append((a, b, c, uv))

    def quad(self, a, b, c, d, uv="hull"):
        """A planar quad, wound so the normal follows the right hand rule around a-b-c-d."""
        self.tri(a, b, c, uv)
        self.tri(a, c, d, uv)

    def extend(self, other):
        self.tris.extend(other.tris)


def _normal(a, b, c):
    ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
    vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
    nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
    length = math.sqrt(nx * nx + ny * ny + nz * nz)
    if length < 1e-9:
        return (0.0, 1.0, 0.0)
    return (nx / length, ny / length, nz / length)


# ---------------------------------------------------------------------------
# Profiles - 2D outlines in the XZ plane, wound counter-clockwise seen from above
# ---------------------------------------------------------------------------


def rect(cx, cz, sx, sz):
    hx, hz = sx * 0.5, sz * 0.5
    return [(cx - hx, cz - hz), (cx + hx, cz - hz), (cx + hx, cz + hz), (cx - hx, cz + hz)]


def octagon(cx, cz, sx, sz, bevel):
    """A rectangle with its four vertical edges chamfered.

    The chamfer is what stops every block reading as a raw cube - shapez's own hard-surface
    art bevels nearly everything, and at the zoom the game is usually played at the bevel
    is most of what you see of a shape's silhouette.
    """
    hx, hz = sx * 0.5, sz * 0.5
    b = min(bevel, hx * 0.9, hz * 0.9)
    return [
        (cx - hx + b, cz - hz), (cx + hx - b, cz - hz),
        (cx + hx, cz - hz + b), (cx + hx, cz + hz - b),
        (cx + hx - b, cz + hz), (cx - hx + b, cz + hz),
        (cx - hx, cz + hz - b), (cx - hx, cz - hz + b),
    ]


def circle(cx, cz, r, segments=14):
    return [
        (cx + r * math.cos(2 * math.pi * i / segments),
         cz + r * math.sin(2 * math.pi * i / segments))
        for i in range(segments)
    ]


def inset(profile, amount):
    """Shrinks a profile towards its centroid. Good enough for a chamfer on convex shapes."""
    cx = sum(p[0] for p in profile) / len(profile)
    cz = sum(p[1] for p in profile) / len(profile)
    out = []
    for x, z in profile:
        dx, dz = x - cx, z - cz
        d = math.sqrt(dx * dx + dz * dz)
        if d < 1e-9:
            out.append((x, z))
            continue
        scale = max(0.0, (d - amount)) / d
        out.append((cx + dx * scale, cz + dz * scale))
    return out


# ---------------------------------------------------------------------------
# Solids
# ---------------------------------------------------------------------------


def loft(mesh, lower, upper, y0, y1, uv="hull", cap_bottom=True, cap_top=True):
    """Joins two profiles with the same vertex count into a closed solid."""
    n = len(lower)
    for i in range(n):
        j = (i + 1) % n
        a = (lower[i][0], y0, lower[i][1])
        b = (lower[j][0], y0, lower[j][1])
        c = (upper[j][0], y1, upper[j][1])
        d = (upper[i][0], y1, upper[i][1])
        mesh.quad(d, c, b, a, uv)
    if cap_bottom:
        for i in range(1, n - 1):
            mesh.tri((lower[0][0], y0, lower[0][1]),
                     (lower[i][0], y0, lower[i][1]),
                     (lower[i + 1][0], y0, lower[i + 1][1]), uv)
    if cap_top:
        for i in range(1, n - 1):
            mesh.tri((upper[0][0], y1, upper[0][1]),
                     (upper[i + 1][0], y1, upper[i + 1][1]),
                     (upper[i][0], y1, upper[i][1]), uv)


def block(mesh, cx, cz, sx, sz, y0, y1, uv="hull", bevel=0.5, cap=0.4, cap_uv=None):
    """The workhorse: a chamfered box.

    Straight sides up to `cap` below the top, then a short inward taper, so the top edge
    reads as a chamfer rather than a knife edge. Pass cap=0 for a plain prism.

    The chamfer takes `cap_uv`, defaulting to one step lighter than the body - see LIGHTER.
    Pass the body's own role to switch that off for a part that should read as one solid.
    """
    profile = octagon(cx, cz, sx, sz, bevel)
    if cap <= 0:
        loft(mesh, profile, profile, y0, y1, uv)
        return
    shoulder = max(y0, y1 - cap)
    loft(mesh, profile, profile, y0, shoulder, uv, cap_top=False)
    loft(mesh, profile, inset(profile, cap), shoulder, y1,
         cap_uv or lighter(uv), cap_bottom=False)


def tube(mesh, cx, cz, r, y0, y1, uv="fluid", segments=14, cap=0.0, cap_uv=None):
    """A vertical cylinder, chamfered like a block when `cap` is given."""
    profile = circle(cx, cz, r, segments)
    if cap <= 0:
        loft(mesh, profile, profile, y0, y1, uv)
        return
    shoulder = max(y0, y1 - cap)
    loft(mesh, profile, profile, y0, shoulder, uv, cap_top=False)
    loft(mesh, profile, inset(profile, cap), shoulder, y1,
         cap_uv or lighter(uv), cap_bottom=False)


def _axis_tube(mesh, axis, along0, along1, cross, y, r, uv, segments):
    """A cylinder lying down, along X or along Z, centred at height `y`.

    Built into a scratch mesh and appended reversed. Winding a lying-down cylinder by
    hand means keeping two cap fans and a wall loop consistent across two different axis
    mappings, which is four chances to get a sign wrong; building it whichever way reads
    clearly and flipping once at the end is one chance. The signed-volume and
    unmatched-edge checks in main() are what keep this honest.
    """
    scratch = Mesh()
    ring = [(r * math.cos(2 * math.pi * i / segments),
             r * math.sin(2 * math.pi * i / segments)) for i in range(segments)]

    def point(t, c, s):
        # The cross component is negated on the X axis on purpose: mapping the ring
        # straight through gives the two axes opposite handedness, which winds pipe_x
        # inside-out while pipe_z comes out right. Caught by the signed-volume check.
        if axis == "x":
            return (t, y + s, cross - c)
        return (cross + c, y + s, t)

    for i in range(segments):
        j = (i + 1) % segments
        ci, si = ring[i]
        cj, sj = ring[j]
        scratch.quad(point(along1, ci, si), point(along1, cj, sj),
                  point(along0, cj, sj), point(along0, ci, si), uv)
    for i in range(1, segments - 1):
        ci, si = ring[i]
        cj, sj = ring[i + 1]
        c0, s0 = ring[0]
        scratch.tri(point(along0, c0, s0), point(along0, ci, si), point(along0, cj, sj), uv)
        scratch.tri(point(along1, c0, s0), point(along1, cj, sj), point(along1, ci, si), uv)

    for a, b, c, key in scratch.tris:
        mesh.tri(c, b, a, key)


def pipe_x(mesh, x0, x1, cz, y, r, uv="fluid", segments=14):
    _axis_tube(mesh, "x", x0, x1, cz, y, r, uv, segments)


def pipe_z(mesh, z0, z1, cx, y, r, uv="fluid", segments=14):
    _axis_tube(mesh, "z", z0, z1, cx, y, r, uv, segments)


def hopper(mesh, cx, cz, top_sx, top_sz, bottom_sx, bottom_sz, y0, y1, uv="hullDark",
           wall=0.7):
    """A funnel with a mouth you can see into: wide at the top, narrow at the bottom.

    **Double-skinned, not an open shell.** The first version was one cone of triangles with no
    lid, which is what a funnel looks like on paper and is invisible in this game: the camera
    looks down into the mouth, the inside of a single-skinned cone is backfaces, backfaces are
    culled, and you see straight through the machine to the platform behind it. It read as the
    hopper not rendering at all, with a bit of it clipping at the edges where the outer skin
    was still turned towards the camera.

    So the funnel is a solid with a hollow in it: an outer skin, a rim across the top, and an
    inner skin going back down that faces *up and inwards*, which is what makes the cavity
    something you can actually look into. `wall` is how thick the lip reads from above.
    """
    outer_bottom = octagon(cx, cz, bottom_sx, bottom_sz, 0.4)
    outer_top = octagon(cx, cz, top_sx, top_sz, 0.8)
    inner_top = inset(outer_top, wall)
    inner_bottom = inset(outer_bottom, wall * 0.5)

    # The cavity floor sits a wall's thickness above the outfeed, so the funnel has a bottom
    # rather than a hole through to the plinth.
    floor = y0 + wall

    # Outer skin, closed underneath, open at the rim.
    loft(mesh, outer_bottom, outer_top, y0, y1, uv, cap_bottom=True, cap_top=False)

    # The rim. Wound the way `loft`'s top cap is - against the profile's own direction - so it
    # faces up; the boundary-edge check catches this immediately if it is the other way round.
    n = len(outer_top)
    for i in range(n):
        j = (i + 1) % n
        mesh.quad((outer_top[j][0], y1, outer_top[j][1]),
                  (outer_top[i][0], y1, outer_top[i][1]),
                  (inner_top[i][0], y1, inner_top[i][1]),
                  (inner_top[j][0], y1, inner_top[j][1]), uv)

    # The cavity: the same funnel again, turned inside out so its faces point back at the
    # camera. Built into a scratch mesh and flipped, which is how _axis_tube does it too -
    # reversing a loft in place would mean reimplementing it.
    bowl = Mesh()
    loft(bowl, inner_bottom, inner_top, floor, y1, uv, cap_bottom=True, cap_top=False)
    bowl.tris = [(c, b, a, u) for a, b, c, u in bowl.tris]
    mesh.extend(bowl)


def legs(mesh, xs, zs, y0, y1, size=1.3, uv="frame"):
    for x in xs:
        for z in zs:
            block(mesh, x, z, size, size, y0, y1, uv, bevel=0.25, cap=0.2)


# ---------------------------------------------------------------------------
# Sweeping a profile along a path
# ---------------------------------------------------------------------------
#
# Everything above builds solids from horizontal slices, which is fine for a machine that sits
# still and useless for track that has to bend. These sweep a rectangular cross-section along a
# path of (x, z, heading) frames, so a corner is real curved geometry rather than a straight mesh
# hoping nobody looks. That was the failure of both earlier belt markers: a mesh authored straight
# cannot follow a bend, and on every corner it ended up poking out of the outside of the curve.

TRACK_Y = -2.07314


def straight_path(x0, x1, steps=2):
    """A run along +X at z = 0."""
    return [(x0 + (x1 - x0) * i / (steps - 1), 0.0, 0.0) for i in range(steps)]


def corner_path(exit_sign, steps=9):
    """A quarter turn from the West edge to the North or South edge.

    Enters at (-10, 0) heading East and leaves at (0, +-10) heading across. The arc is centred
    on the corner so its radius is the half-chunk, which is what the vanilla track does.

    `exit_sign` is +1 for North and -1 for South in mesh space. The first version had this the
    other way round, reasoning that a chunk's North is +y in game space and Unity's Z is -y - the
    corners came out swapped in game, so the reasoning was wrong somewhere and the observation
    wins.
    """
    frames = []
    for i in range(steps):
        t = i / (steps - 1)
        angle = t * math.pi * 0.5
        x = -10.0 + 10.0 * math.sin(angle)
        z = exit_sign * (10.0 - 10.0 * math.cos(angle))
        heading = exit_sign * angle
        frames.append((x, z, heading))
    return frames


def _ring(frame, a0, a1, u0, u1):
    """The four corners of a rectangular cross-section at one frame, in mesh space.

    `u` is measured from zero, not from the track height. The drawer positions each tier itself,
    because how far apart the layers sit is authored theme data (`SpacePathItemRenderingConfig`)
    and cannot be baked into a file generated offline.
    """
    x, z, heading = frame[0], frame[1], frame[2]

    # A fourth entry lifts the whole cross-section. Flat track leaves it off; a lift ramp carries
    # one per frame so the deck climbs as it goes, which is the only difference between a ramp
    # and the straight piece it is otherwise built from.
    y = frame[3] if len(frame) > 3 else 0.0
    u0, u1 = u0 + y, u1 + y

    # The normal of the heading in the XZ plane, not the heading itself. Getting this wrong
    # sweeps the cross-section *along* the path instead of across it, which produces a flat
    # ribbon with no width at all - the generator's Z extent read 0.0 to 0.0.
    rx, rz = -math.sin(heading), math.cos(heading)
    return [
        (x + rx * a0, u0, z + rz * a0),
        (x + rx * a1, u0, z + rz * a1),
        (x + rx * a1, u1, z + rz * a1),
        (x + rx * a0, u1, z + rz * a0),
    ]


def sweep_box(mesh, path, a0, a1, u0, u1, uv="hull", caps=True):
    """A rectangular beam following a path. `a` is across the path, `u` is up from TRACK_Y.

    **`a0` must be the lower bound**, and it is swapped here rather than trusted, because every
    mirrored call in this file gets it the other way round. `_ring` walks its four corners in
    the order a0-low, a1-low, a1-high, a0-high; with a0 above a1 that circuit runs the opposite
    way and every quad in the sweep comes out wound inward.

    The symptom is a part that renders see-through in game and perfectly well in any viewer
    that does not backface-cull. It went unnoticed because the generator's winding check was
    whole-mesh: `for side in (-1, 1)` produced one good wall and one inverted one, and the
    sum of the two is still positive. The check is per-component now.
    """
    if a0 > a1:
        a0, a1 = a1, a0

    rings = [_ring(f, a0, a1, u0, u1) for f in path]

    scratch = Mesh()

    for i in range(len(rings) - 1):
        lo, hi = rings[i], rings[i + 1]
        for j in range(4):
            k = (j + 1) % 4
            scratch.quad(lo[j], hi[j], hi[k], lo[k], uv)

    if caps:
        # Wound against the side walls, not with them. The first pass had these the same way
        # round and every box reported eight unmatched edges - the generator's boundary-edge
        # check named it immediately, which is exactly what it is for.
        first, last = rings[0], rings[-1]
        scratch.tri(first[0], first[1], first[2], uv)
        scratch.tri(first[0], first[2], first[3], uv)
        scratch.tri(last[0], last[2], last[1], uv)
        scratch.tri(last[0], last[3], last[2], uv)

        # Then ask the finished solid which way it is facing, rather than reasoning about it.
        #
        # Swapping a0 and a1 above fixes the mirrored calls, and is not enough on its own: a
        # ramp's sleepers climb 2.5 units across a box 0.26 tall, so the cross-section is
        # sheared far past its own height and the circuit reverses again. Chasing that case by
        # case is how the first two were missed. A closed surface's signed volume is
        # origin-independent, so this is exact rather than a heuristic - and it is the same
        # flip cargo_track and cargo_junction already apply to themselves.
        if signed_volume(scratch.tris) < 0:
            scratch.tris = [(c, b, a, u) for a, b, c, u in scratch.tris]

    mesh.extend(scratch)


def cargo_track(path):
    """The cargo belt's track: a deck in a channel, with sleepers across it.

    One deck carrying all three lane layers abreast, which is how a vanilla space belt does it -
    `SpacePathSimulationRenderer.DrawItems` fans its layers sideways by `TracksSpacing` rather
    than stacking them. This briefly was three stacked tiers instead, and that was the mistake:
    freight scaled to fill a slot needs about three units of headroom, so three tiers made a
    six-unit tower and the top deck hid the two below it from the game's camera angle. A belt
    you cannot read at a glance is worse than one whose layers are hard to tell apart.

    The deck therefore has to be wide enough for three containers side by side. It sweeps to
    +-3.7; `CargoLanes.LayerAcross_W` puts the outer two files at +-2.4 and caps a container at
    2.2 across, so the outermost edge lands at 3.5 and the deck edge stays clear.


    A cargo belt used to draw with vanilla's belt track and be told apart by a marker or a tint.
    Four attempts at that failed - posts read as damage, rails read as debris on corners, an
    accent tint reaches only the trim, and a wash under the deck is too quiet. Every one of them
    was decoration on something that already looked like an ordinary belt.

    So the track itself is different now. Walls either side and ribs across make it read as heavy
    channel section rather than a conveyor, at any zoom and from any angle, with nothing bolted
    on to be mistaken for damage.
    """
    m = Mesh()
    channel(m, path)

    # A sweep's handedness follows the direction it curves, so the two corner pieces come out
    # mirror images and one of them is inside out. Rather than special-casing the ring order per
    # turn - which is the sort of sign that gets fixed in one place and forgotten in another -
    # the volume is measured and the whole thing flipped if it came out negative. The checks in
    # main() then confirm it either way.
    if signed_volume(m.tris) < 0:
        m.tris = [(c, b, a, uv) for a, b, c, uv in m.tris]

    return m


def channel(m, path):
    """Adds one run of channel section - deck, walls, rails, sleepers - along `path`.

    Separate from cargo_track so a junction can lay several arms into one mesh. The arms
    overlap at the centre, which is fine: each is a closed volume on its own, so the union is
    still closed and still outward-wound, and the corner pieces have overlapped like this from
    the start.
    """

    # Deck. Its top is at u = 0, so the drawer can place a tier by the same offset the cargo
    # renderer uses and the containers sit *on* it rather than in it.
    sweep_box(m, path, -3.7, 3.7, -0.42, 0.0, "deck")

    # Side walls. Tall enough to frame the freight and break the outline, low enough to clear
    # the tier above: three of these stack within one belt, CargoLanes.LayerSpacing_W apart.
    for side in (-1, 1):
        sweep_box(m, path, side * 3.7, side * 4.45, -0.42, 1.15, "hullDark")

    # A rail capping each wall, in the accent role so the palette gives it a second colour.
    for side in (-1, 1):
        sweep_box(m, path, side * 3.55, side * 4.6, 1.1, 1.42, "accent")

    # Sleepers across the deck, spaced along the run.
    for i in range(1, len(path) - 1, max(1, (len(path) - 2) // 3)):
        rib = [path[i], path[min(i + 1, len(path) - 1)]]
        if rib[0] == rib[1]:
            continue
        sweep_box(m, rib, -3.4, 3.4, -0.44, -0.18, "frame")


def cargo_track_straight():
    return cargo_track(straight_path(-10.0, 10.0, 5))


ARMS = {
    # direction -> (edge x, edge z, heading). Headings match corner_path's: North is +pi/2.
    "E": (10.0, 0.0, 0.0),
    "N": (0.0, 10.0, math.pi * 0.5),
    "S": (0.0, -10.0, -math.pi * 0.5),
    "W": (-10.0, 0.0, 0.0),
}


# A chunk is twenty units tall, so one layer of climb is twenty units of rise.
LAYER_RISE = 20.0


def _rising(frames, rise):
    """Adds an evenly climbing height to frames that already carry their headings.

    Evenly by *index*: every path here is sampled uniformly, so index and arc length agree,
    and taking the headings as given rather than re-deriving them from neighbours is what
    keeps a ramp's end frames square to the chunk edge. Deriving them cost half a unit of
    overshoot the first time, the same way it did on the junction arcs.
    """
    last = len(frames) - 1
    return [(x, z, heading, rise * (i / last if last else 0.0))
            for i, (x, z, heading) in enumerate(frames)]


def lift_path(name, rise):
    """The path a lift ramp follows: in at the West edge, out at `name`, climbing by `rise`.

    The four exits are the game's own, read off it with `cargotools.dumplift`:

        Forward -> East      Right -> South      Backward -> West      Left -> North

    Forward and the two sides reuse the very paths the flat pieces are built from, so a ramp
    meets the corner or junction beside it without a kink.

    Backward is the odd one: both its ends are on the West edge, a layer apart, so it cannot be
    a through-run. It is a hairpin - out along one side of the centre line, around a half circle,
    and back along the other. The two legs sit 7 apart and are 9.2 wide, so they overlap a little
    in the middle, which is the same thing junction arms do where they meet.
    """
    if name == "E":
        return _rising(straight_path(-10.0, 10.0, 9), rise)

    if name in ("N", "S"):
        return _rising(corner_path(1 if name == "N" else -1, steps=9), rise)

    # The hairpin, built analytically so the end frames are square to the edge.
    # apex = leg + radius, and the channel is 4.6 either side, so leg + radius must stay under
    # 5.4 for the turn to fit inside the chunk. 3 was the first try and pushed 0.9 past the edge.
    radius, leg = 3.5, 1.8
    frames = [(-10.0 + (leg + 10.0) * i / 4.0, -radius, 0.0) for i in range(5)]

    for i in range(1, 8):
        a = -math.pi * 0.5 + math.pi * i / 7.0
        frames.append((
            leg + radius * math.cos(a),
            radius * math.sin(a),
            math.atan2(math.cos(a), -math.sin(a)),
        ))

    frames += [(leg - (leg + 10.0) * i / 4.0, radius, math.pi) for i in range(1, 5)]
    return _rising(frames, rise)


def cargo_lift(name, layers):
    """One lift ramp, climbing `layers` chunk layers - signed, so -1 descends."""
    m = Mesh()
    channel(m, lift_path(name, layers * LAYER_RISE))

    if signed_volume(m.tris) < 0:
        m.tris = [(c, b, a, uv) for a, b, c, uv in m.tris]

    return m


def splitter_arm(name):
    """An arm leading away from the West edge: straight on to East, or curving to a side.

    The side arms are `corner_path` itself - radius 10 about the chunk corner, West edge midpoint
    to side edge midpoint - so a branch leaving a junction follows exactly the line it would round
    a corner, and the two meet without a kink. Reusing it rather than rebuilding the arc also
    means the frames keep the heading convention the corner pieces are already checked against;
    a hand-rolled version differed at the end frames and swept 0.6 units past the chunk edge.

    Straight arms were the first version. They read as a crossroads dropped onto the track rather
    than a belt that divides, which is not how the game draws its own junctions.
    """
    if name == "E":
        return straight_path(-10.0, 10.0, 3)

    return corner_path(1 if name == "N" else -1)


def merger_arm(name):
    """An arm leading into the East edge: a splitter arm mirrored across the chunk's centre line.

    A merger cannot share a splitter's mesh once the arms curve. A left-forward splitter bends
    West-to-North about the North-West corner; a left-forward merger bends North-to-East about
    the North-East one. Mirroring in x maps one to the other, and maps a heading to pi - heading.
    """
    if name == "E":
        return straight_path(-10.0, 10.0, 3)

    return [(-x, z, math.pi - heading) for x, z, heading in corner_path(1 if name == "N" else -1)]


def cargo_junction(arms):
    """A junction, built from one arm per direction it reaches.

    Flow direction is not geometry. A left-forward *splitter* (in from West, out North and East)
    and a left-forward *merger* (in from West and North, out East) occupy the same three arms, so
    they share a mesh; only the Y pair differs, because a Y splitter reaches West/North/South and
    a Y merger reaches North/South/East.

    The side arms curve, sharing a radius and centre with the corner pieces - see junction_arm -
    so cargo leaving a junction sideways follows the same line it would round a corner.

    Every arm starts at the chunk centre, so they overlap there in roughly one channel width.
    That reads as the junction box and costs nothing: each arm is its own closed volume, which is
    what the checks in main() confirm.
    """
    m = Mesh()

    for name, path in arms:
        channel(m, path)

    if signed_volume(m.tris) < 0:
        m.tris = [(c, b, a, uv) for a, b, c, uv in m.tris]

    return m


def splitter(names):
    return cargo_junction([(n, splitter_arm(n)) for n in names])


def merger(names):
    return cargo_junction([(n, merger_arm(n)) for n in names])


def cargo_track_left():
    return cargo_track(corner_path(1))


def cargo_track_right():
    return cargo_track(corner_path(-1))


# ---------------------------------------------------------------------------
# Detail
# ---------------------------------------------------------------------------
#
# Everything below is greeble: it carries no meaning and exists to stop a machine reading as a
# featureless block at the distance the game is actually played at.
#
# The first pass of this was invisible in game, and the reason is worth stating because it is easy
# to make twice: detail that only changes *surface* does not read at the distance a factory is
# looked at. Ribs a third of a unit proud of a hull, a collar a hair wider than its pipe - all of
# it disappears. What reads is **silhouette**: things that break the outline against the sky, at a
# size comparable to the machine itself rather than to its panels. So everything here is large.
#
# Still placed on the hull rather than past the footprint, which is the lesson from the belt
# markers: anything standing off the machine gets read as a connector, or as damage.


def flange(mesh, axis, along, cross, y, r, uv="collar", thickness=0.45):
    """A collar around a pipe. Reads as a joint, and breaks up a long bare cylinder."""
    if axis == "x":
        pipe_x(mesh, along - thickness, along + thickness, cross, y, r, uv, segments=14)
    else:
        pipe_z(mesh, along - thickness, along + thickness, cross, y, r, uv, segments=14)


def ribs(mesh, cx, cz, count, spacing, length, y0, y1, uv="hullDark", width=0.4, along_x=True):
    """A row of thin raised ribs. The cheapest way to make a flat panel read as panelled."""
    start = -(count - 1) * spacing * 0.5
    for i in range(count):
        offset = start + i * spacing
        if along_x:
            block(mesh, cx + offset, cz, width, length, y0, y1, uv, bevel=0.1, cap=0.0)
        else:
            block(mesh, cx, cz + offset, length, width, y0, y1, uv, bevel=0.1, cap=0.0)


def cabin(mesh, cx, cz, y0, uv="glass", scale=1.0):
    """A control cabin on a stalk, with an overhanging roof.

    Deliberately tall. A box the height of a handrail is invisible at play distance; one that
    stands clear of the machine changes the outline, which is the only thing that carries.
    """
    w, d = 3.6 * scale, 3.0 * scale
    h = 4.2 * scale

    block(mesh, cx, cz, w * 0.45, d * 0.45, y0, y0 + h * 0.45, "frame", bevel=0.25, cap=0.0)
    block(mesh, cx, cz, w, d, y0 + h * 0.4, y0 + h, uv, bevel=0.45, cap=0.4)
    block(mesh, cx, cz, w * 1.25, d * 1.25, y0 + h - 0.35, y0 + h + 0.45, "metal",
          bevel=0.4, cap=0.25)


def vent(mesh, cx, cz, y0, height=4.0, r=1.15, uv="metal"):
    """An exhaust stack with a cap.

    `y0` is where it *starts*, and it must be the top of something solid - an earlier version
    floated one beside a gantry leg rather than on it, which read exactly as it sounds: a stem
    hanging in mid-air. Tall enough to break the skyline, which is the point of it.
    """
    tube(mesh, cx, cz, r, y0, y0 + height, uv, segments=12, cap=0.25)
    tube(mesh, cx, cz, r * 1.45, y0 + height - 0.4, y0 + height + 0.5, "warn",
         segments=12, cap=0.2)


def valve(mesh, cx, cz, y0, uv="warn", r=1.9):
    """A handwheel on a stem. Says 'fluid' without a drop of it.

    Sized to be seen: at the first attempt's radius of 0.95 on a fifteen-unit machine it was a
    speck.
    """
    tube(mesh, cx, cz, 0.45, y0, y0 + 2.1, "metal", segments=8)
    tube(mesh, cx, cz, r, y0 + 2.1, y0 + 2.5, uv, segments=14, cap=0.15)
    tube(mesh, cx, cz, r * 0.4, y0 + 2.0, y0 + 2.65, "metal", segments=8)


def feet(mesh, xs, zs, seat, uv="frame", size=1.5):
    """Pads where a machine meets the deck, so it sits on the platform rather than floating."""
    for x in xs:
        for z in zs:
            block(mesh, x, z, size, size, 0.0, seat + 0.15, uv, bevel=0.3, cap=0.0)


# ---------------------------------------------------------------------------
# Shared bits of the family
# ---------------------------------------------------------------------------

# Every machine sits inside this. The chunk is 20 wide; leaving a margin keeps the
# platform's own frame visible around the structure the way vanilla islands do.
FOOTPRINT = 15.0

# The square cargo duct that carries packed cargo. Shared between the shape packager's
# outfeed and the shape unpackager's infeed so a packed line reads as continuous.
DUCT_SX, DUCT_SZ = 5.0, 6.2
DUCT_Y0, DUCT_Y1 = 0.45, 4.2


# How far anything standing on the plinth sinks into it.
#
# Not cosmetic. A block whose bottom face is exactly level with the plinth's top face is
# two coplanar surfaces at identical depth, which z-fights - the flicker is visible in the
# preview render and would be visible in game. Seating everything slightly proud of the
# join costs nothing and removes the whole class of problem.
SEAT = 0.25

# Deliberately thin. The island's own platform deck is already a full 20x20 slab under all
# of this, so a tall pad on top of it reads as a second deck rather than as part of the
# machine. This is a mounting pad, not a floor.
PLINTH_H = 0.7


def plinth(mesh, sx=FOOTPRINT, sz=FOOTPRINT, height=PLINTH_H):
    block(mesh, 0, 0, sx, sz, 0.0, height, "frame", bevel=1.1, cap=0.3)
    return height - SEAT


def duct(mesh, x0, x1, uv="cargo"):
    """A length of cargo duct along the flow axis, with a collar at its outer end."""
    cx = (x0 + x1) * 0.5
    block(mesh, cx, 0.0, abs(x1 - x0), DUCT_SZ, DUCT_Y0, DUCT_Y1, uv, bevel=0.5, cap=0.3)
    collar_x = x1 if x1 > x0 else x0
    block(mesh, collar_x, 0.0, 0.9, DUCT_SZ + 1.4, DUCT_Y0 - 0.3, DUCT_Y1 + 0.5,
          "collar", bevel=0.3, cap=0.25)


def gantry(mesh, y_top, beam_thickness=1.3, span=7.6):
    """Four legs and a crossbeam over the middle of the flow.

    The packager and the unpackager share it. It is most of what distinguishes them at a
    distance from the store, which has no overhead structure at all.
    """
    legs(mesh, [-2.9, 2.9], [-5.0, 5.0], 0.2, y_top)
    block(mesh, 0.0, 0.0, span, 2.6, y_top, y_top + beam_thickness, "metal",
          bevel=0.4, cap=0.3, cap_uv="warn")


# ---------------------------------------------------------------------------
# The six machines
# ---------------------------------------------------------------------------


def cargo_packager():
    """Loose shapes in from the West, one package out to the East.

    A press: an open hopper swallows the loose stream, a ram comes down on it, and what
    leaves is a sealed package in a duct. The hopper being open and the outfeed being
    enclosed is the whole story of the machine, and is the read that has to survive being
    mirrored into the unpackager.
    """
    m = Mesh()
    seat = plinth(m, FOOTPRINT, 13.0)

    # Intake, West. Open-topped and flared, so it is obviously where loose stuff goes in.
    hopper(m, -5.6, 0.0, 6.0, 11.4, 3.4, 5.4, seat, 6.4)
    block(m, -8.8, 0.0, 1.5, 7.4, seat, 3.0, "frame", bevel=0.3, cap=0.25)

    # Press body and die.
    block(m, 0.0, 0.0, 7.6, 11.4, seat, 4.0, "hull", bevel=0.8, cap=0.4)
    block(m, 0.0, 0.0, 3.2, 4.4, 3.8, 5.0, "cargo", bevel=0.2, cap=0.15)

    gantry(m, 8.4)
    # The ram, hanging under the beam with a gap over the die.
    block(m, 0.0, 0.0, 4.6, 6.2, 5.6, 8.4, "accent", bevel=0.45, cap=0.3)

    # Detail: ribbed press flanks, a control box beside the die, a stack over the gantry, and
    # pads where the whole thing meets the deck.
    for z in (-5.9, 5.9):
        ribs(m, 0.0, z, 4, 1.7, 0.9, 1.2, 3.6)
    cabin(m, -0.6, -6.4, seat)
    # On top of the gantry beam, which spans y 8.4..9.7 - not beside a leg, where the first
    # version left it floating.
    vent(m, 0.0, 0.0, 9.7, height=2.8)
    feet(m, [-6.6, 6.6], [-5.4, 5.4], seat)

    duct(m, 4.0, 9.3)
    return m


def cargo_unpackager():
    """One package in from the West, loose shapes out to the East.

    The packager run backwards, and drawn that way on purpose: same plinth, same gantry,
    duct on the other end. What changes is that the ram becomes a hood that has lifted
    clear, and the outfeed is three open chutes instead of one sealed duct - so which
    direction a line is flowing is legible from above without reading the icons.
    """
    m = Mesh()
    seat = plinth(m, FOOTPRINT, 13.0)

    duct(m, -9.3, -4.0)

    # Opening chamber.
    block(m, 0.0, 0.0, 7.6, 11.4, seat, 4.0, "hull", bevel=0.8, cap=0.4)
    block(m, 0.0, 0.0, 3.2, 4.4, 3.8, 4.8, "cargo", bevel=0.2, cap=0.15)

    gantry(m, 8.4)
    # Hood, parked higher than the packager's ram - it is open, not pressing.
    block(m, 0.0, 0.0, 5.4, 7.0, 6.7, 8.4, "accent", bevel=0.5, cap=0.35)

    # Outfeed spreader: three chutes fanning East, open on top like the packager's hopper.
    block(m, 4.3, 0.0, 1.3, 11.0, seat, 4.0, "frame", bevel=0.3, cap=0.25)
    for z in (-3.9, 0.0, 3.9):
        loft(m,
             octagon(5.0, z, 2.2, 3.0, 0.35),
             octagon(8.6, z, 1.5, 2.4, 0.3),
             1.5, 3.6, "hullDark")

    # Detail, mirroring the packager's so the pair still reads as a pair: ribbed flanks, a
    # control box, a stack, deck pads.
    for z in (-5.9, 5.9):
        ribs(m, 0.0, z, 4, 1.7, 0.9, 1.2, 3.6)
    cabin(m, 0.6, -6.4, seat)
    vent(m, 0.0, 0.0, 9.7, height=2.8)
    feet(m, [-6.6, 6.6], [-5.4, 5.4], seat)
    return m


# The rack the stores are built from, and the shelf heights the renderer stacks cargo on.
#
# These two numbers are shared with CargoStoreSimulationRenderer, which places packages on
# these shelves at runtime. Change them here and the C# has to change with them - there is no
# way to read an .obj's shelf heights back out, so this is a seam that cannot be checked by
# the compiler. It is called out in DESIGN.md for that reason.
SHELF_HEIGHTS = (1.5, 4.1, 6.7)
RACK_SLOTS = 5
RACK_INNER = 12.4


def rack(mesh, seat):
    """An open three-shelf rack: corner posts, three shelves of runners, no roof.

    Deliberately empty. Both stores used to have crates modelled in, which was wrong once
    the renderer could show the real contents - a full-looking model on an empty store is
    worse than a plain one.

    Two things here are about being able to *see* the cargo, which is the whole point of a
    buffer you walk past. The roof is gone, so the top shelf is open. And each shelf is
    five runners rather than one solid plate, because a solid shelf is a roof for the shelf
    below it - stacked storage occludes itself from a top-down camera, and there is no
    arrangement of twenty-five packages per layer on a single chunk that avoids stacking.
    Runners at least let a part-empty shelf show through to the one under it.

    The runners sit under the cargo rows, so their spacing is the renderer's slot pitch.
    """
    corner = FOOTPRINT * 0.5 - 1.0
    legs(mesh, [-corner, corner], [-corner, corner], seat, SHELF_HEIGHTS[-1] + 2.6, size=1.5)

    # Diagonal-ish bracing, faked as short blocks between the posts at two heights. Real
    # diagonals would need a rotated box, which the primitives here do not do; stubs at the
    # corners read as bracing from any angle and cost four blocks.
    for height in (SHELF_HEIGHTS[0] - 0.9, SHELF_HEIGHTS[-1] + 1.4):
        for z in (-corner, corner):
            block(mesh, 0.0, z, corner * 1.6, 0.55, height, height + 0.5, "shadow",
                  bevel=0.15, cap=0.0)

    pitch = RACK_INNER / RACK_SLOTS
    runner = pitch * 0.62

    for top in SHELF_HEIGHTS:
        # Side rails along the flow, tying the runners together and the posts in.
        for across in (-RACK_INNER * 0.5 - 0.4, RACK_INNER * 0.5 + 0.4):
            block(mesh, 0.0, across, RACK_INNER + 1.6, 0.8, top - 0.55, top, "rail",
                  bevel=0.2, cap=0.0)

        # One runner per row of cargo.
        for slot in range(RACK_SLOTS):
            across = -RACK_INNER * 0.5 + pitch * (slot + 0.5)
            block(mesh, 0.0, across, RACK_INNER + 0.6, runner, top - 0.5, top, "metal",
                  bevel=0.15, cap=0.0)


def cargo_store():
    """A buffer for shape cargo: an open rack the renderer fills.

    Three shelves because the simulation holds three independent per-layer queues, so a
    glance at the rack says which layers are backed up.
    """
    m = Mesh()
    seat = plinth(m, FOOTPRINT, FOOTPRINT)
    rack(m, seat)

    # Stub ends, so which way the line runs is legible on an empty store.
    for x in (-7.4, 7.4):
        block(m, x, 0.0, 1.4, 6.4, seat, 2.6, "hull", bevel=0.4, cap=0.3)

    cabin(m, 0.0, -7.2, seat, scale=0.9)
    feet(m, [-6.4, 6.4], [-6.4, 6.4], seat)
    return m


def fluid_cargo_store():
    """The same rack, for fluid cargo.

    Also a rack rather than the row of tanks it used to be, and that is a correctness fix
    rather than a style one: a fluid cargo store holds `CargoPackage<FluidId>` - sealed
    containers - not loose fluid. Tanks said it pooled the stuff. The two stores now differ
    by their end stubs and by what the renderer puts on the shelves, which is exactly how
    they differ in the simulation.
    """
    m = Mesh()
    seat = plinth(m, FOOTPRINT, FOOTPRINT)
    rack(m, seat)

    # Raised from 1.9 so the collars clear the deck: a collar is wider than its pipe, and at
    # the old height its underside sat below Y=0. The generator's own check caught it.
    pipe_x(m, -9.4, -5.6, 0.0, 2.25, 1.9)
    pipe_x(m, 5.6, 9.4, 0.0, 2.25, 1.9)
    flange(m, "x", -5.9, 0.0, 2.25, 2.2)
    flange(m, "x", 5.9, 0.0, 2.25, 2.2)

    cabin(m, 2.6, -7.2, seat, scale=0.9)
    valve(m, -3.6, -7.2, seat)
    feet(m, [-6.4, 6.4], [-6.4, 6.4], seat)
    return m


def fluid_cargo_packager():
    """Fluid in, packaged fluid out - both ends pipes.

    Both the fluid packager and the fluid unpackager have a pipe on each side, because the
    fluid line is pipe-tagged end to end. So the ends cannot carry the distinction the way
    they do on the shape pair, and the tank has to: lying across the flow here, standing
    upright on the unpackager.
    """
    m = Mesh()
    seat = plinth(m, FOOTPRINT, 12.0)

    pipe_x(m, -9.6, -3.4, 0.0, 2.3, 1.9)
    pipe_x(m, 3.4, 9.6, 0.0, 2.3, 1.9)
    for x in (-3.6, 3.6):
        block(m, x, 0.0, 0.9, 5.4, seat, 4.4, "frame", bevel=0.3, cap=0.25)

    # Saddles, then the tank across the flow.
    for z in (-4.3, 4.3):
        block(m, 0.0, z, 6.4, 1.4, seat, 4.6, "hullDark", bevel=0.3, cap=0.25)
    pipe_z(m, -5.8, 5.8, 0.0, 6.2, 3.2, "fluid", segments=16)

    # Compressor stack on top - the bit that does the packing.
    block(m, 0.0, 0.0, 3.2, 3.2, 8.6, 10.2, "accent", bevel=0.4, cap=0.3)

    # Detail: collars where the pipes meet the hull, a handwheel, ribs along the tank, a control
    # box and deck pads.
    flange(m, "x", -3.9, 0.0, 2.3, 2.2)
    flange(m, "x", 3.9, 0.0, 2.3, 2.2)
    ribs(m, 0.0, 0.0, 3, 2.6, 7.0, 8.9, 9.3, "rail", width=0.5, along_x=True)
    valve(m, 4.4, -4.4, seat)
    cabin(m, -4.6, -4.4, seat)
    feet(m, [-6.2, 6.2], [-4.6, 4.6], seat)
    return m


def fluid_cargo_unpackager():
    """Packaged fluid in, fluid out. The standing-tank half of the fluid pair."""
    m = Mesh()
    seat = plinth(m, FOOTPRINT, 12.0)

    pipe_x(m, -9.6, -2.4, 0.0, 2.3, 1.9)
    pipe_x(m, 2.4, 9.6, 0.0, 2.3, 1.9)

    # Standing tank, straddling the pipe run.
    block(m, 0.0, 0.0, 8.6, 8.6, seat, 1.3, "hullDark", bevel=0.7, cap=0.3)
    tube(m, 0.0, 0.0, 3.5, 1.1, 9.6, "fluid", segments=16, cap=0.5)

    # Relief manifold off the top, the counterpart to the shape unpackager's chutes.
    for z in (-4.2, 4.2):
        tube(m, 0.0, z, 0.75, 5.4, 8.6, "collar", segments=10)
    pipe_z(m, -4.2, 4.2, 0.0, 8.6, 0.75, "collar", segments=10)

    # Detail: pipe collars, banding around the standing tank, a handwheel, a box and pads.
    flange(m, "x", -2.9, 0.0, 2.3, 2.2)
    flange(m, "x", 2.9, 0.0, 2.3, 2.2)
    for height in (3.4, 6.4):
        tube(m, 0.0, 0.0, 3.75, height, height + 0.4, "rail", segments=16)
    valve(m, 5.2, -4.2, seat)
    cabin(m, -5.0, -4.2, seat)
    feet(m, [-5.4, 5.4], [-4.6, 4.6], seat)
    return m


MACHINES = {
    "CargoTrack": cargo_track_straight,
    "CargoTrackLeft": cargo_track_left,
    "CargoTrackRight": cargo_track_right,
    # Eight, not five: once the arms curve, a splitter and the merger on the same spokes bend
    # opposite ways and can no longer share a mesh. See merger_arm.
    "CargoTrackSplitLeftFwd": lambda: splitter(("E", "N")),
    "CargoTrackSplitRightFwd": lambda: splitter(("E", "S")),
    "CargoTrackSplitY": lambda: splitter(("N", "S")),
    "CargoTrackSplitTriple": lambda: splitter(("E", "N", "S")),
    "CargoTrackMergeLeftFwd": lambda: merger(("E", "N")),
    "CargoTrackMergeRightFwd": lambda: merger(("E", "S")),
    "CargoTrackMergeY": lambda: merger(("N", "S")),
    "CargoTrackMergeTriple": lambda: merger(("E", "N", "S")),

    # Lifts. Sixteen, one per definition: four exits by two climb heights by up and down.
    **{
        f"CargoTrackLift{abs(layers)}{'Up' if layers > 0 else 'Down'}{word}":
            (lambda n=name, l=layers: cargo_lift(n, l))
        for word, name in (("Forward", "E"), ("Right", "S"), ("Backward", "W"), ("Left", "N"))
        for layers in (1, 2, -1, -2)
    },
    "CargoPackager": cargo_packager,
    "CargoUnpackager": cargo_unpackager,
    "CargoStore": cargo_store,
    "FluidCargoPackager": fluid_cargo_packager,
    "FluidCargoUnpackager": fluid_cargo_unpackager,
    "FluidCargoStore": fluid_cargo_store,
}


# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------


def signed_volume(tris):
    """Six times the signed volume, by the divergence theorem.

    Positive means the triangles are wound counter-clockwise seen from outside, which is
    what an .obj is supposed to be. It is the one cheap test that catches an inside-out
    solid, and it caught three of them here - a mesh renders perfectly well in a viewer
    that does not backface-cull and then shows up hollow in game.
    """
    total = 0.0
    for a, b, c, _ in tris:
        total += (a[0] * (b[1] * c[2] - b[2] * c[1])
                  - a[1] * (b[0] * c[2] - b[2] * c[0])
                  + a[2] * (b[0] * c[1] - b[1] * c[0])) / 6.0
    return total


def components(tris):
    """Groups triangles that share a vertex position, so each closed solid is checked alone.

    Parts that merely overlap - junction arms, a rail sitting proud of a wall - share no exact
    vertex and stay separate, which is what makes a per-part winding check possible at all.
    """
    parent = {}

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[rb] = ra

    for triangle in tris:
        for vertex in triangle[:3]:
            parent.setdefault(vertex, vertex)
        union(triangle[0], triangle[1])
        union(triangle[1], triangle[2])

    groups = {}
    for triangle in tris:
        groups.setdefault(find(triangle[0]), []).append(triangle)

    return list(groups.values())


def boundary_edges(tris):
    """Directed edges with no opposing twin, i.e. the holes.

    Zero for a closed solid. Not zero is not automatically wrong - `hopper` is open by
    design and contributes its rim - but an unexpected count means a cap is missing or a
    fan is wound backwards.
    """
    edges = set()
    for a, b, c, _ in tris:
        edges.update(((a, b), (b, c), (c, a)))
    return sum(1 for e in edges if (e[1], e[0]) not in edges)


def write_obj(path, name, mesh):
    """Writes one mesh as one .obj object.

    One object and no materials, because FileMeshLoader calls .Meshes().Single() on the
    Assimp scene - a file that splits into two meshes throws rather than drawing half.

    X is negated here and only here, to cancel the importer's own negation (see THE X FLIP
    in the module docstring). Mirroring an axis reverses which side of a triangle is the
    front, so the vertex order is reversed to match - negating X alone would write a file
    that is inside-out as an .obj, and the importer's winding flip would not save it. The
    normal is computed after both, from the vertices actually being written.
    """
    verts, normals, uvs = [], [], []
    index = {}
    faces = []

    def push(table, value):
        key = (id(table), value)
        got = index.get(key)
        if got is None:
            table.append(value)
            got = len(table)
            index[key] = got
        return got

    for a, b, c, uv_key in mesh.tris:
        flipped = [(-v[0], v[1], v[2]) for v in (c, b, a)]
        ni = push(normals, _normal(*flipped))
        ti = push(uvs, PALETTE.get(uv_key, ATLAS_UV))
        faces.append([(push(verts, v), ti, ni) for v in flipped])

    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("# %s - generated by Tools/generate_meshes.py, do not hand-edit\n" % name)
        handle.write("# Regenerate with: python Tools/generate_meshes.py\n")
        handle.write("o %s\n" % name)
        for v in verts:
            handle.write("v %.5f %.5f %.5f\n" % v)
        for t in uvs:
            handle.write("vt %.5f %.5f\n" % t)
        for n in normals:
            handle.write("vn %.5f %.5f %.5f\n" % n)
        for face in faces:
            handle.write("f %s\n" % " ".join("%d/%d/%d" % c for c in face))

    return len(verts), len(faces)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.normpath(os.path.join(here, os.pardir, "TrainCargoTools", "Resources"))
    if not os.path.isdir(out):
        sys.exit("No Resources folder at %s" % out)

    # Nothing is open any more. The two packagers used to be, because `hopper` was a
    # single-skinned cone; it is a closed solid with a hollow in it now, for the reason
    # written up there.
    expected_open = set()
    problems = 0

    for name, build in sorted(MACHINES.items()):
        mesh = build()
        verts, faces = write_obj(os.path.join(out, name + ".obj"), name, mesh)

        xs = [v[0] for t in mesh.tris for v in t[:3]]
        ys = [v[1] for t in mesh.tris for v in t[:3]]
        zs = [v[2] for t in mesh.tris for v in t[:3]]
        volume = signed_volume(mesh.tris)
        holes = boundary_edges(mesh.tris)

        print("%-24s %4d verts %4d tris  X[%5.1f,%5.1f] Y[%4.1f,%4.1f] Z[%5.1f,%5.1f]"
              % (name + ".obj", verts, faces,
                 min(xs), max(xs), min(ys), max(ys), min(zs), max(zs)))

        if volume <= 0:
            print("   FAIL: signed volume %+.1f - wound inside out, will render hollow"
                  % volume)
            problems += 1

        # Per *component*, because the whole-mesh figure is a sum and hides an inverted part
        # inside a mesh that is positive overall. That is not hypothetical: every mirrored
        # `sweep_box` call in this file produced one good side and one inside-out one, on all
        # 33 track meshes, and this line is the only reason it was ever found.
        for part in components(mesh.tris):
            part_volume = signed_volume(part)
            if part_volume < -1e-6:
                xs2 = [v[0] for t in part for v in t[:3]]
                ys2 = [v[1] for t in part for v in t[:3]]
                zs2 = [v[2] for t in part for v in t[:3]]
                print("   FAIL: a %d-triangle part is wound inside out (volume %+.1f) "
                      "at X[%.1f,%.1f] Y[%.1f,%.1f] Z[%.1f,%.1f]"
                      % (len(part), part_volume, min(xs2), max(xs2),
                         min(ys2), max(ys2), min(zs2), max(zs2)))
                problems += 1
        if holes and name not in expected_open:
            print("   FAIL: %d boundary edges - a cap is missing" % holes)
            problems += 1
        if min(xs) < -10 or max(xs) > 10 or min(zs) < -10 or max(zs) > 10:
            print("   FAIL: pokes outside the 20x20 chunk")
            problems += 1
        # The track pieces are the exception: their deck top is the origin, so the structure
        # under it is legitimately negative. They are positioned by the drawer, not by Y=0.
        if min(ys) < -0.001 and not name.startswith("CargoTrack"):
            print("   FAIL: dips below the deck at Y=0")
            problems += 1
        # Raised from 12: the packager and unpackager carry a gantry with a stack on top, and
        # that is deliberate silhouette rather than something to shrink. 14 still catches a
        # machine that has genuinely run away.
        if max(ys) > 14:
            print("   WARN: %.1f tall, which is most of the chunk width" % max(ys))

    if problems:
        sys.exit("\n%d problem(s); the .obj files were still written, but fix these first."
                 % problems)
    print("\nAll %d closed, outward-wound and inside the chunk." % len(MACHINES))


if __name__ == "__main__":
    main()
