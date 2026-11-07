using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TPS.Core;
using TPS.Game;
using UnityEngine;

namespace TpmVr;

// The core's diagnostic dump (VrRecon: cameras, renderer layers, canvases,
// input module, the UI structure under the windows' roots), written a few
// seconds after each scene change or main camera change, with TPM's own lines:
// the game's App canvases and camera logic, the cursor, and the cameras that
// rendered since the last dump. On with Debug.ReconLogging.
static class TpmRecon
{
    static readonly HashSet<string> renderedCameras = new();
    static readonly string[] UiRoots = { "FinanceUI", "ObjectivesContainer", "HubMenuUI", "CalendarUI" };

    public static void Tick() => VrRecon.Tick(() => App.Instance?.MainCamera?.name, UiRoots, Extra);

    static void Extra(StringBuilder sb)
    {
        var app = App.Instance;
        if (app == null) { sb.AppendLine("[recon]   App: none"); return; }
        var cam = app.MainCamera;
        sb.AppendLine($"[recon]   MainCamera '{cam?.name}' logic {app.MainCameraLogic?.GetIl2CppType().FullName}");
        Canvas("Canvas", app.Canvas, sb);
        Canvas("ActiveUICanvas", app.ActiveUICanvas, sb);
        Canvas("OverEverythingCanvas", app.OverEverythingCanvas, sb);
        var cursor = CursorManager.Instance;
        sb.AppendLine($"[recon]   cursor world {cursor?.WorldPosition} screen {cursor?.ScreenPosition} mode {cursor?._mode?.GetIl2CppType().Name}");
        sb.AppendLine($"[recon]   rendered since the last dump: {string.Join(", ", renderedCameras)}");
        renderedCameras.Clear();
    }

    static void Canvas(string label, Canvas c, StringBuilder sb) =>
        sb.AppendLine(c == null ? $"[recon]   {label}: null"
            : $"[recon]   {label} '{c.name}' mode {c.renderMode} worldCam {(c.worldCamera ? c.worldCamera.name : "none")} scale {c.scaleFactor} children {c.transform.childCount}");

    // The game's own beginCameraRendering handler: patching it gives a
    // per-camera render callback without converting a managed delegate.
    // Not only diagnostics: it raises the core's per-camera events (cylinder
    // mask, spectator view), which under IL2CPP the core can't hook itself.
    [HarmonyPatch(typeof(App), nameof(App.OnBeginCameraRendering))]
    internal static class BeginCameraHook
    {
        static void Postfix(Camera cam)
        {
            if (cam == null) return;
            if (renderedCameras.Count < 64) renderedCameras.Add(cam.name);
            VrPipeline.RaiseBefore(cam);
        }
    }
}
