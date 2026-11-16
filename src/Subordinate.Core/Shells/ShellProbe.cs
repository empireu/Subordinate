using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Subordinate.Core.Shells;

internal static partial class ShellProbe
{
    [GeneratedRegex(@"version (\d+)\.(\d+)\.(\d+)")]
    public static partial Regex BashVersion();

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?")]
    public static partial Regex DottedVersion();

    public static string? RunProbe(string path, IReadOnlyList<string> arguments)
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

    public static string? FindOnPath(string fileName)
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
