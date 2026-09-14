using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace QuinnBast.Shapez2.ModProfiler;

/// <summary>
/// The panel's look: palette, generated textures, styles and the handful of widgets that make
/// IMGUI resemble one of the game's own full-screen pages.
///
/// **Why any of this is needed.** IMGUI's default skin is a grey 2008 toolkit - chunky bevelled
/// buttons, a 16px scrollbar, black-on-grey text. Next to Statistics or Research it reads as a
/// different program, and that was the whole complaint about the first version of this page.
/// The game's pages are, visually, four things: a dark translucent ground, near-white text on a
/// muted blue-grey secondary, soft-cornered surfaces lifted a few percent off the ground, and
/// one warm accent used *only* to say what is selected. None of that needs a HUDPart; it needs
/// textures IMGUI does not ship with.
///
/// So every surface here is a texture generated at startup from a signed distance field -
/// rounded corners with real antialiasing, a one-pixel border, and nothing bevelled. They are
/// used two ways: baked into <see cref="GUIStyle"/> backgrounds where IMGUI draws the element
/// itself (buttons, cards, rows), and drawn directly through <see cref="Rounded"/> where this
/// page positions its own rectangles (the scrollbar, the tooltip, progress bars).
///
/// The palette was matched by eye against a screenshot of the Statistics page. The game's real
/// values live in authored prefabs, which are not readable from an assembly, so these are a
/// close likeness rather than the same numbers.
/// </summary>
internal static class PanelTheme
{
    /// <summary>
    /// How round a surface's corners are - which is really a choice of *mask*, because a
    /// nine-sliced texture cannot be drawn smaller than its own borders without squashing them.
    /// A card gets <see cref="Large"/>; anything six or eight pixels across has to take
    /// <see cref="Tiny"/> or its corners eat the whole shape.
    /// </summary>
    public enum Corner
    {
        Large,
        Small,
        Tiny,
    }

    // ---------------------------------------------------------------- palette

    /// <summary>Primary text - near white, never pure, because pure white vibrates on dark.</summary>
    public static readonly Color Ink = new Color(0.91f, 0.94f, 0.98f);

    /// <summary>Secondary text: row labels, prose.</summary>
    public static readonly Color Muted = new Color(0.72f, 0.78f, 0.86f);

    /// <summary>
    /// Captions and column headings - present, not competing.
    ///
    /// Lifted from 0.46 once the page ground was brightened to match a real one. Against a
    /// near-black page the old value was quiet; against a page at 36,55,90 it was a contrast
    /// ratio of about 3, and the toolbar caption on the CPU tab was the first thing anybody
    /// said they could not read.
    /// </summary>
    public static readonly Color Faint = new Color(0.60f, 0.67f, 0.76f);

    /// <summary>The warm accent under the live tab. Used for selection and nothing else.</summary>
    public static readonly Color Accent = new Color(1.00f, 0.60f, 0.23f);

    /// <summary>The hot core of the accent's glow.</summary>
    public static readonly Color AccentHot = new Color(1.00f, 0.90f, 0.74f);

    /// <summary>Anything owned by a mod rather than by the game.</summary>
    public static readonly Color Cool = new Color(0.49f, 0.80f, 1.00f);

    public static readonly Color Good = new Color(0.42f, 0.86f, 1.00f);
    public static readonly Color Warn = new Color(1.00f, 0.74f, 0.30f);
    public static readonly Color Bad = new Color(1.00f, 0.42f, 0.40f);

    // **Darker than the page, not lighter.** Sampled off a screenshot of the Statistics page:
    // its ground is 36,55,90 and the stat tiles sitting on it are 21,35,51 - the content
    // surfaces are recessed and the *controls* are the things that lift. Getting this backwards
    // is why the first version needed a near-black page to make its cards visible at all.
    private static readonly Color Surface = new Color(0f, 0f, 0f, 0.340f);
    private static readonly Color SurfaceLine = new Color(1f, 1f, 1f, 0.100f);
    private static readonly Color Control = new Color(1f, 1f, 1f, 0.055f);
    private static readonly Color ControlLine = new Color(1f, 1f, 1f, 0.140f);
    private static readonly Color ControlHover = new Color(1f, 1f, 1f, 0.105f);
    private static readonly Color ControlHoverLine = new Color(1f, 1f, 1f, 0.260f);
    private static readonly Color ControlDown = new Color(1f, 0.66f, 0.33f, 0.200f);
    private static readonly Color ControlDownLine = new Color(1f, 0.66f, 0.33f, 0.520f);
    private static readonly Color ControlOn = new Color(1f, 1f, 1f, 0.120f);
    private static readonly Color ControlOnLine = new Color(1f, 1f, 1f, 0.340f);

    // ---------------------------------------------------------------- metrics

    /// <summary>Height of the title / tab band, down to the rule the glow sits on.</summary>
    public const float HeaderHeight = 98f;

    /// <summary>Height of the per-tab caption-and-actions strip under the rule.</summary>
    public const float ToolbarHeight = 32f;

    /// <summary>Width reserved to the right of the scroll view for this page's own scrollbar.</summary>
    public const float Gutter = 16f;

    // ---------------------------------------------------------------- styles

    public static GUIStyle Title;
    public static GUIStyle TabOn;
    public static GUIStyle TabOff;
    public static GUIStyle Close;
    public static GUIStyle Caption;
    public static GUIStyle CaptionRight;
    public static GUIStyle CardTitle;
    public static GUIStyle Label;
    public static GUIStyle LabelRight;
    public static GUIStyle Cell;
    public static GUIStyle CellMod;
    public static GUIStyle Note;
    public static GUIStyle Value;
    public static GUIStyle ValueRight;
    public static GUIStyle TileCaption;
    public static GUIStyle TileValue;
    public static GUIStyle Button;
    public static GUIStyle ButtonOn;
    public static GUIStyle Dropdown;
    public static GUIStyle Ghost;
    public static GUIStyle SegmentOn;
    public static GUIStyle SegmentOff;
    public static GUIStyle RowButton;
    public static GUIStyle Card;
    public static GUIStyle Tile;
    public static GUIStyle Well;
    public static GUIStyle RowEven;
    public static GUIStyle RowOdd;
    public static GUIStyle Tooltip;
    public static GUIStyle FrameLabel;

    // ---------------------------------------------------------------- textures

    /// <summary>White, so <c>GUI.color</c> alone decides what a rectangle looks like.</summary>
    public static Texture2D Pixel;

    private static Texture2D Backdrop;
    private static Texture2D Bloom;
    private static Texture2D Streak;
    private static Texture2D FillLarge;
    private static Texture2D RingLarge;
    private static Texture2D FillSmall;
    private static Texture2D RingSmall;
    private static Texture2D FillTiny;
    private static Texture2D RingTiny;
    private static Texture2D Caret;

    private static GUIStyle FillLargeSlice;
    private static GUIStyle RingLargeSlice;
    private static GUIStyle FillSmallSlice;
    private static GUIStyle RingSmallSlice;
    private static GUIStyle FillTinySlice;
    private static GUIStyle RingTinySlice;

    private static readonly List<Texture2D> Owned = new List<Texture2D>();

    private static bool Built;

    /// <summary>
    /// The typeface. Null means IMGUI's built-in one; see <see cref="PanelFont"/> for where a
    /// better candidate comes from and why it is discovered rather than shipped.
    /// </summary>
    private static Font Face;

    // ---------------------------------------------------------------- lifetime

    /// <summary>
    /// Builds everything on the first draw. IMGUI styles cannot be built anywhere else -
    /// <c>GUI.skin</c> is null outside <c>OnGUI</c> - so this is called at the top of it.
    /// </summary>
    public static void Ensure()
    {
        if (Built)
        {
            return;
        }

        Built = true;

        BuildTextures();
        BuildStyles();
    }

    /// <summary>Swaps the typeface and rebuilds the styles that carry it.</summary>
    public static void SetFont(Font font)
    {
        Face = font;

        if (Built)
        {
            BuildStyles();
        }
    }

    public static Font CurrentFont => Face;

    /// <summary>
    /// Textures are not garbage collected - they are engine objects - so a panel that is
    /// rebuilt on every hot reload would leak one full set per generation, which this mod can
    /// see itself doing in the Unity census.
    /// </summary>
    public static void Release()
    {
        foreach (Texture2D texture in Owned)
        {
            if (texture != null)
            {
                Object.Destroy(texture);
            }
        }

        Owned.Clear();
        Built = false;
    }

    // ---------------------------------------------------------------- surfaces

    /// <summary>
    /// How much to scale the ground by. One is the tuned value; <c>prof.bg</c> moves it.
    ///
    /// Exists because the only way to judge this is to look at the screen next to a real page,
    /// and that cannot be done from here. Applied as a tint at draw time, so it takes effect
    /// without rebuilding a texture.
    /// </summary>
    public static float Brightness = 1f;

    /// <summary>
    /// The page's ground: one gradient sheet over the paused world, then the game's own vignette,
    /// glows and rotating line layers over that - see <see cref="GameBackdrop"/>.
    ///
    /// The sheet carries the tone and the game's layers carry the shape. They were one or the
    /// other before, and a flat sheet dark enough to read cards against came out at half the
    /// brightness of a real page.
    /// </summary>
    public static void DrawBackdrop()
    {
        GUI.color = new Color(Brightness, Brightness, Brightness, 1f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Backdrop);
        GUI.color = Color.white;

        // Under one, because the sheet is already at the target tone and these only have to add
        // the vignette's fall-off at the edges and the warm lift at the bottom.
        GameBackdrop.Draw(0.7f * Brightness);
    }

    public static void Fill(Rect area, Color colour)
    {
        GUI.color = colour;
        GUI.DrawTexture(area, Pixel);
        GUI.color = Color.white;
    }

    /// <summary>
    /// A rounded rectangle at an arbitrary position - fill, border, or both.
    ///
    /// Two draws rather than one texture per colour pair: the fill and the ring are separate
    /// white masks, so any combination is a tint rather than another 24x24 allocation.
    /// </summary>
    public static void Rounded(Rect area, Color fill, Color line, Corner corner = Corner.Large)
    {
        if (Event.current.type != EventType.Repaint)
        {
            return;
        }

        GUIStyle body = corner == Corner.Tiny ? FillTinySlice : corner == Corner.Small ? FillSmallSlice : FillLargeSlice;
        GUIStyle edge = corner == Corner.Tiny ? RingTinySlice : corner == Corner.Small ? RingSmallSlice : RingLargeSlice;

        if (fill.a > 0f)
        {
            GUI.color = fill;
            body.Draw(area, GUIContent.none, false, false, false, false);
        }

        if (line.a > 0f)
        {
            GUI.color = line;
            edge.Draw(area, GUIContent.none, false, false, false, false);
        }

        GUI.color = Color.white;
    }

    /// <summary>
    /// The dropdown chevron, drawn from a generated mask rather than typed.
    ///
    /// Every arrow character worth using lives outside Latin-1, and which of them a face carries
    /// is unknowable here - the built-in IMGUI font and whatever the game's typeface turns out
    /// to be do not have to agree. A missing glyph renders as a box, in the one place on the
    /// page that is meant to say "there is more here".
    /// </summary>
    public static void DrawCaret(Rect area, Color colour)
    {
        GUI.color = colour;
        GUI.DrawTexture(area, Caret);
        GUI.color = Color.white;
    }

    /// <summary>A hairline rule. One pixel, low alpha - the game's own separators are barely there.</summary>
    public static void Rule(float x, float y, float width)
    {
        Fill(new Rect(x, Mathf.Round(y), width, 1f), new Color(1f, 1f, 1f, 0.10f));
    }

    /// <summary>
    /// The warm underline the game puts beneath the live tab: a bloom sitting on the header
    /// rule with a bright streak through it.
    ///
    /// This is the single detail that does most of the work of making the page read as the same
    /// family of screen, which is why it is a pair of generated gradients rather than a flat
    /// two-pixel bar.
    /// </summary>
    public static void DrawTabGlow(float centreX, float ruleY, float width)
    {
        float bloomWidth = Mathf.Max(width * 2.6f, 260f);

        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(centreX - bloomWidth / 2f, ruleY - 26f, bloomWidth, 52f), Bloom);
        GUI.DrawTexture(new Rect(centreX - bloomWidth / 2f, ruleY - 1f, bloomWidth, 2f), Streak);
    }

    // ---------------------------------------------------------------- widgets

    /// <summary>
    /// A segmented control - the game's own row of choices in one outlined group, as on the
    /// Statistics page's time ranges.
    ///
    /// Hand-drawn rather than <c>GUILayout.Toolbar</c>, which has no way to express "one rounded
    /// group with hairlines between the segments and only the live one filled".
    /// </summary>
    public static int Segmented(Rect area, string[] options, int selected)
    {
        Rounded(area, new Color(1f, 1f, 1f, 0.030f), new Color(1f, 1f, 1f, 0.110f));

        float width = area.width / options.Length;

        for (int i = 0; i < options.Length; i++)
        {
            Rect slot = new Rect(area.x + i * width, area.y, width, area.height);

            if (i > 0)
            {
                Fill(new Rect(Mathf.Round(slot.x), slot.y + 7f, 1f, slot.height - 14f),
                    new Color(1f, 1f, 1f, 0.09f));
            }

            if (i == selected)
            {
                // Inset by two so the selected pill sits inside the group's own border rather
                // than doubling it.
                Rounded(new Rect(slot.x + 2f, slot.y + 2f, slot.width - 4f, slot.height - 4f),
                    ControlOn, ControlOnLine);
            }

            if (GUI.Button(slot, options[i], i == selected ? SegmentOn : SegmentOff))
            {
                selected = i;
            }
        }

        return selected;
    }

    /// <summary>A filled track, for progress that has no honest total (see the managed walk).</summary>
    public static void Meter(Rect area, float fraction, Color colour)
    {
        Rounded(area, new Color(1f, 1f, 1f, 0.07f), new Color(0f, 0f, 0f, 0f), Corner.Tiny);

        float width = Mathf.Max(area.height, area.width * Mathf.Clamp01(fraction));

        Rounded(new Rect(area.x, area.y, width, area.height), colour, new Color(0f, 0f, 0f, 0f), Corner.Tiny);
    }

    /// <summary>
    /// This page's own scrollbar, because IMGUI's is 16 pixels of grey bevel and there is no way
    /// to restyle the thumb without replacing <c>GUI.skin</c>, which is process-wide and shared
    /// with the game's debug console.
    ///
    /// Returns the new scroll offset. Wheel scrolling is still the scroll view's own.
    ///
    /// The control id is taken by the caller, at the top of its OnGUI, rather than here: an id is
    /// just a count of how many controls have asked for one this event, so a scrollbar that asks
    /// after its own content would change id the moment a table gained a row - and a drag in
    /// progress would be dropped, because the hot control no longer matches.
    /// </summary>
    public static float ScrollBar(int id, Rect track, float value, float viewHeight, float contentHeight)
    {
        if (contentHeight <= viewHeight + 1f)
        {
            return 0f;
        }

        float range = contentHeight - viewHeight;
        float thumbHeight = Mathf.Max(36f, track.height * (viewHeight / contentHeight));
        float travel = track.height - thumbHeight;

        value = Mathf.Clamp(value, 0f, range);

        Rect thumb = new Rect(track.x, track.y + travel * (value / range), track.width, thumbHeight);

        Event current = Event.current;

        switch (current.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (thumb.Contains(current.mousePosition) || track.Contains(current.mousePosition))
                {
                    // Clicking the track jumps to that point rather than paging, because the
                    // thumb is only ever a few rows tall on a page this long.
                    value = Mathf.Clamp01((current.mousePosition.y - track.y - thumbHeight / 2f) / travel) * range;
                    GUIUtility.hotControl = id;
                    current.Use();
                }

                break;

            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id)
                {
                    value = Mathf.Clamp01((current.mousePosition.y - track.y - thumbHeight / 2f) / travel) * range;
                    current.Use();
                }

                break;

            case EventType.MouseUp:
                if (GUIUtility.hotControl == id)
                {
                    GUIUtility.hotControl = 0;
                    current.Use();
                }

                break;
        }

        thumb.y = track.y + travel * (value / range);

        bool hot = GUIUtility.hotControl == id || thumb.Contains(current.mousePosition);

        Rounded(track, new Color(1f, 1f, 1f, 0.035f), new Color(0f, 0f, 0f, 0f), Corner.Tiny);
        Rounded(thumb, new Color(1f, 1f, 1f, hot ? 0.34f : 0.18f), new Color(0f, 0f, 0f, 0f), Corner.Tiny);

        return value;
    }

    /// <summary>
    /// Greys out a value's unit so the number reads first. IMGUI's rich text is the cheapest
    /// way to get two colours into one label without splitting it into two columns that then
    /// have to be kept aligned.
    /// </summary>
    public static string Unit(string text)
    {
        int cut = text.LastIndexOf(' ');

        return cut <= 0 ? text : text.Substring(0, cut) + " <color=#93a1b5>" + text.Substring(cut + 1) + "</color>";
    }

    /// <summary>
    /// Colours one run of text. A style carries exactly one colour, and a tile whose value goes
    /// amber when the frame is slow would otherwise need a style per band.
    /// </summary>
    public static string Tint(string text, Color colour)
    {
        return "<color=#" + ColorUtility.ToHtmlStringRGB(colour) + ">" + text + "</color>";
    }

    // ---------------------------------------------------------------- construction

    private static void BuildTextures()
    {
        Pixel = Track(Solid(Color.white));

        // Matched to the Statistics page rather than chosen: sampling its empty ground gives
        // roughly 33,51,87 near the top, 36,55,90 through the middle and a warm 55,49,81 at the
        // bottom. These stops sit a little under that, because the game's own glow layers are
        // drawn over them and carry the rest. Opaque enough that the world reads as inactive,
        // short of opaque so it is still obvious what is behind.
        Backdrop = Track(Gradient(
            new Color(0.115f, 0.175f, 0.300f, 0.955f),
            new Color(0.130f, 0.200f, 0.325f, 0.950f),
            new Color(0.195f, 0.180f, 0.300f, 0.945f)));

        Bloom = Track(GlowField(192, 48));
        Streak = Track(GlowLine(192));

        FillLarge = Track(RoundedMask(24, 8f, 0f));
        RingLarge = Track(RoundedMask(24, 8f, 1f));
        FillSmall = Track(RoundedMask(14, 4f, 0f));
        RingSmall = Track(RoundedMask(14, 4f, 1f));
        FillTiny = Track(RoundedMask(8, 3f, 0f));
        RingTiny = Track(RoundedMask(8, 3f, 1f));
        Caret = Track(Triangle(14, 8));

        FillLargeSlice = Slice(FillLarge, 10);
        RingLargeSlice = Slice(RingLarge, 10);
        FillSmallSlice = Slice(FillSmall, 6);
        RingSmallSlice = Slice(RingSmall, 6);
        FillTinySlice = Slice(FillTiny, 3);
        RingTinySlice = Slice(RingTiny, 3);
    }

    private static void BuildStyles()
    {
        GUIStyle label = GUI.skin.label;

        Title = Text(label, 28, FontStyle.Normal, Ink);
        Title.alignment = TextAnchor.MiddleLeft;

        TabOn = Text(label, 16, FontStyle.Normal, Color.white);
        TabOn.alignment = TextAnchor.MiddleCenter;
        TabOn.hover.textColor = Color.white;

        TabOff = Text(label, 16, FontStyle.Normal, new Color(0.66f, 0.72f, 0.80f));
        TabOff.alignment = TextAnchor.MiddleCenter;
        TabOff.hover.textColor = Ink;

        Close = Text(label, 22, FontStyle.Normal, new Color(0.70f, 0.76f, 0.84f));
        Close.alignment = TextAnchor.MiddleCenter;
        Close.hover.textColor = Color.white;

        Caption = Text(label, 13, FontStyle.Normal, Faint);

        CaptionRight = Text(label, 13, FontStyle.Normal, Faint);
        CaptionRight.alignment = TextAnchor.MiddleRight;

        CardTitle = Text(label, 14, FontStyle.Bold, Ink);

        Label = Text(label, 13, FontStyle.Normal, Muted);
        Label.wordWrap = true;

        LabelRight = Text(label, 13, FontStyle.Normal, Muted);
        LabelRight.alignment = TextAnchor.MiddleRight;

        // Table cells clip rather than wrap. A fully qualified method name is longer than any
        // column that leaves room for its numbers, and a wrapped one silently doubles the
        // height of a row, which is how the first version's tables lost their grid.
        Cell = Text(label, 13, FontStyle.Normal, Muted);
        Cell.clipping = TextClipping.Clip;

        CellMod = Text(label, 13, FontStyle.Normal, Cool);
        CellMod.clipping = TextClipping.Clip;

        Note = Text(label, 13, FontStyle.Normal, Faint);
        Note.wordWrap = true;

        Value = Text(label, 13, FontStyle.Bold, Ink);
        Value.richText = true;

        ValueRight = Text(label, 13, FontStyle.Bold, Ink);
        ValueRight.richText = true;
        ValueRight.alignment = TextAnchor.MiddleRight;

        TileCaption = Text(label, 11, FontStyle.Normal, Faint);
        TileValue = Text(label, 26, FontStyle.Normal, Ink);
        TileValue.richText = true;

        FrameLabel = Text(label, 12, FontStyle.Normal, new Color(0.07f, 0.09f, 0.12f));
        FrameLabel.clipping = TextClipping.Clip;

        Tooltip = Text(label, 12, FontStyle.Normal, new Color(0.88f, 0.93f, 1f));
        Tooltip.padding = new RectOffset(10, 10, 7, 7);
        Tooltip.richText = true;

        Button = Pill(Control, ControlLine, ControlHover, ControlHoverLine, ControlDown, ControlDownLine, Ink);
        ButtonOn = Pill(ControlOn, ControlOnLine, ControlHover, ControlHoverLine, ControlDown, ControlDownLine, Color.white);

        // Text hard left with room on the right for the caret, which is drawn over the top of
        // the button rather than set in it - see DrawCaret for why it is not a character.
        Dropdown = new GUIStyle(Button)
        {
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(13, 28, 7, 8),
        };

        Ghost = Text(label, 13, FontStyle.Normal, Muted);
        Ghost.alignment = TextAnchor.MiddleCenter;
        Ghost.hover.textColor = Ink;
        Ghost.padding = new RectOffset(10, 10, 6, 6);

        SegmentOn = Text(label, 12, FontStyle.Normal, Color.white);
        SegmentOn.alignment = TextAnchor.MiddleCenter;
        SegmentOn.hover.textColor = Color.white;

        SegmentOff = Text(label, 12, FontStyle.Normal, Muted);
        SegmentOff.alignment = TextAnchor.MiddleCenter;
        SegmentOff.hover.textColor = Ink;

        // A label would be dead text and a bordered button would be a box round every row; a
        // flat left-aligned button is the row itself, which is what makes clicking it obvious.
        RowButton = Text(label, 13, FontStyle.Normal, Muted);
        RowButton.alignment = TextAnchor.MiddleLeft;
        RowButton.padding = new RectOffset(10, 6, 3, 3);
        RowButton.hover.background = Track(Solid(new Color(1f, 1f, 1f, 0.07f)));
        RowButton.hover.textColor = Ink;
        RowButton.active.textColor = Color.white;

        Card = Panel(FillLarge, RingLarge, 10, Surface, SurfaceLine);
        Card.padding = new RectOffset(18, 18, 14, 16);
        Card.margin = new RectOffset(0, 0, 0, 14);

        Tile = Panel(FillLarge, RingLarge, 10, Surface, SurfaceLine);
        Tile.padding = new RectOffset(16, 16, 12, 14);
        Tile.margin = new RectOffset(0, 0, 0, 0);

        // A well is a surface *below* the card rather than above it - the ground a graph is
        // drawn on, where a lifted panel would fight the bars for attention.
        Well = Panel(FillLarge, RingLarge, 10, new Color(0f, 0f, 0f, 0.50f), new Color(1f, 1f, 1f, 0.06f));
        Well.padding = new RectOffset(0, 0, 0, 0);

        RowEven = Row(null);
        RowOdd = Row(Track(Solid(new Color(1f, 1f, 1f, 0.026f))));
    }

    private static GUIStyle Text(GUIStyle from, int size, FontStyle weight, Color colour)
    {
        // No horizontal margin, because a table's column widths are worked out in pixels and the
        // default four a side turns into thirty across a five column row - which is how the
        // first version's last column ended up squeezed out of its own card.
        GUIStyle style = new GUIStyle(from)
        {
            fontSize = size,
            fontStyle = weight,
            wordWrap = false,
            margin = new RectOffset(0, 0, 2, 2),
            normal = { textColor = colour },
        };

        if (Face != null)
        {
            style.font = Face;
        }

        return style;
    }

    private static GUIStyle Row(Texture2D background)
    {
        return new GUIStyle
        {
            padding = new RectOffset(10, 10, 4, 4),
            normal = { background = background },
        };
    }

    private static GUIStyle Panel(Texture2D fill, Texture2D ring, int border, Color body, Color edge)
    {
        // The two masks are composited into one background here rather than drawn as two passes,
        // because a GUIStyle has room for exactly one texture and GUILayout owns the draw.
        return new GUIStyle
        {
            normal = { background = Track(Composite(fill, ring, body, edge)) },
            border = new RectOffset(border, border, border, border),
        };
    }

    private static GUIStyle Pill(Color body, Color edge, Color hoverBody, Color hoverEdge,
        Color downBody, Color downEdge, Color textColour)
    {
        GUIStyle style = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 13,
            padding = new RectOffset(14, 14, 7, 8),
            margin = new RectOffset(0, 6, 2, 2),
            border = new RectOffset(6, 6, 6, 6),
            normal = { background = Track(Composite(FillSmall, RingSmall, body, edge)), textColor = textColour },
            hover = { background = Track(Composite(FillSmall, RingSmall, hoverBody, hoverEdge)), textColor = Color.white },
            active = { background = Track(Composite(FillSmall, RingSmall, downBody, downEdge)), textColor = Color.white },
        };

        style.focused = style.normal;
        style.onNormal = style.normal;
        style.onHover = style.hover;
        style.onActive = style.active;

        if (Face != null)
        {
            style.font = Face;
        }

        return style;
    }

    private static GUIStyle Slice(Texture2D texture, int border)
    {
        return new GUIStyle
        {
            normal = { background = texture },
            border = new RectOffset(border, border, border, border),
        };
    }

    // ---------------------------------------------------------------- texture generation

    private static Texture2D Solid(Color colour)
    {
        Texture2D texture = New(1, 1);
        texture.SetPixel(0, 0, colour);
        texture.Apply();

        return texture;
    }

    /// <summary>
    /// A three-stop vertical gradient. Row zero is the bottom of the image, so the stops are
    /// written bottom up and land the other way round on screen.
    /// </summary>
    private static Texture2D Gradient(Color top, Color middle, Color bottom)
    {
        const int height = 128;

        Texture2D texture = New(1, height);
        texture.filterMode = FilterMode.Bilinear;

        for (int y = 0; y < height; y++)
        {
            float t = y / (height - 1f);

            texture.SetPixel(0, y, t < 0.5f
                ? Color.Lerp(bottom, middle, t * 2f)
                : Color.Lerp(middle, top, (t - 0.5f) * 2f));
        }

        texture.Apply();

        return texture;
    }

    /// <summary>The soft bloom behind the live tab's underline: a gaussian in both axes.</summary>
    private static Texture2D GlowField(int width, int height)
    {
        Texture2D texture = New(width, height);
        texture.filterMode = FilterMode.Bilinear;

        for (int y = 0; y < height; y++)
        {
            float v = y / (height - 1f) * 2f - 1f;
            float across = Mathf.Exp(-7f * v * v);

            for (int x = 0; x < width; x++)
            {
                float u = x / (width - 1f) * 2f - 1f;
                float along = Mathf.Exp(-5f * u * u);
                float a = along * across;

                Color colour = Color.Lerp(Accent, AccentHot, a * a);
                colour.a = a * 0.55f;

                texture.SetPixel(x, y, colour);
            }
        }

        texture.Apply();

        return texture;
    }

    /// <summary>The bright core of the underline - opaque in the middle, gone at the ends.</summary>
    private static Texture2D GlowLine(int width)
    {
        Texture2D texture = New(width, 1);
        texture.filterMode = FilterMode.Bilinear;

        for (int x = 0; x < width; x++)
        {
            float u = x / (width - 1f) * 2f - 1f;
            float a = Mathf.Clamp01(Mathf.Exp(-4.5f * u * u) * 1.45f);

            Color colour = Color.Lerp(Accent, AccentHot, Mathf.Clamp01(a * 1.3f - 0.3f));
            colour.a = a;

            texture.SetPixel(x, 0, colour);
        }

        texture.Apply();

        return texture;
    }

    /// <summary>
    /// A white rounded-rectangle mask, antialiased from a signed distance field: the whole shape
    /// when <paramref name="lineWidth"/> is zero, a ring of that width otherwise.
    ///
    /// Square and nine-sliced rather than drawn at size, so one texture serves every rectangle on
    /// the page - the corners are copied unscaled and the middle is stretched.
    /// </summary>
    private static Texture2D RoundedMask(int size, float radius, float lineWidth)
    {
        Texture2D texture = New(size, size);

        float half = size / 2f;
        float inner = half - radius;
        Color[] pixels = new Color[size * size];

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float px = Mathf.Abs(x + 0.5f - half) - inner;
                float py = Mathf.Abs(y + 0.5f - half) - inner;

                float ox = Mathf.Max(px, 0f);
                float oy = Mathf.Max(py, 0f);

                float distance = Mathf.Sqrt(ox * ox + oy * oy)
                                 + Mathf.Min(Mathf.Max(px, py), 0f)
                                 - radius;

                float outer = Mathf.Clamp01(0.5f - distance);
                float alpha = lineWidth <= 0f
                    ? outer
                    : Mathf.Max(outer - Mathf.Clamp01(0.5f - (distance + lineWidth)), 0f);

                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();

        return texture;
    }

    /// <summary>A white downward triangle, antialiased along its two sloping edges.</summary>
    private static Texture2D Triangle(int width, int height)
    {
        Texture2D texture = New(width, height);
        texture.filterMode = FilterMode.Bilinear;

        float half = width / 2f;
        Color[] pixels = new Color[width * height];

        for (int y = 0; y < height; y++)
        {
            // Row zero is the bottom of the image, which is the point of the caret.
            float span = half * (y / (height - 1f));

            for (int x = 0; x < width; x++)
            {
                float alpha = Mathf.Clamp01(span - Mathf.Abs(x + 0.5f - half) + 0.5f);

                pixels[y * width + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();

        return texture;
    }

    /// <summary>Tints a fill mask and a ring mask and lays one over the other, straight alpha.</summary>
    private static Texture2D Composite(Texture2D fill, Texture2D ring, Color body, Color edge)
    {
        int size = fill.width;

        Texture2D texture = New(size, size);
        Color[] fills = fill.GetPixels();
        Color[] rings = ring.GetPixels();
        Color[] pixels = new Color[fills.Length];

        for (int i = 0; i < pixels.Length; i++)
        {
            float bodyAlpha = fills[i].a * body.a;
            float edgeAlpha = rings[i].a * edge.a;

            // The edge sits on top, so it takes its share first and the body fills what is left.
            float under = bodyAlpha * (1f - edgeAlpha);
            float alpha = edgeAlpha + under;

            pixels[i] = alpha <= 0.0001f
                ? new Color(0f, 0f, 0f, 0f)
                : new Color(
                    (edge.r * edgeAlpha + body.r * under) / alpha,
                    (edge.g * edgeAlpha + body.g * under) / alpha,
                    (edge.b * edgeAlpha + body.b * under) / alpha,
                    alpha);
        }

        texture.SetPixels(pixels);
        texture.Apply();

        return texture;
    }

    private static Texture2D New(int width, int height)
    {
        return new Texture2D(width, height, TextureFormat.ARGB32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave,
        };
    }

    private static Texture2D Track(Texture2D texture)
    {
        Owned.Add(texture);

        return texture;
    }
}

/// <summary>
/// Finds a typeface that belongs to the game.
///
/// IMGUI needs a <see cref="Font"/>, and every string the game draws goes through TextMeshPro,
/// which uses a <c>TMP_FontAsset</c> - a baked atlas, not a font. The one bridge between them is
/// <c>TMP_FontAsset.sourceFontFile</c>, the TTF the atlas was generated from, which a build keeps
/// only if the asset was imported with its font data included. So this is a probe, not a
/// guarantee: if a source font is there the page is drawn in the game's own type, and if it is
/// not the page is drawn in IMGUI's built-in face and looks no worse than it did before.
///
/// The result is reported through <c>prof.fonts</c>, and <c>prof.font</c> overrides it, because
/// which of several candidates is the right one is a judgement that needs eyes on the screen.
/// </summary>
internal static class PanelFont
{
    private static bool Probed;

    /// <summary>Every dynamic font in the process, TMP source files first.</summary>
    public static List<Font> Candidates()
    {
        List<Font> found = new List<Font>();

        try
        {
            foreach (TMP_FontAsset asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                Font source = asset == null ? null : asset.sourceFontFile;

                if (Usable(source) && !found.Contains(source))
                {
                    found.Add(source);
                }
            }

            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (Usable(font) && !found.Contains(font))
                {
                    found.Add(font);
                }
            }
        }
        catch (System.Exception)
        {
            // A missing TextMeshPro or a stripped type is a reason to use the default face,
            // not a reason for the page to fail to draw.
        }

        return found;
    }

    /// <summary>
    /// Picks a face once, on the first draw. Only a TMP source font is taken automatically -
    /// that one is definitionally the game's UI typeface, where an arbitrary <c>Font</c> lying
    /// around the process is just as likely to be a debug overlay's.
    /// </summary>
    public static string Probe()
    {
        if (Probed)
        {
            return null;
        }

        Probed = true;

        try
        {
            foreach (TMP_FontAsset asset in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                Font source = asset == null ? null : asset.sourceFontFile;

                if (!Usable(source))
                {
                    continue;
                }

                PanelTheme.SetFont(source);

                return source.name;
            }
        }
        catch (System.Exception)
        {
            // As above.
        }

        return null;
    }

    /// <summary>Applies a named face, or the built-in one for "default". Returns what happened.</summary>
    public static string Apply(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "default")
        {
            PanelTheme.SetFont(null);

            return "Using IMGUI's built-in font.";
        }

        foreach (Font font in Candidates())
        {
            if (font.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            PanelTheme.SetFont(font);

            return "Panel font is now " + font.name + ".";
        }

        return "No loaded font matches '" + name + "'. Run prof.fonts for the list.";
    }

    /// <summary>
    /// A non-dynamic font carries a fixed atlas of whatever characters it was built with, so
    /// asking it for a size it does not have renders nothing. Only dynamic faces can be used.
    /// </summary>
    private static bool Usable(Font font)
    {
        return font != null && font.dynamic;
    }
}
