# Blockworks

21 voxel blocks for shapez 2, placeable on the machine layer alongside your machines. They
connect to nothing, process nothing and appear in no statistic. They are there so the empty half
of a platform can be made to look like something.

One block fills exactly one building layer, so they stack three high on a platform and line up
with everything else on the grid - use the layer keys to build upward, the same as any other
building. Placement is in Area mode, so a wall is dragged out as a rectangle rather than clicked
one tile at a time. One research node unlocks the whole set.

## Sparkstone

A second toolbar tab with nine components: torch, lever, button, dust, repeater, comparator,
observer, lamp and converter. They run a real signal simulation on a 0.1s tick - dust carries
0-15 signal strength and loses one per tile, torches invert the block behind them (or act as a
source standing free), repeaters delay 1 to 4 ticks and lock from the side. The block of
sparkstone from the decoration set is a permanent source.

Levers, buttons and repeaters are worked from the **side panel** - select one and the panel has
its switch - because shapez has no click-a-building-in-the-world interaction.

A **Sparkstone Converter** bridges to shapez's own wire layer, both ways at once: a wire signal
that is not Off broadcasts full-strength sparkstone, and sparkstone power puts a true on the wire.
Wire in at the back, wire out at the front.

Signal strength, inversion and repeater locking are modelled directly; the emergent timing
artefacts of other games' implementations - quasi-connectivity, zero-tick pulses, BUD switches -
are not, and circuits built on those will not carry over. See [DESIGN.md](DESIGN.md). No pistons
yet, and signal state does not survive a save and reload.

## Textures

The blocks are textured from ordinary 16x16 PNGs in `DecorationBlocks/Resources/Textures`, named
the way a resource pack names them - `stone.png`, `oak_planks.png`, `grass_block_top.png`. The
atlas is packed at load, so **swapping in a different texture pack is a file copy**, with no
rebuild and nothing in the code to change.

Two scripts fill that folder:

```sh
python tools_import_minecraft.py            # from an installed Minecraft client jar
python tools_import_minecraft.py <path>     # ...or a .minecraft folder you name
python tools_make_placeholders.py           # flat generated colours, for testing with no jar
```

`tools_import_minecraft.py` finds the newest version jar under `.minecraft/versions`, pulls
the 23 files the catalog names, and fixes the two that are not a straight copy: grass is
stored greyscale and tinted per biome at draw time, so the top face and the side overlay are
multiplied by the plains tint and the overlay composited down. Without that, grass comes out
a pale grey square.

> The imported textures are Mojang's artwork, and the published build ships them. They are not
> covered by this repository's Apache licence - see [NOTICE](NOTICE). `tools_make_placeholders.py`
> generates a flat stand-in set if you want to build without them.

`BlockCatalog.cs` is the list of blocks and which file goes on which face. Adding a block is one
entry there plus its PNGs.

## Build

```bash
dotnet build                 # installs into <persistent>/mods/DecorationBlocks  (game CLOSED)
dotnet build -p:Dev=true     # stages into <persistent>/mods-dev/DecorationBlocks
```

Needs `SPZ2_PATH`, `SPZ2_PERSISTENT` and `SPZ2_SHIFTER`, and Shapez Shifter at runtime.

## Console

| Command | What it does |
| --- | --- |
| `db.material` | prints the building shader's name and every texture property, with what is bound to each |
| `db.atlas` | writes the packed atlas to `%persistentDataPath%/DecorationBlocksAtlas.png` |
| `db.shaders` | lists every shader the build actually loaded |
| `db.shader <name>` | rebuilds the block material on a different shader |
| `db.set <smoothness\|metallic\|cutoff> <0..1>` | retunes the material live |

Both exist because the material the blocks borrow is authored Unity data that cannot be read at
build time. If the blocks come out untextured or the wrong colour, `db.material` is the first
thing to run - see [DESIGN.md](DESIGN.md).

## Licence

Apache 2.0. See `LICENSE` and `NOTICE`.
