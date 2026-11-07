using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VrUiKit;
using NVec = System.Numerics.Vector3;

namespace TpmVr;

// The museum's side of the VR UI framework: which game UI objects make up each
// window, the default layout, and the museum's settings on the VR menu.
sealed class TpmUi : IVrUiHost
{
    static readonly TpmUi Instance = new();
    VrSession vr;
    TableRig rig;

    // Window rects measured in the museum (1920x1080): stats bar top right,
    // objectives under it, build bar bottom left (its lists open above it),
    // calendar and speed controls bottom right.
    public static void Init(Plugin cfg)
    {
        VrUi.Init(cfg.Config, "tpmvr-layouts");
        // Panel stays the default. Arranged windows sit round the table and move while the VR
        // menu is open (core 0.1.1 merged Free into it); they can be Popout (each drawn on its
        // own, the default) or Cutout, picked on the UI tab.
        VrUi.Offer(VrUi.UiMode.Arranged);
        // Widening the URP renderer's layers for Popout rebuilds it, which leaves a loaded
        // museum unlit: the core may only do it until a museum is up (the top-down camera).
        VrUi.PopoutPrepareWhile = () => TPS.Core.App.Instance?.MainCameraLogic?.TryCast<TPS.Game.TopDownCameraLogic>() == null;
        VrUi.OfferPopout();
        VrUi.RegisterWindow(new UiWindowDef { Id = "stats", Title = "Stats", Roots = () => Find("FinanceUI") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "objectives", Title = "Objectives", Roots = () => Find("ObjectivesContainer") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "build", Title = "Build", Roots = () => Find("HubMenuUI") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "calendar", Title = "Calendar", Roots = () => Find("CalendarUI") });
        VrUi.RegisterWindow(new UiWindowDef { Id = "focus", Title = "Pop-ups", Remainder = true });
        // The game's own cursor, the paused-game border, world markers, tooltips
        // (which follow the mouse) and the loading screen's watermark (left drawn
        // after loading) don't belong in a window.
        VrUi.Exclude("VirtualCursor(Clone)", "PauseOverlay", "WorldHighlightUIFX", "InWorldElements", "TooltipRoot", "LevelFade",
            "UI_General_P_Tooltip_*", "Watermark");
        // The saving spinner flashes up in a corner; in the pop-ups window it
        // stretches the window across the screen to reach it.
        VrUi.Exclude(SaveIcons);
        VrUi.RegisterLayout(DefaultLayout());
        RegisterActions(cfg);
        RegisterHotkeys();

        VrSettings.Add(new ToggleSetting("Controls", "Left-handed", cfg.LeftHanded));
        VrSettings.Add(new NumberSetting("Controls", "Laser angle", cfg.PointerPitch, 0, 90, 5, "0"));
        // The table's own rows (height, spin coast, scale, the eyes) come from the core's rig.
        VrSettings.Add(new NumberSetting("Game UI panel", "Panel width (m)", cfg.MenuWidth, 0.8f, 2.4f, 0.1f, "0.0"));
        VrSettings.PutOnTab("Game UI panel", "UI");
        VrSettings.Add(new IntSetting("Graphics", "UI redraw every N frames", cfg.MenuRefreshEveryFrames, 1, 4));
        Recorder.Init(cfg.Config);
        SpectatorCam.Init(cfg.Config);
        VrSettings.Add(new ActionSetting("Help", "Screenshot for Claude (3 s delay)", () => SimShots.ButtonText, SimShots.StartCountdown));
        VrModes.InWorld = () => Instance.rig == null || Instance.rig.InWorld;
        VrMenu.Welcome("Two Point Museum", Plugin.Version,
            "Hold both grips to move the museum; pull them apart or together to scale it, and twist to turn it.",
            "Point the laser at the game's screens and pull the trigger to click. The off-hand stick turns and zooms; the pointer stick pans (in Turntable, moves the circle).",
            "Pick a view on the Mode tab: the whole museum, or a turntable circle on the table.");
        AddHelp();
    }

    // The Help page's Two Point Museum questions: what a player wouldn't guess.
    static void AddHelp()
    {
        MenuFaq.Add("The camera tutorial wants me to tilt the camera.",
            "Click the laser hand's stick. Each click tilts the game's camera, up then down, which the tutorial counts. The table itself doesn't tilt: lean or move your head instead. The Tutorial button bar also has the game's camera keys.");
        MenuFaq.Add("How do I pick up and place things?",
            "Point at it, hold the trigger to pick it up, and let go to put it down. While you hold it, the laser hand's stick turns it and a stick click changes how it's placed. The table stays still until you put it down.");
        MenuFaq.Add("How do I pause the game or go back?",
            "The off hand's lower face button pauses and plays. Its upper face button is Back, like Escape. Clicking the off hand's stick opens this menu.");
        MenuFaq.Add("Why is there no Orbit mode?",
            "Orbit circles round a character, and the museum has no character to follow. Use Diorama for the whole museum, or Turntable for a circle of it.");
        MenuFaq.Add("The headset looks flatter than the game on the monitor.",
            "The game's colour grading and bloom are off in the headset, because they darkened the menus and flickered on rooftops. Turn on Post-processing on the Graphics page to bring them back.");
        MenuFaq.Add("Some buttons don't respond when the game's UI is split into windows.",
            "Clicks in the Arranged window mode haven't been fully tested yet. If a button doesn't respond, switch to Panel on the Game UI page.");
    }

    // Controller actions. Groups are the situations an action is used in, so
    // the menu can flag two actions sharing a control at the same time:
    // "Placing" (an item is held), "Browsing" (it isn't), "Free" (moving
    // windows), "Table"; no group = any time. Whatever is being carried is pushed,
    // pulled and resized by the holding hand's own stick, read by the core.
    // The table's own actions (move, turn, zoom, pan, recentre) are the core's TableActions.
    public const string Click = "click", RightClick = "rightclick", MoveWindow = "window.move",
        Pause = "pause", Back = "back", Menu = "menu",
        RotateClockwise = "rotate.cw", RotateAnticlockwise = "rotate.ccw", PlacementMode = "placement.mode",
        Tilt = "tilt",
        HandGrabWindow = "window.handgrab", NextBackground = "diorama.background";

    // The hotkey bar: the game's camera keys, for the camera tutorial (which waits
    // for the keys themselves), shown as icons. Pause, back, rotating and the
    // placement mode are all on the controllers, so they have no bar.
    static readonly Dictionary<string, VKey> hotkeys = new()
    {
        ["W"] = VKey.PanUp, ["A"] = VKey.PanLeft, ["S"] = VKey.PanDown, ["D"] = VKey.PanRight,
        ["Q"] = VKey.TurnCameraLeft, ["E"] = VKey.TurnCameraRight, ["R"] = VKey.PitchUp, ["F"] = VKey.PitchDown,
        ["Up"] = VKey.ZoomIn, ["Down"] = VKey.ZoomOut,
    };

    static void RegisterHotkeys()
    {
        VrHotkeys.UseKeys((key, down) => { if (down && hotkeys.TryGetValue(key, out var k)) VirtualKeyboard.Hold(k); });
        VrHotkeys.Set("Tutorial")
            .Group("Pan", 0, 0)
                .Button("Pan forward", "W", 1, 0, icon: "arrow-up").Button("Pan left", "A", 0, 1, icon: "arrow-left")
                .Button("Pan right", "D", 2, 1, icon: "arrow-right").Button("Pan back", "S", 1, 2, icon: "arrow-down").Set
            .Group("Rotate", 3.5f, 0)
                .Button("Turn left", "Q", 0, 0, icon: "rotate-ccw").Button("Turn right", "E", 1, 0, icon: "rotate-cw").Set
            .Group("Zoom", 6, 0)
                .Button("Zoom in", "Up", 0, 0, icon: "zoom-in").Button("Zoom out", "Down", 0, 1, icon: "zoom-out").Set
            .Group("Tilt", 3.5f, 1.5f)
                .Button("Tilt up", "R", 0, 0, icon: "chevrons-up").Button("Tilt down", "F", 1, 0, icon: "chevrons-down");
    }

    static void RegisterActions(Plugin cfg)
    {
        VrInput.Init(cfg.Config, "tpmvr-controls");
        VrInput.Register(Click, "Click / hold to pick up", "Laser", null, "Pointer.Trigger");
        VrInput.Register(RightClick, "Right click", "Laser", null, "Pointer.Secondary");
        VrInput.Register(Menu, "VR menu", "Game", null, "Off.StickClick");
        VrInput.Register(Pause, "Pause / play", "Game", null, "Off.Primary");
        VrInput.Register(Back, "Back / close", "Game", null, "Off.Secondary");
        // The table, the camera-pivot standard: off stick turns and zooms, pointer stick pans
        // (Turntable: moves the circle), grips move, scale and turn (TableRig reads them).
        TableActions.RegisterTableModes("Browsing");
        VrInput.Register(Tilt, "Tilt camera", "Table", "Browsing", "Pointer.StickClick");
        VrInput.Register(RotateClockwise, "Rotate clockwise", "Placing items", "Placing", "Pointer.Primary", "Pointer.StickRight");
        VrInput.Register(RotateAnticlockwise, "Rotate anticlockwise", "Placing items", "Placing", "Pointer.StickLeft");
        VrInput.Register(PlacementMode, "Placement mode", "Placing items", "Placing", "Pointer.StickClick");
        VrInput.Register(NextBackground, "Next background (sky, light, dark, passthrough)", "Game", null);
        VrInput.Register(MoveWindow, "Grab a window (VR menu open)", "Moving windows", "Free", "Pointer.Grip");
        VrInput.Register(HandGrabWindow, "Grab a window by touching it (VR menu open)", "Moving windows", "Free", "Off.Grip");
        VrInput.Load();
    }

    // Before the rig and pointer read any actions.
    public static void UpdateInput(VrSession session, TableRig vrRig)
    {
        Instance.vr = session;
        Instance.rig = vrRig;
        // The sticks are the core's table bindings (RegisterTableModes); a player's Swap sticks flips them.
        VrInput.Update(Instance, State(session.Left), State(session.Right));
        // Before the rig reads the grips, so a grip that takes a window doesn't also move the table.
        var off = Instance.LeftHanded ? session.Right : session.Left;
        if (off.Valid) VrUi.HandGrab(off.Pose, VrInput.Pressed(HandGrabWindow), VrInput.Held(HandGrabWindow));
        if (VrInput.Pressed(NextBackground)) NextTurntableBackground(vrRig);
    }

    // The controller's "next background": steps the Turntable's background setting.
    static void NextTurntableBackground(TableRig rig)
    {
        var now = rig.Settings.Get<Backdrop>(RigMode.Turntable, "Background");
        var next = (Backdrop)(((int)now + 1) % Enum.GetValues(typeof(Backdrop)).Length);
        rig.Settings.Set(RigMode.Turntable, "Background", next);
        Plugin.Logger.LogInfo($"[table] background {next}");
    }

    static ControllerState State(VrHand h) => new()
    {
        Valid = h.Valid,
        TriggerHeld = h.TriggerHeld, GripHeld = h.GripHeld,
        PrimaryHeld = h.PrimaryHeld, SecondaryHeld = h.SecondaryHeld, StickClickHeld = h.StickClickHeld,
        PrimaryPressed = h.PrimaryPressed, SecondaryPressed = h.SecondaryPressed, StickClickPressed = h.StickClickPressed,
        FlickLeft = h.FlickLeft, FlickRight = h.FlickRight,
        Trigger = h.Trigger, Grip = h.Grip, StickX = h.StickX, StickY = h.StickY,
    };

    // Looked up every couple of seconds, not on every measure.
    static readonly List<Transform> saveIcons = new();
    static float nextSaveIconSearch;
    static IEnumerable<Transform> SaveIcons()
    {
        if (Time.unscaledTime >= nextSaveIconSearch)
        {
            nextSaveIconSearch = Time.unscaledTime + 2f;
            saveIcons.Clear();
            saveIconType ??= GameType("SaveIconUI");
            if (saveIconType != null)
                foreach (var o in UnityEngine.Object.FindObjectsOfType(saveIconType, true))
                    if (o.TryCast<Component>() is Component c) saveIcons.Add(c.transform);
        }
        return saveIcons;
    }

    static Il2CppSystem.Type saveIconType;

    // A game class by its short name (for ones whose namespace isn't known at compile time).
    static Il2CppSystem.Type GameType(string name)
    {
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name != "TPS.Game") continue;
            System.Type[] types;
            try { types = asm.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
            var t = types.FirstOrDefault(x => x != null && x.Name == name);
            if (t != null)
            {
                Plugin.Logger.LogInfo($"[ui] found {t.FullName}");
                return Il2CppInterop.Runtime.Il2CppType.From(t);
            }
        }
        Plugin.Logger.LogWarning($"[ui] game class {name} not found");
        return null;
    }

    static IEnumerable<Transform> Find(string name)
    {
        var go = GameObject.Find(name);
        if (go != null) yield return go.transform;
    }

    // Positions are chosen in the room (metres, origin at the play-space centre,
    // the default table centre at (0, 0.9, 0.6)) and stored relative to the
    // table, so they follow the table height setting. Each window faces a
    // standing player's eyes.
    static UiLayout DefaultLayout()
    {
        var table = new NVec(0, 0.9f, 0.6f);
        var eye = new NVec(0, 1.5f, 0);
        var layout = new UiLayout { Name = "Default" };
        void Add(string id, NVec room, float pivotX, float pivotY, float size)
        {
            var (yaw, pitch) = UiGeometry.Facing(room, eye);
            layout.Placements.Add(new UiPlacement
            {
                Window = id, Anchor = UiAnchor.Table, Position = room - table,
                Yaw = yaw, Pitch = pitch, Size = size, PivotX = pivotX, PivotY = pivotY,
            });
        }
        Add("stats", new NVec(0.72f, 1.6f, 0.8f), 1, 1, 0.55f);
        Add("objectives", new NVec(0.8f, 1.45f, 0.62f), 1, 1, 0.55f);
        Add("build", new NVec(-0.78f, 1.0f, 0.5f), 0, 0, 0.55f);
        Add("calendar", new NVec(0.75f, 1.0f, 0.45f), 1, 0, 0.6f);
        Add("focus", new NVec(0, 1.42f, 0.95f), 0.5f, 0.5f, 0.7f);
        return layout;
    }

    public static void Update(VrSession session, TableRig vrRig)
    {
        var h = Instance;
        h.vr = session;
        h.rig = vrRig;
        VrUi.Update(h);
    }

    // The registered windows, for telling which one a game UI object belongs to.
    public static readonly string[] WindowIds = { "stats", "objectives", "build", "calendar", "focus" };

    public bool Active => rig != null && rig.Active;
    public bool CanArrange => rig != null && rig.InWorld;
    public Camera UiCamera => VrMenuPanel.UiCamera;
    public RenderTexture UiTexture => VrMenuPanel.Texture;
    public IReadOnlyList<Canvas> UiCanvases => VrMenuPanel.Canvases;
    public Pose Head => vr.Head;
    public Pose LeftHand => vr.Left.Pose;
    public Pose RightHand => vr.Right.Pose;
    public bool LeftHanded
    {
        get => Plugin.Instance.LeftHanded.Value;
        set => Plugin.Instance.LeftHanded.Value = value;
    }
    public Vector3 TableCentre => rig.TableCentre;
    public Pose TrackingToWorld(Pose p) => rig.TrackingToWorld(p);
    public float WorldScale => rig.WorldScale;
    public int VisualLayer => VrCore.VisualLayer;
    public Material MakeTexturedMaterial(Texture texture) => Primitives.MakeTextured(texture, transparent: true);
    public void PrepareCamera(Camera cam) => TpmHost.KeepOutOfGi(cam);
    public void ShowFullPanel(bool show) => VrMenuPanel.ShowFull = show;
    public string ConfigDirectory => BepInEx.Paths.ConfigPath;
    public void Log(string message) => Plugin.Logger.LogInfo(message);
}
