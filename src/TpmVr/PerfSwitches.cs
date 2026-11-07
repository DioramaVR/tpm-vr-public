namespace TpmVr;

// Runtime overrides used by the test script to measure what each render costs.
// (The eyes, their shadows and the desktop view are the core rig's settings now.)
static class PerfSwitches
{
    public static bool UiCameraOff;
    public static int? MenuRefreshOverride;
}
