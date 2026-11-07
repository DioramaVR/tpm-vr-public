using System;
using HarmonyLib;
using UnityEngine.EventSystems;
using VrUiKit;
using RaycastList = Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>;

namespace TpmVr;

// Popout windows (Arranged / Free): windows that overlap on the game's screen are apart in VR,
// but the laser drives one mouse pixel, so the game's UI raycast at that pixel
// can hit another window's UI (or a pop-up) that isn't under the laser in VR.
// While the laser is on a window, drop hits that belong to any other window.
// The pop-ups window (Remainder) owns nothing, so it keeps whatever is left.
// Other modes are untouched.
[HarmonyPatch(typeof(EventSystem), nameof(EventSystem.RaycastAll))]
static class PopoutClicks
{
    public static int Dropped;

    static void Postfix(RaycastList raycastResults)
    {
        try
        {
            var window = VrPointer.HitWindow;
            if (window == null || raycastResults == null || VrUi.Windows != VrUi.WindowDraw.Popout || VrUi.Mode is not (VrUi.UiMode.Arranged or VrUi.UiMode.Free)) return;
            for (int i = raycastResults.Count - 1; i >= 0; i--)
                if (OwnedByOther(window, raycastResults[i].gameObject))
                {
                    raycastResults.RemoveAt(i);
                    Dropped++;
                }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[ui] separate click filter: {e.Message}");
        }
    }

    static bool OwnedByOther(string window, UnityEngine.GameObject go)
    {
        if (go == null) return false;
        foreach (var id in TpmUi.WindowIds)
            if (id != window && VrUi.Owns(id, go)) return true;
        return false;
    }
}
