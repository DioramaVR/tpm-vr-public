using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace TpmVr;

// Diagnostics for real play ([Debug] RecordScreens): whenever the game opens or
// closes a menu, adds a root canvas or turns on a camera, logs what changed and
// how the new screen draws (graphics, shaders, textures, video players, the
// canvas's render mode and camera), and saves a snapshot of the UI and the
// left eye. Used to find screens that come up blank in VR.
static class ScreenRecorder
{
    static float nextCheck;
    static HashSet<string> lastMenus = new(), lastCanvases = new(), lastCameras = new();
    static int shot;
    static bool started;

    static void Log(string msg) => Plugin.Logger.LogInfo($"[rec] {msg}");

    public static void Update()
    {
        if (Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.5f;
        if (!started)
        {
            started = true;
            VrUiKit.VrUi.TraceRemainder = true;
            Log($"recording screens; snapshots go to {SimShots.Dir} as rec_NN_*");
        }
        if (Experiment()) return;

        var menus = Object.FindObjectsOfType<TPS.Game.UI.MenuBase>()
            .Where(m => m.isActiveAndEnabled).ToList();
        var menuKeys = menus.Select(Key).ToHashSet();
        var canvases = Object.FindObjectsOfType<Canvas>()
            .Where(c => c.isRootCanvas && c.isActiveAndEnabled).ToList();
        var canvasKeys = canvases.Select(c => c.name).ToHashSet();
        var cameras = Object.FindObjectsOfType<Camera>()
            .Where(c => c.isActiveAndEnabled && !c.name.StartsWith("TpmVr")).ToList();
        var cameraKeys = cameras.Select(c => c.name).ToHashSet();

        bool changed = false;
        foreach (var m in menus.Where(m => !lastMenus.Contains(Key(m))))
        {
            changed = true;
            Log($"menu opened {Key(m)} at {Path(m.transform)} (mode {VrUiKit.VrUi.Mode})");
            DescribeCanvas(m.GetComponentInParent<Canvas>()?.rootCanvas);
            Describe(m.transform);
            if (experimentMenu == null && FullScreenBackdrops(m.transform).Count > 0)
            {
                experimentMenu = m;
                experimentAt = Time.unscaledTime + 2f;
                experimentStep = 0;
                Log($"  has a full-screen blur backdrop: will snapshot it with and without the backdrop in 2 s");
            }
        }
        foreach (var k in lastMenus.Where(k => !menuKeys.Contains(k)))
            Log($"menu closed {k}");
        foreach (var c in canvases.Where(c => !lastCanvases.Contains(c.name)))
        {
            changed = true;
            Log($"root canvas appeared '{c.name}'");
            DescribeCanvas(c);
        }
        foreach (var c in cameras.Where(c => !lastCameras.Contains(c.name)))
        {
            changed = true;
            Log($"camera on '{c.name}' depth {c.depth} mask 0x{c.cullingMask:X} clear {c.clearFlags} target {Tex(c.targetTexture)}");
        }
        foreach (var k in lastCameras.Where(k => !cameraKeys.Contains(k)))
            Log($"camera off '{k}'");

        lastMenus = menuKeys;
        lastCanvases = canvasKeys;
        lastCameras = cameraKeys;
        if (changed)
        {
            var name = $"rec_{shot++:D2}";
            var focus = VrUiKit.VrUi.WindowRect("focus");
            Log($"snapshot {name}; pop-ups window {(VrUiKit.VrUi.WindowVisible("focus") ? focus.ToString() : "hidden")}");
            SimShots.Request(name, light: true);
        }
    }

    static string Key(TPS.Game.UI.MenuBase m) => m.GetIl2CppType().Name + ":" + m.name;

    // A menu with a full-screen blur backdrop (the kind that shows blank in VR):
    // once it has finished opening, log what decides its draw order, snapshot
    // it, hide the backdrop, snapshot again and put the backdrop back.
    static TPS.Game.UI.MenuBase experimentMenu;
    static float experimentAt;
    static int experimentStep;
    static List<Graphic> hiddenBackdrops = new();

    static List<Graphic> FullScreenBackdrops(Transform root) => root.GetComponentsInChildren<Graphic>()
        .Where(g => g.isActiveAndEnabled && g.GetIl2CppType().Name == "UITranslucentImage"
                    && g.rectTransform.rect.width >= 1900 && g.rectTransform.rect.height >= 1000).ToList();

    // Returns true while it is using this tick.
    static bool Experiment()
    {
        if (experimentMenu == null || Time.unscaledTime < experimentAt || SimShots.Busy) return false;
        if (experimentMenu.WasCollected || !experimentMenu.isActiveAndEnabled)
        {
            Log("experiment: the menu closed before it ran");
            foreach (var g in hiddenBackdrops) if (g != null && !g.WasCollected) g.enabled = true;
            hiddenBackdrops.Clear();
            experimentMenu = null;
            return false;
        }
        var name = $"rec_{shot:D2}";
        switch (experimentStep++)
        {
            case 0:
                Log($"experiment on {Key(experimentMenu)}: draw order");
                DescribeOrder(experimentMenu.transform);
                Log($"experiment snapshot {name}_a (as shown)");
                SimShots.Request(name + "_a", light: true);
                return true;
            case 1:
                hiddenBackdrops = FullScreenBackdrops(experimentMenu.transform);
                foreach (var g in hiddenBackdrops) g.enabled = false;
                Log($"experiment: hid {hiddenBackdrops.Count} backdrop(s)");
                return true;
            case 2:
                Log($"experiment snapshot {name}_b (backdrop hidden)");
                SimShots.Request(name + "_b", light: true);
                return true;
            default:
                foreach (var g in hiddenBackdrops) if (g != null && !g.WasCollected) g.enabled = true;
                Log("experiment: backdrop restored");
                hiddenBackdrops.Clear();
                experimentMenu = null;
                shot++;
                return true;
        }
    }

    // What decides draw order in a camera-rendered canvas: nested canvases and
    // their sorting, fade groups, each graphic's canvas, depth and distance from the camera.
    static void DescribeOrder(Transform root)
    {
        for (var t = root; t != null; t = t.parent)
        {
            var c = t.GetComponent<Canvas>();
            if (c != null) Log($"  above: canvas '{Path(t)}' root {c.isRootCanvas} override {c.overrideSorting} order {c.sortingOrder} layer '{c.sortingLayerName}'");
            var cg = t.GetComponent<CanvasGroup>();
            if (cg != null) Log($"  above: group '{Path(t)}' alpha {cg.alpha:F2}");
        }
        foreach (var c in root.GetComponentsInChildren<Canvas>(true))
            Log($"  nested canvas '{Path(c.transform)}' active {c.isActiveAndEnabled} override {c.overrideSorting} order {c.sortingOrder} layer '{c.sortingLayerName}'");
        foreach (var cg in root.GetComponentsInChildren<CanvasGroup>(true))
            Log($"  group '{Path(cg.transform)}' alpha {cg.alpha:F2} active {cg.isActiveAndEnabled}");
        var cam = VrMenuPanel.UiCamera;
        int n = 0;
        foreach (var g in root.GetComponentsInChildren<Graphic>())
        {
            if (!g.isActiveAndEnabled) continue;
            if (n++ >= 60) { Log("  ... more graphics"); break; }
            var cr = g.canvasRenderer;
            float z = cam != null ? cam.WorldToScreenPoint(g.transform.position).z : 0;
            Log($"  {g.GetIl2CppType().Name} '{Path(g.transform)}' canvas '{g.canvas?.name}' depth {cr?.absoluteDepth} z {z:F3} inherited alpha {cr?.GetInheritedAlpha():F2} cull {cr?.cull} scale {g.transform.lossyScale.x:F4}");
        }
    }

    static void DescribeCanvas(Canvas c)
    {
        if (c == null) return;
        bool inVr = VrMenuPanel.Canvases.Any(x => x != null && x.Pointer == c.Pointer);
        Log($"  canvas '{c.name}' mode {c.renderMode} camera '{c.worldCamera?.name}' (mask 0x{c.worldCamera?.cullingMask ?? 0:X}) order {c.sortingOrder} layer {c.gameObject.layer} shown in VR {inVr}");
    }

    // Every visible graphic under a menu (type, size on screen, shader, texture), and any video players.
    static void Describe(Transform root)
    {
        int n = 0;
        foreach (var g in root.GetComponentsInChildren<Graphic>())
        {
            if (!g.isActiveAndEnabled || g.GetIl2CppType().Name.StartsWith("TextMeshPro")) continue;
            if (n++ >= 40) { Log("  ... more graphics"); break; }
            var rt = g.rectTransform.rect;
            var cr = g.canvasRenderer;
            Log($"  {g.GetIl2CppType().Name} '{Path(g.transform)}' {rt.width:F0}x{rt.height:F0} alpha {g.color.a:F2}" +
                $"{(cr != null && cr.cull ? " culled" : "")} shader '{g.materialForRendering?.shader?.name}' queue {g.materialForRendering?.renderQueue} texture {Tex(g.mainTexture)}");
        }
        foreach (var v in root.GetComponentsInChildren<VideoPlayer>(true))
            Log($"  VideoPlayer '{Path(v.transform)}' enabled {v.isActiveAndEnabled} playing {v.isPlaying} prepared {v.isPrepared} " +
                $"render {v.renderMode} target {Tex(v.targetTexture)} source {v.source} clip '{v.clip?.name}' url '{v.url}' time {v.time:F1}");
    }

    static string Tex(Texture t)
    {
        if (t == null) return "none";
        var rt = t.TryCast<RenderTexture>();
        return $"'{t.name}' {t.GetIl2CppType().Name} {t.width}x{t.height}" + (rt != null ? $" {rt.format} created {rt.IsCreated()}" : "");
    }

    static string Path(Transform t)
    {
        var parts = new List<string>();
        for (int i = 0; t != null && i < 6; i++, t = t.parent) parts.Add(t.name);
        parts.Reverse();
        return string.Join("/", parts);
    }
}
