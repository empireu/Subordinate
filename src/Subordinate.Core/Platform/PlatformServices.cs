namespace Subordinate.Core.Platform;

public static class PlatformServices
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    public static IPlatform Current { get; } = Select();

    private static IPlatform Select()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsPlatform.Instance;
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxPlatform.Instance;
        }

        return UnsupportedPlatform.Instance;
    }
}
