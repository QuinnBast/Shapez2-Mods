using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ILogger = Core.Logging.ILogger;

namespace QuinnBast.Shapez2.DecorationBlocks;

/// The material the blocks are drawn with: URP's own Lit shader, with the mod's atlas as its
/// base map.
///
/// **The building shader cannot do this, and that is settled rather than suspected.** A runtime
/// dump (`db.material`) shows the building material's shader is `Shader Graphs/UberBuildingShader`
/// with no `_BaseMap` and no `_MainTex` anywhere in it. What it has is `_MaterialLUT` at
/// 256x256, `_PackedTex`, and a dozen brushed-metal, noise and scratch maps. Colour there is a
/// *lookup* - UV0 picks a texel out of a material palette - and everything else is procedural
/// surface treatment layered over it. Feeding an atlas into the LUT does map the right pixels to
/// the right faces, but the metal and scratch passes then run over the result, and pixel art
/// does not survive that. Neither does `_PackedTex`. Both were tried.
///
/// So the blocks do not borrow the game's shader at all. `db.shaders` lists what the build
/// actually loaded, and **`Universal Render Pipeline/Lit` is in it** - which is the one thing
/// that could not be assumed, because an unreferenced URP shader is stripped from a player build
/// and `Shader.Find` then returns null rather than failing loudly. A fresh URP Lit material takes
/// the atlas in `_BaseMap` and renders it as a texture, lit and shadowed, with nothing layered
/// on top.
///
/// Cloning the theme's material is kept as the fallback for the case where that shader is absent
/// - a future build, or a different graphics tier. It will look wrong, but it will draw.
internal sealed class BlockMaterials
{
    /// Verified present in the shipped build by `db.shaders`. Not guessed.
    private const string PreferredShader = "Universal Render Pipeline/Lit";

    /// Where the atlas goes, most specific first. `_BaseMap` is URP Lit's. `_MaterialLUT` is
    /// shapez's own, kept for the clone fallback path.
    private static readonly string[] PreferredTextureProperties =
    {
        "_BaseMap",
        "_MainTex",
        "_MaterialLUT",
        "_BaseColorMap",
        "_AlbedoMap",
        "_Albedo",
        "_Texture",
    };

    private BlockMaterials(
        Material opaqueMaterial, Material translucentMaterial,
        string shaderName, string chosenProperty, string[] available)
    {
        OpaqueMaterial = opaqueMaterial;
        TranslucentMaterial = translucentMaterial;
        Opaque = new BlockMaterialReference(opaqueMaterial);
        Translucent = new BlockMaterialReference(translucentMaterial);
        ShaderName = shaderName;
        ChosenProperty = chosenProperty;
        AvailableProperties = available;
    }

    public IMaterialReference Opaque { get; }

    public IMaterialReference Translucent { get; }

    public string ShaderName { get; }

    public string ChosenProperty { get; }

    public string[] AvailableProperties { get; }

    private Material OpaqueMaterial { get; }

    private Material TranslucentMaterial { get; }

    /// The atlas the materials are built around, handed over by the mod constructor. Set before
    /// any session exists, which is the opposite of the theme.
    public static Texture2D Atlas { get; set; }

    /// Whatever the last build produced, for the console to report on and rebind. Null until the
    /// first block is drawn.
    public static BlockMaterials Current => Built;

    private static BlockMaterials Built;

    private static VisualThemeBaseResources BuiltFrom;

    /// Set by `db.slot` and `db.shader`, and preferred over the defaults on the next build. Null
    /// means "use the default".
    private static string PropertyOverride;

    private static string ShaderOverride;

    /// Matte, not plastic. URP Lit defaults to 0.5 smoothness, which puts a wet sheen on a
    /// cobblestone block. Exposed so `db.set` can tune them against the real lighting rather
    /// than against a guess.
    public static float Smoothness { get; set; }

    public static float Metallic { get; set; }

    /// Alpha *clip*, not alpha blend, for glass. Blending in URP means setting _Surface, _Blend,
    /// _SrcBlend, _DstBlend, _ZWrite, the render queue and two keywords in agreement, and
    /// getting sort order right afterwards. Minecraft's own glass is a cutout - fully
    /// transparent holes in an opaque frame - so clipping is both simpler and more faithful.
    public static float Cutoff { get; set; } = 0.5f;

    /// Built on first draw, not at mod load. The fallback path clones a material that lives on
    /// the visual theme, and nothing a mod can reach at load time holds a theme -
    /// IGameSessionManagers, what GameHelper.Core returns, exposes the player, mode, registries
    /// and viewport but neither the session nor the theme. The first frame that draws a block is
    /// the earliest point it is in hand, and it arrives as options.Theme.
    public static BlockMaterials Shared(VisualThemeBaseResources theme, ILogger log)
    {
        if (Built != null && ReferenceEquals(BuiltFrom, theme))
        {
            return Built;
        }

        BuiltFrom = theme;
        Built = Create(theme, Atlas, log);
        return Built;
    }

    /// Point the atlas at a different texture property and rebuild, without a restart.
    public static bool Rebind(string property, ILogger log)
    {
        PropertyOverride = property;
        return Rebuild(log);
    }

    /// Build against a different shader and rebuild, without a restart. Pass null to go back to
    /// the default. An unknown or stripped name is reported rather than applied.
    public static bool UseShader(string shaderName, ILogger log)
    {
        ShaderOverride = shaderName;
        return Rebuild(log);
    }

    public static bool Rebuild(ILogger log)
    {
        if (BuiltFrom == null)
        {
            return false;
        }

        BlockMaterials previous = Built;
        Built = Create(BuiltFrom, Atlas, log);

        // The materials are ours, so nothing else can be holding them. Leaving them would leak
        // one per rebuild, which matters when the point of the commands is to try ten.
        previous?.Destroy();
        return true;
    }

    public IMaterialReference For(BlockDefinition block)
    {
        return block.Translucent ? Translucent : Opaque;
    }

    public Material MaterialFor(bool translucent)
    {
        return translucent ? TranslucentMaterial : OpaqueMaterial;
    }

    private void Destroy()
    {
        if (OpaqueMaterial != null)
        {
            UnityEngine.Object.Destroy(OpaqueMaterial);
        }

        if (TranslucentMaterial != null && !ReferenceEquals(TranslucentMaterial, OpaqueMaterial))
        {
            UnityEngine.Object.Destroy(TranslucentMaterial);
        }
    }

    public static BlockMaterials Create(VisualThemeBaseResources theme, Texture2D atlas, ILogger log)
    {
        Shader shader = Shader.Find(ShaderOverride ?? PreferredShader);

        if (shader == null)
        {
            log?.Error?.Log(
                "Decoration blocks: shader '" + (ShaderOverride ?? PreferredShader)
                + "' is not in this build, falling back to a clone of the building material. "
                + "Run `db.shaders` for what is actually loaded.");

            return CreateFromThemeClone(theme, atlas, log);
        }

        Material opaque = Build(shader, atlas, "DecorationBlocks/Opaque", clip: false);
        Material translucent = Build(shader, atlas, "DecorationBlocks/Translucent", clip: true);

        string[] available = Writable(opaque.GetTexturePropertyNames());
        string chosen = Choose(available);

        log?.Info?.Log(
            "Decoration blocks: material built on '" + shader.name + "', atlas in '"
            + (chosen ?? "<none>") + "', smoothness " + Smoothness + ", metallic " + Metallic + ".");

        return new BlockMaterials(opaque, translucent, shader.name, chosen, available);
    }

    private static Material Build(Shader shader, Texture2D atlas, string name, bool clip)
    {
        Material material = new Material(shader) { name = name };

        string property = Choose(Writable(material.GetTexturePropertyNames()));

        if (property != null)
        {
            material.SetTexture(property, atlas);
            material.SetTextureScale(property, Vector2.one);
            material.SetTextureOffset(property, Vector2.zero);
        }

        SetIfHas(material, "_BaseColor", Color.white);
        SetIfHas(material, "_Color", Color.white);
        SetFloatIfHas(material, "_Smoothness", Smoothness);
        SetFloatIfHas(material, "_Glossiness", Smoothness);
        SetFloatIfHas(material, "_Metallic", Metallic);

        if (clip)
        {
            SetFloatIfHas(material, "_AlphaClip", 1.0f);
            SetFloatIfHas(material, "_Cutoff", Cutoff);

            // URP reads the keyword, not just the float. A material built in code has none of
            // the keywords an inspector-edited one would have had set for it, so setting
            // _AlphaClip alone does nothing at all.
            material.EnableKeyword("_ALPHATEST_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }

        // Graphics.RenderMeshInstanced, which is what InstancedMeshRenderer.Flush calls, refuses
        // a material that has not enabled instancing - and refuses it by drawing nothing. A
        // material cloned from one of the game's inherits the flag; one built from a bare shader
        // does not.
        material.enableInstancing = true;

        return material;
    }

    /// The old path: clone the game's building material and overwrite its colour lookup. Kept
    /// only for a build where URP Lit has been stripped. It renders the atlas through the Uber
    /// shader's metal and scratch passes, so it looks like etched metal rather than pixel art.
    private static BlockMaterials CreateFromThemeClone(
        VisualThemeBaseResources theme, Texture2D atlas, ILogger log)
    {
        Material opaqueSource = Resolve(theme.BuildingMaterial);
        Material glassSource = theme.BuildingsGlassMaterial?.GetMaterialInternal();

        string[] available = opaqueSource != null
            ? Writable(opaqueSource.GetTexturePropertyNames())
            : new string[0];

        string chosen = Choose(available);
        Material opaque = Clone(opaqueSource, atlas, chosen, "DecorationBlocks/Opaque");

        Material translucent = glassSource != null
            ? Clone(glassSource, atlas,
                Choose(Writable(glassSource.GetTexturePropertyNames())), "DecorationBlocks/Translucent")
            : opaque;

        log?.Info?.Log(
            "Decoration blocks: cloned '"
            + (opaqueSource == null ? "<null>" : opaqueSource.shader.name)
            + "', atlas in '" + (chosen ?? "<none>") + "'.");

        return new BlockMaterials(
            opaque, translucent,
            opaqueSource == null ? "<null>" : opaqueSource.shader.name,
            chosen, available);
    }

    /// Properties a mod may write to.
    ///
    /// `unity_Lightmaps`, `unity_LightmapsInd` and `unity_ShadowMasks` are Unity's own
    /// per-renderer built-ins and come back from GetTexturePropertyNames like any other. They
    /// are also **first in the list**, which is how an earlier fallback-to-the-first-property
    /// heuristic managed to write the block atlas into the lightmap slot. Never write to them.
    private static string[] Writable(IReadOnlyList<string> names)
    {
        List<string> result = new List<string>(names.Count);

        for (int i = 0; i < names.Count; i++)
        {
            if (!names[i].StartsWith("unity_", StringComparison.Ordinal))
            {
                result.Add(names[i]);
            }
        }

        return result.ToArray();
    }

    private static Material Resolve(ILODMaterial lodMaterial)
    {
        return lodMaterial != null && lodMaterial.TryGet(0, out IMaterialReference reference)
            ? reference.GetMaterialInternal()
            : null;
    }

    private static string Choose(IReadOnlyList<string> available)
    {
        if (PropertyOverride != null)
        {
            for (int i = 0; i < available.Count; i++)
            {
                if (available[i] == PropertyOverride)
                {
                    return PropertyOverride;
                }
            }
        }

        foreach (string preferred in PreferredTextureProperties)
        {
            for (int i = 0; i < available.Count; i++)
            {
                if (available[i] == preferred)
                {
                    return preferred;
                }
            }
        }

        // No fallback to "the first one": on an unknown shader that is a coin flip, and a wrong
        // guess writes a texture into a slot that means something else.
        return null;
    }

    private static Material Clone(Material source, Texture2D atlas, string property, string name)
    {
        if (source == null)
        {
            return null;
        }

        Material clone = new Material(source) { name = name };

        if (property != null)
        {
            clone.SetTexture(property, atlas);
            clone.SetTextureScale(property, Vector2.one);
            clone.SetTextureOffset(property, Vector2.zero);
        }

        clone.enableInstancing = true;
        return clone;
    }

    private static void SetIfHas(Material material, string property, Color value)
    {
        if (material.HasProperty(property))
        {
            material.SetColor(property, value);
        }
    }

    private static void SetFloatIfHas(Material material, string property, float value)
    {
        if (material.HasProperty(property))
        {
            material.SetFloat(property, value);
        }
    }

    /// IMaterialReference has no runtime-constructible implementation. MaterialReference, the one
    /// the game ships, is [Serializable] with a private parameterless constructor and a private
    /// serialized field - it exists to be filled in by the Unity deserializer rather than by
    /// code. The interface is three members and the id generator is public static, so
    /// implementing it is less work than reaching around the publicizer for a constructor that
    /// was never meant to be called.
    private sealed class BlockMaterialReference : IMaterialReference
    {
        private readonly Material Material;

        public BlockMaterialReference(Material material)
        {
            Material = material;

            // Acquired once, at construction, and held for the life of the material. The id is
            // what the instanced renderer batches on: a fresh one per frame would put every
            // block in its own batch and quietly undo the reason for instancing.
            InstanceId = material != null
                ? InstancingIdManager.AcquireMaterialId()
                : MaterialInstanceId.Invalid;
        }

        public MaterialInstanceId InstanceId { get; }

        public bool Empty => Material == null;

        public Material GetMaterialInternal()
        {
            return Material;
        }
    }
}
