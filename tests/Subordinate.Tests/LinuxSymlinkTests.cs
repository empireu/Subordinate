using Subordinate.Core.Classification;
using Subordinate.Core.Platform;
using Subordinate.Core.Policy;
using Subordinate.Core.Running;
using Subordinate.Core.Shells;
using Subordinate.Core.Tools;

namespace Subordinate.Tests;

public class LinuxSymlinkTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));

    public LinuxSymlinkTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "work", "src"));
        Directory.CreateDirectory(Path.Combine(_directory, "outside"));
        File.WriteAllText(Path.Combine(_directory, "work", "src", "a.cs"), "alpha");
        File.WriteAllText(Path.Combine(_directory, "outside", "x"), "secret");

        if (OperatingSystem.IsLinux())
        {
            Directory.CreateSymbolicLink(Path.Combine(_directory, "work", "escape"), "../outside");
            Directory.CreateSymbolicLink(Path.Combine(_directory, "work", "dangling"), "../outside/newfile");
            Directory.CreateSymbolicLink(Path.Combine(_directory, "link"), "work");
        }
    }

    [LinuxFact]
    public void LinkThatPointsOutsideTheWorkdirIsNotInside()
    {
        var work = Path.Combine(_directory, "work");

        Assert.False(LinuxPathSemantics.Instance.IsInside(Path.Combine(work, "escape", "x"), work));
    }

    [LinuxFact]
    public void RealFileInsideTheWorkdirIsInside()
    {
        var work = Path.Combine(_directory, "work");

        Assert.True(LinuxPathSemantics.Instance.IsInside(Path.Combine(work, "src", "a.cs"), work));
    }

    [LinuxFact]
    public void WorkdirReachedThroughALinkContainsItsOwnFiles()
    {
        var link = Path.Combine(_directory, "link");

        Assert.True(LinuxPathSemantics.Instance.IsInside(Path.Combine(link, "src", "a.cs"), link));
    }

    [LinuxFact]
    public void ReadingThroughAnEscapingLinkNeedsReadOutside()
    {
        var work = Path.Combine(_directory, "work");
        var classification = ShellClassifier.Classify("cat escape/x", ShellKind.Bash, new ClassificationScope
        {
            Workdir = work,
            WorkingDirectory = work
        });

        Assert.Contains(Capability.ReadOutside, classification.AllCapabilities);
    }

    [LinuxFact]
    public void WritingThroughAnEscapingLinkNeedsWriteOutside()
    {
        var work = Path.Combine(_directory, "work");
        var environment = CreateEnvironment(work);

        var classification = FileToolPaths.Classify("write_file", "escape/x", true, environment);

        Assert.Contains(Capability.WriteOutside, classification.AllCapabilities);
    }

    [LinuxFact]
    public void ChangingIntoAnEscapingLinkNeedsReadOutside()
    {
        var work = Path.Combine(_directory, "work");
        var classification = ShellClassifier.Classify("cd escape && cat x", ShellKind.Bash, new ClassificationScope
        {
            Workdir = work,
            WorkingDirectory = work
        });

        Assert.Contains(Capability.ReadOutside, classification.AllCapabilities);
    }

    [LinuxFact]
    public void ReadingAfterAnEscapingLinkNeedsReadOutside()
    {
        var work = Path.Combine(_directory, "work");
        var classification = ShellClassifier.Classify("cat escape/../outside/x", ShellKind.Bash, new ClassificationScope
        {
            Workdir = work,
            WorkingDirectory = work
        });

        Assert.Contains(Capability.ReadOutside, classification.AllCapabilities);
    }

    [LinuxFact]
    public void WritingAfterAnEscapingLinkNeedsWriteOutside()
    {
        var work = Path.Combine(_directory, "work");
        var classification = ShellClassifier.Classify("echo x > escape/../outside/x", ShellKind.Bash, new ClassificationScope
        {
            Workdir = work,
            WorkingDirectory = work
        });

        Assert.Contains(Capability.WriteOutside, classification.AllCapabilities);
    }

    [LinuxFact]
    public void WritingThroughADanglingLinkNeedsWriteOutside()
    {
        var work = Path.Combine(_directory, "work");
        var classification = ShellClassifier.Classify("echo x > dangling", ShellKind.Bash, new ClassificationScope
        {
            Workdir = work,
            WorkingDirectory = work
        });

        Assert.Contains(Capability.WriteOutside, classification.AllCapabilities);
    }

    [LinuxFact]
    public void FileToolParentTraversalAfterAnEscapingLinkNeedsWriteOutside()
    {
        var work = Path.Combine(_directory, "work");
        var classification = FileToolPaths.Classify("write_file", "escape/../outside/x", true, CreateEnvironment(work));

        Assert.Contains(Capability.WriteOutside, classification.AllCapabilities);
    }

    [LinuxFact]
    public void FileToolWritingThroughADanglingLinkNeedsWriteOutside()
    {
        var work = Path.Combine(_directory, "work");
        var classification = FileToolPaths.Classify("write_file", "dangling", true, CreateEnvironment(work));

        Assert.Contains(Capability.WriteOutside, classification.AllCapabilities);
    }

    private static ToolEnvironment CreateEnvironment(string work)
    {
        return new ToolEnvironment
        {
            Workdir = work,
            Shell = ShellKind.Bash,
            Executor = new UnusedCommandExecutor(),
            OutputDirectory = work,
            ReadableRoots = [],
            DefaultShellTimeoutSeconds = 1
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class UnusedCommandExecutor : ICommandExecutor
    {
        public ShellKind Shell => ShellKind.Bash;

        public Task<CommandExecutionResult> ExecuteAsync(CommandExecutionRequest request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("The executor is not used by this test.");
        }
    }
}
