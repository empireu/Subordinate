using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;
using Subordinate.Core.Shells;

namespace Subordinate.Core.Platform;

public sealed class WindowsShellEnvironment : IShellEnvironment
{
    public static readonly WindowsShellEnvironment Instance = new();

    private WindowsShellEnvironment()
    {
    }

    public IReadOnlyList<ShellKind> SupportedShells { get; } = [ShellKind.Bash, ShellKind.WindowsPowerShell, ShellKind.PowerShell7];

    public ShellInfo Locate(ShellKind kind, ShellPathsEntry configured)
    {
        return kind switch
        {
            ShellKind.Bash => LocateBash(configured.Bash),
            ShellKind.WindowsPowerShell => LocateWindowsPowerShell(configured.Powershell),
            ShellKind.PowerShell7 => LocatePwsh(configured.Pwsh),
            _ => throw new SubordinateException(ErrorCode.ShellUnavailable, $"Unsupported shell {kind}.")
        };
    }

    public string? ProbeVersion(ShellKind kind, string executablePath)
    {
        return kind switch
        {
            ShellKind.Bash => ProbeBash(executablePath),
            _ => ProbePowerShell(executablePath)
        };
    }

    public string ToolDescription(ShellKind kind)
    {
        return kind switch
        {
            ShellKind.Bash => "Git Bash on Windows. Use POSIX syntax; Windows drives are /c/..., /d/... and so on.",
            ShellKind.WindowsPowerShell => "Windows PowerShell 5.1. Use PowerShell syntax.",
            _ => "PowerShell 7.6. Use PowerShell syntax."
        };
    }

    public string PromptDescription(ShellKind kind)
    {
        return kind switch
        {
            ShellKind.Bash => "Git Bash (bash 5.2 on Windows). Write POSIX shell syntax. Windows drives appear as /c/, /d/ and so on.",
            ShellKind.WindowsPowerShell => "Windows PowerShell 5.1. Write PowerShell syntax.",
            _ => "PowerShell 7.6. Write PowerShell syntax."
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
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (!string.IsNullOrEmpty(root))
                {
                    candidates.Add(Path.Combine(root, "Git", "bin", "bash.exe"));
                }
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(localAppData, "Programs", "Git", "bin", "bash.exe"));

            var git = ShellProbe.FindOnPath("git.exe");
            if (git != null)
            {
                var gitRoot = Path.GetDirectoryName(Path.GetDirectoryName(git));

                if (gitRoot != null)
                {
                    candidates.Add(Path.Combine(gitRoot, "bin", "bash.exe"));
                }
            }
        }

        var rejected = new List<string>();

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            var version = ProbeBash(candidate);

            if (version == null)
            {
                rejected.Add($"{candidate} (not a Git Bash or MSYS2 bash)");
                continue;
            }

            if (!version.StartsWith("5.2.", StringComparison.Ordinal) && !version.StartsWith("5.3.", StringComparison.Ordinal))
            {
                rejected.Add($"{candidate} (bash {version}; the parser supports 5.2 and 5.3)");
                continue;
            }

            return new ShellInfo(ShellKind.Bash, Path.GetFullPath(candidate), version);
        }

        var detail = rejected.Count > 0 ? " Rejected: " + string.Join("; ", rejected) + "." : string.Empty;
        throw new SubordinateException(ErrorCode.ShellUnavailable, "Git Bash 5.2 or 5.3 was not found. Set shells.bash in the configuration to the path of Git's bin\\bash.exe. The bash.exe in System32 is WSL and is never used." + detail);
    }

    private static ShellInfo LocateWindowsPowerShell(string? configured)
    {
        var path = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

        if (!File.Exists(path))
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, $"Windows PowerShell was not found at {path}.");
        }

        var version = ProbePowerShell(path);

        if (version == null || !version.StartsWith("5.1", StringComparison.Ordinal))
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, $"{path} reports version {version ?? "unknown"}; the powershell shell requires Windows PowerShell 5.1.");
        }

        return new ShellInfo(ShellKind.WindowsPowerShell, Path.GetFullPath(path), version);
    }

    private static ShellInfo LocatePwsh(string? configured)
    {
        var path = !string.IsNullOrWhiteSpace(configured) ? configured : ShellProbe.FindOnPath("pwsh.exe");

        if (path == null || !File.Exists(path))
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, "pwsh.exe was not found. Set shells.pwsh in the configuration.");
        }

        var version = ProbePowerShell(path);
        var match = version == null ? null : ShellProbe.DottedVersion().Match(version);

        if (match == null || !match.Success)
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, $"Could not determine the version of {path}.");
        }

        var major = int.Parse(match.Groups[1].Value);
        var minor = int.Parse(match.Groups[2].Value);
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        var supported = major == 7 && minor == 6 && patch >= 4;

        if (!supported)
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, $"{path} is PowerShell {version}. The PowerShell 7 parser dialect supports 7.6.4 up to, but not including, 7.7. Use --shell powershell (Windows PowerShell 5.1) or --shell bash instead.");
        }

        return new ShellInfo(ShellKind.PowerShell7, Path.GetFullPath(path), version!);
    }

    private static string? ProbeBash(string path)
    {
        var output = ShellProbe.RunProbe(path, ["--norc", "--noprofile", "--version"]);

        if (output == null || !output.Contains("-pc-msys", StringComparison.Ordinal) && !output.Contains("-pc-cygwin", StringComparison.Ordinal))
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
