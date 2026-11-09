using System;
using System.Collections.Generic;
using UnityEngine;
using NVec = System.Numerics.Vector3;

namespace TpmVr;

// Shows the game's screen-space UI in VR. While VR is active, every root
// ScreenSpaceOverlay canvas is switched to ScreenSpaceCamera on a hidden UI
// camera that renders into a screen-sized texture, so the game's layout,
// scaling and input code see exactly the same pixel space as on the monitor.
// The texture is drawn on a floating panel; laser hits map to pixels on it.
static class VrMenuPanel
{
    // Panel placement in tracking space (metres). In the museum: from config,
    // above the table, tilted back slightly (the HUD sits at the edges and the
    // middle is transparent). In cinema mode: a big screen straight ahead, the
    // same size and place as VrCinema's game view, which it overlays.
    static NVec PanelCentre(TableRig rig) => !rig.InWorld
        ? CinemaCentre
        : new(0, Plugin.Instance.MenuHeight.Value, Plugin.Instance.MenuDistance.Value);
    static float PanelWidth(TableRig rig) => !rig.InWorld ? CinemaWidth : Plugin.Instance.MenuWidth.Value;
    static float PanelTilt(TableRig rig) => !rig.InWorld ? 0f : -10f;

    public static readonly NVec CinemaCentre = new(0, 1.45f, 2.2f);
    public const float CinemaWidth = 3.0f;

    // The UI camera hosts the converted canvases. It must sit near the origin:
    // the game's soft masks (scrolling lists) compute clipping from world-space
    // vertex positions, which falls apart at large coordinates. Its layer (UI)
    // is excluded from the eye cameras, and it's below the terrain.
    static readonly Vector3 UiCameraPosition = new(0, -60, 0);
    const float CanvasPlaneDistance = 10f;

    static Camera uiCamera;
    static RenderTexture texture;
    static GameObject panel;
    static Material material;
    static readonly HashSet<IntPtr> converted = new();
    static readonly List<Canvas> convertedCanvases = new();
    // Nested canvases moved onto their root canvas's layer, with the layer they had.
    static readonly Dictionary<IntPtr, (GameObject go, int layer)> relayered = new();
    static float nextScan;
    static TableRig lastRig;

    public static bool Visible => panel != null && panel.activeSelf;
    public static bool Hidden; // keeps the panel out of the eye views (captures, and later the VR menu's layouts)
    public static bool ShowFull = true; // false while the VR UI framework shows the UI as separate windows
    public static IReadOnlyList<Canvas> Canvases => convertedCanvases;
    public static RenderTexture Texture => texture;
    public static Camera UiCamera => uiCamera;

    public static void Update(TableRig rig)
    {
        lastRig = rig;
        if (!rig.Active)
        {
            RestoreCanvases();
            if (panel != null) panel.SetActive(false);
            return;
        }
        EnsureResources();

        // Scanning every object for new canvases is slow on a scene this size,
        // so do it rarely; new menus usually live on the canvases already found.
        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 2f;
            nextNestedScan = Time.unscaledTime + 0.25f;
            ConvertCanvases();
        }
        else if (Time.unscaledTime >= nextNestedScan)
        {
            nextNestedScan = Time.unscaledTime + 0.25f;
            RelayerNestedCanvases();
        }

        // Re-rendering the full-screen UI every frame costs a lot on the single
        // render thread; menus look fine refreshed every few frames.
        TpmHost.KeepOutOfGi(uiCamera);
        int every = Math.Max(1, PerfSwitches.MenuRefreshOverride ?? Plugin.Instance.MenuRefreshEveryFrames.Value);
        uiCamera.enabled = !PerfSwitches.UiCameraOff && Time.frameCount % every == 0;

        panel.SetActive(!Hidden && ShowFull);
        // In the museum the user can carry it (VrPane); the cinema screen stays put (the game view is drawn there).
        float aspect = (float)texture.height / texture.width;
        (current, currentWidth) = VrUiKit.VrPane.Place(!rig.InWorld ? null : "Panel",
            new Pose(PanelCentre(rig), Pose.Pitch(PanelTilt(rig))), PanelWidth(rig), aspect, !Hidden && ShowFull);
        var pose = PanelPose(rig);
        float width = currentWidth, height = width * aspect;
        panel.transform.SetPositionAndRotation(pose.UnityPosition, pose.UnityRotation);
        panel.transform.localScale = new Vector3(width * rig.WorldScale, height * rig.WorldScale, 1);
    }

    // Tracking-space point on the panel for a screen pixel (for scripted aiming).
    public static NVec PixelToTracking(Vector2 pixel)
    {
        float width = currentWidth, height = width * texture.height / texture.width;
        var local = new NVec((pixel.x / Screen.width - 0.5f) * width, (pixel.y / Screen.height - 0.5f) * height, 0);
        return current.Position + NVec.Transform(local, current.Rotation);
    }

    // Where the panel was drawn last, in tracking space: where the user carried it, else the default.
    static Pose current = new(new NVec(0, 1.5f, 1.5f), System.Numerics.Quaternion.Identity);
    static float currentWidth = 1.6f;

    // World pose of the panel (rotation only, unscaled).
    public static Pose PanelPose(TableRig rig) => rig.TrackingToWorld(!rig.InWorld
        ? new Pose(PanelCentre(rig), Pose.Pitch(PanelTilt(rig))) : current);

    // Where a world ray hits the panel, as a screen pixel (y up) plus distance along the ray.
    public static bool Raycast(TableRig rig, NVec origin, NVec dir, out Vector2 pixel, out float distance)
    {
        pixel = default;
        distance = 0;
        if (!Visible) return false;
        var pose = PanelPose(rig);
        var normal = NVec.Transform(NVec.UnitZ, pose.Rotation);
        float denom = NVec.Dot(dir, normal);
        if (MathF.Abs(denom) < 1e-5f) return false;
        distance = NVec.Dot(pose.Position - origin, normal) / denom;
        if (distance <= 0) return false;
        var hit = origin + dir * distance;
        var local = NVec.Transform(hit - pose.Position, System.Numerics.Quaternion.Inverse(pose.Rotation)) / rig.WorldScale;
        float width = currentWidth, height = width * texture.height / texture.width;
        float u = local.X / width + 0.5f, v = local.Y / height + 0.5f;
        if (u < 0 || u > 1 || v < 0 || v > 1) return false;
        pixel = new Vector2(u * Screen.width, v * Screen.height);
        return true;
    }

    static void EnsureResources()
    {
        if (texture != null && (texture.width != Screen.width || texture.height != Screen.height))
        {
            texture.Release();
            texture = null;
        }
        if (texture == null)
        {
            texture = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "TpmVr UI",
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            texture.Create();
            if (uiCamera != null) uiCamera.targetTexture = texture;
            if (material != null) SetTexture(material, texture);
        }
        if (uiCamera == null)
        {
            var go = new GameObject("TpmVr UI Camera");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.transform.position = UiCameraPosition;
            uiCamera = go.AddComponent<Camera>();
            uiCamera.clearFlags = CameraClearFlags.SolidColor;
            uiCamera.backgroundColor = new Color(0, 0, 0, 0);
            uiCamera.nearClipPlane = 0.3f;
            uiCamera.farClipPlane = CanvasPlaneDistance * 2;
            uiCamera.depth = -20;
            uiCamera.cullingMask = 0;
            uiCamera.targetTexture = texture;
        }
        if (panel == null)
        {
            panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
            panel.name = "TpmVr Menu Panel";
            panel.layer = VrCore.VisualLayer;
            UnityEngine.Object.DontDestroyOnLoad(panel);
            var collider = panel.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            material = Primitives.MakeTextured(texture, transparent: true);
            var renderer = panel.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // Turntable pulls the eyes' far plane in to the circle (the core's
            // CylinderCull.FitFar) and keeps only registered UI quads inside it;
            // unregistered, this panel was clipped away whenever you looked down.
            VrUiKit.UiGeometry.Register(renderer);
        }
    }

    static void ConvertCanvases()
    {
        var all = UnityEngine.Object.FindObjectsOfType<Canvas>();
        foreach (var canvas in all)
        {
            if (!canvas.isActiveAndEnabled || !canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay) continue;
            if (!converted.Add(canvas.Pointer)) continue;
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = uiCamera;
            canvas.planeDistance = CanvasPlaneDistance;
            uiCamera.cullingMask |= 1 << canvas.gameObject.layer;
            convertedCanvases.Add(canvas);
            Plugin.Logger.LogInfo($"[menu] canvas '{canvas.name}' -> VR panel (order {canvas.sortingOrder}, layer {canvas.gameObject.layer})");
        }
        RelayerNestedCanvases();
    }

    // A canvas drawn by a camera is only drawn if its layer is in the camera's
    // culling mask, nested canvases included; on the monitor (overlay) layers
    // don't matter. Some of the game's nested canvases are on the Default layer
    // (the pause menu's title bar, every part of the full-screen tutorial
    // pop-ups), so in VR they vanished. Put them on their root canvas's layer.
    // (Inactive ones too, so menus are right the moment they open.)
    // Runs often (pop-ups are created when they open), so canvases already
    // checked are skipped.
    static void RelayerNestedCanvases()
    {
        foreach (var root in convertedCanvases)
        {
            if (root == null || root.WasCollected) continue;
            int layer = root.gameObject.layer;
            foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
            {
                if (canvas.Pointer == root.Pointer || checkedNested.Contains(canvas.Pointer)) continue;
                var go = canvas.gameObject;
                // Popout windows give each window's roots a nested canvas on a
                // spare layer of its own: leave those (and check them again once the mode lets go).
                if (IsPopoutLayer(go.layer)) continue;
                checkedNested.Add(canvas.Pointer);
                if (go.layer == layer) continue;
                relayered[canvas.Pointer] = (go, go.layer);
                Plugin.Logger.LogInfo($"[menu] nested canvas '{go.name}' (under '{root.name}') layer {go.layer} -> {layer}");
                go.layer = layer;
            }
        }
    }

    // A layer the core's Popout windows pick: unnamed, not drawn by the UI camera, not ours.
    static bool IsPopoutLayer(int layer) =>
        layer >= 8 && layer != VrCore.VisualLayer && uiCamera != null && (uiCamera.cullingMask & (1 << layer)) == 0
        && string.IsNullOrEmpty(LayerMask.LayerToName(layer));

    static readonly HashSet<IntPtr> checkedNested = new();
    static float nextNestedScan;

    static void RestoreCanvases()
    {
        if (convertedCanvases.Count == 0) return;
        foreach (var (go, layer) in relayered.Values)
            if (go != null && !go.WasCollected) go.layer = layer;
        relayered.Clear();
        checkedNested.Clear();
        foreach (var canvas in convertedCanvases)
            if (canvas != null) canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        convertedCanvases.Clear();
        converted.Clear();
        Plugin.Logger.LogInfo("[menu] canvases restored to the monitor");
    }

    static void SetTexture(Material m, Texture t)
    {
        m.mainTexture = t;
        m.SetTexture("_BaseMap", t);
    }
}
