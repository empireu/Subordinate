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
        return kind switch
        {
            ShellKind.Bash => LocateBash(configured.Bash),
            ShellKind.WindowsPowerShell => throw new SubordinateException(ErrorCode.ShellUnavailable, "Windows PowerShell 5.1 is only available on Windows. Use --shell bash or --shell pwsh."),
            ShellKind.PowerShell7 => LocatePwsh(configured.Pwsh),
            _ => throw new SubordinateException(ErrorCode.ShellUnavailable, $"Unsupported shell {kind}.")
        };
    }

    public string? ProbeVersion(ShellKind kind, string executablePath)
    {
        return kind switch
        {
            ShellKind.Bash => ProbeBash(executablePath),
            ShellKind.PowerShell7 => ProbePowerShell(executablePath),
            _ => null
        };
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

    private static ShellInfo LocateBash(string? configured)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }
        else
        {
            candidates.Add("/usr/bin/bash");
            candidates.Add("/bin/bash");

            var onPath = ShellProbe.FindOnPath("bash");
            if (onPath != null)
            {
                candidates.Add(onPath);
            }
        }

        var rejected = new List<string>();

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = ProbeBash(candidate);

            if (version == null)
            {
                rejected.Add($"{candidate} (not a Linux bash)");
                continue;
            }

            if (!version.StartsWith("5.2.", StringComparison.Ordinal) && !version.StartsWith("5.3.", StringComparison.Ordinal))
            {
                rejected.Add($"{candidate} (bash {version}; the parser supports 5.2 and 5.3)");
                continue;
            }

            return new ShellInfo(ShellKind.Bash, candidate, version);
        }

        var detail = rejected.Count > 0 ? " Rejected: " + string.Join("; ", rejected) + "." : string.Empty;
        throw new SubordinateException(ErrorCode.ShellUnavailable, "bash 5.2 or 5.3 was not found. Set shells.bash in the configuration to the path of a bash 5.2 or 5.3 executable." + detail);
    }

    private static ShellInfo LocatePwsh(string? configured)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }
        else
        {
            var onPath = ShellProbe.FindOnPath("pwsh");
            if (onPath != null)
            {
                candidates.Add(onPath);
            }

            candidates.Add("/opt/microsoft/powershell/7/pwsh");
            candidates.Add("/usr/bin/pwsh");
        }

        var rejected = new List<string>();

        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = ProbePowerShell(candidate);
            var match = version == null ? null : ShellProbe.DottedVersion().Match(version);

            if (match == null || !match.Success)
            {
                rejected.Add($"{candidate} (could not determine the version)");
                continue;
            }

            var major = int.Parse(match.Groups[1].Value);
            var minor = int.Parse(match.Groups[2].Value);
            var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
            var supported = major == 7 && minor == 6 && patch >= 4;

            if (!supported)
            {
                rejected.Add($"{candidate} (PowerShell {version}; the parser supports 7.6.4 up to, but not including, 7.7)");
                continue;
            }

            return new ShellInfo(ShellKind.PowerShell7, candidate, version!);
        }

        var detail = rejected.Count > 0 ? " Rejected: " + string.Join("; ", rejected) + "." : string.Empty;
        throw new SubordinateException(ErrorCode.ShellUnavailable, "pwsh 7.6.4 up to, but not including, 7.7 was not found. Set shells.pwsh in the configuration to the path of a supported pwsh executable." + detail);
    }

    private static string? ProbeBash(string path)
    {
        var output = ShellProbe.RunProbe(path, ["--norc", "--noprofile", "--version"]);

        if (output == null || !output.Contains("-linux", StringComparison.Ordinal))
        {
            return null;
        }

        var match = ShellProbe.BashVersion().Match(output);
        return match.Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}" : null;
    }

    private static string? ProbePowerShell(string path)
    {
        var output = ShellProbe.RunProbe(path, ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"]);
        return output?.Trim();
    }
}
