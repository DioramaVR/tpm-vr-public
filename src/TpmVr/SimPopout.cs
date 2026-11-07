using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using NVec = System.Numerics.Vector3;

namespace TpmVr;

// Popout windows (core VrPopout): Free mode, "Windows: Popout" on the UI tab,
// each window drawn on its own. Checks that the window roots get their own canvas
// and layer, that the game's UI raycast at a window's pixel stays in that window,
// that a click on the build bar lands, frame time in Panel / Free Cutout / Free
// Popout, and that going back to Panel puts every root back as it was. Puts the
// mode, the Windows choice and the Free arrangement back.
static partial class SimScript
{
    static readonly (string id, string root)[] PopoutRoots =
        { ("stats", "FinanceUI"), ("objectives", "ObjectivesContainer"), ("build", "HubMenuUI"), ("calendar", "CalendarUI") };

    static string RootState()
    {
        var parts = new List<string>();
        foreach (var (id, name) in PopoutRoots)
        {
            var go = GameObject.Find(name);
            parts.Add(go == null ? $"{id}: not found"
                : $"{id}: layer {go.layer}, canvas {go.GetComponent<Canvas>() != null}, raycaster {go.GetComponent<GraphicRaycaster>() != null}");
        }
        return string.Join("; ", parts);
    }

    static float measuredMs;

    // Average frame time over a few seconds, into measuredMs.
    static IEnumerable<float> MeasureFrames(string what)
    {
        yield return 1f; // settle after a change
        int f0 = Time.frameCount;
        float t0 = Time.realtimeSinceStartup;
        yield return 5f;
        int frames = Time.frameCount - f0;
        measuredMs = frames > 0 ? (Time.realtimeSinceStartup - t0) * 1000f / frames : 0;
        Log($"[popout] frame time {what}: {measuredMs:F2} ms ({frames} frames)");
    }

    // The game's UI raycast at a pixel (our filter applies), first hit and who owns it.
    static string RaycastAt(Vector2 pixel)
    {
        var es = EventSystem.current;
        if (es == null) return "no EventSystem";
        var data = new PointerEventData(es) { position = pixel };
        var hits = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        es.RaycastAll(data, hits);
        if (hits.Count == 0) return "no hits";
        var go = hits[0].gameObject;
        var owner = TpmUi.WindowIds.FirstOrDefault(id => VrUiKit.VrUi.Owns(id, go)) ?? "none";
        return $"{hits.Count} hits, first '{go?.name}' (window {owner})";
    }

    // The UI tab's "Windows" choice (Popout / Cutout), stepped like its arrows.
    static VrUiKit.VrSetting WindowsSetting => VrUiKit.VrSettings.All.FirstOrDefault(s => s.Section == "UI" && s.Label == "Windows");

    static void SetWindows(string value)
    {
        var s = WindowsSetting;
        for (int i = 0; i < 3 && s != null && s.Value != value; i++) s.Step(1);
        Log($"[popout] windows {VrUiKit.VrUi.Windows} (asked {value})");
    }

    static IEnumerator<float> PopoutScenario(VrSession vr, TableRig rig, Plugin cfg)
    {
        var modeBefore = VrUiKit.VrUi.Mode;
        string windowsBefore = VrUiKit.VrUi.Windows.ToString();
        var layoutDir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "tpmvr-layouts");
        var files = new[] { "free.txt" }.Select(f => System.IO.Path.Combine(layoutDir, f)).ToArray();
        var before = files.Select(f => System.IO.File.Exists(f) ? System.IO.File.ReadAllText(f) : null).ToArray();
        var ahead = new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(25));
        var left = new Pose(ahead.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(-40) * Pose.Pitch(10)));
        var right = new Pose(ahead.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(40) * Pose.Pitch(10)));
        vr.SimulateHead(ahead);
        Log($"[popout] start: mode {modeBefore}, windows {windowsBefore}, popout offered {VrUiKit.VrUi.PopoutOffered}");
        Log($"[popout] roots in {modeBefore}: {RootState()}");
        string rootsBefore = RootState();
        yield return 1f;

        // Panel first: frame time without windows.
        VrUiKit.VrUi.SetMode(VrUiKit.VrUi.UiMode.Panel);
        foreach (var s in MeasureFrames("Panel")) yield return s;
        float panelMs = measuredMs;
        SimShots.Request("pop_0_panel");
        yield return 1f;

        // Arranged with Cutout windows, for comparison (cut from the shared UI picture).
        VrUiKit.VrUi.SetMode(VrUiKit.VrUi.UiMode.Arranged);
        SetWindows("Cutout");
        foreach (var s in MeasureFrames("Arranged, Cutout")) yield return s;
        float freeMs = measuredMs;
        vr.SimulateHead(left);
        yield return 1f;
        SimShots.Request("pop_1_cutout_left");
        yield return 0.5f;
        vr.SimulateHead(right);
        yield return 1f;
        SimShots.Request("pop_2_cutout_right");
        yield return 0.5f;

        // Popout, picked on the UI tab: Arranged, then the Windows row.
        vr.SimulateHead(ahead);
        VrUiKit.VrUi.ToggleMenu();
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "UI")) yield return step;
        foreach (var step in ClickMenu(cfg, "Arranged")) yield return step;
        SetWindows("Popout");
        Log($"[popout] mode {VrUiKit.VrUi.Mode}, windows {VrUiKit.VrUi.Windows}");
        SimShots.Request("pop_3_menu");
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "X")) yield return step;
        yield return 1.5f;
        Log($"[popout] roots with Popout windows: {RootState()}");

        vr.SimulateHead(left);
        yield return 1f;
        SimShots.Request("pop_4_popout_left");
        yield return 0.5f;
        vr.SimulateHead(right);
        yield return 1f;
        SimShots.Request("pop_5_popout_right");
        yield return 0.5f;

        // Raycasts at each window's centre: the hit must be that window's own UI (or nothing).
        foreach (var (id, _) in PopoutRoots)
        {
            var rect = VrUiKit.VrUi.WindowRect(id);
            if (!VrUiKit.VrUi.WindowVisible(id) || !VrUiKit.VrUi.PixelToTracking(id, rect.center, out var p))
            {
                Log($"[popout] {id}: not shown");
                continue;
            }
            AimAtTracking(cfg, p);
            yield return 0.4f;
            Log($"[popout] laser on {id} ({rect}): pointer window {VrPointer.HitWindow ?? "-"}, game raycast: {RaycastAt(rect.center)}");
        }
        Log($"[popout] hits dropped by the filter so far: {PopoutClicks.Dropped}");

        // A click on the build bar: the build menu should open (the bar or the pop-ups window grows).
        vr.SimulateHead(left);
        var build = VrUiKit.VrUi.WindowRect("build");
        var focusBefore = VrUiKit.VrUi.WindowRect("focus");
        var aimPixel = new Vector2(build.xMin + build.width * 0.15f, build.yMin + build.height * 0.5f);
        if (VrUiKit.VrUi.PixelToTracking("build", aimPixel, out var buildPoint))
        {
            AimAtTracking(cfg, buildPoint);
            yield return 0.5f;
            Log($"[popout] before click: {RaycastAt(aimPixel)}");
            trigger = true;
            yield return 0.15f;
            trigger = false;
            yield return 1.5f;
            Log($"[popout] after click: build {build} -> {VrUiKit.VrUi.WindowRect("build")}, pop-ups {focusBefore} -> {VrUiKit.VrUi.WindowRect("focus")} (shown {VrUiKit.VrUi.WindowVisible("focus")})");
            SimShots.Request("pop_6_after_click");
            yield return 1f;
            VirtualKeyboard.Hold(VKey.Escape);
            yield return 1f;
        }
        else Log("[popout] build window not shown; no click test");

        vr.SimulateHead(ahead);
        foreach (var s in MeasureFrames("Free, Popout")) yield return s;
        float popoutMs = measuredMs;
        Log($"[popout] frame time: Panel {panelMs:F2} ms, Free Cutout {freeMs:F2} ms, Free Popout {popoutMs:F2} ms");

        // Back to Panel through the menu: every root as it was.
        VrUiKit.VrUi.ToggleMenu();
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "UI")) yield return step;
        foreach (var step in ClickMenu(cfg, "Panel")) yield return step;
        foreach (var step in ClickMenu(cfg, "X")) yield return step;
        yield return 1f;
        string rootsAfter = RootState();
        Log($"[popout] roots back in {VrUiKit.VrUi.Mode}: {rootsAfter} | same as before: {rootsAfter == rootsBefore}");
        SimShots.Request("pop_7_panel_again");
        yield return 1f;

        // Put things back.
        for (int i = 0; i < files.Length; i++)
            if (before[i] != null) System.IO.File.WriteAllText(files[i], before[i]);
            else if (System.IO.File.Exists(files[i])) System.IO.File.Delete(files[i]);
        VrUiKit.VrUi.ReloadFree();
        SetWindows(windowsBefore);
        VrUiKit.VrUi.SetMode(modeBefore);
        yield return 1f;
    }
}
