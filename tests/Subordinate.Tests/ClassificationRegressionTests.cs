using Subordinate.Core.Classification;
using Subordinate.Core.Policy;

namespace Subordinate.Tests;

public class ClassificationRegressionTests
{
    private const string Workdir = @"D:\work\repo";

    [Fact]
    public void ScriptArgumentsAreNotTreatedAsPaths()
    {
        var classification = AssertCapabilities("bash agent-tools/mc-src.sh grep \"a|b\"", Capability.Read, Capability.Exec);

        Assert.DoesNotContain(classification.AllReasons, reason => reason.Contains("cannot be determined", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TrOperandsAreCharacterSetsNotPaths()
    {
        AssertCapabilities("tr ';' '\\n' < file", Capability.Read);
    }

    [Fact]
    public void RegexPatternDoesNotTriggerTheCredentialCheck()
    {
        var classification = AssertCapabilities("rg -n \"\\.key\\(\" FrostedHeart/src", Capability.Read);

        Assert.DoesNotContain(classification.AllReasons, reason => reason.Contains("credential store", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EchoWithEqualsIsNotUnbounded()
    {
        var classification = AssertCapabilities("echo \"===\"", Capability.Read);

        Assert.DoesNotContain(classification.AllReasons, reason => reason.Contains("option comes before it", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EchoWithAnOptionIsNotUnbounded()
    {
        AssertCapabilities("echo -n hi", Capability.Read);
    }

    [Fact]
    public void RelativeCdInsideTheWorkdirIsTracked()
    {
        var classification = AssertCapabilities("cd FrostedHeart && ls src", Capability.Read);

        Assert.DoesNotContain(classification.AllReasons, reason => reason.Contains("working directory", StringComparison.OrdinalIgnoreCase) && reason.Contains("cannot be determined", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AbsoluteCdOutsideTheWorkdirStillRequiresReadOutside()
    {
        var decision = Decide("cd /tmp && ls", "readonly");

        Assert.False(decision.IsAllowed);
        Assert.Contains(Capability.ReadOutside, decision.Missing);
    }

    [Fact]
    public void XargsWithAReadOnlyInnerCommandIsBoundedButReadsOutside()
    {
        AssertCapabilities("find . -name '*.cs' | xargs wc -l", Capability.Read, Capability.ReadOutside);
    }

    [Fact]
    public void XargsCannotReadFilesNamedOnStandardInputUnderReadonly()
    {
        var decision = Decide("echo ~/.aws/credentials | xargs cat", "readonly");

        Assert.False(decision.IsAllowed);
        Assert.Contains(Capability.ReadOutside, decision.Missing);
    }

    [Fact]
    public void XargsWithADataCommandStaysRead()
    {
        AssertCapabilities("find . -name '*.cs' | xargs echo", Capability.Read);
    }

    [Fact]
    public void XargsWithAWriteNeedsWriteOutside()
    {
        var decision = Decide("find . -name '*.tmp' | xargs rm", "edit");

        Assert.False(decision.IsAllowed);
        Assert.Contains(Capability.WriteOutside, decision.Missing);
    }

    [Fact]
    public void XargsArgumentFileIsStillChecked()
    {
        var decision = Decide("xargs -a /tmp/list echo", "readonly");

        Assert.False(decision.IsAllowed);
        Assert.Contains(Capability.ReadOutside, decision.Missing);
    }

    [Fact]
    public void FindExecWithAReadOnlyInnerCommandIsBounded()
    {
        AssertCapabilities("find . -name '*.cs' -exec cat {} \\;", Capability.Read);
    }

    [Fact]
    public void AwkRegexAlternationIsNotAShellPipe()
    {
        AssertCapabilities("awk '$1 ~ /a|b/ {print $1}' src/a.cs", Capability.Read);
    }

    [Fact]
    public void LocaleAssignmentIsBounded()
    {
        AssertCapabilities("LC_ALL=C grep foo src", Capability.Read);
    }

    [Fact]
    public void NarrowInterpreterGrantMatchesOnlyTheNamedScript()
    {
        Assert.True(Decide("bash agent-tools/mc-src.sh grep foo", "readonly,cmd:bash agent-tools/mc-src.sh").IsAllowed);
        Assert.True(Decide("bash ./agent-tools/mc-src.sh grep foo", "readonly,cmd:bash agent-tools/mc-src.sh").IsAllowed);
        Assert.False(Decide("bash build.sh", "readonly,cmd:bash agent-tools/mc-src.sh").IsAllowed);
        Assert.False(Decide("bash agent-tools/other.sh grep foo", "readonly,cmd:bash agent-tools/mc-src.sh").IsAllowed);
    }

    [Fact]
    public void BroadInterpreterGrantDoesNotCoverScriptFiles()
    {
        Assert.False(Decide("bash build.sh", "readonly,cmd:bash").IsAllowed);
    }

    [Fact]
    public void AwkSystemCallStaysUnbounded()
    {
        var classification = Classify("awk 'BEGIN { system(\"rm x\") }' src/a.cs");

        Assert.Contains(Capability.Unbounded, classification.AllCapabilities);
    }

    [Fact]
    public void FindExecWithAWriteStaysBlocked()
    {
        var decision = Decide("find . -name '*.cs' -exec rm {} \\;", "readonly");

        Assert.False(decision.IsAllowed);
        Assert.Contains(decision.Missing, capability => capability is Capability.Write or Capability.Unbounded or Capability.Unclassified);
    }

    [Fact]
    public void XargsWithAWriteStaysBlocked()
    {
        var decision = Decide("find . -name '*.cs' | xargs rm", "readonly");

        Assert.False(decision.IsAllowed);
        Assert.Contains(decision.Missing, capability => capability is Capability.Write or Capability.Unbounded or Capability.Unclassified);
    }

    [Fact]
    public void BashInlineWriteStaysBlocked()
    {
        var decision = Decide("bash -c 'rm x'", "readonly");

        Assert.False(decision.IsAllowed);
        Assert.Contains(Capability.Write, decision.Missing);
    }

    [Fact]
    public void DangerousEnvironmentAssignmentStaysUnbounded()
    {
        var classification = Classify("PATH=/tmp grep foo src");

        Assert.Contains(Capability.Unbounded, classification.AllCapabilities);
    }

    private static ActionClassification AssertCapabilities(string command, params Capability[] expected)
    {
        var classification = Classify(command);
        var actual = classification.AllCapabilities.OrderBy(capability => (int)capability).Select(capability => capability.ToToken()).ToArray();
        var wanted = expected.OrderBy(capability => (int)capability).Select(capability => capability.ToToken()).ToArray();
        var detail = $"{command}: capabilities [{string.Join(", ", actual)}], reasons [{string.Join("; ", classification.AllReasons)}]";

        Assert.True(wanted.SequenceEqual(actual), $"expected [{string.Join(", ", wanted)}]. {detail}");
        return classification;
    }

    private static PolicyDecision Decide(string command, string policyText)
    {
        Assert.True(PolicySet.TryParse([policyText], out var policy, out var error), error);

        return PolicyEvaluator.Evaluate(Classify(command), policy!, []);
    }

    private static ActionClassification Classify(string command)
    {
        return ShellClassifier.Classify(command, ShellKind.Bash, new ClassificationScope { Workdir = Workdir, WorkingDirectory = Workdir });
    }
}
