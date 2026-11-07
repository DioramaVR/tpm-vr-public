using System.Runtime.InteropServices;
using UnityEngine;
using Valve.VR;

namespace TpmVr;

// Frame-rate and SteamVR frame-drop logging. Judder and "swimming" in the
// headset almost always mean frames arrive late and the compositor reprojects
// old ones, so this is the first thing to check when the view looks wrong.
static class PerfLog
{
    const float Interval = 5f;
    static float windowStart = -1, worstFrame;
    static int frames;
    static Compositor_CumulativeStats lastStats;
    static bool haveStats;

    public static string Label = "";

    public static void Frame(VrSession vr)
    {
        float dt = Time.unscaledDeltaTime;
        if (windowStart < 0) windowStart = Time.unscaledTime;
        frames++;
        if (dt > worstFrame) worstFrame = dt;

        float elapsed = Time.unscaledTime - windowStart;
        if (elapsed < Interval) return;

        string vrStats = "";
        if (vr != null && !vr.Simulated && vr.Compositor != null)
        {
            var stats = new Compositor_CumulativeStats();
            vr.Compositor.GetCumulativeStats(ref stats, (uint)Marshal.SizeOf<Compositor_CumulativeStats>());
            if (haveStats)
                vrStats = $" | SteamVR presents {stats.m_nNumFramePresents - lastStats.m_nNumFramePresents}, dropped {stats.m_nNumDroppedFrames - lastStats.m_nNumDroppedFrames}, reprojected {stats.m_nNumReprojectedFrames - lastStats.m_nNumReprojectedFrames}";
            lastStats = stats;
            haveStats = true;
        }
        Plugin.Logger.LogInfo($"[perf]{(Label.Length > 0 ? " " + Label + ":" : "")} {frames / elapsed:F1} fps, worst frame {worstFrame * 1000:F0} ms, render-thread callbacks {VrSession.RenderThreadCallbacks}, WaitGetPoses avg/worst {VrSession.WaitTimer.TakeSummary()}, submit {VrSession.SubmitTimer.TakeSummary()}{vrStats}");
        VrSession.RenderThreadCallbacks = 0;
        windowStart = Time.unscaledTime;
        frames = 0;
        worstFrame = 0;
    }

    // Start a fresh measurement window (used by the test script between phases).
    public static void Reset()
    {
        windowStart = Time.unscaledTime;
        frames = 0;
        worstFrame = 0;
    }
}
