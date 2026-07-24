namespace DawndNet.Shared;

/// <summary>
/// Config flag bits in the DAWnd_Init parameter word.
/// </summary>
internal static class ConfigFlags
{
    public const uint Marker = 0x8000_0000;
    public const uint Borderless = 0x1;
    public const uint KeepIntro = 0x2;
    public const uint LockAspect = 0x4;
    public const uint CursorFix = 0x8;
    public const uint Rain = 0x10;

    // Window scale (integer multiple of the 640x480 render size)
    // stored as a small field rather than a bit so it can grow past the currently clamped 1-2 range.
    public const int ScaleShift = 8;
    public const uint ScaleMask = 0xF00;

    public const string SettingsFile = "DawndNet.ini";
}
