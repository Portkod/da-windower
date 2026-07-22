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

    public const string SettingsFile = "DawndNet.ini";
}
