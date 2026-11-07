using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TPS.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace TpmVr;

// GameAssembly.dll is Denuvo-protected, so Il2CppInterop's class injection
// (which needs signature scans of IL2CPP internals) is unavailable. We never
// inject types; per-frame work rides on Harmony patches of existing methods.
[BepInPlugin(Guid, "TPM VR", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "dvr.tpm";
    public const string Version = "0.1.0";
    internal static ManualLogSource Logger;
    internal static Plugin Instance;

    internal ConfigEntry<bool> VrEnabled;
    internal ConfigEntry<bool> FlipVertical;
    internal ConfigEntry<bool> ReconLogging;
    internal ConfigEntry<bool> LeftHanded;
    internal ConfigEntry<float> PointerPitch;
    internal ConfigEntry<int> MenuRefreshEveryFrames;
    internal ConfigEntry<float> MenuWidth;
    internal ConfigEntry<float> MenuHeight;
    internal ConfigEntry<float> MenuDistance;
    internal ConfigEntry<bool> SimulateVr;
    internal ConfigEntry<bool> AutoContinueToMuseum;
    internal ConfigEntry<bool> RunTestScript;
    internal ConfigEntry<bool> QuitAfterTest;
    internal ConfigEntry<bool> RecordScreens;

    public override void Load()
    {
        Logger = Log;
        Instance = this;
        VrCore.Init(Log, "TpmVr");
        VrEnabled = Config.Bind("VR", "Enabled", true, "Start OpenVR and render to the headset.");
        FlipVertical = Config.Bind("VR", "FlipVertical", true, "Flip submitted eye images vertically (if the headset view is upside down).");
        ReconLogging = Config.Bind("Debug", "ReconLogging", true, "Log a diagnostic dump (cameras, renderer layers, canvases, UI structure) a few seconds after each scene change.");
        LeftHanded = Config.Bind("Controls", "LeftHanded", false, "Laser pointer on the left controller instead of the right.");
        PointerPitch = Config.Bind("Controls", "PointerPitch", 45f, "Tilt the laser down (+) or up (-) relative to the controller, degrees.");
        MenuRefreshEveryFrames = Config.Bind("Menu", "RefreshEveryFrames", 2, "Redraw the VR menu panel every N frames. 1 = every frame (smoothest, slowest).");
        MenuWidth = Config.Bind("Menu", "Width", 1.6f, "Width of the floating menu panel, metres.");
        MenuHeight = Config.Bind("Menu", "Height", 1.55f, "Height of the menu panel's centre above the floor, metres.");
        MenuDistance = Config.Bind("Menu", "Distance", 1.3f, "Distance of the menu panel in front of the play-space origin, metres.");
        SimulateVr = Config.Bind("Debug", "SimulateVr", false, "Run without a headset: fixed head pose, scripted hands, eye images saved as screenshots.");
        AutoContinueToMuseum = Config.Bind("Debug", "AutoContinue", false, "Click Continue on the main menu and level select automatically.");
        RunTestScript = Config.Bind("Debug", "RunTestScript", false, "With SimulateVr: run the scripted interaction test once in the museum.");
        QuitAfterTest = Config.Bind("Debug", "QuitAfterTest", false, "Quit the game when the test script finishes.");
        RecordScreens = Config.Bind("Debug", "RecordScreens", false, "Log every menu, canvas and camera the game opens, how it draws, and save a snapshot (BepInEx/tpmvr-shots/rec_*). For finding screens that are blank in VR.");
        // The shared core's pieces, wired to TPM: file names, the headset photo
        // (taken once the right eye has rendered), the mask shader from our
        // bundle (its properties are named _TpmVr*), and Octalux GI kept off our cameras.
        Recorder.FilePrefix = "TPM-VR";
        Recorder.SaveHeadset = SimShots.SaveEye;
        // Sim and debug shots (the core's SimShots): BepInEx/tpmvr-shots, both eyes, the UI panel and the desktop.
        SimShots.Dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "tpmvr-shots");
        SimShots.UiTexture = () => VrMenuPanel.Texture;
        SimShots.SaveRightEye = true;
        CylinderMask.ShaderSource = () => ModShaders.Get("CylinderMask");
        CylinderMask.PropertyPrefix = "_TpmVr";
        SpectatorCam.PrepareCamera = TpmHost.KeepOutOfGi;
        // The cinema screen: TPM's own opaque material (bundle Overlay shader), and the mirror kept out of the GI.
        VrCinema.PrepareCamera = TpmHost.KeepOutOfGi;
        VrCinema.ScreenMaterial = texture => Primitives.MakeTextured(texture, transparent: false);
        TpmUi.Init(this);

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(FrameHook));
        harmony.PatchAll(typeof(EndCameraHook));
        harmony.PatchAll(typeof(TpmRecon.BeginCameraHook));
        harmony.PatchAll(typeof(PopoutClicks));
        NativeRayHook.Install();
        Log.LogInfo("TPM VR hooks installed");
    }

    internal string PluginDir => Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
}

static class Vr
{
    static VrSession session;
    static TableRig rig;
    static int lastFrame = -1;

    // Runs once per frame from the EventSystem.Update postfix.
    public static void Frame()
    {
        if (Time.frameCount == lastFrame) return;
        lastFrame = Time.frameCount;
        var cfg = Plugin.Instance;
        // Popout windows need the URP renderer to draw the spare layers. The core widens its
        // masks once they can be read (not yet at load in TPM), retrying each frame until a
        // museum loads (TpmUi: PopoutPrepareWhile). The core's own retry runs only once VR
        // is up; this one runs from the main menu on, VR or not. A no-op once done.
        VrUiKit.VrPopout.PrepareLayers(VrCore.Info);
        if (cfg.AutoContinueToMuseum.Value) AutoContinue.Update();
        // Test runs must never reach the player's save. The game autosaves after
        // a number of in-game months; keep that counter at zero.
        if (cfg.SimulateVr.Value && TPS.Game.LevelManager.Instance != null)
            TPS.Game.LevelManager.Instance._monthsSinceLastAutoSave = 0;

        // Retry every 5 s until the headset is connected (e.g. Virtual Desktop not yet streaming).
        if (session == null && cfg.VrEnabled.Value && VrBoot.StartDue() && VrBoot.GraphicsOk()) Start(cfg);
        if (session == null) return;

        PerfLog.Frame(session);
        // The game caps the frame rate and drops input while unfocused; undone every frame.
        VrBoot.UncapFrameRate(session);
        VrBoot.KeepFocus();
        HandleKeys(cfg);
        // Threaded: the render thread submits last frame's eyes and paces the next.
        rig.QueuePreviousFrame();
        session.BeginFrame();
        if (session.Simulated && cfg.RunTestScript.Value) SimScript.Update(session, rig, cfg);
        TpmUi.UpdateInput(session, rig);
        rig.Update();
        SpectatorCam.Update(rig, session.Head, rig.Eye(0), rig.InWorld);
        VrPointer.Update(session, rig, cfg);
        if (cfg.RecordScreens.Value) ScreenRecorder.Update();
        SimShots.CheckRequestFile();
        Recorder.Update();

        if (Time.unscaledTime >= nextPointerLog && VrPointer.RayActive)
        {
            nextPointerLog = Time.unscaledTime + 5f;
            var cursor = TPS.Game.CursorManager.Instance;
            Plugin.Logger.LogInfo($"[pointer] ray {VrPointer.CurrentRay.origin} -> {VrPointer.CurrentRay.direction} | native ray calls {NativeRayHook.Calls}, overridden {NativeRayHook.Overrides} | cursor {cursor?.WorldPosition} mode {cursor?._mode?.GetIl2CppType().Name}");
            NativeRayHook.Calls = NativeRayHook.Overrides = 0;
        }
    }

    static float nextPointerLog;

    static void Start(Plugin cfg)
    {
        session = cfg.SimulateVr.Value
            ? VrSession.StartSimulated(1832, 1920, allowThreaded: true, head: new Pose(new System.Numerics.Vector3(0, 1.5f, 0), Pose.Pitch(45)))
            : VrSession.TryStart(cfg.PluginDir, allowThreaded: true);
        if (session == null) return;
        session.FlipVertical = cfg.FlipVertical.Value;
        // The core's table (TpmHost: TPM's pivot, defaults and old settings). Sims never save its changes.
        rig = TpmHost.Create(session, cfg);
        rig.SaveChanges = !cfg.SimulateVr.Value;
    }

    static void HandleKeys(Plugin cfg)
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.pageUpKey.wasPressedThisFrame) SetScale(cfg, rig.Scale / 1.25f);
        if (kb.pageDownKey.wasPressedThisFrame) SetScale(cfg, rig.Scale * 1.25f);
        if (kb.f8Key.wasPressedThisFrame) rig.Recenter();
        if (kb.f7Key.wasPressedThisFrame) VrUiKit.VrUi.ToggleMenu();
    }

    static void SetScale(Plugin cfg, float scale)
    {
        rig.Scale = Mathf.Clamp(scale, 5f, 500f);
        if (rig.SaveChanges) rig.SaveScale();
        Plugin.Logger.LogInfo($"Scale {rig.Scale:F1} (museum 100 m = {100f / rig.Scale:F2} m)");
    }

    public static void OnEndCamera(Camera cam)
    {
        VrPipeline.RaiseAfter(cam);
        if (rig == null || !rig.IsRightEye(cam)) return;
        rig.SubmitEyes();
        SimShots.CaptureIfRequested(rig.Left, rig.Right);
    }
}

[HarmonyPatch(typeof(EventSystem), nameof(EventSystem.Update))]
static class FrameHook
{
    static void Postfix()
    {
        Vr.Frame();
        if (Plugin.Instance.ReconLogging.Value) TpmRecon.Tick();
    }
}

// The game's own endCameraRendering handler. The right eye renders after the
// left (same depth, created later), so both textures are complete here.
[HarmonyPatch(typeof(App), nameof(App.OnEndCameraRendering))]
static class EndCameraHook
{
    static void Postfix(Camera cam) => Vr.OnEndCamera(cam);
}
