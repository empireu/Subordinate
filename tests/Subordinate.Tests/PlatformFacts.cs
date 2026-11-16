using Xunit;

namespace Subordinate.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Runs on Windows only.";
        }
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Runs on Windows only.";
        }
    }
}

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Runs on Linux only.";
        }
    }
}

public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Runs on Linux only.";
        }
    }
}

internal static class TestPaths
{
    public static string Workdir => OperatingSystem.IsWindows() ? @"D:\work\repo" : "/work/repo";
}
