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
            string content;

            using (var stream = new FileStream(paths.RecordFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                content = reader.ReadToEnd();
            }

            var record = JsonSerializer.Deserialize<SessionRecord>(content, RecordJsonOptions);

            if (record == null || record.Schema != 1)
            {
                throw new SubordinateException(ErrorCode.StateConflict, $"The session record at {paths.RecordFile} has an unsupported format.");
            }

            return record;
        }
        catch (FileNotFoundException)
        {
            throw new SubordinateException(ErrorCode.UnknownSession, $"No session record exists at {paths.RecordFile}.");
        }
        catch (JsonException ex)
        {
            throw new SubordinateException(ErrorCode.StateConflict, $"The session record at {paths.RecordFile} is corrupt: {ex.Message}");
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
        var messages = new List<ChatMessage>(pointer.Count);

        if (pointer.Count == 0)
        {
            return messages;
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
                messages.Add(TranscriptSerializer.Deserialize(line));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                throw new SubordinateException(ErrorCode.StateConflict, $"Transcript line {messages.Count + 1} cannot be read: {ex.Message}");
            }
        }

        if (messages.Count != pointer.Count)
        {
            throw new SubordinateException(ErrorCode.StateConflict, $"The transcript holds {messages.Count} messages but the session record says {pointer.Count}.");
        }

        return messages;
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

        using var stream = new FileStream(paths.TranscriptFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        stream.SetLength(pointer.Bytes);
        stream.Seek(pointer.Bytes, SeekOrigin.Begin);

        foreach (var message in messages)
        {
            var line = Encoding.UTF8.GetBytes(TranscriptSerializer.Serialize(message) + "\n");
            stream.Write(line);
        }

        stream.Flush(true);

        return new TranscriptPointer
        {
            Count = pointer.Count + messages.Count,
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
