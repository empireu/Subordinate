using System.Text.Json;
using Subordinate.Cli;
using Subordinate.Core;
using Subordinate.Core.Configuration;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;

namespace Subordinate.Tests;

public class CompactionConfigurationTests
{
    [Fact]
    public void CompactionSettingsParseFromConfig()
    {
        var config = JsonSerializer.Deserialize<SubordinateConfig>("""
            {
              "models": {
                "default": { "provider": "test", "model": "test-model", "context_window": 1000000, "autocompact_threshold": 400000 }
              },
              "defaults": { "autocompact_wind_down_turns": 7 }
            }
            """, SubordinateConfig.JsonOptions)!;

        Assert.Equal(400000, config.Models["default"].AutocompactThreshold);
        Assert.Equal(7, config.Defaults.AutocompactWindDownTurns);
    }

    [Fact]
    public void MissingCompactionSettingsUseDefaults()
    {
        var config = JsonSerializer.Deserialize<SubordinateConfig>("""
            {
              "models": { "default": { "provider": "test", "model": "test-model" } }
            }
            """, SubordinateConfig.JsonOptions)!;

        Assert.Null(config.Models["default"].AutocompactThreshold);
        Assert.Equal(5, config.Defaults.AutocompactWindDownTurns);
    }

    [Theory]
    [InlineData(999, null, 5)]
    [InlineData(1000, 1000, 5)]
    [InlineData(2000, 1000, 5)]
    [InlineData(5000, null, -1)]
    [InlineData(5000, null, 51)]
    public void InvalidCompactionSettingsFailWithConfigError(int threshold, int? contextWindow, int windDownTurns)
    {
        var windowText = contextWindow.HasValue ? $", \"context_window\": {contextWindow.Value}" : string.Empty;
        var json = $$"""
            {
              "providers": { "test": { "endpoint": "https://example.invalid/v1", "api_key": "test" } },
              "models": {
                "default": { "provider": "test", "model": "test-model", "autocompact_threshold": {{threshold}}{{windowText}} }
              },
              "defaults": { "autocompact_wind_down_turns": {{windDownTurns}} }
            }
            """;

        var exception = CreateRunnerExpectingFailure(json);

        Assert.Equal(ErrorCode.ConfigError, exception.Code);
        Assert.Contains("default", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactionPromptsComeFromTheHomeDirectoryWhenPresent()
    {
        var home = Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);

        try
        {
            Assert.Equal(CompactionPrompts.DefaultNotice, SubordinateConfig.LoadAutocompactNotice(home).Text);
            Assert.Equal(CompactionPrompts.DefaultRequest, SubordinateConfig.LoadAutocompactPrompt(home).Text);

            var noticePath = Path.Combine(home, SubordinateConfig.AutocompactNoticeFileName);
            var requestPath = Path.Combine(home, SubordinateConfig.AutocompactPromptFileName);
            File.WriteAllText(noticePath, "Custom notice for {turns} turns.");
            File.WriteAllText(requestPath, "Custom request.");

            Assert.Equal(("Custom notice for {turns} turns.", noticePath), SubordinateConfig.LoadAutocompactNotice(home));
            Assert.Equal(("Custom request.", requestPath), SubordinateConfig.LoadAutocompactPrompt(home));

            File.WriteAllText(noticePath, "  ");
            Assert.Equal(ErrorCode.ConfigError, Assert.Throws<SubordinateException>(() => SubordinateConfig.LoadAutocompactNotice(home)).Code);

            File.WriteAllText(noticePath, new string('x', 50_001));
            Assert.Equal(ErrorCode.ConfigError, Assert.Throws<SubordinateException>(() => SubordinateConfig.LoadAutocompactNotice(home)).Code);

            File.WriteAllText(requestPath, string.Empty);
            Assert.Equal(ErrorCode.ConfigError, Assert.Throws<SubordinateException>(() => SubordinateConfig.LoadAutocompactPrompt(home)).Code);

            File.WriteAllText(requestPath, new string('x', 50_001));
            Assert.Equal(ErrorCode.ConfigError, Assert.Throws<SubordinateException>(() => SubordinateConfig.LoadAutocompactPrompt(home)).Code);
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void TheNoticePlaceholderIsReplaced()
    {
        var formatted = CompactionPrompts.FormatNotice(CompactionPrompts.DefaultNotice, 3);

        Assert.Contains("at most 3 more model turns", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(CompactionPrompts.TurnsPlaceholder, formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void InitCreatesCompactionPromptFilesAndReportsThem()
    {
        using var harness = new CliHarness();
        var commandLine = CommandLine.Parse(["init"], new HashSet<string>(), new HashSet<string>());
        var noticePath = Path.Combine(harness.Home, SubordinateConfig.AutocompactNoticeFileName);
        var requestPath = Path.Combine(harness.Home, SubordinateConfig.AutocompactPromptFileName);

        var result = UtilityCommands.Init(commandLine);

        Assert.Equal(CompactionPrompts.DefaultNotice.ReplaceLineEndings("\n") + "\n", File.ReadAllText(noticePath).ReplaceLineEndings("\n"));
        Assert.Equal(CompactionPrompts.DefaultRequest.ReplaceLineEndings("\n") + "\n", File.ReadAllText(requestPath).ReplaceLineEndings("\n"));

        using (var document = JsonDocument.Parse(result.Envelope.ToJson()))
        {
            var root = document.RootElement;
            Assert.Equal(noticePath, root.GetProperty("autocompact_notice_file").GetString());
            Assert.True(root.GetProperty("autocompact_notice_created").GetBoolean());
            Assert.Equal(requestPath, root.GetProperty("autocompact_prompt_file").GetString());
            Assert.True(root.GetProperty("autocompact_prompt_created").GetBoolean());
        }

        Assert.Contains($"Created {noticePath}", result.Summary, StringComparison.Ordinal);
        Assert.Contains($"Created {requestPath}", result.Summary, StringComparison.Ordinal);

        File.WriteAllText(noticePath, "custom notice");
        File.WriteAllText(requestPath, "custom request");

        var second = UtilityCommands.Init(commandLine);

        Assert.Equal("custom notice", File.ReadAllText(noticePath));
        Assert.Equal("custom request", File.ReadAllText(requestPath));

        using (var document = JsonDocument.Parse(second.Envelope.ToJson()))
        {
            Assert.False(document.RootElement.GetProperty("autocompact_notice_created").GetBoolean());
            Assert.False(document.RootElement.GetProperty("autocompact_prompt_created").GetBoolean());
        }

        Assert.Contains($"The compaction notice already exists at {noticePath}", second.Summary, StringComparison.Ordinal);
        Assert.Contains($"The compaction summary request already exists at {requestPath}", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCompactionPromptsAreAsciiOnly()
    {
        Assert.All(CompactionPrompts.DefaultNotice, character => Assert.True(character < 0x80));
        Assert.All(CompactionPrompts.DefaultRequest, character => Assert.True(character < 0x80));
    }

    private static SubordinateException CreateRunnerExpectingFailure(string json)
    {
        var config = JsonSerializer.Deserialize<SubordinateConfig>(json, SubordinateConfig.JsonOptions)!;
        var store = new SessionStore(Path.Combine(Path.GetTempPath(), "subordinate-tests", Guid.NewGuid().ToString("N")));
        var record = new SessionRecord
        {
            Model = new ModelRecord { Alias = "default", Provider = "test", ModelId = "test-model" }
        };

        return Assert.Throws<SubordinateException>(() => RunFactory.CreateRunner(config, store, new SessionPaths(store.SessionsDirectory), record, "run"));
    }
}
