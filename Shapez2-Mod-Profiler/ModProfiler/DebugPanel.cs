using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace QuinnBast.Shapez2.ModProfiler;

/// <summary>
/// The profiler page: a modal screen with tabs, laid out like one of the game's own full
/// screen pages - a title top left, a centred tab row with the live one underlined in warm
/// light, a rule across the screen, then cards of content - and drawn with IMGUI.
///
/// **Why IMGUI and not a HUDPart.** A real screen means the game's prefab and dependency
/// injection machinery: <c>[Construct]</c> that never runs on a runtime clone,
/// <c>HUDLocalizedText</c> throwing because its resolver is null, layout groups to fight. That
/// is where the hours went on the last two HUD additions, and for a page whose whole job is
/// dense tables of numbers it buys nothing back.
///
/// What that trade used to cost was the look, and it no longer has to: everything the game's
/// pages are made of - the translucent ground, soft-cornered surfaces, one warm accent for
/// selection, a hairline scrollbar - is a texture, and <see cref="PanelTheme"/> generates them.
/// Nothing on this page uses IMGUI's default skin.
///
/// The behaviour that matters is reproduced exactly: the page stops the world behind it and
/// closes on Escape. See <see cref="PanelInput"/> and <see cref="SyncBlocker"/>.
///
/// The top bar button that opens it <em>is</em> a real game button, so the page is found the
/// way the rest of the game is found.
/// </summary>
public class DebugPanel : MonoBehaviour
{
    private enum Tab
    {
        Overview,
        Memory,
        Cpu,
    }

    private static readonly string[] TabNames = { "OVERVIEW", "MEMORY", "CPU" };
    private static readonly string[] ScopeNames = { "Mods only", "Everything" };

    /// <summary>
    /// How long Record runs before stopping itself, and what the buttons say. Two minutes is the
    /// default because the failure this prevents is pressing Record and walking away: the weave
    /// keeps costing two timestamps a call, and the tree keeps growing, until somebody stops it.
    /// </summary>
    private static readonly string[] LimitNames = { "30 s", "2 min", "5 min", "No limit" };

    private static readonly float[] LimitValues = { 30f, 120f, 300f, 0f };

    private CounterFeed Feed;
    private ProfilerSession Session;
    private readonly ObjectCensus Census = new ObjectCensus();
    private ManagedCensus Managed;

    private Tab Current = Tab.Overview;

    private CallRecorder.Node Focus;
    private bool ShowAllTypes;
    private bool ShowAllManaged;

    private bool PickerOpen;
    private Rect PickerAnchor;
    private string SelectedMod;
    private List<string> Lines = new List<string>();

    private string AssemblyFilter;

    // A mod's own objects are the only ones this panel can do anything about, and the full table
    // is four hundred rows of the game's. Start where the answer is.
    private bool ModsOnly = true;

    private int LimitChoice = 1;
    private string ExportMessage;

    private string HoverText;
    private Vector2 HoverPoint;

    private GameObject Blocker;

    private Vector2 Scroll;
    private float ContentHeight;

    public bool Open { get; private set; }

    /// <summary>
    /// Creates the panel, or returns null if this build cannot host one.
    ///
    /// **Null is a real outcome after a hot reload**, not a defensive flourish. Mod Reloader
    /// byte-loads a rebuilt assembly - the same property behind the `ModDirectoryLocator`
    /// trap, where a byte-loaded assembly has an empty `Location` - and Unity will not add a
    /// MonoBehaviour whose type comes from one. `AddComponent` simply returns null.
    ///
    /// Assigning through that null took the whole mod down the first time it happened: the
    /// constructor threw, the reloader reported the entry point as dead, and every command
    /// went with it. So the panel is allowed to be absent, and everything that does not need
    /// a MonoBehaviour keeps working.
    /// </summary>
    public static DebugPanel Attach(CounterFeed feed, ProfilerSession session, ManagedCensus managed)
    {
        GameObject host = new GameObject("ModProfilerPanel");
        DontDestroyOnLoad(host);

        DebugPanel panel = host.AddComponent<DebugPanel>();

        if (panel == null)
        {
            Destroy(host);
            return null;
        }

        panel.Feed = feed;
        panel.Session = session;
        panel.Managed = managed;

        return panel;
    }

    public void Toggle()
    {
        Open = !Open;

        // Retried on every open until it succeeds: the game's background only exists inside a
        // session, and the panel can be opened before one has started.
        if (Open && !GameBackdrop.Available)
        {
            GameBackdrop.Invalidate();
            GameBackdrop.Resolve();
        }

        SyncBlocker();
    }

    public void Close()
    {
        if (!Open)
        {
            return;
        }

        Open = false;
        PickerOpen = false;
        SyncBlocker();
    }

    /// <summary>
    /// Raises and lowers a transparent full-screen uGUI graphic while the panel is open.
    ///
    /// <see cref="PanelInput"/> stops the *game* reading input; this stops the game's *UI*
    /// reading it. They are two different systems: the toolbar, the top bar and every button
    /// on them are uGUI, routed by an EventSystem raycast that knows nothing about either the
    /// input context or IMGUI. Without this, buttons behind the panel are still clickable
    /// through it.
    ///
    /// A canvas of our own with a high sorting order wins that raycast, and one Image with
    /// <c>raycastTarget</c> set swallows the event. It is invisible on purpose - the backdrop
    /// is drawn in IMGUI instead, where it is certain to sit under our own page rather than
    /// over it; the two systems' draw order is not worth betting a blacked-out panel on.
    /// </summary>
    private void SyncBlocker()
    {
        try
        {
            if (Blocker == null && Open)
            {
                Blocker = new GameObject("ModProfilerInputBlocker");
                Blocker.transform.SetParent(transform, false);

                Canvas canvas = Blocker.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 30000;

                Blocker.AddComponent<GraphicRaycaster>();

                GameObject sheet = new GameObject("Sheet");
                sheet.transform.SetParent(Blocker.transform, false);

                Image image = sheet.AddComponent<Image>();
                image.color = new Color(0f, 0f, 0f, 0f);
                image.raycastTarget = true;

                RectTransform rect = image.rectTransform;
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }

            if (Blocker != null)
            {
                Blocker.SetActive(Open);
            }
        }
        catch (Exception)
        {
            // Losing the blocker costs click-through behind the panel, not the panel.
        }
    }

    private void Update()
    {
        // Sampled whether or not the panel is showing: the rates and the frame history are
        // only meaningful as a continuous series, and one that restarts every time the page
        // is opened would report a spike that is really just a cold start.
        Feed?.Sample(Time.unscaledDeltaTime);

        // The heap walk runs here, a slice at a time, rather than inside the button that
        // started it. A whole walk in one call is several seconds of frozen game; six
        // milliseconds a frame is a scan that finishes while the game keeps running.
        if (Managed != null && Managed.Running)
        {
            Managed.Step(6);
        }

        // A recording that has run out of time stops here rather than in the panel's draw,
        // because it has to happen whether or not anybody is looking at the page.
        IEnumerable<string> stopped = Session?.Tick();

        if (stopped != null)
        {
            Lines = new List<string>(stopped);
        }
    }

    private void OnGUI()
    {
        if (!Open || Feed == null)
        {
            return;
        }

        PanelTheme.Ensure();
        PanelFont.Probe();

        HoverText = null;

        // A fallback for the game binding PanelInput consumes: if this build calls cancel
        // something other than "global.cancel", Escape still closes the panel.
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        {
            Close();
            Event.current.Use();
            return;
        }

        PanelTheme.DrawBackdrop();

        // Claimed here, before anything else asks, so the scrollbar keeps the same id however
        // many rows the page below it grows or loses. See PanelTheme.ScrollBar.
        int scrollId = GUIUtility.GetControlID(FocusType.Passive);

        // Held to a readable measure rather than stretched across an ultrawide, which is what
        // the game's own pages do with their content.
        float margin = Mathf.Max(52f, (Screen.width - 1560f) / 2f);
        float width = Screen.width - margin * 2f;

        if (DrawHeader(margin))
        {
            return;
        }

        Rect toolbar = new Rect(margin, PanelTheme.HeaderHeight + 12f, width,
            PanelTheme.ToolbarHeight);
        DrawToolbar(toolbar);

        // The open dropdown takes its clicks here, before the content under it is laid out, and
        // is painted at the end. Immediate mode has one pass for both, so a floating menu has to
        // be split in two or it hands every click to whatever it is covering as well.
        if (PickerOpen)
        {
            HandlePicker();
        }

        float top = toolbar.yMax + 16f;
        Rect body = new Rect(margin, top, width - PanelTheme.Gutter, Screen.height - top - 26f);

        GUILayout.BeginArea(body);
        Scroll = GUILayout.BeginScrollView(Scroll, GUIStyle.none, GUIStyle.none);
        GUILayout.BeginVertical();

        switch (Current)
        {
            case Tab.Memory:
                DrawGarbageCollector();
                DrawManagedObjects(body.width);
                DrawObjects(body.width);
                break;

            case Tab.Cpu:
                DrawRecorderStatus();
                DrawFlameGraph(body.width);
                DrawHotMethods(body.width);
                break;

            default:
                DrawTiles(body.width);
                DrawFrameGraph();

                foreach (string section in Feed.SectionNames)
                {
                    DrawSection(section, body.width);
                }

                break;
        }

        GUILayout.EndVertical();

        if (Event.current.type == EventType.Repaint)
        {
            ContentHeight = GUILayoutUtility.GetLastRect().yMax;
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();

        Scroll.y = PanelTheme.ScrollBar(scrollId, new Rect(body.xMax + 6f, body.y, 6f, body.height),
            Scroll.y, body.height, ContentHeight);

        if (PickerOpen)
        {
            PaintPicker();
        }

        DrawTooltip();
    }

    // ------------------------------------------------------------------ chrome

    /// <summary>
    /// Title, tabs and the actions that belong to the page rather than to a tab. Returns true if
    /// the page closed, because everything after it would then be drawing into a dead frame.
    /// </summary>
    private bool DrawHeader(float margin)
    {
        GUI.Label(new Rect(margin, 24f, 520f, 46f), "Mod Profiler", PanelTheme.Title);

        float rule = PanelTheme.HeaderHeight;
        PanelTheme.Rule(0f, rule, Screen.width);

        // Measured rather than fixed-width, so a longer tab name does not crowd its neighbours
        // and the row stays centred on the screen the way the game's does.
        float[] widths = new float[TabNames.Length];
        float total = 0f;

        for (int i = 0; i < TabNames.Length; i++)
        {
            widths[i] = Mathf.Round(PanelTheme.TabOn.CalcSize(new GUIContent(TabNames[i])).x) + 56f;
            total += widths[i];
        }

        float x = Mathf.Round((Screen.width - total) / 2f);

        for (int i = 0; i < TabNames.Length; i++)
        {
            Rect slot = new Rect(x, 28f, widths[i], 50f);
            bool active = (int)Current == i;

            if (active)
            {
                PanelTheme.DrawTabGlow(slot.center.x, rule, slot.width);
            }

            if (GUI.Button(slot, TabNames[i], active ? PanelTheme.TabOn : PanelTheme.TabOff))
            {
                Current = (Tab)i;
                Scroll = Vector2.zero;
                PickerOpen = false;
            }

            x += widths[i];
        }

        Rect close = new Rect(Screen.width - margin - 36f, 28f, 36f, 36f);

        // The multiplication sign, not an ex: it is in Latin-1, so every face has it, and it is
        // symmetrical where a letter is not.
        if (GUI.Button(close, "×", PanelTheme.Close))
        {
            Close();
            return true;
        }

        Rect export = new Rect(close.x - 114f, 31f, 102f, 30f);

        if (GUI.Button(export, "Export", PanelTheme.Button))
        {
            Export();
        }

        GUI.Label(new Rect(export.x - 210f, 33f, 200f, 26f), "Esc to close", PanelTheme.CaptionRight);

        return false;
    }

    /// <summary>
    /// The strip under the rule: what this tab is, on the left, and the one control that acts on
    /// the whole tab, on the right. The same shape as the game's own pages, where the range
    /// selector sits opposite the section name.
    /// </summary>
    private void DrawToolbar(Rect area)
    {
        string caption;

        switch (Current)
        {
            case Tab.Memory:
                caption = ExportMessage
                          ?? "The collector's numbers, this process's managed objects, and the engine's";
                break;

            case Tab.Cpu:
                caption = ExportMessage
                          ?? "Weave one mod's methods, play, and see where its frames went";
                break;

            default:
                caption = ExportMessage
                          ?? "Frame time, and the counters a release player still feeds";
                break;
        }

        // The CPU tab's controls are the widest on the page, so its caption gets less room.
        float room = Mathf.Max(area.width - (Current == Tab.Cpu ? 700f : 420f), 200f);

        GUI.Label(new Rect(area.x, area.y, room, area.height), caption,
            ExportMessage == null ? PanelTheme.Caption : PanelTheme.Note);

        switch (Current)
        {
            case Tab.Memory:
                ModsOnly = PanelTheme.Segmented(Right(area, 240f), ScopeNames, ModsOnly ? 0 : 1) == 0;
                break;

            case Tab.Cpu:
                DrawRecorderControls(area);
                break;

            default:
                if (GUI.Button(Right(area, 130f), "Reset stats", PanelTheme.Button))
                {
                    Feed.ResetStatistics();
                }

                break;
        }
    }

    private static Rect Right(Rect area, float width)
    {
        return new Rect(area.xMax - width, area.y, width, area.height);
    }

    // ------------------------------------------------------------------ recording

    /// <summary>
    /// Start and stop a recording without leaving the page.
    ///
    /// The console commands still exist and still work; this is the same two calls behind a
    /// picker, because choosing from a list of the mods actually loaded beats typing a name
    /// that has to match one.
    /// </summary>
    private void DrawRecorderControls(Rect area)
    {
        IReadOnlyList<string> mods = ModAssemblies.All;

        if (mods.Count == 0)
        {
            GUI.Label(Right(area, 380f), "No mod assemblies are loaded.", PanelTheme.CaptionRight);
            return;
        }

        if (SelectedMod == null || !Contains(mods, SelectedMod))
        {
            SelectedMod = mods[0];
        }

        bool recording = Session != null && Session.Recording;

        Rect action = Right(area, 108f);

        if (recording)
        {
            if (GUI.Button(action, "Stop", PanelTheme.ButtonOn))
            {
                Lines = new List<string>(Session.StopRecording());
                PickerOpen = false;
            }
        }
        else if (GUI.Button(action, "Record", PanelTheme.Button))
        {
            Session.LimitSeconds = LimitValues[LimitChoice];
            Lines = new List<string>(Session.Record(SelectedMod, ProfilerSession.DefaultBudget));
            PickerOpen = false;
        }

        PickerAnchor = new Rect(action.x - 274f, area.y, 264f, area.height);

        if (GUI.Button(PickerAnchor, SelectedMod, PanelTheme.Dropdown))
        {
            PickerOpen = !PickerOpen;
        }

        PanelTheme.DrawCaret(new Rect(PickerAnchor.xMax - 24f, PickerAnchor.y + area.height / 2f - 2f, 11f, 6f),
            PanelTheme.Muted);

        Rect limit = new Rect(PickerAnchor.x - 250f, area.y, 240f, area.height);

        if (!recording)
        {
            LimitChoice = PanelTheme.Segmented(limit, LimitNames, LimitChoice);
            return;
        }

        // While it runs, the slot the limit picker was in says how long is left, because the
        // whole point of the limit is that somebody who walked away can see it counting down.
        // Tested against the limit rather than against the remaining time: Remaining is also zero
        // for the frame between the deadline passing and Update noticing, which would otherwise
        // flash "No limit" at exactly the wrong moment.
        GUI.Label(limit, Session.LimitSeconds <= 0f
            ? "No limit - press Stop"
            : "Stops in " + Clock(Session.Remaining), PanelTheme.CaptionRight);
    }

    /// <summary>Seconds as m:ss, which is how long a capture is talked about.</summary>
    private static string Clock(double seconds)
    {
        int whole = Mathf.CeilToInt((float)seconds);

        return (whole / 60) + ":" + (whole % 60).ToString("00");
    }

    /// <summary>Rows are hit here, before the page under the menu is laid out. See OnGUI.</summary>
    private void HandlePicker()
    {
        IReadOnlyList<string> mods = ModAssemblies.All;
        Rect box = PickerBox(mods.Count);
        Event current = Event.current;

        if (current.type != EventType.MouseDown)
        {
            return;
        }

        if (!box.Contains(current.mousePosition))
        {
            // A click anywhere else closes the menu and is then allowed through, so dismissing
            // it does not also cost the click the user meant to make.
            PickerOpen = false;
            return;
        }

        int index = Mathf.FloorToInt((current.mousePosition.y - box.y - 6f) / 26f);

        if (index >= 0 && index < mods.Count)
        {
            SelectedMod = mods[index];
        }

        PickerOpen = false;
        current.Use();
    }

    private void PaintPicker()
    {
        IReadOnlyList<string> mods = ModAssemblies.All;
        Rect box = PickerBox(mods.Count);

        PanelTheme.Rounded(box, new Color(0.058f, 0.075f, 0.110f, 0.99f), new Color(1f, 1f, 1f, 0.17f));

        for (int i = 0; i < mods.Count; i++)
        {
            Rect row = new Rect(box.x + 5f, box.y + 6f + i * 26f, box.width - 10f, 26f);

            if (row.Contains(Event.current.mousePosition))
            {
                PanelTheme.Rounded(row, new Color(1f, 1f, 1f, 0.07f), new Color(0f, 0f, 0f, 0f),
                    PanelTheme.Corner.Small);
            }

            GUI.Label(row, "  " + mods[i], mods[i] == SelectedMod ? PanelTheme.Value : PanelTheme.Cell);
        }
    }

    private Rect PickerBox(int count)
    {
        return new Rect(PickerAnchor.x, PickerAnchor.yMax + 6f, PickerAnchor.width, count * 26f + 12f);
    }

    private static bool Contains(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] == value)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ overview

    /// <summary>
    /// The four numbers worth reading without scrolling, as tiles.
    ///
    /// A table is the right shape for thirty counters and the wrong shape for the two or three
    /// that answer "is anything wrong right now" - those have to be legible from across the
    /// desk, which is what the game does with its own headline figures.
    /// </summary>
    private void DrawTiles(float width)
    {
        const float gap = 14f;
        float tile = Mathf.Floor((width - gap * 3f) / 4f);

        GUILayout.BeginHorizontal();

        Tile(tile, "FRAME TIME", Feed.FrameLast.ToString("0.0"), "ms", BandOf(Feed.FrameLast));
        GUILayout.Space(gap);

        Tile(tile, "FRAMES PER SECOND",
            (Feed.FrameLast <= 0.01f ? 0f : 1000f / Feed.FrameLast).ToString("0"), "fps", PanelTheme.Ink);
        GUILayout.Space(gap);

        string heap = Bytes(GC.GetTotalMemory(false));
        int cut = heap.LastIndexOf(' ');

        Tile(tile, "MANAGED HEAP", cut < 0 ? heap : heap.Substring(0, cut),
            cut < 0 ? "" : heap.Substring(cut + 1), PanelTheme.Ink);
        GUILayout.Space(gap);

        Tile(tile, "COLLECTIONS PER SECOND", Feed.CollectionRate.ToString("0.00"), "/s", PanelTheme.Ink);

        GUILayout.EndHorizontal();
        GUILayout.Space(14f);
    }

    private static void Tile(float width, string caption, string value, string unit, Color colour)
    {
        GUILayout.BeginVertical(PanelTheme.Tile, GUILayout.Width(width));

        GUILayout.Label(caption, PanelTheme.TileCaption);
        GUILayout.Space(2f);
        GUILayout.Label(PanelTheme.Tint(value, colour) + " <size=13><color=#7a869a>" + unit + "</color></size>",
            PanelTheme.TileValue);

        GUILayout.EndVertical();
    }

    private static Color BandOf(float milliseconds)
    {
        return milliseconds > 33.3f
            ? PanelTheme.Bad
            : milliseconds > 16.7f
                ? PanelTheme.Warn
                : PanelTheme.Good;
    }

    /// <summary>
    /// Frame time, as a graph and as three numbers.
    ///
    /// The graph draws on a recessed well with the two lines that matter marked: 16.7 ms, which
    /// is 60 frames a second, and 33.3 ms, which is 30. A bar's colour says which band it is in,
    /// so a stutter shows up without comparing heights.
    ///
    /// The numbers are there because a graph is a window and a spike leaves it. Min, mean and
    /// max hold since the last reset, so a stutter that happened while you were looking
    /// somewhere else is still on the page.
    /// </summary>
    private void DrawFrameGraph()
    {
        BeginCard("Frame time");

        GUILayout.BeginHorizontal();
        GUILayout.Label("Worst frame per " + (CounterFeed.BucketSeconds * 1000f).ToString("0")
                        + " ms bucket, last " + (CounterFeed.WindowSeconds / 60f).ToString("0")
                        + " minute", PanelTheme.Cell);
        GUILayout.FlexibleSpace();
        Stat("min", Feed.FrameMin);
        Stat("mean", Feed.FrameMean);
        Stat("max", Feed.FrameMax);
        GUILayout.EndHorizontal();

        GUILayout.Space(10f);

        Rect area = GUILayoutUtility.GetRect(10f, 132f);

        PanelTheme.Rounded(area, new Color(0f, 0f, 0f, 0.32f), new Color(1f, 1f, 1f, 0.06f));

        Rect plot = new Rect(area.x + 2f, area.y + 2f, area.width - 4f, area.height - 4f);

        // A fixed ceiling rather than an auto-scaled one: the question a frame graph is asked
        // is "are we missing frames", and a scale that moves with the data hides exactly that.
        const float ceiling = 40f;

        DrawGridline(plot, 16.7f, ceiling, "16.7 ms  ·  60 fps");
        DrawGridline(plot, 33.3f, ceiling, "33.3 ms  ·  30 fps");

        float step = plot.width / CounterFeed.HistoryLength;

        for (int i = 0; i < CounterFeed.HistoryLength; i++)
        {
            float ms = Feed.FrameAt(i);

            if (ms <= 0f)
            {
                continue;
            }

            float height = Mathf.Clamp01(ms / ceiling) * plot.height;

            PanelTheme.Fill(new Rect(plot.x + i * step, plot.y + plot.height - height,
                Mathf.Max(step - 1f, 1f), height), BandOf(ms));
        }

        GUILayout.Space(4f);

        GUILayout.BeginHorizontal();
        // Spelt out rather than drawn with an arrow: U+2190 is outside Latin-1 and a face that
        // lacks it renders a box.
        GUILayout.Label((CounterFeed.WindowSeconds / 60f).ToString("0") + " minute ago",
            PanelTheme.Caption);
        GUILayout.FlexibleSpace();
        GUILayout.Label("now", PanelTheme.CaptionRight);
        GUILayout.EndHorizontal();

        EndCard();
    }

    private static void Stat(string name, float milliseconds)
    {
        GUILayout.Label(name, PanelTheme.Caption, GUILayout.Width(name == "mean" ? 38f : 28f));
        GUILayout.Label(milliseconds.ToString("0.0") + " <color=#7a869a>ms</color>", PanelTheme.Value,
            GUILayout.Width(66f));
    }

    private static void DrawGridline(Rect area, float milliseconds, float ceiling, string label)
    {
        float y = Mathf.Round(area.y + area.height - Mathf.Clamp01(milliseconds / ceiling) * area.height);

        PanelTheme.Fill(new Rect(area.x, y, area.width, 1f), new Color(1f, 1f, 1f, 0.13f));
        GUI.Label(new Rect(area.x + 8f, y - 17f, 240f, 16f), label, PanelTheme.Caption);
    }

    private void DrawSection(string section, float width)
    {
        BeginCard(section);

        const float numbers = 108f;
        float name = NameColumn(width, numbers, 4);

        GUILayout.BeginHorizontal(PanelTheme.RowEven);
        GUILayout.Label("Counter", PanelTheme.Caption, GUILayout.Width(name));
        Head("now", numbers);
        Head("min", numbers);
        Head("mean", numbers);
        Head("max", numbers);
        GUILayout.EndHorizontal();

        int index = 0;

        foreach (CounterFeed.Gauge gauge in Feed.Section(section))
        {
            GUILayout.BeginHorizontal(index++ % 2 == 1 ? PanelTheme.RowOdd : PanelTheme.RowEven);
            GUILayout.Label(gauge.Label, PanelTheme.Cell, GUILayout.Width(name));

            if (!gauge.Live)
            {
                GUILayout.Label("not exposed by this build", PanelTheme.Note);
                GUILayout.EndHorizontal();
                continue;
            }

            GUILayout.Label(PanelTheme.Unit(Format(gauge, gauge.Value)), PanelTheme.ValueRight,
                GUILayout.Width(numbers));
            GUILayout.Label(PanelTheme.Unit(Format(gauge, gauge.Min)), PanelTheme.LabelRight,
                GUILayout.Width(numbers));
            GUILayout.Label(PanelTheme.Unit(Format(gauge, (long)gauge.Mean)), PanelTheme.LabelRight,
                GUILayout.Width(numbers));
            GUILayout.Label(PanelTheme.Unit(Format(gauge, gauge.Max)), PanelTheme.LabelRight,
                GUILayout.Width(numbers));
            GUILayout.EndHorizontal();
        }

        EndCard();
    }

    private static void Head(string text, float width)
    {
        GUILayout.Label(text, PanelTheme.CaptionRight, GUILayout.Width(width));
    }

    /// <summary>
    /// What is left for the name column once the numbers have taken theirs.
    ///
    /// Thirty-six for the card's own padding and twenty for the row's. Both are set on styles
    /// rather than guessed, so the only way to get this wrong is to change one and not the other.
    /// </summary>
    private static float NameColumn(float width, float numbers, int columns)
    {
        return Mathf.Max(width - 56f - numbers * columns, 160f);
    }

    // ------------------------------------------------------------------ memory

    private void DrawGarbageCollector()
    {
        BeginCard("Garbage collector");

        int index = 0;

        Row("Managed heap", Bytes(GC.GetTotalMemory(false)), index++);

        // Boehm's own numbers, straight off the collector, when the runtime exports them.
        // GC.GetTotalMemory is derived from these two; having both makes it obvious how much
        // of the heap is reserved-but-free, which is the difference between "the mod is
        // leaking" and "the collector has not handed the pages back".
        MonoRuntime.Bind();

        if (MonoRuntime.CanReadHeapSize)
        {
            Row("Boehm heap reserved", Bytes(MonoRuntime.HeapSize()), index++);
            Row("Boehm heap in use", Bytes(MonoRuntime.UsedSize()), index++);
        }

        Row("Collections", GC.CollectionCount(0).ToString("N0"), index++);
        Row("Collections / sec", Feed.CollectionRate.ToString("0.00"), index++);
        Row("Heap growth / sec", Bytes((long)Feed.AllocationRate), index);

        GUILayout.Space(10f);
        GUILayout.Label("Heap growth is measured from the sawtooth between collections, because this "
                        + "build exposes no allocation counter: GC.GetAllocatedBytesForCurrentThread is "
                        + "frozen at zero, and neither 'GC Allocated In Frame' nor 'Scripts / GC.Alloc' "
                        + "is fed. It is process-wide and cannot be attributed to a mod.", PanelTheme.Note);

        EndCard();
    }

    /// <summary>
    /// The managed heap by assembly and by type - the half of memory that can be a mod's.
    ///
    /// Assembly first, mods at the top, because the question is "how much of this is mine".
    /// The walk runs a slice per frame, so the button starts it rather than performing it, and
    /// the status line is live while it runs.
    /// </summary>
    private void DrawManagedObjects(float width)
    {
        BeginCard("Managed objects");

        if (Managed == null)
        {
            EndCard();
            return;
        }

        GUILayout.BeginHorizontal();

        if (Managed.Running)
        {
            if (GUILayout.Button("Cancel", PanelTheme.ButtonOn, GUILayout.Width(118f)))
            {
                Managed.Cancel();
            }
        }
        else if (GUILayout.Button(Managed.HasScanned ? "Scan again" : "Scan", PanelTheme.Button,
                     GUILayout.Width(118f)))
        {
            Managed.Begin();
        }

        GUILayout.Space(12f);
        GUILayout.Label(Managed.Status, PanelTheme.Cell);
        GUILayout.EndHorizontal();

        if (Managed.Running)
        {
            GUILayout.Space(8f);

            // There is no total to measure against - the size of the heap is the thing being
            // discovered - so this shows the share of known work done, not a true fraction.
            float share = Managed.Visited + Managed.Queued == 0
                ? 0f
                : Managed.Visited / (float)(Managed.Visited + Managed.Queued);

            PanelTheme.Meter(GUILayoutUtility.GetRect(10f, 6f), share, PanelTheme.Cool);
        }

        if (!Managed.HasScanned)
        {
            GUILayout.Space(8f);
            GUILayout.Label("Every C# object reachable from a static field or from a live UnityEngine.Object, "
                            + "grouped by the assembly that declares its type. A mod's objects are mostly held "
                            + "by the game - Shifter holds the IMod, the simulation holds whatever the mod "
                            + "registered - so the walk starts everywhere and attributes by type rather than "
                            + "by where it started. Sizes are estimated from field layout; counts are exact.",
                PanelTheme.Note);

            EndCard();
            return;
        }

        if (Managed.Truncated)
        {
            GUILayout.Space(6f);
            GUILayout.Label("The walk hit its cap, so this is a partial count.", PanelTheme.Note);
        }

        GUILayout.Space(14f);

        const float numbers = 120f;
        float name = NameColumn(width, numbers, 2);

        GUILayout.BeginHorizontal(PanelTheme.RowEven);
        GUILayout.Label("Assembly  ·  click to filter the table below", PanelTheme.Caption, GUILayout.Width(name));
        Head("objects", numbers);
        Head("size", numbers);
        GUILayout.EndHorizontal();

        int index = 0;

        foreach (ManagedCensus.Entry entry in Managed.ByAssembly)
        {
            if (ModsOnly && !entry.IsMod)
            {
                continue;
            }

            bool selected = AssemblyFilter == entry.Assembly;

            GUILayout.BeginHorizontal(index++ % 2 == 1 ? PanelTheme.RowOdd : PanelTheme.RowEven);

            GUI.color = selected ? new Color(1f, 0.78f, 0.42f) : Color.white;

            if (GUILayout.Button((entry.IsMod ? "•  " : "    ") + entry.TypeName, PanelTheme.RowButton,
                    GUILayout.Width(name)))
            {
                AssemblyFilter = selected ? null : entry.Assembly;
            }

            GUI.color = Color.white;

            GUILayout.Label(entry.Count.ToString("N0"), PanelTheme.LabelRight, GUILayout.Width(numbers));
            GUILayout.Label(PanelTheme.Unit(Bytes(entry.Bytes)), PanelTheme.ValueRight, GUILayout.Width(numbers));
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(16f);

        List<ManagedCensus.Entry> rows = new List<ManagedCensus.Entry>();

        foreach (ManagedCensus.Entry entry in Managed.Entries)
        {
            if (AssemblyFilter != null && entry.Assembly != AssemblyFilter)
            {
                continue;
            }

            if (ModsOnly && !entry.IsMod)
            {
                continue;
            }

            rows.Add(entry);
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label(AssemblyFilter == null ? "By type" : "By type in " + AssemblyFilter, PanelTheme.Value);

        if (AssemblyFilter != null)
        {
            GUILayout.Space(12f);

            if (GUILayout.Button("Clear filter", PanelTheme.Button, GUILayout.Width(110f)))
            {
                AssemblyFilter = null;
            }
        }

        GUILayout.FlexibleSpace();

        if (rows.Count > 40)
        {
            ShowAllManaged = PanelTheme.Segmented(GUILayoutUtility.GetRect(230f, 28f, GUILayout.Width(230f)),
                new[] { "Top 40", "All " + rows.Count }, ShowAllManaged ? 1 : 0) == 1;
        }

        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        if (rows.Count == 0)
        {
            GUILayout.Label("Nothing matches that filter.", PanelTheme.Note);
            EndCard();
            return;
        }

        GUILayout.BeginHorizontal(PanelTheme.RowEven);
        GUILayout.Label("Type", PanelTheme.Caption, GUILayout.Width(name));
        Head("objects", numbers);
        Head("size", numbers);
        GUILayout.EndHorizontal();

        int limit = ShowAllManaged ? rows.Count : Mathf.Min(40, rows.Count);

        for (int i = 0; i < limit; i++)
        {
            ManagedCensus.Entry entry = rows[i];

            GUILayout.BeginHorizontal(i % 2 == 1 ? PanelTheme.RowOdd : PanelTheme.RowEven);
            GUILayout.Label(entry.TypeName, entry.IsMod ? PanelTheme.CellMod : PanelTheme.Cell,
                GUILayout.Width(name));
            GUILayout.Label(entry.Count.ToString("N0"), PanelTheme.LabelRight, GUILayout.Width(numbers));
            GUILayout.Label(PanelTheme.Unit(Bytes(entry.Bytes)), PanelTheme.ValueRight, GUILayout.Width(numbers));
            GUILayout.EndHorizontal();
        }

        EndCard();
    }

    /// <summary>
    /// The heap by type, largest first - counts and sizes for every live UnityEngine.Object.
    ///
    /// Scanned on demand rather than every frame: the walk covers every object the engine
    /// knows about, and on a large save that is a visible hitch.
    /// </summary>
    private void DrawObjects(float width)
    {
        BeginCard("Unity objects");

        GUILayout.BeginHorizontal();

        if (GUILayout.Button(Census.HasScanned ? "Scan again" : "Scan", PanelTheme.Button, GUILayout.Width(118f)))
        {
            Census.Scan();
        }

        GUILayout.Space(12f);

        if (Census.HasScanned)
        {
            GUILayout.Label(Census.TotalObjects.ToString("N0") + " objects, " + Bytes(Census.TotalBytes)
                            + ", scanned in " + Census.Milliseconds.ToString("0") + " ms", PanelTheme.Cell);
        }

        GUILayout.FlexibleSpace();

        if (Census.HasScanned && Census.Entries.Count > 40)
        {
            ShowAllTypes = PanelTheme.Segmented(GUILayoutUtility.GetRect(230f, 28f, GUILayout.Width(230f)),
                new[] { "Top 40", "All " + Census.Entries.Count }, ShowAllTypes ? 1 : 0) == 1;
        }

        GUILayout.EndHorizontal();

        if (!Census.HasScanned)
        {
            GUILayout.Space(8f);
            GUILayout.Label("Every live UnityEngine.Object - textures, meshes, materials, audio, "
                            + "components - by type, largest first, with the real size the engine reports. "
                            + "This is the engine's half of the heap and nearly all of it is the game's.",
                PanelTheme.Note);

            EndCard();
            return;
        }

        if (Census.SizesUnavailable)
        {
            GUILayout.Space(6f);
            GUILayout.Label("Sizes all came back zero, so Profiler.GetRuntimeMemorySizeLong is present "
                            + "but does nothing in this build - the same way the allocation counter is. "
                            + "The counts below are still real.", PanelTheme.Note);
        }

        GUILayout.Space(12f);

        const float numbers = 120f;
        float name = NameColumn(width, numbers, 2);

        GUILayout.BeginHorizontal(PanelTheme.RowEven);
        GUILayout.Label("Type", PanelTheme.Caption, GUILayout.Width(name));
        Head("objects", numbers);
        Head("size", numbers);
        GUILayout.EndHorizontal();

        int limit = ShowAllTypes ? Census.Entries.Count : Mathf.Min(40, Census.Entries.Count);

        for (int i = 0; i < limit; i++)
        {
            ObjectCensus.Entry entry = Census.Entries[i];

            GUILayout.BeginHorizontal(i % 2 == 1 ? PanelTheme.RowOdd : PanelTheme.RowEven);
            GUILayout.Label(entry.TypeName, PanelTheme.Cell, GUILayout.Width(name));
            GUILayout.Label(entry.Count.ToString("N0"), PanelTheme.LabelRight, GUILayout.Width(numbers));
            GUILayout.Label(PanelTheme.Unit(Bytes(entry.Bytes)), PanelTheme.ValueRight, GUILayout.Width(numbers));
            GUILayout.EndHorizontal();
        }

        EndCard();
    }

    // ------------------------------------------------------------------ cpu

    /// <summary>What the recorder last said, and what it is doing now. The controls are in the toolbar.</summary>
    private void DrawRecorderStatus()
    {
        if (Session == null)
        {
            return;
        }

        BeginCard("Recording");

        bool recording = Session.Recording;

        GUILayout.Label(recording
            ? "Recording " + Session.Target + ". Play through whatever you want to measure, then Stop."
            : "Record weaves enter and exit hooks into the selected assembly, and Stop takes them out "
              + "again. Only that mod's own methods are woven, and methods on generic types cannot be "
              + "hooked at all, so they will be missing.", PanelTheme.Note);

        if (Lines.Count > 0)
        {
            GUILayout.Space(12f);

            // The recorder's own report, kept verbatim in a recessed block: it is the line that
            // says whether the weave found anything, and paraphrasing it has already cost a
            // session once.
            GUILayout.BeginVertical(PanelTheme.Well);
            GUILayout.Space(10f);

            foreach (string line in Lines)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(12f);
                GUILayout.Label(line, PanelTheme.Cell);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(10f);
            GUILayout.EndVertical();
        }

        EndCard();
    }

    /// <summary>
    /// The flame graph: one row per call depth, width proportional to time.
    ///
    /// Unlike the counters and the Unity census, this is entirely your code - these frames are
    /// methods the recorder was compiled into, so every one carries your own type and method
    /// name. Click a frame to zoom into it; hover for the numbers that do not fit on it.
    /// </summary>
    private void DrawFlameGraph(float width)
    {
        BeginCard("Flame graph");

        if (Session == null)
        {
            EndCard();
            return;
        }

        CallRecorder.Node root = CallRecorder.Tree;

        if (root == null || root.Children.Count == 0)
        {
            GUILayout.Label(Session.Recording
                ? "Recording. Stop when you have played through whatever you want to measure."
                : "Nothing recorded yet. Pick a mod in the bar above and press Record, play for a few "
                  + "seconds, then Stop.", PanelTheme.Note);

            EndCard();
            return;
        }

        CallRecorder.Node shown = Focus ?? root;
        long total = TotalOf(shown);

        if (total <= 0)
        {
            GUILayout.Label("The recording captured no time.", PanelTheme.Note);
            EndCard();
            return;
        }

        GUILayout.BeginHorizontal();

        if (Focus != null)
        {
            if (GUILayout.Button("Back", PanelTheme.Button, GUILayout.Width(86f)))
            {
                Focus = Focus.Parent == CallRecorder.Tree ? null : Focus.Parent;
            }

            GUILayout.Space(12f);
        }

        GUILayout.Label(Focus == null
            ? "All frames  ·  " + Milliseconds(total) + " over " + Capture()
            : Session.NameOf(shown.MethodId) + "  ·  " + Milliseconds(total) + " over "
              + shown.Calls.ToString("N0") + " calls", PanelTheme.Cell);

        GUILayout.EndHorizontal();
        GUILayout.Space(10f);

        const float rowHeight = 21f;

        // Sized to the rows that will survive the one-pixel cull rather than to the tree's true
        // depth. A deep capture is mostly frames too narrow to draw, and measuring the tree's true depth
        // left two thirds of the box empty.
        float plot = Mathf.Max(width - 44f, 80f);
        int depth = Mathf.Clamp(VisibleDepth(shown, plot, total, 0), 1, 24);

        Rect area = GUILayoutUtility.GetRect(10f, depth * rowHeight + 8f);

        PanelTheme.Rounded(area, new Color(0f, 0f, 0f, 0.32f), new Color(1f, 1f, 1f, 0.06f));

        Rect inner = new Rect(area.x + 4f, area.y + 4f, area.width - 8f, area.height - 8f);

        DrawFrames(shown, inner, inner.x, inner.y, inner.width, total, rowHeight);

        EndCard();
    }

    /// <summary>
    /// Lays out one node's children across the span its own time earned, starting at its own
    /// left edge.
    ///
    /// **That left edge is the whole point of a flame graph** and it was missing: this took a
    /// width but no origin and restarted every level at <c>area.x</c>, so each row was packed
    /// against the left of the graph instead of sitting under the frame that called it. Widths
    /// were right, positions were not, and the picture said nothing about who called whom.
    ///
    /// The span passed down is the child's full share, not its drawn width - subtracting the
    /// two-pixel gap before recursing shrank every level a little more than the last.
    /// </summary>
    private void DrawFrames(CallRecorder.Node node, Rect area, float left, float y, float width,
        long total, float rowHeight)
    {
        if (y > area.yMax || width < 1f || total <= 0)
        {
            return;
        }

        float x = left;

        foreach (KeyValuePair<int, CallRecorder.Node> pair in node.Children)
        {
            CallRecorder.Node child = pair.Value;
            float span = width * (child.Ticks / (float)total);

            if (span < 1f)
            {
                x += span;
                continue;
            }

            Rect frame = new Rect(x, y, Mathf.Max(span - 2f, 1f), rowHeight - 2f);

            // Square, not rounded: a frame can be one pixel wide, and rounded corners on a
            // shape narrower than their own radius stop being corners and start being the
            // whole shape. The two-pixel gaps are what separate the frames.
            PanelTheme.Fill(frame, ColourFor(child.MethodId));

            // The numbers go on the frame when they fit and in the tooltip when they do not.
            // A truncated name with no timing was the worst of both.
            if (span > 150f)
            {
                GUI.Label(frame, "  " + Short(Session.NameOf(child.MethodId)) + "   " + Milliseconds(child.Ticks),
                    PanelTheme.FrameLabel);
            }
            else if (span > 55f)
            {
                GUI.Label(frame, "  " + Short(Session.NameOf(child.MethodId)), PanelTheme.FrameLabel);
            }

            if (frame.Contains(Event.current.mousePosition))
            {
                HoverText = "<b>" + Session.NameOf(child.MethodId) + "</b>"
                            + "\n" + Milliseconds(child.Ticks) + " total, " + Milliseconds(SelfOf(child)) + " self"
                            + "\n" + child.Calls.ToString("N0") + " calls, "
                            + (child.Ticks * 100f / total).ToString("0.0") + "% of the frame above";

                HoverPoint = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);

                if (Event.current.type == EventType.MouseDown)
                {
                    Focus = child;
                    Event.current.Use();
                }
            }

            DrawFrames(child, area, x, y + rowHeight, span, child.Ticks, rowHeight);
            x += span;
        }
    }

    /// <summary>
    /// The same recording as a list, ordered by self time.
    ///
    /// A flame graph answers "what called what", and is a poor way to answer "what is
    /// expensive" - a method called from six places is six narrow frames, none of them
    /// obviously the problem. Self time summed across the tree is the number that names it.
    /// </summary>
    private void DrawHotMethods(float width)
    {
        CallRecorder.Node root = CallRecorder.Tree;

        if (Session == null || root == null || root.Children.Count == 0)
        {
            return;
        }

        BeginCard("Hottest methods, by self time");

        Dictionary<int, long[]> totals = new Dictionary<int, long[]>();
        Accumulate(root, totals);

        List<KeyValuePair<int, long[]>> ordered = new List<KeyValuePair<int, long[]>>(totals);
        ordered.Sort((a, b) => b.Value[0].CompareTo(a.Value[0]));

        const float numbers = 110f;
        float name = NameColumn(width, numbers, 3);

        GUILayout.BeginHorizontal(PanelTheme.RowEven);
        GUILayout.Label("Method", PanelTheme.Caption, GUILayout.Width(name));
        Head("self", numbers);
        Head("total", numbers);
        Head("calls", numbers);
        GUILayout.EndHorizontal();

        for (int i = 0; i < Mathf.Min(20, ordered.Count); i++)
        {
            KeyValuePair<int, long[]> row = ordered[i];

            GUILayout.BeginHorizontal(i % 2 == 1 ? PanelTheme.RowOdd : PanelTheme.RowEven);
            GUILayout.Label(Session.NameOf(row.Key), PanelTheme.CellMod, GUILayout.Width(name));
            GUILayout.Label(PanelTheme.Unit(Milliseconds(row.Value[0])), PanelTheme.ValueRight,
                GUILayout.Width(numbers));
            GUILayout.Label(PanelTheme.Unit(Milliseconds(row.Value[1])), PanelTheme.LabelRight,
                GUILayout.Width(numbers));
            GUILayout.Label(row.Value[2].ToString("N0"), PanelTheme.LabelRight, GUILayout.Width(numbers));
            GUILayout.EndHorizontal();
        }

        EndCard();
    }

    /// <summary>
    /// How long the recording ran and across how many threads.
    ///
    /// Without it the header is a bare total, and a bare total invites the reasonable question of
    /// whether it is the whole capture or some sample of it. It is the whole capture - and it is
    /// summed across threads, so on a mod whose simulation runs on the pool it can legitimately
    /// exceed the wall clock beside it. Saying both is what makes that read as arithmetic rather
    /// than as a bug.
    /// </summary>
    private static string Capture()
    {
        double seconds = (CallRecorder.StoppedAt - CallRecorder.StartedAt)
                         / (double)System.Diagnostics.Stopwatch.Frequency;

        int threads = CallRecorder.Threads.Count;

        return seconds.ToString("0.0") + " s, " + threads + (threads == 1 ? " thread" : " threads");
    }

    /// <summary>Sums self time, total time and calls per method across every path it appears on.</summary>
    private static void Accumulate(CallRecorder.Node node, Dictionary<int, long[]> totals)
    {
        foreach (KeyValuePair<int, CallRecorder.Node> pair in node.Children)
        {
            CallRecorder.Node child = pair.Value;

            if (!totals.TryGetValue(child.MethodId, out long[] row))
            {
                row = new long[3];
                totals[child.MethodId] = row;
            }

            row[0] += SelfOf(child);
            row[1] += child.Ticks;
            row[2] += child.Calls;

            Accumulate(child, totals);
        }
    }

    private static long SelfOf(CallRecorder.Node node)
    {
        long children = 0;

        foreach (KeyValuePair<int, CallRecorder.Node> pair in node.Children)
        {
            children += pair.Value.Ticks;
        }

        return Math.Max(node.Ticks - children, 0);
    }

    // ------------------------------------------------------------------ shared pieces

    /// <summary>
    /// A card: a surface lifted a few percent off the ground with its name along the top.
    ///
    /// <c>BeginVertical</c> with a style is the only way IMGUI will draw a background behind a
    /// group whose height is not known until its contents have been laid out, which is every
    /// section on this page.
    /// </summary>
    private static void BeginCard(string title)
    {
        GUILayout.BeginVertical(PanelTheme.Card);

        if (title == null)
        {
            return;
        }

        GUILayout.Label(title, PanelTheme.CardTitle);

        Rect rule = GUILayoutUtility.GetRect(10f, 11f);
        PanelTheme.Fill(new Rect(rule.x, Mathf.Round(rule.y + 5f), rule.width, 1f), new Color(1f, 1f, 1f, 0.07f));
    }

    private static void EndCard()
    {
        GUILayout.EndVertical();
    }

    private static void Row(string label, string value, int index)
    {
        GUILayout.BeginHorizontal(index % 2 == 1 ? PanelTheme.RowOdd : PanelTheme.RowEven);
        GUILayout.Label(label, PanelTheme.Cell);
        GUILayout.FlexibleSpace();
        GUILayout.Label(PanelTheme.Unit(value), PanelTheme.ValueRight, GUILayout.Width(180f));
        GUILayout.EndHorizontal();
    }

    /// <summary>
    /// Writes everything on the page to a file beside <c>Player.log</c> and puts the folder on
    /// the clipboard.
    ///
    /// The clipboard gets the path rather than the data because the data is thousands of rows;
    /// what a developer wants is to open it in something that can search.
    /// </summary>
    private void Export()
    {
        string path = ReportWriter.Write(Feed, Census, Managed, Session, out string error);

        if (path == null)
        {
            ExportMessage = "Export failed - " + error;
            return;
        }

        string folder = System.IO.Path.GetDirectoryName(path);

        try
        {
            GUIUtility.systemCopyBuffer = folder;
        }
        catch (Exception)
        {
            // The clipboard is the convenience; the path in the message is the fallback.
        }

        // Short enough to fit the toolbar strip. The folder itself is on the clipboard, which is
        // what a developer does with it anyway.
        ExportMessage = "Wrote " + System.IO.Path.GetFileName(path)
                        + " beside Player.log - folder path copied to the clipboard.";
    }

    /// <summary>
    /// Drawn last and outside the scroll view, so it is never clipped by the row it belongs
    /// to - which is what a tooltip inside a scrolling table would otherwise be.
    /// </summary>
    private void DrawTooltip()
    {
        if (HoverText == null || Event.current.type != EventType.Repaint)
        {
            return;
        }

        Vector2 point = GUIUtility.ScreenToGUIPoint(HoverPoint);
        Vector2 size = PanelTheme.Tooltip.CalcSize(new GUIContent(HoverText));

        size.x = Mathf.Min(size.x + 8f, 660f);
        size.y += 6f;

        float x = Mathf.Min(point.x + 16f, Screen.width - size.x - 8f);
        float y = Mathf.Min(point.y + 18f, Screen.height - size.y - 8f);

        Rect box = new Rect(x, y, size.x, size.y);

        PanelTheme.Rounded(box, new Color(0.058f, 0.075f, 0.110f, 0.98f), new Color(1f, 1f, 1f, 0.18f));
        GUI.Label(box, HoverText, PanelTheme.Tooltip);
    }

    private static long TotalOf(CallRecorder.Node node)
    {
        if (node.MethodId >= 0)
        {
            return node.Ticks;
        }

        long total = 0;

        foreach (KeyValuePair<int, CallRecorder.Node> pair in node.Children)
        {
            total += pair.Value.Ticks;
        }

        return total;
    }

    /// <summary>
    /// How many rows will actually be drawn, applying the same sub-pixel cull that
    /// <see cref="DrawFrames"/> does, so the box can be sized to its contents.
    /// </summary>
    private static int VisibleDepth(CallRecorder.Node node, float width, long total, int depth)
    {
        if (width < 1f || total <= 0)
        {
            return depth;
        }

        int deepest = depth;

        foreach (KeyValuePair<int, CallRecorder.Node> pair in node.Children)
        {
            CallRecorder.Node child = pair.Value;
            float span = width * (child.Ticks / (float)total);

            if (span < 1f)
            {
                continue;
            }

            int found = VisibleDepth(child, span, child.Ticks, depth + 1);

            if (found > deepest)
            {
                deepest = found;
            }
        }

        return deepest;
    }

    /// <summary>Stable per-method colour, so a frame keeps its colour between captures.</summary>
    private static Color ColourFor(int methodId)
    {
        float hue = (methodId * 0.618034f) % 1f;

        // Bright and saturated: the first version drew a dark fill on a dark page, which made
        // the graph a texture rather than a diagram. The on-frame text is dark to match.
        return Color.HSVToRGB(hue, 0.55f, 0.95f);
    }

    private static string Short(string name)
    {
        int cut = name.LastIndexOf('.');

        if (cut <= 0)
        {
            return name;
        }

        int previous = name.LastIndexOf('.', cut - 1);

        return previous < 0 ? name : name.Substring(previous + 1);
    }

    private static string Milliseconds(long ticks)
    {
        double ms = ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        return ms >= 100 ? ms.ToString("0") + " ms" : ms.ToString("0.0") + " ms";
    }

    private static string Format(CounterFeed.Gauge gauge, long value)
    {
        if (gauge.IsTime)
        {
            // Counters in this category are nanoseconds.
            return (value / 1_000_000.0).ToString("0.00") + " ms";
        }

        return gauge.IsBytes ? Bytes(value) : value.ToString("N0");
    }

    private static string Bytes(long value)
    {
        return ManagedCensus.Bytes(value);
    }

    private void OnDestroy()
    {
        PanelTheme.Release();
    }
}
