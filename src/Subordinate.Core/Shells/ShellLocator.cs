using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;

namespace Subordinate.Core.Shells;

/// <summary>
///     A located and version-checked shell executable.
/// </summary>
public sealed record ShellInfo(ShellKind Kind, string ExecutablePath, string Version);

/// <summary>
///     Finds the executable for a shell and checks that its version matches the parser dialect. Never falls back to another shell.
/// </summary>
public static partial class ShellLocator
{
    [GeneratedRegex(@"version (\d+)\.(\d+)\.(\d+)")]
    private static partial Regex BashVersion();

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex DottedVersion();

    public static ShellInfo Locate(ShellKind kind, ShellPathsEntry configured)
    {
        return kind switch
        {
            ShellKind.Bash => LocateBash(configured.Bash),
            ShellKind.WindowsPowerShell => LocateWindowsPowerShell(configured.Powershell),
            ShellKind.PowerShell7 => LocatePwsh(configured.Pwsh),
            _ => throw new SubordinateException(ErrorCode.ShellUnavailable, $"Unsupported shell {kind}.")
        };
    }

    /// <summary>
    ///     Checks that a shell recorded in a session is still present with the same version.
    /// </summary>
    public static void VerifyUnchanged(ShellInfo recorded)
    {
        if (!File.Exists(recorded.ExecutablePath))
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, $"The {recorded.Kind.ToToken()} executable recorded for this session no longer exists at {recorded.ExecutablePath}.");
        }

        var current = recorded.Kind switch
        {
            ShellKind.Bash => ProbeBash(recorded.ExecutablePath),
            _ => ProbePowerShell(recorded.ExecutablePath)
        };

        if (current == null || !string.Equals(current, recorded.Version, StringComparison.Ordinal))
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, $"The {recorded.Kind.ToToken()} executable at {recorded.ExecutablePath} changed version from {recorded.Version} to {current ?? "unknown"}. Start a new session.");
        }
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

            var git = FindOnPath("git.exe");
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
        var path = !string.IsNullOrWhiteSpace(configured) ? configured : FindOnPath("pwsh.exe");

        if (path == null || !File.Exists(path))
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, "pwsh.exe was not found. Set shells.pwsh in the configuration.");
        }

        var version = ProbePowerShell(path);
        var match = version == null ? null : DottedVersion().Match(version);

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
        var output = RunProbe(path, ["--norc", "--noprofile", "--version"]);

        if (output == null || !output.Contains("-pc-msys", StringComparison.Ordinal) && !output.Contains("-pc-cygwin", StringComparison.Ordinal))
        {
            return null;
        }

        var match = BashVersion().Match(output);
        return match.Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}" : null;
    }

    private static string? ProbePowerShell(string path)
    {
        var output = RunProbe(path, ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.ToString()"]);
        return output?.Trim();
    }

    private static string? RunProbe(string path, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);

            if (process == null)
            {
                return null;
            }

            process.StandardInput.Close();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                process.Kill(true);
                return null;
            }

            Task.WaitAll(outputTask, errorTask);
            return process.ExitCode == 0 ? outputTask.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
