using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;
using Subordinate.Core.Shells;

namespace Subordinate.Core.Platform;

public sealed class LinuxShellEnvironment : IShellEnvironment
{
    public static readonly LinuxShellEnvironment Instance = new();

    private LinuxShellEnvironment()
    {
    }

    public IReadOnlyList<ShellKind> SupportedShells { get; } = [ShellKind.Bash, ShellKind.PowerShell7];

    public ShellInfo Locate(ShellKind kind, ShellPathsEntry configured)
    {
        throw new SubordinateException(ErrorCode.ShellUnavailable, "Shell discovery on Linux is not implemented yet.");
    }

    public string? ProbeVersion(ShellKind kind, string executablePath)
    {
        throw new SubordinateException(ErrorCode.ShellUnavailable, "Shell discovery on Linux is not implemented yet.");
    }

    public string ToolDescription(ShellKind kind)
    {
        return kind switch
        {
            ShellKind.Bash => "bash on Linux. Use POSIX syntax.",
            ShellKind.PowerShell7 => "PowerShell 7.6 on Linux. Use PowerShell syntax and forward slashes in paths.",
            _ => "Windows PowerShell 5.1. Use PowerShell syntax."
        };
    }

    public string PromptDescription(ShellKind kind)
    {
        return kind switch
        {
            ShellKind.Bash => "bash (5.2 or 5.3) on Linux. Write POSIX shell syntax.",
            ShellKind.PowerShell7 => "PowerShell 7.6 on Linux. Write PowerShell syntax and use forward slashes in paths.",
            _ => "Windows PowerShell 5.1. Write PowerShell syntax."
        };
    }
}
