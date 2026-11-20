using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnowledgeSystem.Ai.Providers.Abstractions;

using Subordinate.Core.Platform;

namespace Subordinate.Core.Sessions;

/// <summary>
///     The committed transcript in file order, plus the messages the model sees after the last compaction.
/// </summary>
public sealed class TranscriptLog
{
    public List<TranscriptEntry> Entries { get; } = [];

    public List<ChatMessage> AllMessages { get; } = [];

    public List<ChatMessage> ActiveMessages { get; } = [];

    public List<int> ActiveLineIndices { get; } = [];

    public string OriginalTask { get; set; } = string.Empty;

    public int CompactionCount { get; set; }
}

/// <summary>
///     The files of one session.
/// </summary>
public sealed class SessionPaths(string directory)
{
    public string Directory { get; } = directory;

    public string RecordFile => Path.Combine(Directory, "session.json");

    public string TranscriptFile => Path.Combine(Directory, "transcript.jsonl");

    public string LockFile => Path.Combine(Directory, "session.lock");

    public string StopFile => Path.Combine(Directory, "stop.request");

    public string RunsDirectory => Path.Combine(Directory, "runs");

    public string ImagesDirectory => Path.Combine(Directory, "images");

    public string RunDirectory(string runId)
    {
        return Path.Combine(RunsDirectory, runId);
    }
}

/// <summary>
///     Holds the exclusive lock of a session for as long as it is not disposed.
/// </summary>
public sealed class SessionLock : IDisposable
{
    private readonly FileStream _stream;

    private SessionLock(FileStream stream)
    {
        _stream = stream;
    }

    public static SessionLock? TryAcquire(SessionPaths paths)
    {
        try
        {
            var stream = new FileStream(paths.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var pid = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            stream.SetLength(0);
            stream.Write(pid);
            stream.Flush();
            return new SessionLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static SessionLock AcquireWithRetry(SessionPaths paths, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var acquired = TryAcquire(paths);

            if (acquired != null)
            {
                return acquired;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new SubordinateException(ErrorCode.SessionBusy, "The session is in use by another subordinate process.");
            }

            Thread.Sleep(100);
        }
    }

    public static bool IsHeld(SessionPaths paths)
    {
        using var probe = TryAcquire(paths);
        return probe == null && File.Exists(paths.LockFile);
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

/// <summary>
///     Stores sessions under the Subordinate home directory, one directory per (workdir, name) pair.
/// </summary>
public sealed class SessionStore
{
    public const int MaxNameLength = 80;
    private const int LoadAttempts = 20;
    private const int LoadRetryDelayMilliseconds = 25;

    public static readonly JsonSerializerOptions RecordJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        IgnoreReadOnlyProperties = true
    };

    public SessionStore(string home)
    {
        Home = home;
        SessionsDirectory = Path.Combine(home, "sessions");
    }

    public string Home { get; }

    public string SessionsDirectory { get; }

    public static string NormalizeWorkdir(string workdir)
    {
        return PlatformServices.Current.Paths.NormalizeDirectory(workdir);
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, "The session name is empty.");
        }

        if (name.Length > MaxNameLength)
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, $"The session name is longer than {MaxNameLength} characters.");
        }

        foreach (var character in name)
        {
            if (character < ' ' || character > '~')
            {
                throw new SubordinateException(ErrorCode.InvalidArguments, "The session name may only contain printable ASCII characters.");
            }
        }

        if (name != name.Trim())
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, "The session name may not start or end with whitespace.");
        }
    }

    public SessionPaths PathsFor(string workdir, string name)
    {
        var identity = PlatformServices.Current.Paths.IdentityKey(NormalizeWorkdir(workdir)) + "\n" + name.ToLowerInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
        var slug = new StringBuilder();

        foreach (var character in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }

            if (slug.Length >= 32)
            {
                break;
            }
        }

        var prefix = slug.ToString().Trim('-');
        return new SessionPaths(Path.Combine(SessionsDirectory, (prefix.Length > 0 ? prefix + "-" : string.Empty) + hash));
    }

    public IEnumerable<SessionPaths> Enumerate()
    {
        if (!System.IO.Directory.Exists(SessionsDirectory))
        {
            yield break;
        }

        foreach (var directory in System.IO.Directory.EnumerateDirectories(SessionsDirectory))
        {
            var paths = new SessionPaths(directory);

            if (File.Exists(paths.RecordFile))
            {
                yield return paths;
            }
        }
    }

    /// <summary>
    ///     Creates the directory of a new session. Fails loudly instead of overwriting an existing session.
    /// </summary>
    public SessionPaths Create(SessionRecord record)
    {
        var paths = PathsFor(record.Workdir, record.Name);
        System.IO.Directory.CreateDirectory(paths.Directory);

        try
        {
            using var stream = new FileStream(paths.RecordFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            record.UpdatedAt = DateTimeOffset.UtcNow;
            stream.Write(new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(record, RecordJsonOptions)));
        }
        catch (IOException) when (File.Exists(paths.RecordFile))
        {
            throw new SubordinateException(ErrorCode.SessionExists, $"A session named \"{record.Name}\" already exists for {record.Workdir}. Resume it, delete it, or pick another name.");
        }

        File.WriteAllBytes(paths.TranscriptFile, []);
        return paths;
    }

    public SessionRecord Load(SessionPaths paths)
    {
        try
        {
            var content = ReadRecordText(paths);
            var record = JsonSerializer.Deserialize<SessionRecord>(content, RecordJsonOptions);

            if (record == null || record.Schema != 1)
            {
                throw new SubordinateException(ErrorCode.StateConflict, $"The session record at {paths.RecordFile} has an unsupported format.");
            }

            return record;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SubordinateException(ErrorCode.UnknownSession, $"No session record exists at {paths.RecordFile}.");
        }
        catch (JsonException ex)
        {
            throw new SubordinateException(ErrorCode.StateConflict, $"The session record at {paths.RecordFile} is corrupt: {ex.Message}");
        }
    }

    /// <summary>
    ///     Reads the record file, retrying while a concurrent save is swapping it in. On Windows that swap briefly hides the file or locks it.
    /// </summary>
    private static string ReadRecordText(SessionPaths paths)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(paths.RecordFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (attempt < LoadAttempts && (ex is IOException or UnauthorizedAccessException) && ex is not DirectoryNotFoundException && System.IO.Directory.Exists(paths.Directory))
            {
                Thread.Sleep(LoadRetryDelayMilliseconds);
            }
        }
    }

    public void Save(SessionPaths paths, SessionRecord record)
    {
        record.UpdatedAt = DateTimeOffset.UtcNow;
        WriteAtomically(paths.RecordFile, JsonSerializer.Serialize(record, RecordJsonOptions));
    }

    public static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // File.Replace succeeds while a reader holds the target open with FileShare.Delete, unlike File.Move:
                if (OperatingSystem.IsWindows() && File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporary, path, null, true);
                    }
                    catch (FileNotFoundException)
                    {
                        File.Move(temporary, path, true);
                    }
                }
                else
                {
                    File.Move(temporary, path, true);
                }

                return;
            }
            catch (Exception ex) when (attempt < 10 && (ex is IOException or UnauthorizedAccessException) && ex is not FileNotFoundException && ex is not DirectoryNotFoundException)
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>
    ///     Reads the committed part of the transcript. Bytes after the committed length are a torn write and are ignored.
    /// </summary>
    public List<ChatMessage> LoadTranscript(SessionPaths paths, TranscriptPointer pointer)
    {
        return LoadTranscriptLog(paths, pointer).ActiveMessages;
    }

    /// <summary>
    ///     Reads the committed transcript entries, every message line, and the messages the model sees after the last compaction.
    /// </summary>
    public TranscriptLog LoadTranscriptLog(SessionPaths paths, TranscriptPointer pointer)
    {
        var log = new TranscriptLog();

        if (pointer.Count == 0)
        {
            return log;
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(paths.TranscriptFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < pointer.Bytes)
            {
                throw new SubordinateException(ErrorCode.StateConflict, "The transcript is shorter than the session record says. The session cannot be resumed safely.");
            }

            bytes = new byte[pointer.Bytes];
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException)
        {
            throw new SubordinateException(ErrorCode.StateConflict, "The transcript file is missing. The session cannot be resumed safely.");
        }

        var text = Encoding.UTF8.GetString(bytes);

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                log.Entries.Add(TranscriptSerializer.DeserializeEntry(line));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new SubordinateException(ErrorCode.StateConflict, $"Transcript line {log.Entries.Count + 1} cannot be read: {ex.Message}");
            }
        }

        if (log.Entries.Count != pointer.Count)
        {
            throw new SubordinateException(ErrorCode.StateConflict, $"The transcript holds {log.Entries.Count} messages but the session record says {pointer.Count}.");
        }

        var previousMarkerIndex = -1;

        for (var index = 0; index < log.Entries.Count; index++)
        {
            switch (log.Entries[index])
            {
                case TranscriptMessageEntry messageEntry:
                {
                    log.AllMessages.Add(messageEntry.Message);
                    break;
                }
                case TranscriptCompactionEntry:
                {
                    ValidateCompactionMarker(log.Entries, index, previousMarkerIndex);
                    previousMarkerIndex = index;
                    log.CompactionCount++;
                    break;
                }
            }
        }

        var firstUserMessage = log.AllMessages.FirstOrDefault(message => message.Role == ChatRole.User);
        log.OriginalTask = firstUserMessage?.GetText() ?? string.Empty;
        BuildActiveMessages(log);
        return log;
    }

    private static void ValidateCompactionMarker(List<TranscriptEntry> entries, int markerIndex, int previousMarkerIndex)
    {
        var marker = ((TranscriptCompactionEntry)entries[markerIndex]).Marker;

        if (marker.KeptFromLine < 1 || marker.KeptFromLine > marker.KeptToLine || marker.KeptToLine > markerIndex || marker.KeptFromLine <= previousMarkerIndex)
        {
            throw InvalidCompactionMarker(markerIndex);
        }

        for (var line = marker.KeptFromLine; line < marker.KeptToLine; line++)
        {
            if (entries[line] is not TranscriptMessageEntry)
            {
                throw InvalidCompactionMarker(markerIndex);
            }
        }
    }

    private static SubordinateException InvalidCompactionMarker(int markerIndex)
    {
        return new SubordinateException(ErrorCode.StateConflict, $"The compaction marker on line {markerIndex} refers to messages that cannot be kept. The session cannot be resumed safely.");
    }

    private static void BuildActiveMessages(TranscriptLog log)
    {
        var lastMarkerIndex = log.Entries.FindLastIndex(entry => entry is TranscriptCompactionEntry);

        if (lastMarkerIndex < 0)
        {
            for (var index = 0; index < log.Entries.Count; index++)
            {
                if (log.Entries[index] is TranscriptMessageEntry messageEntry)
                {
                    log.ActiveMessages.Add(messageEntry.Message);
                    log.ActiveLineIndices.Add(index);
                }
            }

            return;
        }

        var marker = ((TranscriptCompactionEntry)log.Entries[lastMarkerIndex]).Marker;

        if (log.AllMessages.Count > 0)
        {
            log.ActiveMessages.Add(log.AllMessages[0]);
            log.ActiveLineIndices.Add(0);
        }

        log.ActiveMessages.Add(new ChatMessage
        {
            Role = ChatRole.User,
            AuthorName = CompactionAuthors.Summary,
            Contents = [new TextContent { Text = marker.Message }]
        });
        log.ActiveLineIndices.Add(-1);

        for (var line = marker.KeptFromLine; line < marker.KeptToLine; line++)
        {
            log.ActiveMessages.Add(CompactionImages.StripImages(((TranscriptMessageEntry)log.Entries[line]).Message));
            log.ActiveLineIndices.Add(line);
        }

        for (var index = lastMarkerIndex + 1; index < log.Entries.Count; index++)
        {
            if (log.Entries[index] is TranscriptMessageEntry messageEntry)
            {
                log.ActiveMessages.Add(messageEntry.Message);
                log.ActiveLineIndices.Add(index);
            }
        }
    }

    /// <summary>
    ///     Appends messages after the committed length, discarding any torn tail, and returns the new pointer. The caller commits it by saving the record.
    /// </summary>
    public TranscriptPointer AppendTranscript(SessionPaths paths, TranscriptPointer pointer, IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return pointer;
        }

        var lines = messages.Select(TranscriptSerializer.Serialize).ToList();
        return AppendLines(paths, pointer, lines);
    }

    /// <summary>
    ///     Appends a compaction marker after the committed length, discarding any torn tail, and returns the new pointer. The caller commits it by saving the record.
    /// </summary>
    public TranscriptPointer AppendCompaction(SessionPaths paths, TranscriptPointer pointer, CompactionMarker marker)
    {
        return AppendLines(paths, pointer, [TranscriptSerializer.SerializeCompaction(marker)]);
    }

    private static TranscriptPointer AppendLines(SessionPaths paths, TranscriptPointer pointer, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return pointer;
        }

        using var stream = new FileStream(paths.TranscriptFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        stream.SetLength(pointer.Bytes);
        stream.Seek(pointer.Bytes, SeekOrigin.Begin);

        foreach (var line in lines)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes);
        }

        stream.Flush(true);

        return new TranscriptPointer
        {
            Count = pointer.Count + lines.Count,
            Bytes = stream.Length
        };
    }

    public string CreateRunDirectory(SessionPaths paths, int runNumber, int keepRuns)
    {
        var runId = $"{runNumber:D4}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var directory = paths.RunDirectory(runId);
        System.IO.Directory.CreateDirectory(directory);
        PruneRuns(paths, keepRuns, runId);
        return runId;
    }

    private static void PruneRuns(SessionPaths paths, int keepRuns, string currentRunId)
    {
        if (keepRuns <= 0 || !System.IO.Directory.Exists(paths.RunsDirectory))
        {
            return;
        }

        var runs = System.IO.Directory.GetDirectories(paths.RunsDirectory).OrderBy(directory => Path.GetFileName(directory), StringComparer.Ordinal).ToList();

        while (runs.Count > keepRuns)
        {
            var oldest = runs[0];
            runs.RemoveAt(0);

            if (Path.GetFileName(oldest) == currentRunId)
            {
                continue;
            }

            try
            {
                System.IO.Directory.Delete(oldest, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
