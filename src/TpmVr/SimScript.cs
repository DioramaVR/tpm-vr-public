using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using TPS.Core;
using TPS.Game;
using TPS.Game.UI;
using Unity.Entities;
using UnityEngine;
using UnityEngine.InputSystem;
using NVec = System.Numerics.Vector3;

namespace TpmVr;

// Headset-free test harness (Debug.SimulateVr). Drives the right hand through
// a scripted sequence in the loaded museum and logs what the game does:
// cursor follows the laser, hover, click, and hold-to-move. Non-destructive:
// any item picked up is put back exactly where it was.
static partial class SimScript
{
    static IEnumerator<float> script, nested;
    static Pose rightPose = Pose.Identity;
    static bool trigger, grip, cinemaShot, sway, primaryPress, rightStickClick, leftX, leftY, leftTrigger, leftGrip;
    static float leftStickY, rightStickY;
    static float stickHoldX, stickHoldY;   // right stick, held until changed
    static int rightFlick, leftFlick;
    static float leftStickHoldX;   // left stick x, held until changed
    static Pose leftPose = new(new NVec(-0.2f, 1.0f, 0.3f), System.Numerics.Quaternion.Identity);
    static float resumeAt;

    static readonly NVec HandPosition = new(0.15f, 1.1f, 0.25f);

    public static void Update(VrSession vr, TableRig rig, Plugin cfg)
    {
        // Scenarios keep running when the game leaves the museum (e.g. the crate-opening scene).
        bool scenarioRunning = Scenario != "" && script != null && rig.Active;
        if (!scenarioRunning && (!rig.Active || !rig.InWorld))
        {
            // VR runs from the main menu (cinema mode); record what that looks like once.
            if (rig.Active && !cinemaShot && Time.unscaledTime > 8f)
            {
                cinemaShot = true;
                AimAtTracking(cfg, VrMenuPanel.PixelToTracking(new Vector2(Screen.width * 0.5f, Screen.height * 0.4f)));
                vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(0)));
                SimShots.Request("00_cinema");
            }
            vr.Right.Simulate(rightPose, false, false);
            script = null;
            return;
        }
        if (script == null) vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(45)));
        if (sway)
        {
            float s = MathF.Sin(Time.unscaledTime * 1.3f);
            vr.SimulateHead(new Pose(new NVec(0.06f * s, 1.5f, 0), System.Numerics.Quaternion.Normalize(Pose.Yaw(12 * s) * Pose.Pitch(45))));
        }
        script ??= Run(vr, rig, cfg);
        if (Time.unscaledTime >= resumeAt && script != null)
        {
            // A step can start a nested sequence (ClickPanel); run it to the end first.
            if (nested != null && nested.MoveNext()) resumeAt = Time.unscaledTime + nested.Current;
            else
            {
                nested = null;
                if (script.MoveNext()) resumeAt = Time.unscaledTime + script.Current;
                else script = Done();
            }
        }
        vr.Right.Simulate(rightPose, trigger, grip, primaryPress, false, rightStickClick, rightFlick, stickHoldX, rightStickY + stickHoldY);
        vr.Left.Simulate(leftPose, leftTrigger, leftGrip, leftX, leftY, false, leftFlick, leftStickHoldX, leftStickY);
        primaryPress = rightStickClick = leftX = leftY = false;
        rightFlick = leftFlick = 0;
        leftStickY = rightStickY = 0;
    }

    static IEnumerator<float> Done()
    {
        while (true) yield return 60f;
    }

    static void Log(string msg) => Plugin.Logger.LogInfo($"[sim] {msg}");

    // Point the laser at a world position (accounting for the configured pointer pitch).
    static void AimAt(TableRig rig, Plugin cfg, NVec world)
    {
        var target = rig.WorldToTracking(world);
        var look = Pose.LookRotation(target - HandPosition, NVec.UnitY);
        rightPose = new Pose(HandPosition, System.Numerics.Quaternion.Normalize(look * Pose.Pitch(-cfg.PointerPitch.Value)));
    }

    // Point at a spot on the menu panel (fractions of the screen, origin bottom-left) and click.
    // Waits use unscaled time, so this works while the game is paused.
    static IEnumerator<float> ClickPanelSteps(Plugin cfg, Vector2 fraction)
    {
        var pixel = new Vector2(fraction.x * Screen.width, fraction.y * Screen.height);
        AimAtTracking(cfg, VrMenuPanel.PixelToTracking(pixel));
        yield return 0.4f;
        trigger = true;
        yield return 0.15f;
        trigger = false;
        yield return 1f;
    }

    static float ClickPanel(Plugin cfg, Vector2 fraction)
    {
        nested = ClickPanelSteps(cfg, fraction);
        return 0;
    }

    static void AimAtTracking(Plugin cfg, NVec target)
    {
        var look = Pose.LookRotation(target - HandPosition, NVec.UnitY);
        rightPose = new Pose(HandPosition, System.Numerics.Quaternion.Normalize(look * Pose.Pitch(-cfg.PointerPitch.Value)));
    }

    static IEnumerator<float> Run(VrSession vr, TableRig rig, Plugin cfg)
    {
        Log("museum camera active; waiting for the loading screen to finish");
        // The museum camera comes up while the loading screen is still showing
        // (time scale 0). Wait for game time to run (or 25 s, in case the save
        // is paused), then let the loading screen fade out.
        float waitStart = Time.unscaledTime;
        while (Time.timeScale == 0 && Time.unscaledTime - waitStart < 25f)
            yield return 0.25f;
        yield return 4f;
        var centre = rig.AnchorN();
        AimAt(rig, cfg, centre);
        Log($"loaded; {MouseInfo()}");
        DumpInputBindings();
        yield return 1f;

        // Performance: what each render costs, at headset eye resolution, with
        // the head swaying like a real one. Each phase gets a fresh window.
        sway = true;
        var phases = new (string name, int? refresh, bool uiOff)[]
        {
            ("menu every frame", 1, false),
            ("defaults: menu every 2nd frame", null, false),
            ("menu camera off", null, true),
        };
        foreach (var p in Scenario == "" ? phases : phases[..0])
        {
            PerfSwitches.MenuRefreshOverride = p.refresh;
            PerfSwitches.UiCameraOff = p.uiOff;
            yield return 1f;
            PerfLog.Label = p.name;
            PerfLog.Reset();
            yield return 5.2f;
        }
        PerfLog.Label = "";
        PerfSwitches.MenuRefreshOverride = null;
        PerfSwitches.UiCameraOff = false;
        SimShots.Request("00_sway");
        yield return 0.5f;
        sway = false;
        vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(45)));

        SimShots.Request("01_idle");
        yield return 0.5f;

        if (Scenario == "poster" || Scenario == "light")
        {
            var poster = Scenario == "light" ? LightScenario(vr, rig) : PosterScenario();
            while (poster.MoveNext()) yield return poster.Current;
            Log("script finished");
            QuitIfRequested(cfg);
            yield break;
        }

        // 0. Dismiss the level intro card by clicking its Continue button on the VR panel.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var button = GameObject.Find("buttonContinue");
            if (button == null || VrMenuPanel.UiCamera == null) break;
            var screen = VrMenuPanel.UiCamera.WorldToScreenPoint(button.transform.position);
            var target = VrMenuPanel.PixelToTracking(new Vector2(screen.x, screen.y));
            AimAtTracking(cfg, target);
            yield return 0.4f;
            Log($"intro card: aiming at buttonContinue pixel ({screen.x:F0},{screen.y:F0}) onMenu {VrPointer.OnMenu} | {MouseInfo()}");
            trigger = true;
            yield return 0.15f;
            trigger = false;
            yield return 1.5f;
            Log($"after clicking Continue: button still active {GameObject.Find("buttonContinue") != null} | {MouseInfo()}");
        }
        SimShots.Request("01a_after_intro");
        yield return 0.5f;
        if (Scenario != "")
        {
            var crate = Scenario == "crate" ? CrateScenario(rig, cfg) : Scenario == "ui" ? UiScenario() : Scenario == "windows" ? WindowsScenario() : Scenario == "vrui" ? VrUiScenario(vr, rig, cfg) : Scenario == "free" ? FreeScenario(vr, rig, cfg) : Scenario == "controls" ? ControlsScenario(vr, rig, cfg) : Scenario == "wrist" ? WristScenario(vr, rig, cfg) : Scenario == "popout" ? PopoutScenario(vr, rig, cfg) : Scenario == "backdrop" ? BackdropScenario(vr) : Scenario == "diorama" ? DioramaScenario(vr, rig) : Scenario == "cullprobe" ? CullProbeScenario(vr, rig) : Scenario == "spectator" ? SpectatorScenario(vr) : RewardScenario(vr, rig, cfg);
            while (crate.MoveNext()) yield return crate.Current;
            Log("script finished");
            QuitIfRequested(cfg);
            yield break;
        }

        // 1. Does the game cursor follow the laser?
        var offsets = new[] { NVec.Zero, new NVec(8, 0, 0), new NVec(-8, 0, 0), new NVec(0, 0, 8), new NVec(0, 0, -8) };
        foreach (var o in offsets)
        {
            var target = centre + o;
            AimAt(rig, cfg, target);
            yield return 0.5f;
            var cursor = CursorManager.Instance?.WorldPosition ?? default;
            float err = new NVec(cursor.x - target.X, 0, cursor.z - target.Z).Length();
            Log($"aim {Fmt(target)} -> cursor {cursor} (horizontal error {err:F1}) | native ray calls {NativeRayHook.Calls} overridden {NativeRayHook.Overrides} | {MouseInfo()}");
            NativeRayHook.Calls = NativeRayHook.Overrides = 0;
        }

        // 1b. Menu panel: pointing at it should move the mouse onto the game's UI.
        var panelPixels = new[] { new Vector2(0.5f, 0.5f), new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f), new Vector2(0.25f, 0.75f) };
        foreach (var f in panelPixels)
        {
            var want = new Vector2(f.x * Screen.width, f.y * Screen.height);
            AimAtTracking(cfg, VrMenuPanel.PixelToTracking(want));
            yield return 0.4f;
            Log($"panel aim at pixel {want}: onMenu {VrPointer.OnMenu} mouse {Mouse.current?.position.ReadValue()}");
        }

        // Click HUD buttons on the panel: pause, then play, checked via time scale.
        float before = Time.timeScale;
        yield return ClickPanel(cfg, new Vector2(0.923f, 0.035f));
        float paused = Time.timeScale;
        Log($"clicked pause on panel: timeScale {before} -> {paused}");
        // Look up at the panel for the screenshot, then back down at the table.
        vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(5)));
        yield return 0.3f;
        SimShots.Request("01b_menu");
        yield return 0.5f;
        vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(45)));
        yield return ClickPanel(cfg, new Vector2(0.9625f, 0.035f));
        Log($"clicked play on panel: timeScale {paused} -> {Time.timeScale}");

        // Open toolbar menus (they contain scrolling lists), screenshot, close with Escape.
        var toolbar = new[] { new Vector2(0.047f, 0.037f), new Vector2(0.085f, 0.037f) };
        for (int i = 0; i < toolbar.Length; i++)
        {
            yield return ClickPanel(cfg, toolbar[i]);
            SimShots.Request($"06_menu_{i}");
            yield return 1f;
            VirtualKeyboard.TapEscape();
            yield return 1f;
        }

        // 2. Find a hold-selectable item by sweeping the laser in a spiral.
        GameItemInstance item = null;
        NVec itemAim = default;
        for (float r = 2; r <= 30 && item == null; r += 2)
        {
            for (int a = 0; a < 12 && item == null; a++)
            {
                float ang = a * MathF.PI / 6;
                itemAim = centre + new NVec(MathF.Cos(ang) * r, 0, MathF.Sin(ang) * r);
                AimAt(rig, cfg, itemAim);
                yield return 0.12f;
                var sel = SelectResult();
                var hit = sel?.Result?.TryCast<GameItemInstance>();
                if (hit != null && sel.IsHoldSelectable) item = hit;
                if (a == 0)
                    Log($"spiral r={r}: cursor {CursorManager.Instance?.WorldPosition} result {sel?.Result?.GetIl2CppType().Name ?? "-"} hitType {sel?.HitType} | overGui {App.Instance?.InputManager?.IsMouseOverGui} live UI: {LiveUiUnderMouse()}");
            }
        }
        if (item == null)
        {
            Log("no hold-selectable item found under the laser in a 30 m spiral; hover is not working");
            SimShots.Request("02_no_item");
            yield return 1f;
            QuitIfRequested(cfg);
            yield break;
        }
        var startPos = item.WorldPosition;
        Log($"hovering item '{item.Name}' at {startPos} (highlighted entity {HighlightManager.Instance?._highlightedEntity.Index})");
        SimShots.Request("02_hover");
        yield return 0.5f;

        // 3. Click.
        trigger = true;
        yield return 0.15f;
        trigger = false;
        yield return 1f;
        Log($"after click: mode {Mode()} | {MouseInfo()}");
        SimShots.Request("03_clicked");
        yield return 1f;

        // 4. Grab, drag somewhere (likely invalid for this item), release: expect
        //    a valid drop to place it, an invalid one to cancel back to the start.
        AimAt(rig, cfg, itemAim);
        yield return 0.5f;
        trigger = true;
        yield return 1.5f;
        var placing = CursorManager.Instance?._mode?.TryCast<CursorModePlaceItem>();
        Log($"grab 1 holding: mode {Mode()} item {(placing?.Item?.Name ?? "-")} grabbing {VrPointer.Grabbing}");
        bool droppedValid = false;
        if (placing != null)
        {
            AimAt(rig, cfg, itemAim + new NVec(3, 0, 0));
            yield return 1f;
            droppedValid = placing.IsItemValidForPlacement();
            Log($"grab 1 dragged +3m: item now {placing.Item?.WorldPosition} valid {droppedValid}");
            SimShots.Request("04_holding_moved");
            yield return 0.3f;
            if (droppedValid)
            {
                // Don't rearrange the user's museum: drag back before letting go.
                AimAt(rig, cfg, itemAim);
                yield return 1f;
            }
        }
        trigger = false;
        yield return 1.5f;
        Log($"grab 1 released: mode {Mode()} item at {item.WorldPosition} (start {startPos})");
        SimShots.Request("05_released");
        yield return 1f;

        // 5. Grab and release in place: expect a valid drop, placed where it was.
        AimAt(rig, cfg, itemAim);
        yield return 0.5f;
        trigger = true;
        yield return 1.5f;
        placing = CursorManager.Instance?._mode?.TryCast<CursorModePlaceItem>();
        Log($"grab 2 holding: mode {Mode()} valid {placing?.IsItemValidForPlacement()}");
        // Rotate with A eight times (45 degrees each: a full turn, so the museum ends up unchanged).
        if (placing != null)
        {
            float startRotation = placing.Item?.WorldRotation ?? 0;
            for (int i = 1; i <= 8; i++)
            {
                primaryPress = true;
                yield return 0.6f;
                Log($"A press {i}: item rotation {placing.Item?.WorldRotation:F0} (start {startRotation:F0}) valid {placing.IsItemValidForPlacement()}");
                if (i == 1) SimShots.Request("07_rotated");
            }
            // Right stick click cycles placement mode (free placement); twice restores it.
            bool freeBefore = placing.IsFreePlacement;
            rightStickClick = true;
            yield return 0.5f;
            bool freeAfter = placing.IsFreePlacement;
            rightStickClick = true;
            yield return 0.5f;
            Log($"stick click: free placement {freeBefore} -> {freeAfter} -> {placing.IsFreePlacement}");
        }
        trigger = false;
        yield return 1.5f;
        Log($"grab 2 released: mode {Mode()} item at {item.WorldPosition} (start {startPos})");

        // Camera sticks (not placing; the table standard): the pointer stick pans, the off
        // stick zooms (and scales the table) and turns the table and game camera, click pitches.
        var cameraLogic = App.Instance?.MainCameraLogic?.TryCast<TopDownCameraLogic>();
        if (cameraLogic != null)
        {
            var focal0 = cameraLogic.CurrentFocalPoint;
            for (float t = 0; t < 1f; t += Time.unscaledDeltaTime)
            {
                rightStickY = 1;
                yield return 0;
            }
            yield return 0.5f;
            Log($"pointer stick up 1s (pan): focal {focal0} -> {cameraLogic.CurrentFocalPoint}");

            float zoom0 = cameraLogic.CurrentZoomScalar, tableScale0 = rig.Scale;
            for (float t = 0; t < 1f; t += Time.unscaledDeltaTime)
            {
                leftStickY = 1;
                yield return 0;
            }
            yield return 0.5f;
            Log($"off stick up 1s (zoom): zoom {zoom0:F2} -> {cameraLogic.CurrentZoomScalar:F2}, table scale {tableScale0:F1} -> {rig.Scale:F1}");
            rig.Scale = tableScale0;   // test only: keep the player's table scale

            float yaw0 = rig.Yaw, camYaw0 = cameraLogic._currentRotation;
            leftFlick = 1;
            yield return 0.6f;
            float yaw1 = rig.Yaw, camYaw1 = cameraLogic._currentRotation;
            leftFlick = -1;
            yield return 0.6f;
            Log($"off-stick flick right then left: table yaw {yaw0:F0} -> {yaw1:F0} -> {rig.Yaw:F0}, camera yaw {camYaw0:F1} -> {camYaw1:F1} -> {cameraLogic._currentRotation:F1}");

            float pitch0 = cameraLogic._currentPitch;
            rightStickClick = true;
            yield return 0.6f;
            float pitch1 = cameraLogic._currentPitch;
            rightStickClick = true;
            yield return 0.6f;
            Log($"right stick click twice: camera pitch {pitch0:F1} -> {pitch1:F1} -> {cameraLogic._currentPitch:F1}");
            for (int i = 0; i < 4; i++)
            {
                leftFlick = 1;
                yield return 0.4f;
                rightStickClick = true;
                yield return 0.4f;
            }
            SimShots.Request("07b_camera_tutorial");

            // The Tutorial hotkey bar, menu closed: clicking its buttons presses the game's keys.
            // Each set is its own bar, switched on by itself: turn this one on for the test.
            var tutorialBar = VrUiKit.VrHotkeys.Set("Tutorial");
            bool tutorialWasOn = VrUiKit.VrHotkeys.IsOn(tutorialBar);
            VrUiKit.VrHotkeys.SetOn(tutorialBar, true);
            yield return 0.5f;
            var qAt = VrUiKit.VrUi.HotbarPointOf("Turn left");
            Log($"hotbar: shown {VrUiKit.VrHotbar.Visible}, set '{VrUiKit.VrHotkeys.Current?.Name}', Q at {qAt}");
            if (qAt is NVec q)
            {
                float camYawQ = cameraLogic._currentRotation;
                AimAtTracking(cfg, q);
                yield return 0.4f;
                Log($"hotbar: laser on bar {VrPointer.OnVrMenu}");
                SimShots.Request("07c_hotbar");
                yield return 0.5f;
                trigger = true;
                yield return 0.15f;
                trigger = false;
                yield return 0.8f;
                Log($"hotbar Q: camera yaw {camYawQ:F1} -> {cameraLogic._currentRotation:F1}");
                if (VrUiKit.VrUi.HotbarPointOf("Pan forward") is NVec wAt)
                {
                    var focalW = cameraLogic.CurrentFocalPoint;
                    AimAtTracking(cfg, wAt);
                    yield return 0.4f;
                    trigger = true;
                    yield return 0.6f;
                    trigger = false;
                    yield return 0.6f;
                    Log($"hotbar W held 0.6s: focal {focalW} -> {cameraLogic.CurrentFocalPoint}");
                }
            }
            VrUiKit.VrHotkeys.SetOn(tutorialBar, tutorialWasOn);
            yield return 0.5f;
        }
        // Left hand: X pauses / plays, Y is back (Escape).
        yield return 1f;
        float scale0 = Time.timeScale;
        leftX = true;
        yield return 0.6f;
        float scale1 = Time.timeScale;
        leftX = true;
        yield return 0.6f;
        Log($"left X twice: timeScale {scale0} -> {scale1} -> {Time.timeScale}");

        bool menu0 = App.Instance?.InputManager?.IsFullscreenMenuActive == true;
        leftY = true;
        yield return 1.2f;
        bool menu1 = App.Instance?.InputManager?.IsFullscreenMenuActive == true;
        SimShots.Request("08_left_y_menu");
        yield return 0.5f;
        leftY = true;
        yield return 1.2f;
        Log($"left Y twice: fullscreen menu {menu0} -> {menu1} -> {App.Instance?.InputManager?.IsFullscreenMenuActive == true}");

        if (CursorManager.Instance?._mode?.TryCast<CursorModePlaceItem>() != null)
        {
            // Does an ordinary click place it?
            trigger = true;
            yield return 0.15f;
            trigger = false;
            yield return 1.5f;
            Log($"grab 2 extra click: mode {Mode()} item at {item.WorldPosition}");
        }
        // Safety net: never leave the user's item stuck to the cursor. Escape
        // cancels a move (calling OnCancelItem directly crashes the game).
        if (CursorManager.Instance?._mode?.TryCast<CursorModePlaceItem>() != null)
        {
            Log("item still in placement mode; pressing Escape");
            VirtualKeyboard.TapEscape();
            yield return 1f;
            Log($"after Escape: mode {Mode()} item at {item.WorldPosition}");
        }
        Log("script finished");
        QuitIfRequested(cfg);
    }

    // Test runs quit the game themselves so the next build can be deployed.
    static void QuitIfRequested(Plugin cfg)
    {
        if (!cfg.QuitAfterTest.Value) return;
        Log("quitting game (Debug.QuitAfterTest)");
        Application.Quit();
    }

    // Writes the game's input bindings to BepInEx/tpmvr-input-actions.json (for mapping controller buttons).
    static void DumpInputBindings()
    {
        var actions = App.Instance?.MainCameraLogic?.TryCast<TopDownCameraLogic>()?.InputActions;
        var json = actions?.asset?.ToJson();
        if (json == null)
        {
            Log("input bindings: not available");
            return;
        }
        var path = Path.Combine(BepInEx.Paths.BepInExRootPath, "tpmvr-input-actions.json");
        File.WriteAllText(path, json);
        Log($"input bindings written to {path} ({json.Length} chars)");
    }

    static ESCursorSelectResult SelectResult()
    {
        var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
        return world?.GetExistingSystemManaged(Il2CppType.Of<ESCursorSelectResult>())?.TryCast<ESCursorSelectResult>();
    }

    static string Mode() => CursorManager.Instance?._mode?.GetIl2CppType().Name ?? "-";

    static string MouseInfo()
    {
        var m = Mouse.current;
        var input = App.Instance?.InputManager;
        return $"Mouse.current={m?.name} pos={m?.position.ReadValue()} left={m?.leftButton.isPressed} | game mouse {input?.GetMousePosition()} overGui {input?.IsMouseOverGui} fullscreenMenu {input?.IsFullscreenMenuActive} | focused {Application.isFocused} insideWindow {input?.IsMouseInsideWindow()} enabled {input?.Enabled} mouseActive {input?.IsMouseActive()} lastMoved {input?.TimeMouseMoved:F1} now {Time.time:F1} | timeScale {Time.timeScale} | live UI: {LiveUiUnderMouse()} | cached: {UiUnderMouse(input)}";
    }

    // Our own UI raycast at the virtual mouse position, independent of the game's cached result.
    static string LiveUiUnderMouse()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        var m = Mouse.current;
        if (es == null || m == null) return "no event system";
        var data = new UnityEngine.EventSystems.PointerEventData(es) { position = m.position.ReadValue() };
        var results = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
        es.RaycastAll(data, results);
        if (results.Count == 0) return "nothing";
        var names = new List<string>();
        for (int i = 0; i < results.Count && i < 3; i++)
        {
            var go = results[i].gameObject;
            names.Add(go == null ? "?" : $"{go.transform.parent?.name}/{go.name}");
        }
        return string.Join(", ", names);
    }

    static string UiUnderMouse(TPS.Core.Input.InputManager input)
    {
        var results = input?.RaycastResults;
        if (results == null || results.Count == 0) return "-";
        var names = new List<string>();
        for (int i = 0; i < results.Count && i < 4; i++)
        {
            var go = results[i].gameObject;
            names.Add(go == null ? "?" : $"{go.transform.parent?.name}/{go.name}");
        }
        return string.Join(", ", names);
    }

    static string Fmt(NVec v) => $"({v.X:F1},{v.Y:F1},{v.Z:F1})";
}

// Debug.AutoContinue: click through the main menu's Continue and the level
// select's Continue so a test run lands in the most recent museum unattended.
static class AutoContinue
{
    static float next;
    static int attempts, polls;

    public static void Update()
    {
        if (attempts > 20 || Time.unscaledTime < next) return;
        if (App.Instance?.MainCameraLogic?.TryCast<TopDownCameraLogic>() != null) return;
        next = Time.unscaledTime + 4f;

        var frontend = UnityEngine.Object.FindObjectOfType<FrontendV2UI>();
        // Leave a trail when stuck somewhere unexpected.
        if (++polls % 3 == 0)
        {
            Directory.CreateDirectory(SimShots.Dir);
            ScreenCapture.CaptureScreenshot(Path.Combine(SimShots.Dir, $"auto_{polls:D2}.png"));
            Plugin.Logger.LogInfo($"[auto] waiting: frontend {(frontend == null ? "none" : frontend.isActiveAndEnabled ? "active" : "inactive")}, camera {App.Instance?.MainCameraLogic?.GetIl2CppType().Name ?? "-"}, timeScale {Time.timeScale}");
        }
        if (frontend != null && frontend.isActiveAndEnabled)
        {
            attempts++;
            Plugin.Logger.LogInfo("[auto] main menu Continue");
            frontend.OnContinueButtonClick();
            return;
        }
        var metagame = UnityEngine.Object.FindObjectOfType<MetagameLevelSelectInspector>();
        if (metagame != null && metagame.isActiveAndEnabled)
        {
            if (SimScript.HoldOnLevelSelect()) return;
            attempts++;
            Plugin.Logger.LogInfo("[auto] metagame level Continue");
            metagame.OnClickContinue();
            return;
        }
        var levelSelect = UnityEngine.Object.FindObjectOfType<LevelSelectInspectorUI>();
        if (levelSelect != null && levelSelect.isActiveAndEnabled)
        {
            if (SimScript.HoldOnLevelSelect()) return;
            attempts++;
            Plugin.Logger.LogInfo("[auto] level select Continue");
            levelSelect.OnClickContinue();
            return;
        }
        var popup = UnityEngine.Object.FindObjectOfType<PopUpLevelMenuInspector>();
        if (popup != null && popup.isActiveAndEnabled)
        {
            attempts++;
            Plugin.Logger.LogInfo("[auto] level popup Continue");
            popup.OnClickContinue();
        }
    }
}
