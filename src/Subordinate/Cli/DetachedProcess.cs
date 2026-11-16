using Subordinate.Core;

namespace Subordinate.Cli;

internal static class DetachedProcess
{
    public static IDetachedProcessLauncher Current { get; } = Select();

    private static IDetachedProcessLauncher Select()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsDetachedProcessLauncher.Instance;
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxDetachedProcessLauncher.Instance;
        }

        return UnsupportedDetachedProcessLauncher.Instance;
    }
}

internal sealed class UnsupportedDetachedProcessLauncher : IDetachedProcessLauncher
{
    public static readonly UnsupportedDetachedProcessLauncher Instance = new();

    private UnsupportedDetachedProcessLauncher()
    {
    }

    public int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        throw CreateException();
    }

    public void DetachCurrentProcess()
    {
        throw CreateException();
    }

    private static SubordinateException CreateException()
    {
        return new SubordinateException(ErrorCode.InternalError, "The background worker is not supported on this operating system.");
    }
}
