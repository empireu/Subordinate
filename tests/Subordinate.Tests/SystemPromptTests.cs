using Subordinate.Core;
using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;
using Subordinate.Core.Policy;
using Subordinate.Core.Running;

namespace Subordinate.Tests;

public class SystemPromptTests
{
    [Fact]
    public void ComposesBaseThenSessionThenSupervisorInstructions()
    {
        Assert.True(PolicySet.TryParse(["build"], out var policy, out _));

        var prompt = SubordinateAgent.BuildSystemPrompt("BASE TEXT", @"D:\work", ShellKind.Bash, policy!, "Focus on the parser.");

        var baseIndex = prompt.IndexOf("BASE TEXT", StringComparison.Ordinal);
        var sessionIndex = prompt.IndexOf("Workdir: D:\\work", StringComparison.Ordinal);
        var supervisorIndex = prompt.IndexOf("Focus on the parser.", StringComparison.Ordinal);

        Assert.True(baseIndex == 0 && baseIndex < sessionIndex && sessionIndex < supervisorIndex, prompt);
        Assert.Contains("Automatically allowed: read, exec.", prompt);
    }

    [Fact]
    public void OmitsTheSupervisorSectionWhenNothingWasPassed()
    {
        Assert.True(PolicySet.TryParse(["readonly"], out var policy, out _));

        var prompt = SubordinateAgent.BuildSystemPrompt(SubordinateAgent.DefaultBasePrompt, @"D:\work", ShellKind.Bash, policy!, null);

        Assert.DoesNotContain("Instructions from the supervisor", prompt);
        Assert.Contains("PENDING APPROVAL", prompt);
    }

    [Fact]
    public void BasePromptComesFromTheHomeDirectoryWhenPresent()
    {
        var home = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        try
        {
            Assert.Equal(SubordinateAgent.DefaultBasePrompt, SubordinateConfig.LoadBasePrompt(home).Text);

            var path = Path.Combine(home, SubordinateConfig.BasePromptFileName);
            File.WriteAllText(path, "Custom base.");
            Assert.Equal(("Custom base.", path), SubordinateConfig.LoadBasePrompt(home));

            File.WriteAllText(path, "  ");
            Assert.Equal(ErrorCode.ConfigError, Assert.Throws<SubordinateException>(() => SubordinateConfig.LoadBasePrompt(home)).Code);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [LinuxFact]
    public void LinuxBashPromptMentionsLinux()
    {
        Assert.True(PolicySet.TryParse(["readonly"], out var policy, out _));

        var prompt = SubordinateAgent.BuildSystemPrompt("BASE", "/work", ShellKind.Bash, policy!, null);

        Assert.Contains("Linux", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Git Bash", prompt, StringComparison.Ordinal);
    }
}
