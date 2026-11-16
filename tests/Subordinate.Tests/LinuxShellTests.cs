using System.Diagnostics;
using Subordinate.Core;
using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;
using Subordinate.Core.Shells;
using Xunit.Abstractions;

namespace Subordinate.Tests;

public class LinuxShellTests
{
    private readonly ITestOutputHelper _output;

    public LinuxShellTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LinuxFact]
    public void LocatesSystemBash()
    {
        var info = ShellLocator.Locate(ShellKind.Bash, new ShellPathsEntry());

        Assert.True(File.Exists(info.ExecutablePath));
        Assert.True(info.Version.StartsWith("5.2.", StringComparison.Ordinal) || info.Version.StartsWith("5.3.", StringComparison.Ordinal));
    }

    [LinuxFact]
    public void WindowsPowerShellIsUnavailableOnLinux()
    {
        var exception = Assert.Throws<SubordinateException>(() => ShellLocator.Locate(ShellKind.WindowsPowerShell, new ShellPathsEntry()));

        Assert.Equal(ErrorCode.ShellUnavailable, exception.Code);
    }

    [LinuxFact]
    public void ConfiguredProgramThatIsNotBashIsRejected()
    {
        var exception = Assert.Throws<SubordinateException>(() => ShellLocator.Locate(ShellKind.Bash, new ShellPathsEntry { Bash = "/bin/true" }));

        Assert.Equal(ErrorCode.ShellUnavailable, exception.Code);
    }

    [LinuxFact]
    public void VerifyUnchangedAcceptsTheLocatedBash()
    {
        var info = ShellLocator.Locate(ShellKind.Bash, new ShellPathsEntry());

        ShellLocator.VerifyUnchanged(info);
    }

    [LinuxFact]
    public async Task ExecutorRunsInTheWorkingDirectory()
    {
        var info = ShellLocator.Locate(ShellKind.Bash, new ShellPathsEntry());
        var directory = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var executor = new ShellCommandExecutor(info, []);
            var result = await executor.ExecuteAsync(CreateRequest("pwd", directory, TimeSpan.FromSeconds(30)), CancellationToken.None);

            Assert.Equal(0, result.ExitCode!.Value);
            Assert.Equal(directory, result.StandardOutput!.Text.Trim());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [LinuxFact]
    public async Task ExecutorRemovesSecretVariables()
    {
        var info = ShellLocator.Locate(ShellKind.Bash, new ShellPathsEntry());
        var previous = Environment.GetEnvironmentVariable("SUBORDINATE_SMOKE_TOKEN");
        Environment.SetEnvironmentVariable("SUBORDINATE_SMOKE_TOKEN", "secret");

        try
        {
            var executor = new ShellCommandExecutor(info, []);
            var result = await executor.ExecuteAsync(CreateRequest("printenv SUBORDINATE_SMOKE_TOKEN", Path.GetTempPath(), TimeSpan.FromSeconds(30)), CancellationToken.None);

            Assert.Equal(string.Empty, result.StandardOutput!.Text.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUBORDINATE_SMOKE_TOKEN", previous);
        }
    }

    [LinuxFact]
    public async Task ExecutorKillsOnTimeout()
    {
        var info = ShellLocator.Locate(ShellKind.Bash, new ShellPathsEntry());
        var executor = new ShellCommandExecutor(info, []);
        var stopwatch = Stopwatch.StartNew();
        var result = await executor.ExecuteAsync(CreateRequest("sleep 30", Path.GetTempPath(), TimeSpan.FromSeconds(1)), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(result.TimedOut);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [LinuxFact]
    public async Task PwshRunsWhenAvailable()
    {
        var executable = Environment.GetEnvironmentVariable("SUBORDINATE_TEST_PWSH");

        if (string.IsNullOrEmpty(executable))
        {
            _output.WriteLine("SUBORDINATE_TEST_PWSH is not set; skipping the pwsh check.");
            return;
        }

        var info = ShellLocator.Locate(ShellKind.PowerShell7, new ShellPathsEntry { Pwsh = executable });

        Assert.StartsWith("7.6.", info.Version);

        var executor = new ShellCommandExecutor(info, []);
        var result = await executor.ExecuteAsync(CreateRequest("Get-Location", Path.GetTempPath(), TimeSpan.FromSeconds(30)), CancellationToken.None);

        Assert.Equal(0, result.ExitCode!.Value);
        Assert.Contains(Path.GetTempPath().TrimEnd('/'), result.StandardOutput!.Text, StringComparison.Ordinal);
    }

    private static CommandExecutionRequest CreateRequest(string command, string workingDirectory, TimeSpan timeout)
    {
        return new CommandExecutionRequest
        {
            Command = command,
            WorkingDirectory = workingDirectory,
            Timeout = timeout,
            OutputDirectory = Path.GetTempPath(),
            OutputStem = "subordinate-test-" + Guid.NewGuid().ToString("N")
        };
    }
}
