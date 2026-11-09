using System;
using System.Collections.Generic;
using System.Linq;
using TPS.Core;
using TPS.Game;
using UnityEngine;
using NVec = System.Numerics.Vector3;

namespace TpmVr;

// Investigation scenarios for the sim harness, chosen with the TPMVR_SCENARIO
// environment variable (tools/run-sim-test.sh passes it through).
static partial class SimScript
{
    static readonly string Scenario = Environment.GetEnvironmentVariable("TPMVR_SCENARIO") ?? "";

    // Opens the first crate found under the laser and records what renders it.
    static IEnumerator<float> CrateScenario(TableRig rig, Plugin cfg)
    {
        var centre = rig.AnchorN();
        DumpCameras("before crate");
        var seen = new HashSet<string>();
        NVec crateAim = default;
        bool found = false;
        for (float r = 0; r <= 40 && !found; r += 1.5f)
        {
            int steps = r == 0 ? 1 : 16;
            for (int a = 0; a < steps && !found; a++)
            {
                float ang = a * MathF.PI * 2 / steps;
                var aim = centre + new NVec(MathF.Cos(ang) * r, 0, MathF.Sin(ang) * r);
                AimAt(rig, cfg, aim);
                yield return 0.08f;
                var sel = SelectResult();
                var result = sel?.Result;
                if (result == null) continue;
                string type = result.GetIl2CppType().Name;
                string name = result.TryCast<GameItemInstance>()?.Name ?? "";
                if (seen.Add(type + " " + name)) Log($"crate search: {type} '{name}' at {Fmt(aim)}");
                if ((type + name).IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    crateAim = aim;
                    found = true;
                }
            }
        }
        if (!found)
        {
            Log("crate search: nothing with 'crate' in its name under the laser");
            yield break;
        }
        Log($"crate found at {Fmt(crateAim)}; clicking");
        AimAt(rig, cfg, crateAim);
        yield return 0.5f;
        trigger = true;
        yield return 0.15f;
        trigger = false;
        for (int i = 0; i < 20; i++)
        {
            yield return 1f;
            DumpCameras($"crate +{i + 1}s");
            if (i == 1 || i == 5 || i == 11 || i == 19) SimShots.Request($"crate_{i + 1:00}s");
        }
    }

    // Plays an expedition's crate opening as a preview (claimReward false) and
    // records cameras, canvases and screenshots while it runs. Test runs never save.
    static IEnumerator<float> RewardScenario(VrSession vr, TableRig rig, Plugin cfg)
    {
        var scene = UnityEngine.Object.FindObjectOfType<TPS.Game.ExpeditionRewards.ExpeditionRewardScene>(true);
        var manager = ExpeditionManager.Instance;
        Log($"reward: scene {(scene != null ? scene.name : "none")} manager {(manager != null)} expeditions {manager?._expeditions?.Count}");
        if (scene == null || manager?._expeditions == null) yield break;
        // Fast-forward until the expedition's crate is delivered (the run's saves are restored afterwards).
        Expedition pick = null;
        foreach (var pair in manager._expeditions) pick = pair.Value;
        if (pick == null) yield break;
        float ffStart = Time.unscaledTime, nextLog = 0;
        while (Time.unscaledTime - ffStart < 240f)
        {
            string stage = pick.Stage.ToString();
            if (Time.unscaledTime >= nextLog)
            {
                nextLog = Time.unscaledTime + 5f;
                Log($"reward: fast-forward stage {stage} time {pick.Time:F1}/{pick.Duration:F1} return {pick.CurrentReturnTime:F1} item {pick.ItemID.ID} rewards {pick.Rewards?.Count} inFlow {scene.IsInRewardFlow} logic {App.Instance?.MainCameraLogic?.GetIl2CppType().Name}");
            }
            if (scene.IsInRewardFlow || manager._expeditions.Count == 0 || (stage != "InProgress" && stage != "Return" && stage != "DropStaff")) break;
            Time.timeScale = 100f;
            yield return 0.5f;
        }
        Time.timeScale = 1f;
        Log($"reward: stopped at stage {pick.Stage} item {pick.ItemID.ID} rewards {pick.Rewards?.Count} inFlow {scene.IsInRewardFlow}");
        foreach (var item in UnityEngine.Object.FindObjectsOfType<Transform>())
            if (item.name.IndexOf("crate", StringComparison.OrdinalIgnoreCase) >= 0 && item.gameObject.activeInHierarchy)
                Log($"reward: crate-like object '{item.name}' at {item.position}");
        SimShots.Request("reward_00_arrived");
        yield return 2f;
        DumpCameras("before reward");
        DumpCanvases("before reward");
        // Open it the way a player does: point at the crate and click (direct calls into the game hang).
        Transform crate = null;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            if (t.name == "A_Exhibit_Crate_V1" && t.gameObject.activeInHierarchy) crate = t;
        if (crate != null && !scene.IsInRewardFlow)
        {
            var p = crate.position;
            // The clickable thing is the helipad item the crate sits on; sweep around the crate until it's under the laser.
            ESCursorSelectResult sel = null;
            bool onPad = false;
            for (float r = 0; r <= 6 && !onPad; r += 0.75f)
                for (int a = 0; a < (r == 0 ? 1 : 12) && !onPad; a++)
                {
                    float ang = a * MathF.PI / 6;
                    AimAt(rig, cfg, new NVec(p.x + MathF.Cos(ang) * r, 0.3f, p.z + MathF.Sin(ang) * r));
                    yield return 0.1f;
                    sel = SelectResult();
                    var hovered = sel?.Result?.TryCast<GameItemInstance>();
                    onPad = hovered != null && hovered.ID == pick.ItemID.ID;
                }
            yield return 0.5f;
            Log($"reward: aiming at crate {p}: cursor {CursorManager.Instance?.WorldPosition} hover {sel?.Result?.GetIl2CppType().Name} '{sel?.Result?.TryCast<GameItemInstance>()?.Name}'");
            trigger = true;
            yield return 0.15f;
            trigger = false;
            yield return 2f;
            SimShots.Request("reward_00b_clicked");
            yield return 1f;
            Log($"reward: after click inFlow {scene.IsInRewardFlow} logic {App.Instance?.MainCameraLogic?.GetIl2CppType().Name} cursor {Mode()}");
            DumpCanvases("after crate click");
            // The helipad inspector's "Open Crate" button (measured from a screenshot at 1920x1080).
            var open = ClickPanelSteps(cfg, new Vector2(1686f / 1920f, 1f - 636f / 1080f));
            while (open.MoveNext()) yield return open.Current;
            Log($"reward: after Open Crate inFlow {scene.IsInRewardFlow} logic {App.Instance?.MainCameraLogic?.GetIl2CppType().Name} main {App.Instance?.MainCamera?.name}");
            // Look straight ahead at the cinema screen for the screenshots.
            vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(0)));
        }
        for (int i = 0; i < 24; i++)
        {
            yield return 1f;
            Log($"reward +{i + 1}s: rig mode {rig.Mode} inRewardFlow {scene.IsInRewardFlow} main {App.Instance?.MainCamera?.name} logic {App.Instance?.MainCameraLogic?.GetIl2CppType().Name}");
            if (i % 4 == 1)
            {
                DumpCameras($"reward +{i + 1}s");
                DumpCanvases($"reward +{i + 1}s");
            }
            if (i == 1 || i == 4 || i == 8 || i == 16 || i == 22) SimShots.Request($"reward_{i + 1:00}s");
            if (i == 2)
            {
                // The reveal's "Open" button (measured at 1920x1080).
                var reveal = ClickPanelSteps(cfg, new Vector2(958f / 1920f, 1f - 1025f / 1080f));
                while (reveal.MoveNext()) yield return reveal.Current;
                Log("reward: clicked Open");
            }
            if (i == 12)
            {
                var send = ClickPanelSteps(cfg, new Vector2(1712f / 1920f, 1f - 610f / 1080f));
                while (send.MoveNext()) yield return send.Current;
                Log("reward: clicked Send to Inventory");
            }
        }
        // Leave the reward screen the way a player would.
        VirtualKeyboard.TapEscape();
        yield return 2f;
        Log($"reward: after Escape inRewardFlow {scene.IsInRewardFlow} rig mode {rig.Mode}");
    }

    // TPMVR_SCENARIO=levelselect: stay on the career level select (where the advisor
    // character appears) for a while, recording what renders it.
    static float levelSelectSince = -1;
    public static bool HoldOnLevelSelect()
    {
        if (Scenario != "levelselect") return false;
        if (levelSelectSince < 0) levelSelectSince = Time.unscaledTime;
        float t = Time.unscaledTime - levelSelectSince;
        if (t < 4.5f) return true;
        if (t < 9f)
        {
            DumpCameras("level select");
            DumpCanvases("level select");
            foreach (var img in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.RawImage>())
                if (img.isActiveAndEnabled)
                    Log($"[ui] active rawimage '{UiPath(img.transform)}' color {img.color} texture '{img.texture?.name}' shader '{img.materialForRendering?.shader?.name}'");
            SimShots.Request("levelselect");
            return true;
        }
        return t < 14f;
    }

    // Every RawImage (including inactive ones), with the shader and texture it draws.
    static IEnumerator<float> UiScenario()
    {
        foreach (var img in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.RawImage>(true))
        {
            var mat = img.materialForRendering;
            var tex = img.texture;
            string texInfo = tex == null ? "none" : $"'{tex.name}' {tex.GetIl2CppType().Name}" + (tex.TryCast<RenderTexture>() is RenderTexture rt ? $" {rt.format} {rt.width}x{rt.height}" : "");
            Log($"[ui] rawimage '{UiPath(img.transform)}' active {img.isActiveAndEnabled} shader '{mat?.shader?.name}' material '{mat?.name}' color {img.color} texture {texInfo}");
        }
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>(true))
            if (c.name == "AdvisorCamera")
                Log($"[ui] AdvisorCamera clear {c.clearFlags} background {c.backgroundColor} target {c.targetTexture?.format}");
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
            if (c.gameObject.layer != 5)
                Log($"[ui] canvas on layer {c.gameObject.layer} '{UiPath(c.transform)}' root {c.isRootCanvas} mode {c.renderMode} active {c.isActiveAndEnabled} override {c.overrideSorting}");
        var video = UnityEngine.Object.FindObjectOfType<TPS.Game.UI.VideoTutorialMenu>(true);
        Log($"[ui] VideoTutorialMenu {(video != null ? UiPath(video.transform) : "none")} rawimage shader '{video?._videoRawImage?.materialForRendering?.shader?.name}'");
        yield break;
    }

    // Sits in the museum for a while; whenever the set of open menus changes it
    // logs every graphic on them (type, shader, texture) and takes screenshots.
    static IEnumerator<float> PosterScenario()
    {
        string last = null;
        int shot = 0;
        for (float t = 0; t < 75; t += 1f)
        {
            var open = UnityEngine.Object.FindObjectsOfType<TPS.Game.UI.MenuBase>()
                .Where(m => m.isActiveAndEnabled).Select(m => m.GetIl2CppType().Name + ":" + m.name).OrderBy(n => n).ToList();
            var key = string.Join(", ", open);
            if (key != last)
            {
                last = key;
                Log($"[poster] t={t} open menus: {key}");
                foreach (var g in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Graphic>())
                {
                    if (!g.isActiveAndEnabled || g.GetIl2CppType().Name.StartsWith("TextMeshPro")) continue;
                    var shader = g.materialForRendering?.shader?.name ?? "";
                    var type = g.GetIl2CppType().Name;
                    if (shader == "UI/Default" && type != "UITranslucentImage" && type != "RawImage") continue;
                    var tex = g.mainTexture;
                    Log($"[poster]   {type} '{UiPath(g.transform)}' shader '{shader}' material '{g.materialForRendering?.name}' texture '{tex?.name}' {tex?.width}x{tex?.height} color {g.color}");
                }
                SimShots.Request($"poster_{shot++:D2}");
            }
            yield return 1f;
        }
    }

    // Looks up and back down, measuring the brightness of the middle of the left
    // eye: if the lighting follows the head, it is off just after looking back down
    // and recovers over a second or two.
    static IEnumerator<float> LightScenario(VrSession vr, TableRig rig)
    {
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>(true))
        {
            var o = c.GetComponent<TPS.Octalux.OctaluxAdditionalCameraData>();
            Log($"[light] camera '{c.name}' enabled {c.enabled} octalux {(o == null ? "none" : $"canUpdate {o.OctaluxCanUpdateUsingCamera} mode {o.LightingMode} visit {o.OctaluxVisitIndex}")}");
        }
        var down = new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(45));
        for (int round = 0; round < 2; round++)
        {
            vr.SimulateHead(down);
            yield return 4f;
            Log($"[light] round {round} settled looking down: {CentreBrightness(rig):F4}");
            vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(-20)));
            yield return 3f;
            vr.SimulateHead(down);
            for (int i = 0; i < 8; i++)
            {
                yield return 0.33f;
                Log($"[light] round {round} back down +{(i + 1) * 0.33f:F2}s: {CentreBrightness(rig):F4}");
            }
            SimShots.Request($"light_{round}");
            yield return 0.5f;
        }
    }

    static Texture2D brightnessReadback;

    static float CentreBrightness(TableRig rig)
    {
        var rt = rig.Left;
        int w = rt.width / 4, h = rt.height / 4;
        if (brightnessReadback == null) brightnessReadback = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        brightnessReadback.ReadPixels(new Rect(rt.width / 2 - w / 2, rt.height / 2 - h / 2, w, h), 0, 0);
        RenderTexture.active = prev;
        var px = brightnessReadback.GetPixels();
        double sum = 0;
        foreach (var p in px) sum += 0.2126 * p.r + 0.7152 * p.g + 0.0722 * p.b;
        return (float)(sum / px.Length);
    }

    static string UiPath(Transform t)
    {
        var parts = new List<string>();
        for (int i = 0; t != null && i < 5; i++, t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    static void DumpCanvases(string label)
    {
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
        {
            if (!c.isRootCanvas || !c.isActiveAndEnabled) continue;
            Log($"[canvas] {label}: '{c.name}' mode {c.renderMode} cam {(c.worldCamera ? c.worldCamera.name : "-")} layer {c.gameObject.layer} order {c.sortingOrder} plane {c.planeDistance}");
        }
    }

    // Every camera in the scene (enabled or not), what it draws into, and the rig mode.
    static void DumpCameras(string label)
    {
        var app = App.Instance;
        var main = app?.MainCamera;
        Log($"[cams] {label}: main '{main?.name}' logic {app?.MainCameraLogic?.GetIl2CppType().Name} | cursor {Mode()} | timeScale {Time.timeScale}");
        foreach (var c in UnityEngine.Object.FindObjectsOfType<Camera>(true))
        {
            if (c.name.StartsWith("TpmVr")) continue;
            var target = c.targetTexture;
            Log($"[cams]   '{c.name}' active {c.gameObject.activeInHierarchy} enabled {c.enabled} depth {c.depth} mask 0x{c.cullingMask:X} target {(target ? $"{target.name} {target.width}x{target.height}" : "screen")} pos {c.transform.position}");
        }
        foreach (var img in UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.RawImage>())
        {
            if (!img.isActiveAndEnabled || img.texture == null) continue;
            var canvas = img.canvas;
            Log($"[cams]   rawimage '{img.transform.parent?.name}/{img.name}' texture '{img.texture.name}' ({img.texture.GetIl2CppType().Name}) canvas '{canvas?.rootCanvas?.name}' {canvas?.rootCanvas?.renderMode}");
        }
    }
}

// TPMVR_SCENARIO=windows: screen rects of the UI pieces a split layout could
// cut out of the UI texture: every open menu, and every visible child (two
// levels down) of each root canvas.
static partial class SimScript
{
    static IEnumerator<float> WindowsScenario()
    {
        yield return 2f;
        Log($"[windows] screen {Screen.width}x{Screen.height}");
        DumpWindowBounds("idle");
        SimShots.Request("windows_idle");
        yield return 1f;
        // Open each hub button in turn (they sit along the bottom-left), dump, close.
        foreach (var x in new[] { 90f, 165f, 238f, 312f, 390f, 475f })
        {
            AimAtTracking(Plugin.Instance, VrMenuPanel.PixelToTracking(new Vector2(x, 40)));
            yield return 0.4f;
            trigger = true;
            yield return 0.15f;
            trigger = false;
            yield return 1.5f;
            DumpWindowBounds($"after hub click x={x}");
            SimShots.Request($"windows_hub_{x:F0}");
            yield return 1f;
            VirtualKeyboard.TapEscape();
            yield return 1f;
        }
    }

    // Bounds of what each open menu (and each HideableRoot child) actually draws.
    static void DumpWindowBounds(string label)
    {
        var cam = VrMenuPanel.UiCamera;
        Log($"[windows] --- {label}");
        foreach (var m in UnityEngine.Object.FindObjectsOfType<TPS.Game.UI.MenuBase>())
            if (m.isActiveAndEnabled)
                Log($"[windows] menu {m.GetIl2CppType().Name} '{UiPath(m.transform)}' drawn {DrawnRect(cam, m.transform)}");
        var root = GameObject.Find("HideableRoot");
        if (root != null)
            for (int i = 0; i < root.transform.childCount; i++)
            {
                var c = root.transform.GetChild(i);
                if (c.gameObject.activeInHierarchy) Log($"[windows] hideable '{c.name}' drawn {DrawnRect(cam, c)}");
            }
        foreach (var name in new[] { "Popups", "NonHideableRoot" })
        {
            var go = GameObject.Find(name);
            if (go != null) Log($"[windows] '{name}' drawn {DrawnRect(cam, go.transform)}");
        }
    }

    // Union of the screen rects of every visible graphic under t.
    static string DrawnRect(Camera cam, Transform t)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        int n = 0;
        foreach (var g in t.GetComponentsInChildren<UnityEngine.UI.Graphic>())
        {
            if (!g.isActiveAndEnabled || g.color.a < 0.01f) continue;
            var rt = g.rectTransform;
            var r = rt.rect;
            var a = cam.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0)));
            var b = cam.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0)));
            if (b.x - a.x >= Screen.width && b.y - a.y >= Screen.height) continue; // full-screen blockers
            x0 = Math.Min(x0, a.x); y0 = Math.Min(y0, a.y); x1 = Math.Max(x1, b.x); y1 = Math.Max(y1, b.y);
            n++;
        }
        return n == 0 ? "nothing" : $"({x0:F0},{y0:F0} {x1 - x0:F0}x{y1 - y0:F0}) from {n} graphics";
    }

    static void DumpChildren(Camera cam, Transform t, int depth)
    {
        for (int i = 0; i < t.childCount; i++)
        {
            var child = t.GetChild(i);
            if (!child.gameObject.activeInHierarchy) continue;
            Log($"[windows] {new string(' ', depth * 2)}'{child.name}' rect {ScreenRect(cam, child)}");
            if (depth < 3) DumpChildren(cam, child, depth + 1);
        }
    }

    // Screen-pixel rect (x, y from bottom-left, w, h) of a RectTransform on the UI camera.
    static string ScreenRect(Camera cam, Transform t)
    {
        var rt = t.TryCast<RectTransform>();
        if (rt == null || cam == null) return "-";
        var r = rt.rect;
        var a = cam.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0)));
        var b = cam.WorldToScreenPoint(rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0)));
        return $"({a.x:F0},{a.y:F0} {b.x - a.x:F0}x{b.y - a.y:F0})";
    }
}

// TPMVR_SCENARIO=vrui: the VR menu and UI modes. Opens the menu, switches to
// Arranged by clicking it, looks around the windows, opens a build list
// through its window and the pause menu (the pop-up window), then goes back
// to Panel mode so the player's config is left as it was.
static partial class SimScript
{
    static IEnumerator<float> VrUiScenario(VrSession vr, TableRig rig, Plugin cfg)
    {
        var modeBefore = VrUiKit.VrUi.Mode;
        var ahead = new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(25));
        vr.SimulateHead(ahead);
        yield return 1f;
        SimShots.Request("vrui_0_panel");
        yield return 1f;

        VrUiKit.VrUi.ToggleMenu();
        yield return 1f;
        SimShots.Request("vrui_1_menu");
        yield return 1f;
        // The menu must stay on top of scenery: lift the ground above it (the menu
        // hangs ~22 cm below the eyes, the table goes to 5 cm below them).
        float tableHeight0 = rig.Tuning.TableHeight;
        rig.Settings.Set(null, "TableHeight", 1.45f);
        yield return 1f;
        SimShots.Request("vrui_1b_menu_under_ground");
        yield return 1f;
        rig.Settings.Set(null, "TableHeight", tableHeight0);
        yield return 0.5f;
        foreach (var step in ClickMenu(cfg, "UI")) yield return step;
        foreach (var step in ClickMenu(cfg, "Arranged")) yield return step;
        Log($"[vrui] after clicking Arranged: mode {VrUiKit.VrUi.Mode}");
        SimShots.Request("vrui_2_menu_arranged");
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "Game")) yield return step;
        SimShots.Request("vrui_3_settings");
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "Mode")) yield return step;
        SimShots.Request("vrui_3b_view");
        yield return 1f;
        // View modes: pick Turntable, then Diorama, then put back what the player had.
        var viewBefore = VrUiKit.VrModes.Current?.Id;
        foreach (var step in ClickMenu(cfg, "Turntable")) yield return step;
        Log($"[vrui] after clicking Turntable: mode {rig.Mode}");
        SimShots.Request("vrui_3c_turntable");
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "Diorama")) yield return step;
        Log($"[vrui] after clicking Diorama: mode {rig.Mode}");
        if (viewBefore != null) VrUiKit.VrModes.Select(viewBefore);
        foreach (var step in ClickMenu(cfg, "Welcome")) yield return step;
        SimShots.Request("vrui_3d_welcome");
        yield return 1f;
        // A game can turn tabs and rows off: hide Record and a row, then put them back.
        VrUiKit.VrMenu.HideTab("Record");
        VrUiKit.VrMenu.HideRow("Table", "Spin coast (s)");
        yield return 0.5f;
        Log($"[vrui] Record hidden: on menu {VrUiKit.VrUi.MenuPointOf("Record") != null}");
        foreach (var step in ClickMenu(cfg, "Mode")) yield return step;
        Log($"[vrui] Spin coast hidden: on menu {VrUiKit.VrUi.MenuPointOf("Spin coast (s) +") != null}");
        SimShots.Request("vrui_3e_hidden");
        yield return 1f;
        VrUiKit.VrMenu.HideTab("Record", false);
        VrUiKit.VrMenu.HideRow("Table", "Spin coast (s)", false);
        yield return 0.5f;
        Log($"[vrui] shown again: Record {VrUiKit.VrUi.MenuPointOf("Record") != null}, Spin coast {VrUiKit.VrUi.MenuPointOf("Spin coast (s) +") != null}");
        // Carry the menu like a Free-mode window: laser on it, grip held, laser moved, let go.
        var menuBefore = VrUiKit.VrUi.MenuPointToTracking(400, 400);
        var tableBeforeCarry = rig.AnchorN();
        AimAtTracking(cfg, menuBefore);
        yield return 0.4f;
        grip = true;
        yield return 0.3f;
        Log($"[vrui] grip on the menu: carrying {VrUiKit.VrUi.CarryingPanel}");
        AimAtTracking(cfg, menuBefore + new NVec(0.25f, 0.1f, 0));
        yield return 0.5f;
        grip = false;
        yield return 0.5f;
        Log($"[vrui] menu carried: {menuBefore} -> {VrUiKit.VrUi.MenuPointToTracking(400, 400)}, carrying {VrUiKit.VrUi.CarryingPanel}, table moved {(rig.AnchorN() - tableBeforeCarry).Length():F4}");
        SimShots.Request("vrui_3f_menu_moved");
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "X")) yield return step;
        Log($"[vrui] menu open after close click: {VrUiKit.VrUi.MenuOpen}");

        yield return 1f;
        SimShots.Request("vrui_4_arranged");
        yield return 1f;
        foreach (var yaw in new[] { -40f, 40f })
        {
            vr.SimulateHead(new Pose(ahead.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(yaw) * Pose.Pitch(20))));
            yield return 1f;
            SimShots.Request($"vrui_5_look_{(yaw < 0 ? "left" : "right")}");
            yield return 1f;
        }

        // Open the items list through the build window.
        if (VrUiKit.VrUi.PixelToTracking("build", new Vector2(90, 40), out var target))
        {
            vr.SimulateHead(new Pose(ahead.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(-40) * Pose.Pitch(10))));
            AimAtTracking(cfg, target);
            yield return 0.5f;
            Log($"[vrui] aiming at build window: onMenu {VrPointer.OnMenu} | {MouseInfo()}");
            trigger = true;
            yield return 0.15f;
            trigger = false;
            yield return 2f;
            SimShots.Request("vrui_6_build_open");
            yield return 1f;
            VirtualKeyboard.TapEscape();
            yield return 1f;
        }
        else Log("[vrui] build window not shown");

        vr.SimulateHead(ahead);
        VirtualKeyboard.TapEscape();   // pause menu: should appear in the pop-up window
        yield return 2f;
        SimShots.Request("vrui_7_pause");
        yield return 1f;
        VirtualKeyboard.TapEscape();
        yield return 1f;

        VrUiKit.VrUi.SetMode(modeBefore);
        yield return 1f;
    }

    // Arranged windows (core 0.1.1: one Arranged mode, moved while the VR menu is
    // open): the grip does nothing to a window with the menu closed; with it open,
    // grab the build window, move / push / resize it, save the arrangement as a
    // layout, check it's used, then delete it again.
    static IEnumerator<float> FreeScenario(VrSession vr, TableRig rig, Plugin cfg)
    {
        VrUiKit.VrUi.TraceRemainder = true;
        var layoutBefore = VrUiKit.VrUi.LayoutName;
        var modeBefore = VrUiKit.VrUi.Mode;
        var ahead = new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(25));
        vr.SimulateHead(ahead);
        var layoutDir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "tpmvr-layouts");
        var freePath = System.IO.Path.Combine(layoutDir, "free.txt");
        string freeBefore = System.IO.File.Exists(freePath) ? System.IO.File.ReadAllText(freePath) : null;
        yield return 1f;

        VrUiKit.VrUi.ToggleMenu();
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "UI")) yield return step;
        foreach (var step in ClickMenu(cfg, "Arranged")) yield return step;
        Log($"[free] mode {VrUiKit.VrUi.Mode}");
        SimShots.Request("free_0_menu");
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "X")) yield return step;
        yield return 1f;

        vr.SimulateHead(new Pose(ahead.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(-40) * Pose.Pitch(10))));
        yield return 1f;
        SimShots.Request("free_1_frames");
        yield return 1f;

        var rect = VrUiKit.VrUi.WindowRect("build");
        if (!VrUiKit.VrUi.PixelToTracking("build", rect.center, out var start))
        {
            Log("[free] build window not shown");
            yield break;
        }
        // Menu closed: the grip on a window must not take it.
        AimAtTracking(cfg, start);
        yield return 0.5f;
        grip = true;
        yield return 0.3f;
        Log($"[free] grip with the menu closed: holding {VrUiKit.VrUi.GrabbingWindow} (expect False)");
        grip = false;
        yield return 0.5f;

        // Menu open: now it moves (with the pink outline on).
        VrUiKit.VrUi.ToggleMenu();
        yield return 1f;
        var tableBefore = rig.AnchorN();
        AimAtTracking(cfg, start);
        yield return 0.5f;
        SimShots.Request("free_1b_outlines");
        yield return 0.5f;
        grip = true;
        yield return 0.3f;
        Log($"[free] grip with the menu open: holding {VrUiKit.VrUi.GrabbingWindow} (expect True)");
        // Sweep the laser up and to the right, then pull it in and make it bigger.
        for (int i = 1; i <= 10; i++)
        {
            AimAtTracking(cfg, start + new NVec(0.03f * i, 0.025f * i, 0));
            yield return 0.05f;
        }
        stickHoldY = -1;
        yield return 0.5f;
        stickHoldY = 0;
        stickHoldX = 1;
        yield return 0.6f;
        stickHoldX = 0;
        SimShots.Request("free_2_held");
        yield return 0.5f;
        grip = false;
        yield return 0.5f;
        Log($"[free] released: holding {VrUiKit.VrUi.GrabbingWindow}, table moved {(rig.AnchorN() - tableBefore).Length():F4}");
        SimShots.Request("free_3_moved");
        yield return 1f;

        // Save as a new layout (the menu is still open), then show it.
        vr.SimulateHead(ahead);
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "Save as new layout")) yield return step;
        Log($"[free] after save: layout '{VrUiKit.VrUi.LayoutName}' built-in {VrUiKit.VrUi.LayoutIsBuiltIn}, files: {string.Join(", ", System.IO.Directory.GetFiles(layoutDir).Select(System.IO.Path.GetFileName))}");
        SimShots.Request("free_4_saved");
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "UI")) yield return step;
        foreach (var step in ClickMenu(cfg, "Arranged")) yield return step;
        foreach (var step in ClickMenu(cfg, "X")) yield return step;
        vr.SimulateHead(new Pose(ahead.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(-40) * Pose.Pitch(10))));
        yield return 1f;
        SimShots.Request("free_5_arranged_custom");
        yield return 1f;

        // Clean up: delete the saved layout (two clicks) and the working Free layout.
        vr.SimulateHead(ahead);
        VrUiKit.VrUi.ToggleMenu();
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "Delete layout")) yield return step;
        foreach (var step in ClickMenu(cfg, "Delete layout")) yield return step;   // again, to delete
        Log($"[free] after delete: layout '{VrUiKit.VrUi.LayoutName}', files: {string.Join(", ", System.IO.Directory.GetFiles(layoutDir).Select(System.IO.Path.GetFileName))}");
        VrUiKit.VrUi.ToggleMenu();
        if (freeBefore != null) System.IO.File.WriteAllText(freePath, freeBefore);
        else if (System.IO.File.Exists(freePath)) System.IO.File.Delete(freePath);
        VrUiKit.VrUi.ReloadFree();
        VrUiKit.VrUi.SelectLayout(layoutBefore);
        VrUiKit.VrUi.SetMode(modeBefore);
        yield return 1f;
    }

    // Controller actions: the menu chord (and that it leaves the table where it
    // was), rebinding Pause, left-handed mirroring, saving / using / deleting a
    // preset. Puts the bindings back as they were.
    static IEnumerator<float> ControlsScenario(VrSession vr, TableRig rig, Plugin cfg)
    {
        var ahead = new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(25));
        vr.SimulateHead(ahead);
        var dir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "tpmvr-controls");
        var working = System.IO.Path.Combine(dir, "controls.txt");
        string workingBefore = System.IO.File.Exists(working) ? System.IO.File.ReadAllText(working) : null;
        var pause = VrUiKit.VrInput.Actions.First(a => a.Id == TpmUi.Pause);
        string Files() => System.IO.Directory.Exists(dir) ? string.Join(", ", System.IO.Directory.GetFiles(dir).Select(System.IO.Path.GetFileName)) : "-";
        Log($"[controls] pause = {VrUiKit.VrInput.BindingText(pause)}, menu open {VrUiKit.VrUi.MenuOpen}");
        yield return 1f;

        // Chord: grips first (moving a hand drags the table), then both triggers.
        var tableBefore = rig.AnchorN();
        float scaleBefore = rig.Scale;
        grip = leftGrip = true;
        yield return 0.3f;
        rightPose = new Pose(rightPose.Position + new NVec(0.1f, 0, 0.05f), rightPose.Rotation);
        yield return 0.3f;
        Log($"[controls] gripping: table moved {(rig.AnchorN() - tableBefore).Length():F3}, scale {rig.Scale:F1} (was {scaleBefore:F1})");
        trigger = leftTrigger = true;
        yield return 0.3f;
        Log($"[controls] chord: menu open {VrUiKit.VrUi.MenuOpen}, table moved {(rig.AnchorN() - tableBefore).Length():F3}, scale {rig.Scale:F1}");
        grip = leftGrip = trigger = leftTrigger = false;
        rightPose = new Pose(rightPose.Position - new NVec(0.1f, 0, 0.05f), rightPose.Rotation);
        yield return 0.5f;
        if (!VrUiKit.VrUi.MenuOpen) VrUiKit.VrUi.ToggleMenu();
        yield return 0.5f;

        foreach (var step in ClickMenu(cfg, "Controls")) yield return step;
        SimShots.Request("controls_0_page");
        yield return 1f;

        // Rebind Pause to the laser hand's A (it then clashes with Rotate when placing).
        foreach (var step in ClickMenu(cfg, "bind:" + TpmUi.Pause)) yield return step;
        Log($"[controls] capturing {VrUiKit.VrInput.Capturing?.Id}");
        yield return 0.5f;
        primaryPress = true;
        yield return 0.5f;
        Log($"[controls] pause = {VrUiKit.VrInput.BindingText(pause)}, conflict {pause.Bindings.Any(b => VrUiKit.VrInput.Conflicts(pause, b))}");
        SimShots.Request("controls_1_rebound");
        yield return 1f;

        // Left-handed: the same bindings, labelled for the other side.
        foreach (var step in ClickMenu(cfg, "Left-handed")) yield return step;
        Log($"[controls] left-handed {cfg.LeftHanded.Value}: pause = {VrUiKit.VrInput.BindingText(pause)}");
        // The laser is now on the left hand; switch back with the keyboard-free route.
        cfg.LeftHanded.Value = false;
        yield return 0.5f;

        // Presets: save, go back to Default, then delete the saved one.
        foreach (var step in ClickMenu(cfg, "Save as new")) yield return step;
        Log($"[controls] saved: preset '{VrUiKit.VrInput.Preset}', files {Files()}");
        foreach (var step in ClickMenu(cfg, "Preset -")) yield return step;   // to Default (the stepper)
        foreach (var step in ClickMenu(cfg, "Use preset")) yield return step;
        Log($"[controls] preset '{VrUiKit.VrInput.Preset}' in use: pause = {VrUiKit.VrInput.BindingText(pause)}");
        foreach (var step in ClickMenu(cfg, "Preset +")) yield return step;   // to Custom 1
        SimShots.Request("controls_2_preset");
        yield return 1f;
        foreach (var step in ClickMenu(cfg, "Delete preset")) yield return step;
        foreach (var step in ClickMenu(cfg, "Delete preset")) yield return step;   // Sure?
        Log($"[controls] after delete: preset '{VrUiKit.VrInput.Preset}', files {Files()}");

        foreach (var step in ClickMenu(cfg, "X")) yield return step;
        if (workingBefore != null) System.IO.File.WriteAllText(working, workingBefore);
        else if (System.IO.File.Exists(working)) System.IO.File.Delete(working);
        yield return 1f;
    }

    // Wrist windows: the laser brings the build window to the off-hand wrist and
    // lets go; it rides on that hand (wrist windows are always shown). Then
    // the laser takes it off, the off hand takes it from the laser and puts it
    // on the laser hand's wrist. Puts the Free layout and mode back.
    static IEnumerator<float> WristScenario(VrSession vr, TableRig rig, Plugin cfg)
    {
        const string id = "build";
        var modeBefore = VrUiKit.VrUi.Mode;
        var freePath = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "tpmvr-layouts", "free.txt");
        string freeBefore = System.IO.File.Exists(freePath) ? System.IO.File.ReadAllText(freePath) : null;
        var leftBefore = leftPose;
        var head = new Pose(new NVec(0, 1.5f, 0), System.Numerics.Quaternion.Normalize(Pose.Yaw(-40) * Pose.Pitch(10)));
        vr.SimulateHead(head);
        VrUiKit.VrUi.SetMode(VrUiKit.VrUi.UiMode.Arranged);
        VrUiKit.VrUi.ResetFree();
        // Windows and bars move only while the VR menu is open (core 0.1.1).
        if (!VrUiKit.VrUi.MenuOpen) VrUiKit.VrUi.ToggleMenu();
        leftPose = new Pose(new NVec(-0.2f, 1.1f, 0.35f), System.Numerics.Quaternion.Identity);
        yield return 1.5f;
        string State() => $"anchor {VrUiKit.VrUi.AnchorOf(id)}, shown {VrUiKit.VrUi.WindowVisible(id)}, centre {VrUiKit.VrUi.WindowCentre(id)}";
        Log($"[wrist] start: {State()}");

        if (!VrUiKit.VrUi.PixelToTracking(id, VrUiKit.VrUi.WindowRect(id).center, out var start))
        {
            Log("[wrist] build window not shown");
            yield break;
        }
        // Laser grab, then steer the held window to the off-hand wrist.
        AimAtTracking(cfg, start);
        yield return 0.5f;
        grip = true;
        yield return 0.3f;
        Log($"[wrist] laser grip: holding {VrUiKit.VrUi.GrabbingWindow}");
        var wrist = leftPose.Position - leftPose.Forward * 0.08f;
        float want = (wrist - HandPosition).Length();
        for (int i = 0; i < 80; i++)
        {
            AimAtTracking(cfg, wrist);
            float d = VrUiKit.VrUi.GrabDistanceWorld / rig.WorldScale;
            stickHoldY = d > want + 0.01f ? -1 : d < want - 0.01f ? 1 : 0;
            if (stickHoldY == 0) break;
            yield return 0.05f;
        }
        stickHoldY = 0;
        yield return 0.3f;
        Log($"[wrist] near the off wrist: would go on {VrUiKit.VrUi.SnapPreview?.ToString() ?? "nothing"}");
        SimShots.Request("wrist_0_snap");
        yield return 0.5f;
        grip = false;
        yield return 0.5f;
        Log($"[wrist] let go: {State()}");

        // The window rides on the hand.
        var offset = VrUiKit.VrUi.WindowCentre(id) - leftPose.Position;
        leftPose = new Pose(leftPose.Position + new NVec(0.15f, 0.05f, 0), System.Numerics.Quaternion.Normalize(Pose.Yaw(30)));
        yield return 0.5f;
        var expected = leftPose.Position + NVec.Transform(offset, Pose.Yaw(30));
        Log($"[wrist] hand moved and turned: window off by {(VrUiKit.VrUi.WindowCentre(id) - expected).Length():F4} m from where the hand carried it");
        vr.SimulateHead(new Pose(head.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(-10) * Pose.Pitch(35))));
        yield return 0.5f;
        SimShots.Request("wrist_1_on_wrist");
        yield return 0.5f;

        // The laser takes it off; the off hand (gripping the table otherwise) takes it from the laser.
        var tableBefore = rig.AnchorN();
        AimAtTracking(cfg, VrUiKit.VrUi.WindowCentre(id));
        yield return 0.4f;
        grip = true;
        yield return 0.3f;
        Log($"[wrist] laser grabs it off: {State()}, holding {VrUiKit.VrUi.GrabbingWindow}");
        leftPose = new Pose(VrUiKit.VrUi.WindowCentre(id), leftPose.Rotation);
        yield return 0.3f;
        leftGrip = true;
        yield return 0.3f;
        grip = false;
        yield return 0.3f;
        Log($"[wrist] off hand took it: holding {VrUiKit.VrUi.GrabbingWindow}, by laser {VrUiKit.VrUi.GrabbingWithLaser}");

        // Carry it to the laser hand's wrist.
        AimAtTracking(cfg, new NVec(0.4f, 0.9f, 1.0f));
        yield return 0.2f;
        var pointerWrist = rightPose.Position - rightPose.Forward * 0.08f;
        var carryFrom = leftPose.Position;
        for (int i = 1; i <= 10; i++)
        {
            leftPose = new Pose(NVec.Lerp(carryFrom, pointerWrist, i / 10f), leftPose.Rotation);
            yield return 0.05f;
        }
        yield return 0.3f;
        // Turning the carrying hand turns the window with it (to lay it along the wrist).
        var windowBefore = VrUiKit.VrUi.WindowRotation(id);
        var twist = System.Numerics.Quaternion.Normalize(Pose.Yaw(25) * System.Numerics.Quaternion.CreateFromAxisAngle(NVec.UnitZ, 40f * MathF.PI / 180f));
        leftPose = new Pose(leftPose.Position, System.Numerics.Quaternion.Normalize(leftPose.Rotation * twist));
        yield return 0.3f;
        var turned = System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.Inverse(windowBefore) * VrUiKit.VrUi.WindowRotation(id));
        float turnedBy = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(turned.W))) * 180f / MathF.PI;
        float twistBy = 2f * MathF.Acos(Math.Min(1f, MathF.Abs(twist.W))) * 180f / MathF.PI;
        Log($"[wrist] hand twisted {twistBy:F1} deg: window turned {turnedBy:F1} deg");
        Log($"[wrist] at the laser wrist: would go on {VrUiKit.VrUi.SnapPreview?.ToString() ?? "nothing"}, table moved {(rig.AnchorN() - tableBefore).Length():F4}");
        leftGrip = false;
        yield return 0.5f;
        Log($"[wrist] let go: {State()}");
        var onPointer = VrUiKit.VrUi.WindowCentre(id) - rightPose.Position;
        AimAtTracking(cfg, new NVec(-0.3f, 0.9f, 1.0f));
        yield return 0.5f;
        Log($"[wrist] laser hand turned: window stayed {(VrUiKit.VrUi.WindowCentre(id) - rightPose.Position).Length():F3} m from it (was {onPointer.Length():F3})");
        leftPose = leftBefore;
        vr.SimulateHead(new Pose(head.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(15) * Pose.Pitch(30))));
        yield return 0.5f;
        SimShots.Request("wrist_3_pointer_wrist");
        yield return 1f;

        // A button bar (core 0.1.1): the laser carries the Tutorial bar to the off-hand
        // wrist (green preview there), and it rides on the hand like a window.
        var tutorial = VrUiKit.VrHotkeys.Set(TpmUi.TutorialBar);
        bool tutorialWasOn = VrUiKit.VrHotkeys.IsOn(tutorial);
        VrUiKit.VrHotkeys.SetOn(tutorial, true);
        leftPose = new Pose(new NVec(-0.2f, 1.1f, 0.35f), System.Numerics.Quaternion.Identity);
        vr.SimulateHead(new Pose(head.Position, System.Numerics.Quaternion.Normalize(Pose.Yaw(-20) * Pose.Pitch(30))));
        yield return 1f;
        var barKey = VrUiKit.VrUi.HotbarPointOf("Pan forward");
        Log($"[wrist] bar: W key at {barKey?.ToString() ?? "not shown"}");
        if (barKey != null)
        {
            AimAtTracking(cfg, barKey.Value);
            yield return 0.5f;
            SimShots.Request("wrist_4_bar_outline");
            yield return 0.5f;
            grip = true;
            yield return 0.3f;
            Log($"[wrist] bar: laser grip holding {VrUiKit.VrUi.GrabbingWindow}");
            var barWrist = leftPose.Position - leftPose.Forward * 0.08f;
            float barWant = (barWrist - HandPosition).Length();
            for (int i = 0; i < 80; i++)
            {
                AimAtTracking(cfg, barWrist);
                float d = VrUiKit.VrUi.GrabDistanceWorld / rig.WorldScale;
                stickHoldY = d > barWant + 0.01f ? -1 : d < barWant - 0.01f ? 1 : 0;
                if (stickHoldY == 0) break;
                yield return 0.05f;
            }
            stickHoldY = 0;
            yield return 0.3f;
            Log($"[wrist] bar near the off wrist: would go on {VrUiKit.VrUi.SnapPreview?.ToString() ?? "nothing"}");
            SimShots.Request("wrist_5_bar_snap");
            yield return 0.5f;
            grip = false;
            yield return 0.5f;
            var barOn = VrUiKit.VrUi.HotbarPointOf("Pan forward");
            leftPose = new Pose(leftPose.Position + new NVec(0.1f, 0.05f, 0), leftPose.Rotation);
            yield return 0.5f;
            var barAfter = VrUiKit.VrUi.HotbarPointOf("Pan forward");
            Log($"[wrist] bar let go, hand moved 0.112 m: W key moved {((barAfter ?? default) - (barOn ?? default)).Length():F3} m (expect ~0.112 if it rides on the wrist)");
            SimShots.Request("wrist_6_bar_on_wrist");
            yield return 1f;
        }
        VrUiKit.VrHotkeys.SetOn(tutorial, tutorialWasOn);
        leftPose = leftBefore;
        if (VrUiKit.VrUi.MenuOpen) VrUiKit.VrUi.ToggleMenu();

        // Put things back.
        if (freeBefore != null) System.IO.File.WriteAllText(freePath, freeBefore);
        else if (System.IO.File.Exists(freePath)) System.IO.File.Delete(freePath);
        VrUiKit.VrUi.ReloadFree();
        VrUiKit.VrUi.SetMode(modeBefore);
        yield return 1f;
    }

    // Opens the staff list (a menu with a full-screen blur backdrop, like the
    // tutorial pop-ups) so the screen recorder's backdrop experiment runs on it.
    static IEnumerator<float> BackdropScenario(VrSession vr)
    {
        vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(10)));
        yield return 2f;
        var menu = UnityEngine.Object.FindObjectsOfType<TPS.Game.UI.MenuBase>(true)
            .FirstOrDefault(m => m.GetIl2CppType().Name == "StaffListUI");
        Log($"[backdrop] staff list {(menu != null ? UiPath(menu.transform) : "not found")}");
        if (menu == null) yield break;
        menu.gameObject.SetActive(true);
        yield return 8f;
        Log($"[backdrop] still open {menu.isActiveAndEnabled}");
        menu.gameObject.SetActive(false);
        yield return 1f;
    }

    // Diorama background and radius: frame rate with a swaying head, then a
    // still snapshot, for each setting. Puts the user's settings back.
    // Spectator view: frame rate off and on (swaying head), and the monitor
    // picture next to the left eye. Puts the setting back.
    static IEnumerator<float> SpectatorScenario(VrSession vr)
    {
        bool was = SpectatorCam.Enabled;
        foreach (var on in new[] { false, true })
        {
            SpectatorCam.Enabled = on;
            sway = true;
            yield return 1f;
            PerfLog.Label = $"spectator {(on ? "on" : "off")}";
            PerfLog.Reset();
            yield return 5.2f;
        }
        PerfLog.Label = "";
        // Still swaying: the monitor view should be steadier than the eye.
        SimShots.Request("spectator");
        while (SimShots.Busy) yield return 0.1f;
        yield return 1f;
        sway = false;
        Log($"[spectator] camera {(SpectatorCam.Camera != null ? "on" : "off")}, screen {Screen.width}x{Screen.height}");

        // The Record tab: about five seconds of video, then a photo.
        sway = true;
        Recorder.ToggleVideo();
        yield return 5f;
        Recorder.ToggleVideo();
        yield return 1f;
        Recorder.TakePhoto();
        yield return 1f;
        sway = false;
        SpectatorCam.Enabled = was;
        yield return 0.5f;
    }

    // What a per-object cylinder cut would have to handle: how many renderers
    // there are, what finding them costs, and what is left on screen with all
    // of them hidden (drawn by Entities Graphics instead).
    static IEnumerator<float> CullProbeScenario(VrSession vr, TableRig rig)
    {
        vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(35)));
        yield return 1f;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var all = UnityEngine.Object.FindObjectsOfType<Renderer>();
        long findMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var centre = rig.AnchorN();
        int outside = 0;
        foreach (var r in all)
        {
            var b = r.bounds;
            float dx = b.center.x - centre.X, dz = b.center.z - centre.Z;
            if (dx * dx + dz * dz > 40f * 40f) outside++;
        }
        long boundsMs = sw.ElapsedMilliseconds;
        Log($"[cull] {all.Length} renderers, find {findMs} ms, bounds check {boundsMs} ms, {outside} centred beyond 40 units");
        foreach (var g in all.GroupBy(r => r.GetIl2CppType().Name).OrderByDescending(g => g.Count()))
            Log($"[cull]   {g.Key}: {g.Count()} (active {g.Count(r => r.enabled && r.gameObject.activeInHierarchy)})");
        foreach (var r in all.Where(r => r.enabled && r.gameObject.activeInHierarchy).OrderByDescending(r => r.bounds.size.x * r.bounds.size.z).Take(8))
            Log($"[cull]   biggest '{UiPath(r.transform)}' {r.bounds.size}");
        SimShots.Request("cull_all");
        while (SimShots.Busy) yield return 0.1f;
        var hidden = all.Where(r => !r.forceRenderingOff).ToList();
        foreach (var r in hidden) r.forceRenderingOff = true;
        yield return 0.5f;
        SimShots.Request("cull_renderers_off");
        while (SimShots.Busy) yield return 0.1f;
        foreach (var r in hidden) if (r != null && !r.WasCollected) r.forceRenderingOff = false;
        yield return 0.5f;
    }

    // The core's table (TableRig) in TPM's two modes: frame rate with a swaying head,
    // then a still snapshot, for each mode and background; the Turntable mask's
    // edges; then the Turntable grips (spin, fling, sliding the town), its circle
    // stick, and Diorama's smooth stick turn. The expected numbers are TPM's own
    // rig's (the reference). Puts the mode, the settings, the table and its turn back.
    static void SetView(TableRig rig, string mode, Backdrop background)
    {
        VrUiKit.VrModes.Select(mode);
        rig.Settings.Set(RigMode.Turntable, "Background", background);
    }

    static IEnumerator<float> DioramaScenario(VrSession vr, TableRig rig)
    {
        string mode0 = VrUiKit.VrModes.Current?.Id;
        var background0 = rig.Settings.Get<Backdrop>(RigMode.Turntable, "Background");
        var edge0 = rig.Settings.Get<EdgeStyle>(RigMode.Turntable, "Edge");
        float scale0 = rig.Scale, yaw0 = rig.Yaw;
        // Scale 4x = zoomed out over far more of the town.
        var cases = new (string name, string mode, Backdrop bg, float zoomOut)[]
        {
            ("diorama", "Diorama", Backdrop.Light, 1),
            ("turntable_dark", "Turntable", Backdrop.Dark, 1),
            ("turntable_light", "Turntable", Backdrop.Light, 1),
            ("turntable_passthrough", "Turntable", Backdrop.Passthrough, 1),
            ("zoomed_out_diorama", "Diorama", Backdrop.Dark, 4),
            ("zoomed_out_turntable", "Turntable", Backdrop.Dark, 4),
        };
        var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
        foreach (var c in cases)
        {
            SetView(rig, c.mode, c.bg);
            yield return 0.2f;
            rig.Scale = scale0 * c.zoomOut;
            sway = true;
            yield return 1f;
            PerfLog.Label = "table " + c.name;
            PerfLog.Reset();
            yield return 5.2f;
            sway = false;
            vr.SimulateHead(new Pose(new NVec(0, 1.5f, 0), Pose.Pitch(35)));
            yield return 0.5f;
            Log($"[diorama] {c.name}: mode {rig.Mode}, scale {rig.Scale:F0}, anchor {rig.AnchorN()}, cut radius {rig.CutRadius:F0}, far {rig.Eye(0).farClipPlane:F0}, shadow distance {urp?.shadowDistance:F0}, renderers {CylinderCull.Count}");
            SimShots.Request("diorama_" + c.name);
            while (SimShots.Busy) yield return 0.1f;
            yield return 0.5f;
        }
        PerfLog.Label = "";
        rig.Scale = scale0;

        // The per-pixel mask: sharp and soft edges, and the image flip the
        // shader assumes (the wrong one puts the circle in the wrong place).
        Log($"[diorama] mask available {CylinderMask.Available}");
        SetView(rig, "Turntable", Backdrop.Dark);
        foreach (var (edgeName, edgeStyle, flip) in new[] { ("sharp", EdgeStyle.Sharp, true), ("soft", EdgeStyle.Soft, true), ("sharp_noflip", EdgeStyle.Sharp, false) })
        {
            rig.Settings.Set(RigMode.Turntable, "Edge", edgeStyle);
            CylinderMask.FlipY = flip;
            yield return 0.5f;
            SimShots.Request("mask_" + edgeName);
            while (SimShots.Busy) yield return 0.1f;
        }
        CylinderMask.FlipY = true;
        rig.Settings.Set(RigMode.Turntable, "Edge", edge0);

        // Turntable grips. The off hand (left unless left-handed) sweeps a quarter
        // turn around the table centre: the model should turn 90 degrees and its
        // centre stay put. The pointer hand drags 10 cm right: the table centre
        // should stay, the world point under the hand follow it.
        yield return 0.5f;
        bool leftIsOff = !Plugin.Instance.LeftHanded.Value;
        var t = rig.TableCentreN();
        var anchorBefore = rig.AnchorN();
        float yawBefore = rig.Yaw;
        SetOffHand(leftIsOff, new Pose(t + new NVec(0.3f, 0, 0), System.Numerics.Quaternion.Identity), false);
        yield return 0.3f;
        SetOffHand(leftIsOff, new Pose(t + new NVec(0.3f, 0, 0), System.Numerics.Quaternion.Identity), true);
        yield return 0.3f;
        for (int i = 1; i <= 10; i++)
        {
            float a = (90 - 9 * i) * MathF.PI / 180; // 90 degrees (+X) round to 0 (+Z)
            SetOffHand(leftIsOff, new Pose(t + new NVec(0.3f * MathF.Sin(a), 0, 0.3f * MathF.Cos(a)), System.Numerics.Quaternion.Identity), true);
            yield return 0.05f;
        }
        yield return 0.2f;
        SetOffHand(leftIsOff, new Pose(t + new NVec(0, 0, 0.3f), System.Numerics.Quaternion.Identity), false);
        yield return 0.3f;
        Log($"[diorama] turntable: yaw {yawBefore:F0} -> {rig.Yaw:F0} (expect +90), centre world {anchorBefore} -> {rig.AnchorN()}");
        SimShots.Request("diorama_turned");
        while (SimShots.Busy) yield return 0.1f;

        // Fling: a fast quarter sweep, let go mid-swing; it should keep turning and slow down.
        SetOffHand(leftIsOff, new Pose(t + new NVec(0.3f, 0, 0), System.Numerics.Quaternion.Identity), true);
        yield return 0.2f;
        for (int i = 1; i <= 6; i++)
        {
            float a = (90 - 15 * i) * MathF.PI / 180;
            SetOffHand(leftIsOff, new Pose(t + new NVec(0.3f * MathF.Sin(a), 0, 0.3f * MathF.Cos(a)), System.Numerics.Quaternion.Identity), true);
            yield return 0.03f;
        }
        SetOffHand(leftIsOff, new Pose(t + new NVec(0, 0, 0.3f), System.Numerics.Quaternion.Identity), false);
        float flungAt = rig.Yaw;
        yield return 0.5f;
        float flung05 = rig.Yaw;
        yield return 1.5f;
        float flung2 = rig.Yaw;
        yield return 2f;
        Log($"[diorama] fling: yaw at release {flungAt:F0}, +0.5 s {flung05:F0}, +2 s {flung2:F0}, +4 s {rig.Yaw:F0} (expect still turning, then slowing to a stop)");

        // Turntable: the pointer stick moves the circle, and
        // pushing forward brings it towards you (as if you'd walked forward).
        var tableBeforeStick = rig.TableCentreN();
        stickHoldY = 1;
        yield return 0.5f;
        stickHoldY = 0;
        yield return 0.3f;
        Log($"[diorama] pointer stick forward 0.5 s: table {tableBeforeStick} -> {rig.TableCentreN()} (expect ~0.25 m towards the head at z 0), yaw {rig.Yaw:F0}");

        var tableBefore = rig.TableCentreN();
        var anchorAfterTurn = rig.AnchorN();
        var pointerAt = tableBefore + new NVec(0, 0.1f, 0.1f);
        SetPointerHand(leftIsOff, new Pose(pointerAt, System.Numerics.Quaternion.Identity), false);
        yield return 0.3f;
        SetPointerHand(leftIsOff, new Pose(pointerAt, System.Numerics.Quaternion.Identity), true);
        yield return 0.3f;
        for (int i = 1; i <= 10; i++)
        {
            SetPointerHand(leftIsOff, new Pose(pointerAt + new NVec(0.01f * i, 0.05f, 0), System.Numerics.Quaternion.Identity), true);
            yield return 0.05f;
        }
        yield return 0.2f;
        SetPointerHand(leftIsOff, new Pose(pointerAt + new NVec(0.1f, 0.05f, 0), System.Numerics.Quaternion.Identity), false);
        yield return 0.3f;
        Log($"[diorama] slide: table {tableBefore} -> {rig.TableCentreN()} (expect unchanged), centre world {anchorAfterTurn} -> {rig.AnchorN()} (expect moved, height unchanged)");
        SimShots.Request("diorama_slid");
        while (SimShots.Busy) yield return 0.1f;

        // Diorama: the off stick turns smoothly (no snaps) and coasts to a stop.
        SetView(rig, "Diorama", Backdrop.Dark);
        yield return 0.5f;
        float turn0 = rig.Yaw;
        leftStickHoldX = 1;   // the off stick turns (the table standard)
        yield return 0.25f;
        float turnQuarter = rig.Yaw;
        yield return 0.75f;
        float turnHeld = rig.Yaw;
        leftStickHoldX = 0;
        yield return 3f;
        Log($"[diorama] stick turn: yaw {turn0:F0} -> {turnQuarter:F0} after 0.25 s -> {turnHeld:F0} after 1 s held -> {rig.Yaw:F0} after coasting 3 s (TPM's rig: ~70 deg in the first second, then a little more; off stick)");

        rig.Turn(rig.Yaw - yaw0);
        rig.Recenter();
        if (mode0 != null) VrUiKit.VrModes.Select(mode0);
        rig.Settings.Set(RigMode.Turntable, "Background", background0);
        yield return 0.5f;
        Log($"[diorama] restored: mode {rig.Mode}, background {rig.Settings.Get<Backdrop>(RigMode.Turntable, "Background")}");
    }

    static void SetOffHand(bool leftIsOff, Pose pose, bool gripHeld)
    {
        if (leftIsOff) { leftPose = pose; leftGrip = gripHeld; }
        else { rightPose = pose; grip = gripHeld; }
    }

    static void SetPointerHand(bool leftIsOff, Pose pose, bool gripHeld) => SetOffHand(!leftIsOff, pose, gripHeld);

    static IEnumerable<float> ClickMenu(Plugin cfg, float x, float y) =>
        ClickMenuAt(cfg, VrUiKit.VrUi.MenuPointToTracking(x, y), $"({x},{y})");

    // Clicks the menu control with this label (a page, a hero card, a row, a button,
    // "bind:<action>"...). The menu scrolls it into view; rows behind a closed fold or a
    // collapsed section need "more:<section id>" / "section:<section id>" first.
    static IEnumerable<float> ClickMenu(Plugin cfg, string label)
    {
        if (VrUiKit.VrUi.MenuPointOf(label) is not NVec p)
        {
            Log($"[vrui] no '{label}' on the menu");
            yield break;
        }
        foreach (var step in ClickMenuAt(cfg, p, $"'{label}'")) yield return step;
    }

    static IEnumerable<float> ClickMenuAt(Plugin cfg, NVec point, string what)
    {
        AimAtTracking(cfg, point);
        yield return 0.4f;
        Log($"[vrui] menu click at {what}: on VR menu {VrPointer.OnVrMenu}");
        trigger = true;
        yield return 0.15f;
        trigger = false;
        yield return 0.6f;
    }
}
