using System;
using HarmonyLib;
using TPS.Core;
using TPS.Game;
using Unity.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using VrUiKit;
using NVec = System.Numerics.Vector3;

namespace TpmVr;

// Laser pointer on one controller. The game's cursor follows it because every
// screen-to-world ray it builds from the main camera is replaced with the laser
// ray; the trigger drives a virtual Input System mouse, so clicks and
// click-and-hold go through the game's normal input actions.
static class VrPointer
{
    public static bool RayActive { get; private set; }
    public static Ray CurrentRay { get; private set; }
    // Native pointer of the game's main camera, for NativeRayHook to compare against.
    public static IntPtr MainCameraPtr { get; private set; }

    static VrHandVisuals[] hands;
    static GameObject laser, hitMarker;
    static bool built;

    public static void Update(VrSession vr, TableRig rig, Plugin cfg)
    {
        var main = App.Instance?.MainCamera;
        if (!rig.Active)
        {
            RayActive = false;
            SetVisible(false);
            return;
        }
        if (!built) Build();
        MainCameraPtr = main != null ? main.m_CachedPtr : IntPtr.Zero;
        VrMenuPanel.Update(rig);
        TpmUi.Update(vr, rig);
        // Cinema (main menu, map, loading): the game's view on a big screen behind the panel, at its place and size.
        var cinemaPose = VrMenuPanel.PanelPose(rig);
        VrCinema.Update(rig.Active && !rig.InWorld, () => TPS.Core.App.Instance?.MainCamera,
            cinemaPose.UnityPosition, cinemaPose.UnityRotation, VrMenuPanel.CinemaWidth);
        VrKeyboard.Update(vr);
        bool museum = rig.InWorld;

        hands[0].Place(vr.Left, rig);
        hands[1].Place(vr.Right, rig);

        var hand = cfg.LeftHanded.Value ? vr.Left : vr.Right;
        if (!hand.Valid)
        {
            RayActive = false;
            laser.SetActive(false);
            hitMarker.SetActive(false);
            return;
        }

        // Aim: controller pose tilted down by a configurable pitch (Touch poses point up along the ring).
        var aimTracking = hand.Pose.Compose(new Pose(NVec.Zero, Pose.Pitch(cfg.PointerPitch.Value)));
        var aim = rig.TrackingToWorld(aimTracking);
        var origin = aim.Position;
        var dir = NVec.Normalize(aim.Forward);
        // Only the museum's camera rays are redirected; elsewhere the mouse
        // position on the cinema screen drives the game's own rays.
        RayActive = museum;

        float scale = rig.WorldScale;
        float length = 3f * scale;
        bool hit = false;
        Vector2 mousePixel;
        // While an item is held, the panel is ignored so the item keeps following the laser.
        Vector2 panelPixel = default;
        float panelDistance = 0;
        // Free UI mode: the pointer grip moves the window under the laser.
        if (VrUiKit.VrUi.GrabbingWithLaser)
        {
            if (VrInput.Held(TpmUi.MoveWindow))
                // The core reads the holding hand's own stick for push / pull and size.
                VrUiKit.VrUi.UpdateGrab(aimTracking, 0, 0, Time.unscaledDeltaTime);
            else VrUiKit.VrUi.EndGrab();
        }
        // Our VR menu and the split UI windows first; then the full panel (Panel mode).
        var uiHit = Grabbing ? default : VrUiKit.VrUi.Raycast(origin, dir);
        if (!VrUiKit.VrUi.GrabbingWindow && VrInput.Pressed(TpmUi.MoveWindow) && uiHit.Kind is VrUiKit.UiHitKind.GameUi or VrUiKit.UiHitKind.Frame)
            VrUiKit.VrUi.BeginGrab(aimTracking);
        bool holdingWindow = VrUiKit.VrUi.GrabbingWithLaser;
        if (holdingWindow) uiHit = new VrUiKit.UiHit { Kind = VrUiKit.UiHitKind.Frame, Distance = VrUiKit.VrUi.GrabDistanceWorld };
        // On our menu, an empty window frame or a window being moved: the game gets no clicks.
        OnVrMenu = uiHit.Kind is VrUiKit.UiHitKind.Menu or VrUiKit.UiHitKind.Frame;
        OnMenu = uiHit.Kind != VrUiKit.UiHitKind.None;
        HitWindow = uiHit.Kind == VrUiKit.UiHitKind.GameUi ? uiHit.Window : null;
        if (OnMenu)
        {
            panelPixel = OnVrMenu ? lastMenuPixel : uiHit.Pixel;
            panelDistance = uiHit.Distance;
        }
        else OnMenu = !Grabbing && VrMenuPanel.Raycast(rig, origin, dir, out panelPixel, out panelDistance);
        if (OnMenu)
        {
            // Pointing at the menu: the mouse goes to that pixel, and the world
            // ray points at the sky so nothing in the museum reacts.
            CurrentRay = new Ray(new Vector3(origin.X, origin.Y, origin.Z), Vector3.up);
            mousePixel = lastMenuPixel = panelPixel;
            length = panelDistance;
            hit = true;
        }
        else
        {
            CurrentRay = new Ray(new Vector3(origin.X, origin.Y, origin.Z), new Vector3(dir.X, dir.Y, dir.Z));
            // Off the cinema screen the mouse stays where the laser last left it.
            mousePixel = museum ? ParkingPixel() : lastMenuPixel;
        }

        // In the world, the cursor's position is where the game's own raycast landed (last frame).
        var cursor = CursorManager.Instance;
        if (museum && !OnMenu && cursor != null)
        {
            var w = cursor.WorldPosition;
            var toHit = new NVec(w.x, w.y, w.z) - origin;
            float along = NVec.Dot(toHit, dir);
            if (along > 0 && along < 20f * scale && (toHit - dir * along).Length() < 0.05f * scale)
            {
                length = along;
                hit = true;
            }
        }

        float thickness = 0.003f * scale;
        var mid = origin + dir * (length * 0.5f);
        laser.SetActive(true);
        laser.transform.SetPositionAndRotation(new Vector3(mid.X, mid.Y, mid.Z), aim.UnityRotation);
        laser.transform.localScale = new Vector3(thickness, thickness, length);

        hitMarker.SetActive(hit);
        if (hit)
        {
            var end = origin + dir * length;
            hitMarker.transform.position = new Vector3(end.X, end.Y, end.Z);
            hitMarker.transform.localScale = Vector3.one * (0.012f * scale);
        }

        UpdateGrab(out bool left, out bool right);
        // Clicks on our own menu are handled by it, never seen by the game.
        if (OnVrMenu)
        {
            if (VrInput.Pressed(TpmUi.Click) && uiHit.Kind == VrUiKit.UiHitKind.Menu) VrUiKit.VrUi.Click();
            left = right = false;
        }
        UpdateButtons(vr, hand, rig, museum, holdingWindow);
        if (!museum) grabbing = false;
        VirtualMouse.Send(mousePixel, left, right);
        VirtualKeyboard.Update();
        wasOnMenu = OnMenu;
    }

    public static bool OnMenu { get; private set; }
    public static bool OnVrMenu { get; private set; }
    // The UI window the laser is on (null: the full panel, our menu or the world).
    public static string HitWindow { get; private set; }
    static bool wasOnMenu;
    static Vector2 lastMenuPixel = new(Screen.width * 0.5f, Screen.height * 0.5f);

    // Grab-and-release: the game picks an item up on click-and-hold but then
    // expects a second click to put it down. If a pick-up started during this
    // trigger hold, releasing the trigger finishes the move: a click where the
    // spot is valid, otherwise Escape, which cancels the move and puts the item
    // back. (Right click rotates the item, and calling OnCancelItem directly
    // crashes the game.)
    // A pick-up only counts as a grab if the trigger went down on the world:
    // clicking an item in a build menu on the panel also starts placement mode
    // mid-press, and that must stay the game's normal click-to-place.
    //
    // On release the game may still be validating the spot (reachability checks
    // take a few frames), so the decision waits up to SettleSeconds with the
    // laser frozen where it was released.
    // Synthetic click, counted down per frame: a short gap after the real
    // release, then the button held for a few frames, then released.
    const int ClickGapFrames = 4, ClickHoldFrames = 4;
    const float SettleSeconds = 0.6f;
    static int clickFrames;
    static bool placingAtPress, pressedOnMenu, grabbing, settling;
    static float settleUntil;
    static Ray frozenRay;

    public static bool Grabbing => grabbing || settling;
    // An item is in hand (picked up, or the game's placement mode is on).
    public static bool HoldingItem => Grabbing || CursorManager.Instance?._mode?.TryCast<CursorModePlaceItem>() != null;

    static void UpdateGrab(out bool left, out bool right)
    {
        var placing = CursorManager.Instance?._mode?.TryCast<CursorModePlaceItem>();
        bool clickHeld = VrInput.Held(TpmUi.Click);
        if (VrInput.Pressed(TpmUi.Click))
        {
            placingAtPress = placing != null;
            pressedOnMenu = OnMenu;
        }
        if (clickHeld && !placingAtPress && !pressedOnMenu && placing != null) grabbing = true;

        // Let go (or blocked by the menu chord): put the item down.
        if (!clickHeld && grabbing)
        {
            grabbing = false;
            settling = true;
            settleUntil = Time.unscaledTime + SettleSeconds;
            frozenRay = CurrentRay;
        }
        if (settling)
        {
            CurrentRay = frozenRay;
            bool valid = placing != null && placing.IsItemValidForPlacement();
            bool pending = placing != null && (placing.IsThinking || placing.ItemAwaitingFirstValidation || placing.AreReachabilityChecksInProgress);
            if (placing == null || valid || (!pending && Time.unscaledTime >= settleUntil - SettleSeconds + 0.1f) || Time.unscaledTime >= settleUntil)
            {
                settling = false;
                Plugin.Logger.LogInfo($"[grab] released '{placing?.Item?.Name}': {(valid ? "placing" : "invalid spot, returning it")}");
                if (valid) clickFrames = ClickGapFrames + ClickHoldFrames;
                else if (placing != null) VirtualKeyboard.TapEscape();
            }
        }

        left = clickHeld;
        right = VrInput.Pressed(TpmUi.RightClick);
        if (clickFrames > 0)
        {
            left = clickFrames <= ClickHoldFrames;
            clickFrames--;
        }
    }

    // Controls are rebindable actions (TpmUi.RegisterActions has the defaults;
    // the VR menu's Controls page changes them). What they do:
    //   click             click / hold to pick up / release to place (UpdateGrab)
    //   right click       right click (UpdateGrab)
    //   move table        one hand drags the table; both scale and turn it (TableRig)
    //   grab a window     in Free UI mode, with the laser on a window: move it;
    //                     while held, push / pull / bigger / smaller act on it (VrUi)
    //   placing:          rotate clockwise (Z) / anticlockwise (X), cycle
    //                     placement mode / free placement (Tab)
    //   not placing:      turn the table (and the game camera, Q / E: TpmHost),
    //                     tilt the game camera (R / F, alternating), zoom (Up /
    //                     Down arrows, with the table's zoom)
    //   pause (Space), back (Escape), VR menu, pan (W A S D, relative to where
    //   you're looking). Both grips + both triggers always opens the VR menu.
    // Keys are the game's own bindings, pressed on the virtual keyboard, so the
    // game (and its camera tutorial) sees normal camera input.
    const float StickHoldThreshold = 0.5f;
    const int CameraKeyFrames = 15;
    static bool pitchUpNext = true;

    // The table's turn, zoom and pan in the museum are the core rig's (TableRig, with
    // TpmHost holding the game's pan and turn keys). Here: the game's own actions.
    static void UpdateButtons(VrSession vr, VrHand pointer, TableRig rig, bool museum, bool holdingWindow)
    {
        bool placing = museum && CursorManager.Instance?._mode?.TryCast<CursorModePlaceItem>() != null;
        // While a UI window is held, its own actions use the controls (VrUi.UpdateGrab).
        if (placing && !holdingWindow)
        {
            if (VrInput.Pressed(TpmUi.RotateClockwise)) VirtualKeyboard.Tap(VKey.RotateClockwise);
            if (VrInput.Pressed(TpmUi.RotateAnticlockwise)) VirtualKeyboard.Tap(VKey.RotateAnticlockwise);
            if (VrInput.Pressed(TpmUi.PlacementMode)) VirtualKeyboard.Tap(VKey.CyclePlacementMode);
        }
        else if (pointer.Valid && !holdingWindow)
        {
            if (VrInput.Pressed(TpmUi.Tilt))
            {
                VirtualKeyboard.Hold(pitchUpNext ? VKey.PitchUp : VKey.PitchDown, CameraKeyFrames);
                pitchUpNext = !pitchUpNext;
            }
            // Outside the museum the zoom stick holds the game's zoom keys (the map screens).
            // In the museum the rig zooms the table and TpmHost.Sticks echoes the keys.
            float amount = VrInput.Value(TableActions.ZoomIn) - VrInput.Value(TableActions.ZoomOut);
            if (!museum && (VrInput.Held(TableActions.ZoomIn) || VrInput.Held(TableActions.ZoomOut)))
                VirtualKeyboard.Hold(amount > 0 ? VKey.ZoomIn : VKey.ZoomOut);
        }

        if (museum && VrInput.Pressed(TpmUi.Pause)) VirtualKeyboard.Tap(VKey.Pause);
        if (VrInput.Pressed(TpmUi.Back)) VirtualKeyboard.Tap(VKey.Escape);
        if (VrInput.Pressed(TpmUi.Menu)) VrUiKit.VrUi.ToggleMenu();
        if (!museum) HoldScreenPanKeys();
    }

    // Outside the museum (map screens) the pan stick holds the game's W A S D, the
    // screen being the frame. In the museum the rig pans (TpmHost.Pan).
    static void HoldScreenPanKeys()
    {
        float x = VrInput.Value(TableActions.PanRight) - VrInput.Value(TableActions.PanLeft);
        float y = VrInput.Value(TableActions.PanUp) - VrInput.Value(TableActions.PanDown);
        if (x * x + y * y < StickHoldThreshold * StickHoldThreshold) return;
        float len = MathF.Sqrt(x * x + y * y);
        float forward = y / len, right = x / len;
        // Within ~67 degrees of an axis counts, so diagonals hold two keys.
        const float axis = 0.38f;
        if (forward > axis) VirtualKeyboard.Hold(VKey.PanUp);
        if (forward < -axis) VirtualKeyboard.Hold(VKey.PanDown);
        if (right > axis) VirtualKeyboard.Hold(VKey.PanRight);
        if (right < -axis) VirtualKeyboard.Hold(VKey.PanLeft);
    }

    // In the world the mouse position only matters for the game's "is the mouse
    // over UI" test (the ray itself is replaced), so park it on a pixel with no
    // UI under it. Walk through candidates while the game reports UI there.
    static readonly Vector2[] ParkingCandidates =
    {
        new(0.5f, 0.5f), new(0.5f, 0.6f), new(0.5f, 0.4f), new(0.4f, 0.5f), new(0.6f, 0.5f),
        new(0.35f, 0.65f), new(0.65f, 0.65f), new(0.35f, 0.35f), new(0.65f, 0.35f),
    };
    static int parkingIndex;
    static int parkedFrames;

    static Vector2 ParkingPixel()
    {
        if (wasOnMenu) parkedFrames = 0;
        // The game updates IsMouseOverGui a frame after the mouse moves.
        if (++parkedFrames > 3 && App.Instance?.InputManager?.IsMouseOverGui == true)
        {
            parkingIndex = (parkingIndex + 1) % ParkingCandidates.Length;
            parkedFrames = 0;
        }
        var c = ParkingCandidates[parkingIndex];
        return new Vector2(c.x * Screen.width, c.y * Screen.height);
    }

    static void Build()
    {
        built = true;
        int layer = VrCore.VisualLayer;
        hands = new[] { new VrHandVisuals("TpmVr Hand L", layer), new VrHandVisuals("TpmVr Hand R", layer) };
        // Hands, laser and hit marker come from the core: drawn after the UI panels
        // and the VR menu (VrDrawOrder.Pointer), so they show on them.
        laser = VrVisuals.Primitive(PrimitiveType.Cube, "TpmVr Laser", new Color(0.3f, 0.9f, 1f), layer);
        hitMarker = VrVisuals.Primitive(PrimitiveType.Sphere, "TpmVr Hit", new Color(1f, 0.85f, 0.2f), layer);
        VirtualMouse.Init();
        VirtualKeyboard.Init();
    }

    static void SetVisible(bool on)
    {
        if (!built) return;
        laser.SetActive(on);
        hitMarker.SetActive(on);
        foreach (var h in hands) h.Root.SetActive(on);
    }
}

sealed class VrHandVisuals
{
    public readonly GameObject Root;

    public VrHandVisuals(string name, int layer)
    {
        Root = VrVisuals.Primitive(PrimitiveType.Sphere, name, new Color(0.9f, 0.9f, 0.95f), layer);
    }

    public void Place(VrHand hand, TableRig rig)
    {
        Root.SetActive(hand.Valid);
        if (!hand.Valid) return;
        var p = rig.TrackingToWorld(hand.Pose);
        Root.transform.SetPositionAndRotation(p.UnityPosition, p.UnityRotation);
        float size = (hand.GripHeld ? 0.05f : 0.035f) * rig.WorldScale;
        Root.transform.localScale = Vector3.one * size;
    }
}

static class Primitives
{
    static Shader shader;

    // Unlit, textured, always drawn on top (the diorama's surrounding terrain
    // would otherwise hide parts of it). Transparent: alpha-blended, for the
    // menu panel. Opaque: for the cinema screen, drawn just before the panel.
    // Uses URP Unlit's surface settings (the values its material editor writes).
    public static Material MakeTextured(Texture texture, bool transparent)
    {
        // Our own overlay shader really is drawn over everything; URP Unlit
        // ignores _ZTest, so scenery right next to the head could cover a panel.
        var overlay = ModShaders.Get("Overlay");
        if (overlay != null)
        {
            var m = new Material(overlay) { mainTexture = texture, color = Color.white };
            m.SetFloat("_SrcBlend", (float)(transparent ? UnityEngine.Rendering.BlendMode.SrcAlpha : UnityEngine.Rendering.BlendMode.One));
            m.SetFloat("_DstBlend", (float)(transparent ? UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha : UnityEngine.Rendering.BlendMode.Zero));
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay - (transparent ? 0 : 1);
            return m;
        }
        shader ??= Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
        var mat = new Material(shader) { mainTexture = texture, color = Color.white };
        mat.SetTexture("_BaseMap", texture);
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
        mat.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always);
        mat.SetFloat("_ZWrite", 0);
        if (transparent)
        {
            mat.SetFloat("_Surface", 1);
            mat.SetFloat("_Blend", 0);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay;
        }
        else
        {
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Overlay - 1;
        }
        return mat;
    }
}

// A second mouse device whose state we write directly with non-generic
// Input System calls (generic ones may not exist in the IL2CPP build).
static unsafe class VirtualMouse
{
    // InputEvent header (20 bytes) + StateEvent.stateFormat (4 bytes).
    const int StateDataOffset = 24;

    static InputDevice device;
    static uint positionOffset, deltaOffset, buttonsByte;
    static uint leftBit, rightBit;
    static bool jiggle;
    static Vector2 lastPosition;

    public static void Init()
    {
        device = InputSystem.AddDevice("Mouse", "TpmVrMouse", null);
        var mouse = device.Cast<Mouse>();
        uint deviceBase = device.stateBlock.byteOffset;
        positionOffset = mouse.position.stateBlock.byteOffset - deviceBase;
        deltaOffset = mouse.delta.stateBlock.byteOffset - deviceBase;
        var left = mouse.leftButton.stateBlock;
        var right = mouse.rightButton.stateBlock;
        buttonsByte = left.byteOffset - deviceBase;
        leftBit = left.bitOffset;
        rightBit = right.bitOffset + (right.byteOffset - left.byteOffset) * 8;
        Plugin.Logger.LogInfo($"Virtual mouse added: position@{positionOffset}, buttons@{buttonsByte} bits L{leftBit} R{rightBit}");
    }

    public static void Send(Vector2 position, bool left, bool rightClick)
    {
        if (device == null) return;
        bool right = rightClick;
        // The game only re-tests what UI is under the mouse when the mouse moves,
        // so a perfectly still virtual mouse leaves a stale "over UI" result.
        // Jiggle by half a pixel every frame, like a hand on a real mouse, and
        // report the matching delta (movement detection reads delta, not position).
        jiggle = !jiggle;
        position.x += jiggle ? 0.5f : -0.5f;
        var delta = position - lastPosition;
        lastPosition = position;
        // A right "click" is press this frame, release next frame.

        StateEvent.From(device, out var eventPtr, Allocator.Temp);
        byte* state = (byte*)eventPtr.data + StateDataOffset;
        *(float*)(state + positionOffset) = position.x;
        *(float*)(state + positionOffset + 4) = position.y;
        *(float*)(state + deltaOffset) = delta.x;
        *(float*)(state + deltaOffset + 4) = delta.y;
        var buttons = (ushort*)(state + buttonsByte);
        *buttons = Set(*buttons, leftBit, left);
        *buttons = Set(*buttons, rightBit, right);
        InputSystem.QueueEvent(eventPtr);
    }

    static ushort Set(ushort value, uint bit, bool on) => (ushort)(on ? value | (1 << (int)bit) : value & ~(1 << (int)bit));
}

// A second keyboard device for the few keys VR needs, all the game's own
// bindings: Escape (back / cancel a move), Z / X (rotate the held item),
// Space (pause / play), Tab (cycle placement mode), W A S D (pan) and Up /
// Down arrows (zoom) on map screens. Same non-generic
// state-event approach as VirtualMouse.
enum VKey { Escape, RotateClockwise, RotateAnticlockwise, Pause, CyclePlacementMode, PanUp, PanLeft, PanDown, PanRight, ZoomIn, ZoomOut, TurnCameraLeft, TurnCameraRight, PitchUp, PitchDown }

static unsafe class VirtualKeyboard
{
    const int StateDataOffset = 24;
    const int TapFrames = 2;
    static readonly int KeyCount = Enum.GetValues(typeof(VKey)).Length;

    static InputDevice device;
    static readonly (uint byteIndex, uint bit)[] offsets = new (uint, uint)[KeyCount];
    static readonly int[] framesLeft = new int[KeyCount];   // >0: held for this many more frames
    static readonly bool[] sentDown = new bool[KeyCount];

    public static void Init()
    {
        device = InputSystem.AddDevice("Keyboard", "TpmVrKeyboard", null);
        var keyboard = device.Cast<Keyboard>();
        offsets[(int)VKey.Escape] = Offset(keyboard.escapeKey);
        offsets[(int)VKey.RotateClockwise] = Offset(keyboard.zKey);
        offsets[(int)VKey.RotateAnticlockwise] = Offset(keyboard.xKey);
        offsets[(int)VKey.Pause] = Offset(keyboard.spaceKey);
        offsets[(int)VKey.CyclePlacementMode] = Offset(keyboard.tabKey);
        offsets[(int)VKey.PanUp] = Offset(keyboard.wKey);
        offsets[(int)VKey.PanLeft] = Offset(keyboard.aKey);
        offsets[(int)VKey.PanDown] = Offset(keyboard.sKey);
        offsets[(int)VKey.PanRight] = Offset(keyboard.dKey);
        offsets[(int)VKey.ZoomIn] = Offset(keyboard.upArrowKey);
        offsets[(int)VKey.ZoomOut] = Offset(keyboard.downArrowKey);
        offsets[(int)VKey.TurnCameraLeft] = Offset(keyboard.qKey);
        offsets[(int)VKey.TurnCameraRight] = Offset(keyboard.eKey);
        offsets[(int)VKey.PitchUp] = Offset(keyboard.rKey);
        offsets[(int)VKey.PitchDown] = Offset(keyboard.fKey);
        Plugin.Logger.LogInfo($"Virtual keyboard added: {string.Join(", ", Enum.GetNames(typeof(VKey)))} at {string.Join(", ", offsets)}");
    }

    // Key bits are packed from the start of the state; bitOffset can exceed 7.
    static (uint, uint) Offset(KeyControl key)
    {
        var block = key.stateBlock;
        return (block.byteOffset - device.stateBlock.byteOffset + block.bitOffset / 8, block.bitOffset % 8);
    }

    public static void Tap(VKey key) => framesLeft[(int)key] = TapFrames;
    public static void TapEscape() => Tap(VKey.Escape);

    // Keep a key down while this is called every frame; it releases a frame after the calls stop.
    public static void Hold(VKey key, int frames = TapFrames) => framesLeft[(int)key] = Math.Max(framesLeft[(int)key], frames);

    public static void Update()
    {
        if (device == null) return;
        bool changed = false;
        for (int i = 0; i < framesLeft.Length; i++)
        {
            bool down = framesLeft[i] > 0;
            if (framesLeft[i] > 0) framesLeft[i]--;
            if (down != sentDown[i]) changed = true;
            sentDown[i] = down;
        }
        if (!changed) return;

        StateEvent.From(device, out var eventPtr, Allocator.Temp);
        byte* state = (byte*)eventPtr.data + StateDataOffset;
        for (int i = 0; i < offsets.Length; i++)
        {
            var (index, bit) = offsets[i];
            state[index] = (byte)(sentDown[i] ? state[index] | (1 << (int)bit) : state[index] & ~(1 << (int)bit));
        }
        InputSystem.QueueEvent(eventPtr);
    }
}
