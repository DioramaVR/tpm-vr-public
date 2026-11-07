using System;
using TPS.Core;
using TPS.Game;
using UnityEngine;
using VrUiKit;
using NVec = System.Numerics.Vector3;

namespace TpmVr;

// Two Point Museum's side of the core's table rig (TableRig, docs/RIG.md): the
// game camera's pivot is TopDownCameraLogic's focal point. Panning holds the
// game's own W A S D (so its edges and clamps apply), turning echoes the game's
// camera-turn keys (its camera tutorial waits for them), and the grips and
// sticks stay with an item while one is held.
sealed class TpmHost : PivotHost
{
    static TopDownCameraLogic Logic => App.Instance?.MainCameraLogic?.TryCast<TopDownCameraLogic>();

    public override Camera GameCamera => App.Instance?.MainCamera;
    public override bool InWorld => App.Instance?.MainCamera != null && Logic != null;
    // While an item is held, the grips don't move the table (squeezing the controller
    // to hold an item shouldn't drag the world out from under it).
    public override bool GripsBusy => VrPointer.Grabbing;
    // Placing an item: the pointer stick turns the item (VrPointer), so the table
    // doesn't turn, zoom or pan with the sticks meanwhile (as TPM's own rig did).
    public override TableInputs Taken =>
        CursorManager.Instance?._mode?.TryCast<CursorModePlaceItem>() != null ? TableInputs.All : TableInputs.None;

    // The game's camera keys echo the table sticks, so the game (and its camera
    // tutorial, which waits for Q / E and the zoom keys) sees normal camera input;
    // the rig still turns and zooms the table itself.
    const float EchoThreshold = 0.5f;

    public override TableInputs Sticks(TableSticks s)
    {
        if (s.Turn > EchoThreshold) VirtualKeyboard.Hold(VKey.TurnCameraLeft);
        if (s.Turn < -EchoThreshold) VirtualKeyboard.Hold(VKey.TurnCameraRight);
        if (s.Zoom > EchoThreshold) VirtualKeyboard.Hold(VKey.ZoomIn);
        if (s.Zoom < -EchoThreshold) VirtualKeyboard.Hold(VKey.ZoomOut);
        return TableInputs.None;
    }

    public override bool TryGetPivot(out Vector3 focal, out float yaw)
    {
        focal = default;
        yaw = 0;
        var logic = Logic;
        var cam = App.Instance?.MainCamera;
        if (logic == null || cam == null) return false;
        focal = logic.CurrentFocalPoint;
        yaw = cam.transform.eulerAngles.y;
        return true;
    }

    // Keep the game camera centred on what the table shows, so its culling and LOD
    // follow. Sideways only: the game owns the focal height.
    public override Vector3 SetPivot(Vector3 focal, float yaw)
    {
        var logic = Logic;
        if (logic == null) return focal;
        var current = logic.CurrentFocalPoint;
        var to = new Vector3(focal.x, current.y, focal.z);
        logic.SetFocalPoint(to, true, logic._focalYOffset);
        return to;
    }

    // Panning holds the game's W A S D. "Pan away" means away from you across the
    // table: the rig gives the direction in the world, matched here against the game
    // camera's own forward / right (which can differ from the table's turn).
    const float StickHoldThreshold = 0.5f;

    public override bool Pan(Vector3 worldDelta)
    {
        float x = VrInput.Value(TableActions.PanRight) - VrInput.Value(TableActions.PanLeft);
        float y = VrInput.Value(TableActions.PanUp) - VrInput.Value(TableActions.PanDown);
        var cam = App.Instance?.MainCamera;
        if (x * x + y * y < StickHoldThreshold * StickHoldThreshold || cam == null) return true;
        var cf = cam.transform.forward;
        var camForward = new Vector3(cf.x, 0, cf.z).normalized;
        var camRight = new Vector3(camForward.z, 0, -camForward.x);
        var dir = new Vector3(worldDelta.x, 0, worldDelta.z).normalized;
        float forward = Vector3.Dot(dir, camForward), right = Vector3.Dot(dir, camRight);
        // Within ~67 degrees of an axis counts, so diagonals hold two keys.
        const float axis = 0.38f;
        if (forward > axis) VirtualKeyboard.Hold(VKey.PanUp);
        if (forward < -axis) VirtualKeyboard.Hold(VKey.PanDown);
        if (right > axis) VirtualKeyboard.Hold(VKey.PanRight);
        if (right < -axis) VirtualKeyboard.Hold(VKey.PanLeft);
        return true;
    }


    // The game's GI (Octalux) re-centres its light probes on whichever camera
    // rendered last. If the eyes take part, every head turn moves the probes and
    // the lighting visibly re-converges over a second or two, so leave it to the
    // game's own camera. The game adds the component when a camera first renders,
    // so this is checked every frame.
    public static void KeepOutOfGi(Camera cam)
    {
        var gi = cam.GetComponent<TPS.Octalux.OctaluxAdditionalCameraData>();
        if (gi == null) gi = cam.gameObject.AddComponent<TPS.Octalux.OctaluxAdditionalCameraData>();
        if (gi._octaluxCanUpdateUsingCamera) gi._octaluxCanUpdateUsingCamera = false;
    }

    // The table for TPM: the old [VR] / [Diorama] settings carry over once, and the
    // defaults keep TPM's feel (TPM's rig is the reference for Diorama and Turntable),
    // apart from the user's decisions (smooth turning with coast, the table at 0.75 m).
    public static TableRig Create(VrSession vr, Plugin cfg)
    {
        void Feel(TableTuning t)
        {
            t.Scale = 60f; t.MinScale = 5f; t.MaxScale = 500f;
            t.TurnSpeed = 90f; t.TurnRamp = 0.25f; t.SpinCoast = 0.7f; t.DeadZone = 0.15f;
            t.ZoomSpeed = 0.405f;   // ln 1.5: the scale changes 1.5x a second at full stick
            t.CircleSpeed = 0.5f;
            t.Background = Backdrop.Light;   // TPM's look in both modes (the core's Diorama default is the game's own)
        }
        var rig = new TableRig(vr, cfg.Config, new TpmHost())
            // Eye post-processing off by default: TPM's eyes never had the game's (it darkened the
            // menu and flickered on rooftops while scaling, 2026-11-06); the Graphics tab can turn it on.
            .Table(t => { t.TableHeight = 0.75f; t.TableDistance = 0.6f; }, e => { e.EyeResolution = 1f; e.EyePostProcessing = false; },
                new OldKey("TableHeight", "VR", "TableHeight", 0.9f),
                new OldKey("TableDistance", "VR", "TableDistance", 0.6f),
                new OldKey("FollowGameCamera", "VR", "FollowFocalPoint", true),
                new OldKey("DesktopView", "VR", "DesktopView", false),
                new OldKey("EyeResolution", "VR", "EyeResolution", 1f),
                new OldKey("EyeShadows", "VR", "EyeShadows", true))
            .Offer(RigMode.Diorama, Feel, new[] { "Laser", "Game", "Placing items" },
                new OldKey("Scale", "VR", "Scale", 60f),
                new OldKey("SpinCoast", "VR", "SpinCoast", 0.7f),
                new OldKey("Background", "Diorama", "Background", "Light"))
            .Offer(RigMode.Turntable, t => { Feel(t); t.Radius = 0.6f; },
                new[] { "Laser", "Game", "Placing items" },
                new OldKey("Scale", "VR", "Scale", 60f),
                new OldKey("SpinCoast", "VR", "SpinCoast", 0.7f),
                new OldKey("Radius", "Diorama", "DioramaRadiusMetres", 0.6f),
                new OldKey("Edge", "Diorama", "Edge", "Soft"),
                new OldKey("Background", "Diorama", "Background", "Light"),
                new OldKey("KeyColour", "Diorama", "PassthroughKeyColour", "009933"))
            // TPM's old view setting: VR view was Diorama, its Diorama was Turntable (Tabletop was hidden).
            .OldMode("Diorama", "View", "VrView", ("VrView", RigMode.Diorama), ("Diorama", RigMode.Turntable), ("Tabletop", RigMode.Turntable));
        rig.PrepareEye = KeepOutOfGi;
        return rig;
    }
}

// TPM's code works in System.Numerics; the core's rig in Unity's types.
static class TableRigNumerics
{
    public static NVec TrackingToWorld(this TableRig rig, NVec t)
    {
        var w = rig.TrackingToWorld(new Vector3(t.X, t.Y, t.Z));
        return new NVec(w.x, w.y, w.z);
    }

    public static NVec WorldToTracking(this TableRig rig, NVec w)
    {
        var t = rig.WorldToTracking(new Vector3(w.X, w.Y, w.Z));
        return new NVec(t.x, t.y, t.z);
    }

    public static NVec AnchorN(this TableRig rig) => new(rig.Anchor.x, rig.Anchor.y, rig.Anchor.z);
    public static NVec TableCentreN(this TableRig rig) => new(rig.TableCentre.x, rig.TableCentre.y, rig.TableCentre.z);

    public static bool IsRightEye(this TableRig rig, Camera cam) => cam != null && rig.Eye(1) != null && cam.Pointer == rig.Eye(1).Pointer;
}
