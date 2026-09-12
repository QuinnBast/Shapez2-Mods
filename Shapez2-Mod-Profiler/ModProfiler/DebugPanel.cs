using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The profiler page: a modal screen with tabs, laid out like one of the game's own full
/// screen pages - a title bar, a row of tabs, a scrolling body - and drawn with IMGUI.
///
/// **Why IMGUI and not a HUDPart.** It will not look exactly like the game's UI, and that is
/// the trade being made deliberately. A real screen means the game's prefab and dependency
/// injection machinery: <c>[Construct]</c> that never runs on a runtime clone,
/// <c>HUDLocalizedText</c> throwing because its resolver is null, layout groups to fight. That
/// is where the hours went on the last two HUD additions, and for a page whose whole job is
/// dense tables of numbers it buys nothing back. What the game's screens *do* have that
/// matters is behaviour - they stop the world behind them, they close on Escape - and that is
/// reproduced exactly: see <see cref="PanelInput"/> and <see cref="SyncBlocker"/>.
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

    private static readonly string[] TabNames = { "Overview", "Memory", "CPU" };

    private CounterFeed Feed;
    private ProfilerSession Session;
    private readonly ObjectCensus Census = new ObjectCensus();
    private ManagedCensus Managed;

    private Tab Current = Tab.Overview;

    private CallRecorder.Node Focus;
    private bool ShowAllTypes;
    private bool ShowAllManaged;

    private bool PickerOpen;
    private string SelectedMod;
    private List<string> Lines = new List<string>();

    private string AssemblyFilter;
    private bool ModsOnly;
    private string ExportMessage;

    private string HoverText;
    private Vector2 HoverPoint;

    private GameObject Blocker;

    private Vector2 Scroll;
    private GUIStyle Header;
    private GUIStyle Label;
    private GUIStyle Value;
    private GUIStyle Title;
    private GUIStyle CloseButton;
    private GUIStyle TabOn;
    private GUIStyle TabOff;
    private GUIStyle Tooltip;
    private GUIStyle FrameLabel;
    private Texture2D Pixel;
    private Texture2D Chrome;
    private Texture2D Dim;
    private Texture2D Graph;

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
    /// <c>raycastTarget</c> set swallows the event. It is invisible on purpose - the dimming
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
    }

    private void OnGUI()
    {
        if (!Open || Feed == null)
        {
            return;
        }

        EnsureStyles();
        HoverText = null;

        // A fallback for the game binding PanelInput consumes: if this build calls cancel
        // something other than "global.cancel", Escape still closes the panel.
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
        {
            Close();
            Event.current.Use();
            return;
        }

        // Full screen rather than a window, because that is what the game's own pages are:
        // Statistics fills the screen, puts its title top left, its tabs centred and its close
        // button top right. Same skeleton here.
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Dim);

        float margin = Mathf.Max(46f, (Screen.width - 1500f) / 2f);
        const float headerHeight = 104f;

        GUI.Label(new Rect(margin, 26f, 500f, 44f), "Mod Profiler", Title);

        DrawTabs(new Rect(0f, 32f, Screen.width, 34f));

        if (GUI.Button(new Rect(Screen.width - margin - 34f, 30f, 34f, 30f), "X", CloseButton))
        {
            Close();
            return;
        }

        GUI.Label(new Rect(Screen.width - margin - 150f, 34f, 110f, 22f), "Esc to close", Label);

        if (GUI.Button(new Rect(Screen.width - margin - 250f, 30f, 90f, 30f), "Export", CloseButton))
        {
            Export();
        }

        if (ExportMessage != null)
        {
            GUI.Label(new Rect(margin, 72f, Screen.width - margin * 2f - 60f, 20f), ExportMessage, Label);
        }

        GUI.color = new Color(0.45f, 0.62f, 0.78f, 0.35f);
        GUI.DrawTexture(new Rect(margin, headerHeight - 10f, Screen.width - margin * 2f, 1f), Pixel);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(margin, headerHeight, Screen.width - margin * 2f,
            Screen.height - headerHeight - 24f));

        Scroll = GUILayout.BeginScrollView(Scroll);

        switch (Current)
        {
            case Tab.Memory:
                DrawGarbageCollector();
                DrawManagedObjects();
                DrawObjects();
                break;

            case Tab.Cpu:
                DrawRecorderControls();
                DrawFlameGraph();
                DrawHotMethods();
                break;

            default:
                DrawFrameGraph();

                foreach (string section in Feed.SectionNames)
                {
                    DrawSection(section);
                }

                break;
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();

        DrawTooltip();
    }

    /// <summary>
    /// The tab row. Tabs rather than one long page because the three questions get asked at
    /// different times: what is the frame doing now, what is on the heap, and where did the
    /// time go in the recording I just took.
    /// </summary>
    private void DrawTabs(Rect area)
    {
        const float width = 150f;
        const float gap = 14f;

        float total = TabNames.Length * width + (TabNames.Length - 1) * gap;
        float x = Mathf.Round((area.width - total) / 2f);

        for (int i = 0; i < TabNames.Length; i++)
        {
            Rect slot = new Rect(x + i * (width + gap), area.y, width, area.height);
            bool active = (int)Current == i;

            if (GUI.Button(slot, TabNames[i].ToUpperInvariant(), active ? TabOn : TabOff))
            {
                Current = (Tab)i;
                Scroll = Vector2.zero;
                PickerOpen = false;
            }

            if (!active)
            {
                continue;
            }

            // The game marks the live tab with a warm underline rather than a filled
            // background; copying that is most of what makes this read as the same family of
            // screen.
            GUI.color = new Color(1f, 0.62f, 0.25f);
            GUI.DrawTexture(new Rect(slot.x + 12f, slot.yMax + 2f, slot.width - 24f, 2f), Pixel);
            GUI.color = Color.white;
        }
    }

    /// <summary>
    /// Start and stop a recording without leaving the page.
    ///
    /// The console commands still exist and still work; this is the same two calls behind a
    /// picker, because choosing from a list of the mods actually loaded beats typing a name
    /// that has to match one.
    /// </summary>
    private void DrawRecorderControls()
    {
        GUILayout.Label("RECORDING", Header);

        IReadOnlyList<string> mods = ModAssemblies.All;

        if (mods.Count == 0)
        {
            GUILayout.Label("No mod assemblies are loaded, so there is nothing to record.", Label);
            return;
        }

        if (SelectedMod == null || !Contains(mods, SelectedMod))
        {
            SelectedMod = mods[0];
        }

        GUILayout.BeginHorizontal();

        GUILayout.Label("Mod", Label, GUILayout.Width(34f));

        // A dropdown, hand-rolled: IMGUI has no popup that floats above a scroll view, so the
        // list opens as rows underneath instead of over the content.
        if (GUILayout.Button(SelectedMod + (PickerOpen ? "   ^" : "   v"), GUILayout.Width(260f)))
        {
            PickerOpen = !PickerOpen;
        }

        GUILayout.Space(10f);

        bool recording = Session != null && Session.Recording;

        if (recording)
        {
            if (GUILayout.Button("Stop", GUILayout.Width(110f)))
            {
                Lines = new List<string>(Session.StopRecording());
                PickerOpen = false;
            }
        }
        else if (GUILayout.Button("Record", GUILayout.Width(110f)))
        {
            Lines = new List<string>(Session.Record(SelectedMod, ProfilerSession.DefaultBudget));
            PickerOpen = false;
        }

        GUILayout.EndHorizontal();

        GUILayout.Label(recording
            ? "Recording " + Session.Target + " - play through whatever you want to measure, then Stop."
            : "Record weaves enter/exit hooks into that assembly, and Stop takes them out again.", Label);

        if (PickerOpen)
        {
            foreach (string mod in mods)
            {
                if (GUILayout.Button("   " + mod, TabOff, GUILayout.Width(260f)))
                {
                    SelectedMod = mod;
                    PickerOpen = false;
                }
            }
        }

        if (Lines.Count > 0)
        {
            GUILayout.Space(6f);

            foreach (string line in Lines)
            {
                GUILayout.Label(line, Label);
            }
        }

        GUILayout.Space(6f);
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

    /// <summary>
    /// Frame time, as a graph and as four numbers.
    ///
    /// The graph was unreadable in its first form - dark bars on a dark page with nothing to
    /// measure them against. It now draws on a lighter ground with the two lines that matter
    /// marked: 16.7 ms, which is 60 frames a second, and 33.3 ms, which is 30. A bar's colour
    /// says which band it is in, so a stutter shows up without comparing heights.
    ///
    /// The numbers are there because a graph is a window and a spike leaves it. Min, mean and
    /// max hold since the last reset, so a stutter that happened while you were looking
    /// somewhere else is still on the page.
    /// </summary>
    private void DrawFrameGraph()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("FRAME TIME", Header, GUILayout.Width(120f));

        GUILayout.Label("now " + Feed.FrameLast.ToString("0.0") + " ms", Value, GUILayout.Width(110f));
        GUILayout.Label("min " + Feed.FrameMin.ToString("0.0"), Label, GUILayout.Width(80f));
        GUILayout.Label("mean " + Feed.FrameMean.ToString("0.0"), Label, GUILayout.Width(95f));
        GUILayout.Label("max " + Feed.FrameMax.ToString("0.0"), Label, GUILayout.Width(80f));

        if (GUILayout.Button("Reset stats", GUILayout.Width(110f)))
        {
            Feed.ResetStatistics();
        }

        GUILayout.EndHorizontal();

        Rect area = GUILayoutUtility.GetRect(10f, 110f);

        GUI.color = Color.white;
        GUI.DrawTexture(area, Graph);

        // A fixed ceiling rather than an auto-scaled one: the question a frame graph is asked
        // is "are we missing frames", and a scale that moves with the data hides exactly that.
        const float ceiling = 40f;

        DrawGridline(area, 16.7f, ceiling, "16.7 ms - 60 fps");
        DrawGridline(area, 33.3f, ceiling, "33.3 ms - 30 fps");

        float step = area.width / CounterFeed.HistoryLength;

        for (int i = 0; i < CounterFeed.HistoryLength; i++)
        {
            float ms = Feed.FrameAt(i);

            if (ms <= 0f)
            {
                continue;
            }

            float height = Mathf.Clamp01(ms / ceiling) * area.height;

            GUI.color = ms > 33.3f
                ? new Color(1f, 0.38f, 0.36f)
                : ms > 16.7f
                    ? new Color(1f, 0.74f, 0.3f)
                    : new Color(0.42f, 0.86f, 1f);

            GUI.DrawTexture(new Rect(area.x + i * step, area.y + area.height - height,
                Mathf.Max(step, 1f), height), Pixel);
        }

        GUI.color = Color.white;
        GUILayout.Space(6f);
    }

    private void DrawGridline(Rect area, float milliseconds, float ceiling, string label)
    {
        float y = area.y + area.height - Mathf.Clamp01(milliseconds / ceiling) * area.height;

        GUI.color = new Color(0.55f, 0.68f, 0.8f, 0.35f);
        GUI.DrawTexture(new Rect(area.x, y, area.width, 1f), Pixel);
        GUI.color = Color.white;

        GUI.Label(new Rect(area.x + 6f, y - 16f, 220f, 16f), label, Label);
    }

    private void DrawSection(string section)
    {
        GUILayout.Space(10f);

        GUILayout.BeginHorizontal();
        GUILayout.Label(section.ToUpperInvariant(), Header, GUILayout.Width(240f));
        GUILayout.Label("now", Header, GUILayout.Width(110f));
        GUILayout.Label("min", Header, GUILayout.Width(110f));
        GUILayout.Label("mean", Header, GUILayout.Width(110f));
        GUILayout.Label("max", Header);
        GUILayout.EndHorizontal();

        foreach (CounterFeed.Gauge gauge in Feed.Section(section))
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(gauge.Label, Label, GUILayout.Width(240f));

            if (!gauge.Live)
            {
                GUILayout.Label("not exposed by this build", Label);
                GUILayout.EndHorizontal();
                continue;
            }

            GUILayout.Label(Format(gauge, gauge.Value), Value, GUILayout.Width(110f));
            GUILayout.Label(Format(gauge, gauge.Min), Label, GUILayout.Width(110f));
            GUILayout.Label(Format(gauge, (long)gauge.Mean), Label, GUILayout.Width(110f));
            GUILayout.Label(Format(gauge, gauge.Max), Label);
            GUILayout.EndHorizontal();
        }
    }

    private void DrawGarbageCollector()
    {
        GUILayout.Label("GARBAGE COLLECTOR", Header);

        Row("Managed heap", Bytes(GC.GetTotalMemory(false)));

        // Boehm's own numbers, straight off the collector, when the runtime exports them.
        // GC.GetTotalMemory is derived from these two; having both makes it obvious how much
        // of the heap is reserved-but-free, which is the difference between "the mod is
        // leaking" and "the collector has not handed the pages back".
        MonoRuntime.Bind();

        if (MonoRuntime.CanReadHeapSize)
        {
            Row("Boehm heap reserved", Bytes(MonoRuntime.HeapSize()));
            Row("Boehm heap in use", Bytes(MonoRuntime.UsedSize()));
        }

        Row("Collections", GC.CollectionCount(0).ToString());
        Row("Collections / sec", Feed.CollectionRate.ToString("0.00"));
        Row("Heap growth / sec", Bytes((long)Feed.AllocationRate));

        GUILayout.Space(6f);
        GUILayout.Label("Heap growth is measured from the sawtooth between collections, because this "
                        + "build exposes no allocation counter: GC.GetAllocatedBytesForCurrentThread is "
                        + "frozen at zero, and neither 'GC Allocated In Frame' nor 'Scripts / GC.Alloc' "
                        + "is fed. It is process-wide and cannot be attributed to a mod.", Label);
    }

    /// <summary>
    /// The managed heap by assembly and by type - the half of memory that can be a mod's.
    ///
    /// Assembly first, mods at the top, because the question is "how much of this is mine".
    /// The walk runs a slice per frame, so the button starts it rather than performing it, and
    /// the status line is live while it runs.
    /// </summary>
    private void DrawManagedObjects()
    {
        GUILayout.Space(14f);
        GUILayout.Label("MANAGED OBJECTS", Header);

        if (Managed == null)
        {
            return;
        }

        GUILayout.BeginHorizontal();

        if (Managed.Running)
        {
            if (GUILayout.Button("Cancel", GUILayout.Width(120f)))
            {
                Managed.Cancel();
            }
        }
        else if (GUILayout.Button(Managed.HasScanned ? "Scan again" : "Scan", GUILayout.Width(120f)))
        {
            Managed.Begin();
        }

        GUILayout.Label(Managed.Status, Label);
        GUILayout.EndHorizontal();

        if (Managed.Running)
        {
            // There is no total to measure against - the size of the heap is the thing being
            // discovered - so this shows the share of known work done, not a true fraction.
            Rect area = GUILayoutUtility.GetRect(10f, 6f);
            GUI.DrawTexture(area, Chrome);

            float share = Managed.Visited + Managed.Queued == 0
                ? 0f
                : Managed.Visited / (float)(Managed.Visited + Managed.Queued);

            GUI.color = new Color(0.45f, 0.75f, 0.95f);
            GUI.DrawTexture(new Rect(area.x, area.y, area.width * share, area.height), Pixel);
            GUI.color = Color.white;
        }

        if (!Managed.HasScanned)
        {
            GUILayout.Label("Every C# object reachable from a static field or from a live UnityEngine.Object, "
                            + "grouped by the assembly that declares its type. A mod's objects are mostly held "
                            + "by the game - Shifter holds the IMod, the simulation holds whatever the mod "
                            + "registered - so the walk starts everywhere and attributes by type rather than "
                            + "by where it started. Sizes are estimated from field layout; counts are exact.",
                Label);
            return;
        }

        if (Managed.Truncated)
        {
            GUILayout.Label("The walk hit its cap, so this is a partial count.", Label);
        }

        GUILayout.Space(8f);
        GUILayout.Label("By assembly - click one to filter the table below", Value);

        foreach (ManagedCensus.Entry entry in Managed.ByAssembly)
        {
            bool selected = AssemblyFilter == entry.Assembly;

            GUILayout.BeginHorizontal();

            GUI.color = selected
                ? new Color(1f, 0.78f, 0.42f)
                : entry.IsMod
                    ? new Color(0.65f, 0.9f, 1f)
                    : Color.white;

            // A label would be dead text and a button would be a box round every row; a
            // left-aligned flat button is the row itself, which is what makes clicking it the
            // obvious thing to do.
            if (GUILayout.Button((entry.IsMod ? "* " : "   ") + entry.TypeName, TabOff, GUILayout.Width(430f)))
            {
                AssemblyFilter = selected ? null : entry.Assembly;
            }

            GUI.color = Color.white;

            GUILayout.Label(entry.Count.ToString("N0"), Value, GUILayout.Width(90f));
            GUILayout.Label(Bytes(entry.Bytes), Value);
            GUILayout.EndHorizontal();
        }

        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();

        GUILayout.Label("By type", Value, GUILayout.Width(70f));

        if (GUILayout.Button(ModsOnly ? "Mods only: on" : "Mods only: off", GUILayout.Width(130f)))
        {
            ModsOnly = !ModsOnly;
        }

        if (AssemblyFilter != null)
        {
            if (GUILayout.Button("Clear filter", GUILayout.Width(110f)))
            {
                AssemblyFilter = null;
            }

            GUILayout.Label("showing " + AssemblyFilter, Label);
        }

        GUILayout.EndHorizontal();

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

        if (rows.Count == 0)
        {
            GUILayout.Label("Nothing matches that filter.", Label);
            return;
        }

        int limit = ShowAllManaged ? rows.Count : Mathf.Min(40, rows.Count);

        for (int i = 0; i < limit; i++)
        {
            ManagedCensus.Entry entry = rows[i];

            GUILayout.BeginHorizontal();

            GUI.color = entry.IsMod ? new Color(0.65f, 0.9f, 1f) : Color.white;
            GUILayout.Label(entry.TypeName, Label, GUILayout.Width(430f));
            GUI.color = Color.white;

            GUILayout.Label(entry.Count.ToString("N0"), Value, GUILayout.Width(90f));
            GUILayout.Label(Bytes(entry.Bytes), Value);
            GUILayout.EndHorizontal();
        }

        if (rows.Count > 40)

        {
            GUILayout.Space(4f);

            if (GUILayout.Button(ShowAllManaged
                    ? "Show top 40"
                    : "Show all " + rows.Count + " types", GUILayout.Width(200f)))
            {
                ShowAllManaged = !ShowAllManaged;
            }
        }
    }

    /// <summary>
    /// The heap by type, largest first - counts and sizes for every live UnityEngine.Object.
    ///
    /// Scanned on demand rather than every frame: the walk covers every object the engine
    /// knows about, and on a large save that is a visible hitch.
    /// </summary>
    private void DrawObjects()
    {
        GUILayout.Space(14f);
        GUILayout.Label("UNITY OBJECTS", Header);

        GUILayout.BeginHorizontal();

        if (GUILayout.Button(Census.HasScanned ? "Scan again" : "Scan", GUILayout.Width(120f)))
        {
            Census.Scan();
        }

        if (Census.HasScanned)
        {
            GUILayout.Label(Census.TotalObjects.ToString("N0") + " objects, " + Bytes(Census.TotalBytes)
                            + ", scanned in " + Census.Milliseconds.ToString("0") + " ms", Label);
        }

        GUILayout.EndHorizontal();

        if (!Census.HasScanned)
        {
            GUILayout.Label("Every live UnityEngine.Object - textures, meshes, materials, audio, "
                            + "components - by type, largest first, with the real size the engine reports. "
                            + "This is the engine's half of the heap and nearly all of it is the game's.",
                Label);
            return;
        }

        if (Census.SizesUnavailable)
        {
            GUILayout.Label("Sizes all came back zero, so Profiler.GetRuntimeMemorySizeLong is present "
                            + "but does nothing in this build - the same way the allocation counter is. "
                            + "The counts below are still real.", Label);
        }

        GUILayout.Space(6f);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Type", Header, GUILayout.Width(420f));
        GUILayout.Label("Count", Header, GUILayout.Width(90f));
        GUILayout.Label("Size", Header);
        GUILayout.EndHorizontal();

        int limit = ShowAllTypes ? Census.Entries.Count : Mathf.Min(40, Census.Entries.Count);

        for (int i = 0; i < limit; i++)
        {
            ObjectCensus.Entry entry = Census.Entries[i];

            GUILayout.BeginHorizontal();
            GUILayout.Label(entry.TypeName, Label, GUILayout.Width(420f));
            GUILayout.Label(entry.Count.ToString("N0"), Value, GUILayout.Width(90f));
            GUILayout.Label(Bytes(entry.Bytes), Value);
            GUILayout.EndHorizontal();
        }

        if (Census.Entries.Count > 40)
        {
            GUILayout.Space(4f);

            if (GUILayout.Button(ShowAllTypes
                    ? "Show top 40"
                    : "Show all " + Census.Entries.Count + " types", GUILayout.Width(200f)))
            {
                ShowAllTypes = !ShowAllTypes;
            }
        }
    }

    /// <summary>
    /// The flame graph: one row per call depth, width proportional to time.
    ///
    /// Unlike the counters and the Unity census, this is entirely your code - these frames are
    /// methods the recorder was compiled into, so every one carries your own type and method
    /// name. Click a frame to zoom into it; hover for the numbers that do not fit on it.
    /// </summary>
    private void DrawFlameGraph()
    {
        GUILayout.Space(10f);
        GUILayout.Label("FLAME GRAPH", Header);

        if (Session == null)
        {
            return;
        }

        CallRecorder.Node root = CallRecorder.Tree;

        if (root == null || root.Children.Count == 0)
        {
            GUILayout.Label(Session.Recording
                ? "Recording. Stop when you have played through whatever you want to measure."
                : "Nothing recorded yet. Pick a mod above and press Record, play for a few seconds, then "
                  + "Stop. Only that mod's own methods are woven - and methods on generic types cannot be "
                  + "hooked at all, so they will be missing.", Label);
            return;
        }

        CallRecorder.Node shown = Focus ?? root;
        long total = TotalOf(shown);

        if (total <= 0)
        {
            GUILayout.Label("The recording captured no time.", Label);
            return;
        }

        GUILayout.BeginHorizontal();

        if (Focus != null && GUILayout.Button("Back", GUILayout.Width(90f)))
        {
            Focus = Focus.Parent == CallRecorder.Tree ? null : Focus.Parent;
        }

        GUILayout.Label(Focus == null
            ? "All frames, " + Milliseconds(total) + " total"
            : Session.NameOf(shown.MethodId) + " - " + Milliseconds(total) + " over "
              + shown.Calls.ToString("N0") + " calls", Label);

        GUILayout.EndHorizontal();

        const float rowHeight = 20f;
        int depth = DepthOf(shown, 0);
        Rect area = GUILayoutUtility.GetRect(10f, Mathf.Min(depth, 20) * rowHeight + 4f);

        DrawFrames(shown, area, area.y, area.width, total, rowHeight);

        GUI.color = Color.white;
    }

    /// <summary>Lays out one node's children across the width its own time earned.</summary>
    private void DrawFrames(CallRecorder.Node node, Rect area, float y, float width, long total, float rowHeight)
    {
        if (y > area.yMax || width < 1f || total <= 0)
        {
            return;
        }

        float x = area.x;

        foreach (KeyValuePair<int, CallRecorder.Node> pair in node.Children)
        {
            CallRecorder.Node child = pair.Value;
            float span = width * (child.Ticks / (float)total);

            if (span < 1f)
            {
                x += span;
                continue;
            }

            Rect frame = new Rect(x, y, Mathf.Max(span - 1f, 1f), rowHeight - 1f);

            GUI.color = ColourFor(child.MethodId);
            GUI.DrawTexture(frame, Pixel);
            GUI.color = Color.white;

            // The numbers go on the frame when they fit and in the tooltip when they do not.
            // A truncated name with no timing was the worst of both.
            if (span > 150f)
            {
                GUI.Label(frame, " " + Short(Session.NameOf(child.MethodId)) + "  " + Milliseconds(child.Ticks),
                    FrameLabel);
            }
            else if (span > 55f)
            {
                GUI.Label(frame, " " + Short(Session.NameOf(child.MethodId)), FrameLabel);
            }

            if (frame.Contains(Event.current.mousePosition))
            {
                HoverText = Session.NameOf(child.MethodId)
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

            DrawFrames(child, area, y + rowHeight, Mathf.Max(span - 1f, 1f), child.Ticks, rowHeight);
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
    private void DrawHotMethods()
    {
        CallRecorder.Node root = CallRecorder.Tree;

        if (Session == null || root == null || root.Children.Count == 0)
        {
            return;
        }

        GUILayout.Space(16f);
        GUILayout.Label("HOTTEST METHODS, BY SELF TIME", Header);

        Dictionary<int, long[]> totals = new Dictionary<int, long[]>();
        Accumulate(root, totals);

        List<KeyValuePair<int, long[]>> ordered = new List<KeyValuePair<int, long[]>>(totals);
        ordered.Sort((a, b) => b.Value[0].CompareTo(a.Value[0]));

        GUILayout.BeginHorizontal();
        GUILayout.Label("Method", Header, GUILayout.Width(430f));
        GUILayout.Label("Self", Header, GUILayout.Width(90f));
        GUILayout.Label("Total", Header, GUILayout.Width(90f));
        GUILayout.Label("Calls", Header);
        GUILayout.EndHorizontal();

        for (int i = 0; i < Mathf.Min(20, ordered.Count); i++)
        {
            KeyValuePair<int, long[]> row = ordered[i];

            GUILayout.BeginHorizontal();
            GUILayout.Label(Session.NameOf(row.Key), Label, GUILayout.Width(430f));
            GUILayout.Label(Milliseconds(row.Value[0]), Value, GUILayout.Width(90f));
            GUILayout.Label(Milliseconds(row.Value[1]), Value, GUILayout.Width(90f));
            GUILayout.Label(row.Value[2].ToString("N0"), Value);
            GUILayout.EndHorizontal();
        }
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

    /// <summary>
    /// Drawn last and outside the scroll view, so it is never clipped by the row it belongs
    /// to - which is what a tooltip inside a scrolling table would otherwise be.
    /// </summary>
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

        ExportMessage = "Wrote " + System.IO.Path.GetFileName(path) + " to " + folder
                        + "  (folder path copied to the clipboard)";
    }

    private void DrawTooltip()
    {
        if (HoverText == null || Event.current.type != EventType.Repaint)
        {
            return;
        }

        Vector2 point = GUIUtility.ScreenToGUIPoint(HoverPoint);
        Vector2 size = Tooltip.CalcSize(new GUIContent(HoverText));

        size.x = Mathf.Min(size.x + 16f, 640f);
        size.y += 10f;

        float x = Mathf.Min(point.x + 16f, Screen.width - size.x - 8f);
        float y = Mathf.Min(point.y + 18f, Screen.height - size.y - 8f);

        Rect box = new Rect(x, y, size.x, size.y);

        GUI.color = new Color(0.30f, 0.55f, 0.72f, 0.95f);
        GUI.DrawTexture(new Rect(box.x - 1f, box.y - 1f, box.width + 2f, box.height + 2f), Pixel);
        GUI.color = Color.white;
        GUI.DrawTexture(box, Chrome);
        GUI.Label(box, HoverText, Tooltip);
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

    private static int DepthOf(CallRecorder.Node node, int depth)
    {
        int deepest = depth;

        foreach (KeyValuePair<int, CallRecorder.Node> pair in node.Children)
        {
            int found = DepthOf(pair.Value, depth + 1);

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
        return Color.HSVToRGB(hue, 0.62f, 0.98f);
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

    private void Row(string label, string value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, Label, GUILayout.Width(220f));
        GUILayout.Label(value, Value);
        GUILayout.EndHorizontal();
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

    /// <summary>
    /// IMGUI styles have to be built inside OnGUI - GUI.skin is null anywhere else - so this
    /// runs on the first draw rather than at construction.
    /// </summary>
    private void EnsureStyles()
    {
        if (Header != null)
        {
            return;
        }

        // White, so GUI.color alone decides what a rectangle looks like. Every fill in this
        // page is this texture tinted, which is why the bars could be the page's own colour
        // by accident in the first place.
        Pixel = Fill(Color.white);
        Chrome = Fill(new Color(0.11f, 0.14f, 0.18f, 1f));

        // A lighter ground for graphs: bars drawn on the page's own colour were invisible,
        // which is the complaint this answers.
        Graph = Fill(new Color(0.10f, 0.13f, 0.17f, 1f));

        // The backdrop is drawn by us rather than by the blocker canvas, so it is certain to
        // sit under this page instead of over it. Dark enough that the game reads as inactive,
        // short of opaque so it is still obvious what is behind.
        Dim = Fill(new Color(0.035f, 0.055f, 0.085f, 0.955f));

        Header = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.6f, 0.85f, 1f) },
        };

        Title = new GUIStyle(GUI.skin.label)
        {
            fontSize = 24,
            normal = { textColor = new Color(0.93f, 0.96f, 1f) },
        };

        CloseButton = new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
        };

        // Tabs are drawn as text, not as buttons with a frame: the game's tab row is a line of
        // labels with the live one underlined, and a chunky IMGUI button next to that reads as
        // a different piece of software.
        TabOn = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = Color.white },
            hover = { textColor = Color.white },
        };

        TabOff = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.62f, 0.68f, 0.76f) },
            hover = { textColor = new Color(0.85f, 0.9f, 0.96f) },
        };

        Label = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            wordWrap = true,
            normal = { textColor = new Color(0.75f, 0.78f, 0.82f) },
        };

        Value = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white },
        };

        FrameLabel = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = new Color(0.08f, 0.09f, 0.12f) },
        };

        Tooltip = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            padding = new RectOffset(8, 8, 5, 5),
            normal = { textColor = new Color(0.88f, 0.93f, 1f) },
        };
    }

    private static Texture2D Fill(Color colour)
    {
        Texture2D texture = new Texture2D(1, 1);
        texture.SetPixel(0, 0, colour);
        texture.Apply();

        return texture;
    }

    private void OnDestroy()
    {
        Discard(Pixel);
        Discard(Chrome);
        Discard(Dim);
        Discard(Graph);
    }

    private static void Discard(Texture2D texture)
    {
        if (texture != null)
        {
            Destroy(texture);
        }
    }
}
