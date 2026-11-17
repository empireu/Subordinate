using System.Text;
using Subordinate.Core.Configuration;
using Subordinate.Core.Monitoring;

namespace Subordinate.Tests;

public class MonitorProtocolTests
{
    private static readonly DateTimeOffset Time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void RoundTripsEveryMessageTypeWithAllFields()
    {
        var messages = new MonitorMessage[]
        {
            new MonitorHello("/home/sessions/a", "a", "/work", "0001-20261117-101500", "start", 1234, "alias", "model-id", Time),
            new MonitorSnapshot(
                "answering",
                Time,
                3,
                "reasoning tail",
                10,
                "text tail",
                20,
                [new MonitorProposedCall("call_1", "shell", "ls")],
                new MonitorActiveTool("call_1", "shell", "ls", Time),
                new MonitorStatusPayload("running", 12.5, 7, 1, "waiting", new MonitorStatusCounters(1, 2, 3, 4, 5), new MonitorStatusCounters(6, 7, 8, 9, 10), 11),
                42,
                new MonitorLastTurn(3.5, 100)),
            new MonitorTurnStarted(2, Time),
            new MonitorReasoningDelta("think"),
            new MonitorTextDelta("answer"),
            new MonitorToolCallProposed("call_1", "shell", "ls"),
            new MonitorTurnCompleted("stop", 100, 20, 30, Time),
            new MonitorTurnFailed("boom"),
            new MonitorToolStarted("call_1", "shell", "ls", Time),
            new MonitorToolFinished("call_1", true, "ok", Time),
            new MonitorStatus("running", 12.5, 7, 1, "waiting", new MonitorStatusCounters(1, 2, 3, 4, 5), new MonitorStatusCounters(6, 7, 8, 9, 10), 11),
            new MonitorTranscriptCommitted(5, 500),
            new MonitorRunEnded("finished", "oops")
        };

        foreach (var message in messages)
        {
            AssertRoundTrips(message);
        }
    }

    [Fact]
    public void RoundTripsOptionalFieldsAsNull()
    {
        AssertRoundTrips(new MonitorSnapshot("starting", Time, 0, string.Empty, 0, string.Empty, 0, [], null, null, 0, null));
        AssertRoundTrips(new MonitorTurnCompleted("stop", null, null, null, Time));
        AssertRoundTrips(new MonitorRunEnded("finished", null));
    }

    [Fact]
    public void SerializedLinesAreAsciiWithoutNewlines()
    {
        var line = MonitorProtocol.Serialize(new MonitorTextDelta("caf\u00e9 \u4e2d"));

        Assert.DoesNotContain('\n', line);
        Assert.All(line, character => Assert.InRange(character, ' ', '~'));
        var parsed = MonitorProtocol.Parse(Encoding.UTF8.GetBytes(line));
        Assert.Equal(MonitorParseOutcome.Ok, parsed.Outcome);
        Assert.Equal("caf\u00e9 \u4e2d", Assert.IsType<MonitorTextDelta>(parsed.Message).Text);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"v\":1}")]
    [InlineData("{\"v\":2,\"type\":\"hello\"}")]
    [InlineData("{\"v\":1,\"type\":\"turn_started\",\"model_call\":\"one\",\"at\":\"2023-11-14T22:13:20Z\"}")]
    public void InvalidLinesAreRejected(string line)
    {
        Assert.Equal(MonitorParseOutcome.Invalid, MonitorProtocol.Parse(Encoding.UTF8.GetBytes(line)).Outcome);
    }

    [Fact]
    public void UnknownTypesAreIgnored()
    {
        var result = MonitorProtocol.Parse(Encoding.UTF8.GetBytes("{\"v\":1,\"type\":\"future_thing\"}"));

        Assert.Equal(MonitorParseOutcome.Ignored, result.Outcome);
        Assert.Null(result.Message);
    }

    [Fact]
    public void EndpointDefaultsToTheHomeSocket()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");
        var endpoint = MonitorEndpoint.Resolve(null, home);

        Assert.True(endpoint.Enabled);
        Assert.Equal(Path.Combine(home, "monitor.sock"), endpoint.SocketPath);
        Assert.Null(endpoint.Problem);
    }

    [Fact]
    public void DisabledEndpointHasNoPathOrProblem()
    {
        var config = new SubordinateConfig { Monitor = new MonitorEntry { Enabled = false } };
        var endpoint = MonitorEndpoint.Resolve(config, Path.GetTempPath());

        Assert.False(endpoint.Enabled);
        Assert.Null(endpoint.SocketPath);
        Assert.Null(endpoint.Problem);
    }

    [Fact]
    public void RelativeSocketPathIsRejected()
    {
        var config = new SubordinateConfig { Monitor = new MonitorEntry { SocketPath = "relative.sock" } };
        var endpoint = MonitorEndpoint.Resolve(config, Path.GetTempPath());

        Assert.False(endpoint.Enabled);
        Assert.Equal("monitor.socket_path must be an absolute path.", endpoint.Problem);
    }

    [Fact]
    public void LongSocketPathIsRejected()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var path = root + new string('a', 101 - root.Length);
        Assert.Equal(101, Encoding.UTF8.GetByteCount(path));
        var config = new SubordinateConfig { Monitor = new MonitorEntry { SocketPath = path } };
        var endpoint = MonitorEndpoint.Resolve(config, Path.GetTempPath());

        Assert.False(endpoint.Enabled);
        Assert.Equal("The monitor socket path is longer than 100 bytes. Set monitor.socket_path to a shorter absolute path.", endpoint.Problem);
    }

    [Fact]
    public void HundredByteSocketPathIsAccepted()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var path = root + new string('a', 100 - root.Length);
        Assert.Equal(100, Encoding.UTF8.GetByteCount(path));
        var config = new SubordinateConfig { Monitor = new MonitorEntry { SocketPath = path } };
        var endpoint = MonitorEndpoint.Resolve(config, Path.GetTempPath());

        Assert.True(endpoint.Enabled);
        Assert.Equal(path, endpoint.SocketPath);
        Assert.Null(endpoint.Problem);
    }

    private static void AssertRoundTrips(MonitorMessage message)
    {
        var line = MonitorProtocol.Serialize(message);

        Assert.DoesNotContain('\n', line);
        Assert.All(line, character => Assert.InRange(character, ' ', '~'));
        var result = MonitorProtocol.Parse(Encoding.UTF8.GetBytes(line));
        Assert.Equal(MonitorParseOutcome.Ok, result.Outcome);
        Assert.Equivalent(message, result.Message, strict: true);
    }
}
