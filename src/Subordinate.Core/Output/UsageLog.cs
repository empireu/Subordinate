using System.Text;
using System.Text.Json;
using Subordinate.Core.Json;
using Subordinate.Core.Sessions;

namespace Subordinate.Core.Output;

public static class UsageLog
{
    public const string FileName = "usage.jsonl";

    public static string FilePath(string home)
    {
        return Path.Combine(home, FileName);
    }

    public static void Append(string home, SessionRecord record, RunRecord run)
    {
        var path = FilePath(home);

        try
        {
            var json = AsciiJson.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("schema", 1);
                writer.WriteString("timestamp", RunArtifacts.FormatTime(DateTimeOffset.UtcNow));
                writer.WriteString("run_id", run.RunId);
                writer.WriteString("session", record.Name);
                writer.WriteString("workdir", record.Workdir);
                writer.WriteString("kind", run.Kind);
                writer.WriteString("state", run.State);
                writer.WriteString("model_alias", record.Model.Alias);
                writer.WriteString("model_provider", record.Model.Provider);
                writer.WriteString("model_id", record.Model.ModelId);
                writer.WriteNumber("duration_seconds", Math.Round(((run.EndedAt ?? DateTimeOffset.UtcNow) - run.StartedAt).TotalSeconds, 1));
                writer.WriteNumber("model_calls", run.Counters.ModelCalls);
                writer.WriteNumber("tool_calls", run.Counters.ToolCalls);
                writer.WriteNumber("input_tokens", run.Counters.InputTokens);
                writer.WriteNumber("cached_input_tokens", run.Counters.CachedInputTokens);
                writer.WriteNumber("output_tokens", run.Counters.OutputTokens);
                writer.WriteEndObject();
            }, false);

            File.AppendAllText(path, json + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
