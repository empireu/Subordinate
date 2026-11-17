using System.Text;
using System.Text.Json;

namespace Subordinate.Core.Sessions;

public static class StopRequesters
{
    public const string Supervisor = "supervisor";
    public const string User = "user";
}

/// <summary>
///     The content of stop.request, which asks the running worker of a session to stop.
/// </summary>
public sealed class StopRequest
{
    public const string UserStopNotice = "The session was stopped by the user via the TUI. Do not resume it until the user tells you to.";
    public const string UserStopAgentNote = "The user stopped the previous run from the session monitor.";
    private const int MaxFileBytes = 4096;

    public string By { get; set; } = StopRequesters.Supervisor;

    public string? Source { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public static void Write(SessionPaths paths, string requestedBy, string source)
    {
        var request = new StopRequest { By = requestedBy, Source = source, RequestedAt = DateTimeOffset.UtcNow };
        SessionStore.WriteAtomically(paths.StopFile, JsonSerializer.Serialize(request, SessionStore.RecordJsonOptions));
    }

    public static void Clear(SessionPaths paths)
    {
        try
        {
            File.Delete(paths.StopFile);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    ///     Returns who asked for the stop. A file that cannot be read or parsed counts as a supervisor stop.
    /// </summary>
    public static string ReadRequester(SessionPaths paths)
    {
        try
        {
            using var stream = new FileStream(paths.StopFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length > MaxFileBytes)
            {
                return StopRequesters.Supervisor;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var request = JsonSerializer.Deserialize<StopRequest>(reader.ReadToEnd(), SessionStore.RecordJsonOptions);
            return request?.By == StopRequesters.User ? StopRequesters.User : StopRequesters.Supervisor;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return StopRequesters.Supervisor;
        }
    }
}
