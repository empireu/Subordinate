using System.Diagnostics;
using System.Text;
using Subordinate.Core.Classification;

namespace Subordinate.Core.Shells;

public sealed class CommandExecutionRequest
{
    public required string Command { get; init; }

    public required string WorkingDirectory { get; init; }

    public required TimeSpan Timeout { get; init; }

    /// <summary>
    ///     The directory and file name stem used when output is too large to return inline.
    /// </summary>
    public required string OutputDirectory { get; init; }

    public required string OutputStem { get; init; }
}

public sealed class CommandExecutionResult
{
    public int? ExitCode { get; init; }

    public bool TimedOut { get; init; }

    public bool Cancelled { get; init; }

    public string? StartError { get; init; }

    public TimeSpan Duration { get; init; }

    public CapturedOutput? StandardOutput { get; init; }

    public CapturedOutput? StandardError { get; init; }
}

/// <summary>
///     Runs a command string. Implementations must execute it under exactly the shell its classification was parsed for.
/// </summary>
public interface ICommandExecutor
{
    ShellKind Shell { get; }

    Task<CommandExecutionResult> ExecuteAsync(CommandExecutionRequest request, CancellationToken cancellationToken);
}

/// <summary>
///     Executes commands in a fresh, non-interactive, profile-free shell process with a scrubbed environment and closed standard input.
/// </summary>
public sealed class ShellCommandExecutor(ShellInfo shell, IReadOnlyCollection<string> extraSecretVariables) : ICommandExecutor
{
    private const int StandardOutputHead = 8000;
    private const int StandardOutputTail = 4000;
    private const int StandardErrorHead = 3000;
    private const int StandardErrorTail = 3000;

    private static readonly string[] SecretMarkers = ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "COOKIE", "PRIVATE", "_AUTH", "AUTH_"];

    private static readonly string[] BashStartupVariables = ["BASH_ENV", "ENV", "SHELLOPTS", "BASHOPTS", "CDPATH", "GLOBIGNORE", "POSIXLY_CORRECT", "BASH_COMPAT"];

    public ShellKind Shell => shell.Kind;

    public async Task<CommandExecutionResult> ExecuteAsync(CommandExecutionRequest request, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(request);
        var stopwatch = Stopwatch.StartNew();
        Process? process;

        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new CommandExecutionResult { StartError = $"The shell could not be started: {ex.Message}" };
        }

        if (process == null)
        {
            return new CommandExecutionResult { StartError = "The shell could not be started." };
        }

        using (process)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }

            using var standardOutput = new BoundedCapture(StandardOutputHead, StandardOutputTail, Path.Combine(request.OutputDirectory, request.OutputStem + ".stdout.txt"));
            using var standardError = new BoundedCapture(StandardErrorHead, StandardErrorTail, Path.Combine(request.OutputDirectory, request.OutputStem + ".stderr.txt"));
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(request.Timeout);

            var outputTask = standardOutput.ReadToEndAsync(process.StandardOutput, CancellationToken.None);
            var errorTask = standardError.ReadToEndAsync(process.StandardError, CancellationToken.None);
            var timedOut = false;
            var cancelled = false;

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
                timedOut = !cancelled;

                try
                {
                    process.Kill(true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                // We don't pass the token, so the caller has to wait for the process to exit:
                // ReSharper disable once MethodSupportsCancellation
                await process.WaitForExitAsync();
            }

            try
            {
                await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
            }

            return new CommandExecutionResult
            {
                ExitCode = timedOut || cancelled ? null : process.ExitCode,
                TimedOut = timedOut,
                Cancelled = cancelled,
                Duration = stopwatch.Elapsed,
                StandardOutput = standardOutput.Complete(),
                StandardError = standardError.Complete()
            };
        }
    }

    private ProcessStartInfo CreateStartInfo(CommandExecutionRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = shell.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        switch (shell.Kind)
        {
            case ShellKind.Bash:
            {
                startInfo.ArgumentList.Add("--noprofile");
                startInfo.ArgumentList.Add("--norc");
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(request.Command);
                break;
            }
            default:
            {
                var script = "$ProgressPreference='SilentlyContinue'\n[Console]::OutputEncoding=[System.Text.Encoding]::UTF8\n$OutputEncoding=[System.Text.Encoding]::UTF8\n" + request.Command;
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-EncodedCommand");
                startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                break;
            }
        }

        PrepareEnvironment(startInfo.Environment);
        return startInfo;
    }

    private void PrepareEnvironment(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.ToArray())
        {
            if (IsSecretName(name) || extraSecretVariables.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                environment.Remove(name);
                continue;
            }

            if (shell.Kind == ShellKind.Bash && (BashStartupVariables.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith("BASH_FUNC_", StringComparison.OrdinalIgnoreCase)))
            {
                environment.Remove(name);
            }
        }

        environment["GIT_TERMINAL_PROMPT"] = "0";
        environment["GCM_INTERACTIVE"] = "never";
        environment["GIT_PAGER"] = "cat";
        environment["PAGER"] = "cat";
        environment["GIT_EDITOR"] = "false";
        environment["EDITOR"] = "false";
        environment["NO_COLOR"] = "1";
        environment["TERM"] = "dumb";
    }

    public static bool IsSecretName(string name)
    {
        var upper = name.ToUpperInvariant();

        foreach (var marker in SecretMarkers)
        {
            if (upper.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
