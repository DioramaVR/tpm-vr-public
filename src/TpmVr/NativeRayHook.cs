using System;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime;

namespace TpmVr;

// IL2CPP-compiled game code calls Unity's native Camera::ScreenPointToRay
// directly, bypassing the managed wrappers Harmony can patch. So we detour the
// native icall in UnityPlayer.dll (not Denuvo-protected): for the game's main
// camera, the returned ray becomes the VR laser.
static unsafe class NativeRayHook
{
    // void ScreenPointToRay_Injected(IntPtr self, Vector2* pos, int eye, Ray* ret)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void RayFn(IntPtr self, IntPtr pos, int eye, IntPtr ret);

    static RayFn screenOriginal, viewportOriginal;
    // Detour targets and the detours themselves must stay referenced.
    static RayFn screenHook, viewportHook;
    static INativeDetour screenDetour, viewportDetour;

    public static int Calls, Overrides;

    public static void Install()
    {
        screenHook = OnScreenPointToRay;
        viewportHook = OnViewportPointToRay;
        screenDetour = Hook("UnityEngine.Camera::ScreenPointToRay_Injected", screenHook, out screenOriginal);
        viewportDetour = Hook("UnityEngine.Camera::ViewportPointToRay_Injected", viewportHook, out viewportOriginal);
    }

    static INativeDetour Hook(string icall, RayFn hook, out RayFn original)
    {
        original = null;
        var fn = IL2CPP.il2cpp_resolve_icall(icall);
        if (fn == IntPtr.Zero)
        {
            Plugin.Logger.LogError($"icall not found: {icall}");
            return null;
        }
        var detour = INativeDetour.CreateAndApply(fn, hook, out original);
        Plugin.Logger.LogInfo($"Hooked {icall} at 0x{fn.ToInt64():X}");
        return detour;
    }

    static void OnScreenPointToRay(IntPtr self, IntPtr pos, int eye, IntPtr ret)
    {
        screenOriginal(self, pos, eye, ret);
        Replace(self, ret);
    }

    static void OnViewportPointToRay(IntPtr self, IntPtr pos, int eye, IntPtr ret)
    {
        viewportOriginal(self, pos, eye, ret);
        Replace(self, ret);
    }

    static void Replace(IntPtr self, IntPtr ret)
    {
        Calls++;
        try
        {
            if (!VrPointer.RayActive || self != VrPointer.MainCameraPtr) return;
            var ray = VrPointer.CurrentRay;
            var o = ray.origin;
            var d = ray.direction;
            var f = (float*)ret;
            f[0] = o.x; f[1] = o.y; f[2] = o.z;
            f[3] = d.x; f[4] = d.y; f[5] = d.z;
            Overrides++;
        }
        catch (Exception e)
        {
            // Never let an exception unwind into native Unity code.
            Plugin.Logger.LogError($"Ray hook: {e.Message}");
        }
    }
}
