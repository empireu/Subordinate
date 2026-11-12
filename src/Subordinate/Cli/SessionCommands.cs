using System.Globalization;
using System.Text;
using System.Text.Json;
using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core;
using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;
using Subordinate.Core.Gate;
using Subordinate.Core.Json;
using Subordinate.Core.Output;
using Subordinate.Core.Policy;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;
using Subordinate.Core.Shells;
using Subordinate.Core.Tools;

namespace Subordinate.Cli;

internal static class SessionCommands
{
    private const int MaxPromptCharacters = 2_000_000;
    private const int MaxInlineAnswerCharacters = 20_000;

    public static async Task<CommandResult> StartAsync(CommandLine commandLine)
    {
        var name = commandLine.Required("name");
        SessionStore.ValidateName(name);

        var workdir = SessionStore.NormalizeWorkdir(commandLine.Single("workdir") ?? Directory.GetCurrentDirectory());

        if (!Directory.Exists(workdir))
        {
            throw new SubordinateException(ErrorCode.WorkdirMissing, $"The workdir {workdir} does not exist.");
        }

        if (!PolicySet.TryParse(commandLine.All("policy"), out var policy, out var policyError))
        {
            throw new SubordinateException(ErrorCode.BadPolicy, policyError);
        }

        if (commandLine.Single("prompt-file") == "-" && commandLine.Single("system-prompt-file") == "-")
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, "Only one of --prompt-file and --system-prompt-file can read from stdin.");
        }

        var prompt = ReadText(commandLine, "prompt", "prompt-file", ErrorCode.PromptError, "prompt") ?? throw new SubordinateException(ErrorCode.PromptError, "Give the task with --prompt-file <path>, --prompt-file - for stdin, or --prompt <text>.");
        var supervisorInstructions = ReadText(commandLine, "system-prompt", "system-prompt-file", ErrorCode.PromptError, "system prompt");

        var home = SubordinateConfig.ResolveHome();
        var config = SubordinateConfig.Load(home);
        var (basePrompt, _) = SubordinateConfig.LoadBasePrompt(home);
        var (alias, model, provider) = config.ResolveModel(commandLine.Single("model"));
        SubordinateConfig.CreateProvider(model.Provider, provider, model);

        var shellToken = commandLine.Single("shell") ?? config.Defaults.Shell;
        if (!ShellKindNames.TryParse(shellToken, out var shellKind))
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, $"Unknown shell \"{shellToken}\". Use bash, powershell or pwsh.");
        }

        if (shellKind == ShellKind.Bash && ShellPaths.ToBashPath(workdir) == null)
        {
            throw new SubordinateException(ErrorCode.WorkdirMissing, $"The workdir {workdir} is not on a drive letter, which the bash shell requires.");
        }

        var shell = ShellLocator.Locate(shellKind, config.Shells);
        var now = DateTimeOffset.UtcNow;

        var record = new SessionRecord
        {
            Name = name,
            Workdir = workdir,
            CreatedAt = now,
            Shell = new ShellRecord
            {
                Kind = shell.Kind.ToToken(),
                Dialect = shell.Kind.DialectName(),
                ExecutablePath = shell.ExecutablePath,
                Version = shell.Version
            },
            Model = new ModelRecord { Alias = alias, Provider = model.Provider, ModelId = model.Model },
            Policy = policy!.Tokens.ToList(),
            Limits = new LimitsRecord
            {
                MaxTurns = commandLine.Integer("max-turns", 1, 10_000) ?? config.Defaults.MaxTurns,
                MaxMinutes = commandLine.Integer("max-minutes", 1, 24 * 60) ?? config.Defaults.MaxMinutes,
                ShellTimeoutSeconds = commandLine.Integer("shell-timeout", 1, ShellToolHandler.MaxTimeoutSeconds) ?? config.Defaults.ShellTimeoutSeconds
            },
            State = SessionStates.Starting
        };

        var store = new SessionStore(home);
        var paths = store.Create(record);

        using (var sessionLock = SessionLock.AcquireWithRetry(paths, TimeSpan.FromSeconds(5)))
        {
            record.Transcript = store.AppendTranscript(paths, record.Transcript,
            [
                new ChatMessage { Role = ChatRole.System, Contents = [new TextContent { Text = SubordinateAgent.BuildSystemPrompt(basePrompt, workdir, shellKind, policy!, supervisorInstructions) }] },
                new ChatMessage { Role = ChatRole.User, Contents = [new TextContent { Text = prompt }] }
            ]);
            store.Save(paths, record);
        }

        return await LaunchAsync("start", config, store, paths, commandLine.Has("wait"));
    }

    public static async Task<CommandResult> ResumeAsync(CommandLine commandLine)
    {
        var home = SubordinateConfig.ResolveHome();
        var store = new SessionStore(home);
        var (paths, _) = SessionResolver.Resolve(store, commandLine.Required("name"), commandLine.Single("workdir"));
        var config = SubordinateConfig.Load(home);
        var message = ReadText(commandLine, "message", "message-file", ErrorCode.InvalidArguments, "message");

        using (var sessionLock = SessionLock.TryAcquire(paths) ?? throw new SubordinateException(ErrorCode.SessionBusy, "The session is running. Use 'subordinate status' to follow it, or 'subordinate stop' to stop it."))
        {
            var record = store.Load(paths);
            var ledger = new PendingLedger(record);
            var decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var decisions = 0;

            void Decide(string id, Action<PendingRecord> apply)
            {
                var item = ledger.FindById(id) ?? throw new SubordinateException(ErrorCode.BadApproval, $"Unknown pending id \"{id}\". Pending ids are listed in pending.json.");

                if (item.Status != PendingStatuses.Pending)
                {
                    throw new SubordinateException(ErrorCode.BadApproval, $"Pending item {item.Id} was already {item.Status}.");
                }

                if (!decided.Add(item.Id))
                {
                    throw new SubordinateException(ErrorCode.BadApproval, $"Pending item {item.Id} was given more than one decision.");
                }

                apply(item);
                decisions++;
            }

            foreach (var id in SplitIds(commandLine.All("approve")))
            {
                Decide(id, item => ledger.Approve(item, ApprovalScope.Once, null));
            }

            foreach (var id in SplitIds(commandLine.All("approve-session")))
            {
                Decide(id, item => ledger.Approve(item, ApprovalScope.Session, null));
            }

            foreach (var value in commandLine.All("deny"))
            {
                var separator = value.IndexOf(':');
                var id = (separator < 0 ? value : value[..separator]).Trim();
                var reason = separator < 0 ? null : value[(separator + 1)..].Trim();
                Decide(id, item => ledger.Deny(item, string.IsNullOrWhiteSpace(reason) ? null : reason));
            }

            var liveState = SessionStates.IsActive(record.State) ? SessionStates.Interrupted : record.State;

            if (decisions == 0 && message == null && liveState is SessionStates.Finished or SessionStates.AwaitingApproval)
            {
                throw new SubordinateException(ErrorCode.NothingToResume, liveState == SessionStates.AwaitingApproval
                    ? $"The session is waiting for {record.PendingCount} approval(s). Pass --approve, --approve-session or --deny with ids from pending.json, or --message."
                    : "The session finished. Pass --message with a follow-up to continue it.");
            }

            SessionRunner.Validate(store, paths, record);

            if (message != null)
            {
                record.QueuedMessage = record.QueuedMessage == null ? message : record.QueuedMessage + "\n\n" + message;
            }

            if (commandLine.Integer("max-turns", 1, 10_000) is { } maxTurns)
            {
                record.Limits.MaxTurns = maxTurns;
            }

            if (commandLine.Integer("max-minutes", 1, 24 * 60) is { } maxMinutes)
            {
                record.Limits.MaxMinutes = maxMinutes;
            }

            store.Save(paths, record);
        }

        return await LaunchAsync("resume", config, store, paths, commandLine.Has("wait"));
    }

    private static async Task<CommandResult> LaunchAsync(string command, SubordinateConfig config, SessionStore store, SessionPaths paths, bool wait)
    {
        string runId;
        SessionRecord record;
        var sessionLock = SessionLock.AcquireWithRetry(paths, TimeSpan.FromSeconds(5));

        try
        {
            record = store.Load(paths);
            runId = store.CreateRunDirectory(paths, record.Runs.Count + 1, config.Defaults.KeepRuns);
            record.Runs.Add(new RunRecord { RunId = runId, Kind = command, StartedAt = DateTimeOffset.UtcNow, State = SessionStates.Starting });
            record.State = SessionStates.Starting;
            record.Error = null;
            store.Save(paths, record);

            if (wait)
            {
                var runner = RunFactory.CreateRunner(config, store, paths, record, runId);
                var outcome = await runner.RunAsync(LimitsFor(record), CancellationToken.None);
                record = store.Load(paths);
                return FinishedResult(command, paths, record, outcome.State);
            }
        }
        finally
        {
            sessionLock.Dispose();
        }

        var executable = Environment.ProcessPath ?? throw new SubordinateException(ErrorCode.InternalError, "The path of the subordinate executable is unknown.");
        var arguments = new List<string>();

        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add(typeof(SessionCommands).Assembly.Location);
        }

        arguments.AddRange(["__worker", "--session-dir", paths.Directory, "--run-id", runId]);

        int processId;
        try
        {
            processId = DetachedProcess.Start(executable, arguments, record.Workdir);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MarkLaunchFailed(store, paths, runId, $"The background worker could not be started: {ex.Message}");
            throw new SubordinateException(ErrorCode.InternalError, $"The background worker could not be started: {ex.Message}");
        }

        var runDirectory = paths.RunDirectory(runId);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            var status = SessionResolver.ReadStatus(runDirectory);

            if (status is { } element && element.TryGetProperty("pid", out var pid) && pid.GetInt32() == processId)
            {
                break;
            }

            if (!IsProcessAlive(processId))
            {
                break;
            }

            await Task.Delay(100);
        }

        record = store.Load(paths);
        var state = SessionResolver.LiveState(paths, record);

        if (state == SessionStates.Interrupted && record.CurrentRun?.RunId == runId && record.CurrentRun.State == SessionStates.Starting)
        {
            throw new SubordinateException(ErrorCode.InternalError, $"The background worker (pid {processId}) exited before it started the run. Check {runDirectory} for details.");
        }

        var envelope = Envelope.ForSession(command, record, runDirectory, writer => writer.WriteNumber("pid", processId), state);
        var summary = SessionStates.IsActive(state)
            ? $"Session \"{record.Name}\" is running in the background (pid {processId}).\nFollow it with: subordinate status --name \"{record.Name}\"\nBlock until it stops with: subordinate wait --name \"{record.Name}\""
            : DescribeOutcome(record, state, runDirectory);

        return new CommandResult(envelope, summary);
    }

    public static async Task<int> WorkerAsync(CommandLine commandLine)
    {
        var paths = new SessionPaths(commandLine.Required("session-dir"));
        var runId = commandLine.Required("run-id");
        var store = new SessionStore(Path.GetDirectoryName(Path.GetDirectoryName(paths.Directory)!)!);

        try
        {
            using var sessionLock = SessionLock.AcquireWithRetry(paths, TimeSpan.FromSeconds(15));
            var record = store.Load(paths);

            if (record.CurrentRun?.RunId != runId)
            {
                return ErrorCode.StateConflict.ToExitCode();
            }

            var config = SubordinateConfig.Load(store.Home);
            var runner = RunFactory.CreateRunner(config, store, paths, record, runId);
            var outcome = await runner.RunAsync(LimitsFor(record), CancellationToken.None);
            return outcome.State == SessionStates.Failed ? ErrorCode.RunFailed.ToExitCode() : 0;
        }
        catch (SubordinateException ex)
        {
            MarkLaunchFailed(store, paths, runId, ex.Message, ex.Code);
            return ex.Code.ToExitCode();
        }
        catch (Exception ex)
        {
            MarkLaunchFailed(store, paths, runId, $"The worker failed: {ex.GetType().Name}: {ex.Message}");
            return ErrorCode.InternalError.ToExitCode();
        }
    }

    public static CommandResult Status(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var (paths, record) = SessionResolver.Resolve(store, commandLine.Required("name"), commandLine.Single("workdir"));
        var state = SessionResolver.LiveState(paths, record);
        var runDirectory = SessionResolver.CurrentRunDirectory(paths, record);
        var status = SessionResolver.ReadStatus(runDirectory);

        var envelope = Envelope.ForSession("status", record, runDirectory, writer => WriteProgress(writer, record, status), state);
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Session \"{record.Name}\" ({record.Workdir}): {state}.\n");

        if (status is { } element)
        {
            builder.Append(CultureInfo.InvariantCulture, $"Run {record.CurrentRun?.RunId}: {Read(element, "model_calls")} model calls, {Read(element, "tool_calls")} tool calls, {Read(element, "elapsed_seconds")} s elapsed, last activity: {Read(element, "last_activity")}.\n");
        }

        if (record.PendingCount > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{record.PendingCount} pending approval(s); see {RunArtifacts.PendingFile(runDirectory ?? paths.Directory)}.\n");
        }

        return new CommandResult(envelope, builder.ToString().TrimEnd('\n'));
    }

    public static async Task<CommandResult> WaitAsync(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var (paths, record) = SessionResolver.Resolve(store, commandLine.Required("name"), commandLine.Single("workdir"));
        var timeout = commandLine.Integer("timeout-seconds", 1, 7 * 24 * 3600);
        var deadline = timeout.HasValue ? DateTime.UtcNow.AddSeconds(timeout.Value) : DateTime.MaxValue;
        var state = SessionResolver.LiveState(paths, record);

        while (SessionStates.IsActive(state) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);
            record = store.Load(paths);
            state = SessionResolver.LiveState(paths, record);
        }

        return FinishedResult("wait", paths, record, state);
    }

    public static CommandResult Result(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var (paths, record) = SessionResolver.Resolve(store, commandLine.Required("name"), commandLine.Single("workdir"));
        var state = SessionResolver.LiveState(paths, record);
        var runDirectory = SessionResolver.CurrentRunDirectory(paths, record);
        var result = FinishedResult("result", paths, record, state);
        var text = runDirectory != null && File.Exists(RunArtifacts.TextFile(runDirectory)) ? File.ReadAllText(RunArtifacts.TextFile(runDirectory), Encoding.UTF8) : string.Empty;

        return result with { Summary = text.Length > 0 ? text : result.Summary };
    }

    public static CommandResult Stop(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var (paths, record) = SessionResolver.Resolve(store, commandLine.Required("name"), commandLine.Single("workdir"));
        var state = SessionResolver.LiveState(paths, record);

        if (!SessionStates.IsActive(state))
        {
            return new CommandResult(Envelope.ForSession("stop", record, SessionResolver.CurrentRunDirectory(paths, record), null, state), $"Session \"{record.Name}\" is not running ({state}).");
        }

        File.WriteAllText(paths.StopFile, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        return new CommandResult(Envelope.ForSession("stop", record, SessionResolver.CurrentRunDirectory(paths, record), writer => writer.WriteBoolean("stop_requested", true), state),
            $"Stop requested for \"{record.Name}\". The run stops at its next step and stays resumable. Use 'subordinate wait' to block until it has stopped.");
    }

    public static CommandResult Delete(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var (paths, record) = SessionResolver.Resolve(store, commandLine.Required("name"), commandLine.Single("workdir"));

        using (var sessionLock = SessionLock.TryAcquire(paths) ?? throw new SubordinateException(ErrorCode.SessionBusy, "The session is running. Stop it before deleting it."))
        {
        }

        Directory.Delete(paths.Directory, true);
        return new CommandResult(new Envelope { Command = "delete", Session = record.Name, Workdir = record.Workdir, State = "deleted" }, $"Deleted session \"{record.Name}\" ({record.Workdir}).");
    }

    public static CommandResult List(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var filter = commandLine.Single("workdir") is { } workdir ? SessionStore.NormalizeWorkdir(workdir) : null;
        var rows = new List<(SessionPaths Paths, SessionRecord Record, string State)>();

        foreach (var paths in store.Enumerate())
        {
            SessionRecord record;
            try
            {
                record = store.Load(paths);
            }
            catch (SubordinateException)
            {
                continue;
            }

            if (filter != null && !string.Equals(record.Workdir, filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rows.Add((paths, record, SessionResolver.LiveState(paths, record)));
        }

        rows.Sort((left, right) => right.Record.UpdatedAt.CompareTo(left.Record.UpdatedAt));

        var envelope = new Envelope
        {
            Command = "list",
            Extra = writer =>
            {
                writer.WriteNumber("count", rows.Count);
                writer.WriteStartArray("sessions");

                foreach (var (paths, record, state) in rows)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", record.Name);
                    writer.WriteString("workdir", record.Workdir);
                    writer.WriteString("state", state);
                    writer.WriteNumber("pending_count", record.PendingCount);
                    writer.WriteString("updated_at", RunArtifacts.FormatTime(record.UpdatedAt));
                    writer.WriteString("model", record.Model.Alias);
                    writer.WriteString("shell", record.Shell.Kind);
                    writer.WriteStringArray("policy", record.Policy);
                    writer.WriteNullableString("run_dir", SessionResolver.CurrentRunDirectory(paths, record));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
        };

        var builder = new StringBuilder();
        if (rows.Count == 0)
        {
            builder.Append("No sessions.");
        }

        foreach (var (_, record, state) in rows)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{record.Name,-28} {state,-18} pending={record.PendingCount,-3} {RunArtifacts.FormatTime(record.UpdatedAt)}  {record.Workdir}\n");
        }

        return new CommandResult(envelope, builder.ToString().TrimEnd('\n'));
    }

    public static CommandResult Prune(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var days = commandLine.Integer("older-than-days", 0, 3650) ?? 14;
        var dryRun = commandLine.Has("dry-run");
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var removed = new List<string>();

        foreach (var paths in store.Enumerate().ToList())
        {
            SessionRecord record;
            try
            {
                record = store.Load(paths);
            }
            catch (SubordinateException)
            {
                continue;
            }

            if (record.UpdatedAt > cutoff)
            {
                continue;
            }

            using (var sessionLock = SessionLock.TryAcquire(paths))
            {
                if (sessionLock == null)
                {
                    continue;
                }
            }

            removed.Add($"{record.Name} ({record.Workdir})");

            if (!dryRun)
            {
                try
                {
                    Directory.Delete(paths.Directory, true);
                }
                catch (IOException)
                {
                    removed[^1] += " [could not delete]";
                }
            }
        }

        var envelope = new Envelope
        {
            Command = "prune",
            Extra = writer =>
            {
                writer.WriteBoolean("dry_run", dryRun);
                writer.WriteNumber("older_than_days", days);
                writer.WriteStringArray("removed", removed);
            }
        };

        var summary = removed.Count == 0
            ? $"No sessions older than {days} days."
            : $"{(dryRun ? "Would remove" : "Removed")} {removed.Count} session(s):\n" + string.Join("\n", removed);
        return new CommandResult(envelope, summary);
    }

    private static CommandResult FinishedResult(string command, SessionPaths paths, SessionRecord record, string state)
    {
        var runDirectory = SessionResolver.CurrentRunDirectory(paths, record);
        var errorRecord = record.Error;
        var envelope = Envelope.ForSession(command, record, runDirectory, writer =>
        {
            writer.WriteNumber("model_calls", record.Counters.ModelCalls);
            writer.WriteNumber("tool_calls", record.Counters.ToolCalls);
            writer.WriteStringArray("files_written", record.FilesWritten);

            if (errorRecord != null)
            {
                writer.WriteStartObject("run_error");
                writer.WriteString("code", errorRecord.Code);
                writer.WriteString("message", errorRecord.Message);
                writer.WriteEndObject();
            }
        }, state);

        var exitCode = state == SessionStates.Failed ? ErrorCode.RunFailed.ToExitCode() : 0;
        return new CommandResult(envelope, DescribeOutcome(record, state, runDirectory), exitCode);
    }

    private static string DescribeOutcome(SessionRecord record, string state, string? runDirectory)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"Session \"{record.Name}\": {state}.");

        switch (state)
        {
            case SessionStates.Finished:
            {
                builder.Append(" The final answer follows and is also in result.md.");
                break;
            }
            case SessionStates.AwaitingApproval:
            {
                builder.Append(CultureInfo.InvariantCulture, $" {record.PendingCount} call(s) need a decision:\n");

                foreach (var item in record.Pending.Where(item => item.Status == PendingStatuses.Pending))
                {
                    builder.Append(CultureInfo.InvariantCulture, $"  {item.Id}  {item.Tool,-10} {Shorten(item.Summary, 90)}  [missing: {string.Join(",", item.Missing)}; scope: {item.ProposedScope}{(item.NeedsHuman ? "; NEEDS HUMAN" : string.Empty)}]\n");
                }

                builder.Append("Decide with: subordinate resume --name \"").Append(record.Name).Append("\" --approve <id> --deny <id>:<reason>");
                break;
            }
            case SessionStates.LimitReached:
            case SessionStates.Stopped:
            case SessionStates.Interrupted:
            {
                builder.Append(" The session can be resumed with 'subordinate resume'.");
                break;
            }
            case SessionStates.Failed:
            {
                builder.Append(CultureInfo.InvariantCulture, $" {record.Error?.Message}");
                break;
            }
        }

        if (runDirectory != null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\nRun directory: {runDirectory}");
        }

        if (!string.IsNullOrWhiteSpace(record.FinalText))
        {
            var heading = state == SessionStates.Finished ? "Final answer" : "Last message from the subagent";
            var text = record.FinalText.Trim();
            builder.Append("\n\n").Append(heading).Append(":\n");

            if (text.Length > MaxInlineAnswerCharacters)
            {
                builder.Append(text, 0, MaxInlineAnswerCharacters);
                var textFile = runDirectory != null ? Path.Combine(runDirectory, RunArtifacts.TextFileName) : "result.md";
                builder.Append(CultureInfo.InvariantCulture, $"\n[Truncated at {MaxInlineAnswerCharacters} of {text.Length} characters. The full text is in {textFile}.]");
            }
            else
            {
                builder.Append(text);
            }
        }

        return builder.ToString();
    }

    private static void WriteProgress(Utf8JsonWriter writer, SessionRecord record, JsonElement? status)
    {
        writer.WriteStartObject("progress");
        writer.WriteNumber("model_calls", record.Counters.ModelCalls);
        writer.WriteNumber("tool_calls", record.Counters.ToolCalls);

        if (status is { } element)
        {
            foreach (var property in new[] { "elapsed_seconds", "last_activity", "updated_at", "pid", "input_tokens", "output_tokens" })
            {
                if (element.TryGetProperty(property, out var value))
                {
                    writer.WritePropertyName(property);
                    value.WriteTo(writer);
                }
            }
        }

        writer.WriteEndObject();
    }

    private static string Read(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) ? value.ToString() : "?";
    }

    private static RunLimits LimitsFor(SessionRecord record)
    {
        return new RunLimits
        {
            MaxModelCalls = record.Limits.MaxTurns > 0 ? record.Limits.MaxTurns : 100,
            MaxDuration = TimeSpan.FromMinutes(record.Limits.MaxMinutes > 0 ? record.Limits.MaxMinutes : 60)
        };
    }

    private static void MarkLaunchFailed(SessionStore store, SessionPaths paths, string runId, string message, ErrorCode code = ErrorCode.InternalError)
    {
        try
        {
            using var sessionLock = SessionLock.TryAcquire(paths);

            if (sessionLock == null)
            {
                return;
            }

            var record = store.Load(paths);
            var run = record.Runs.FirstOrDefault(candidate => candidate.RunId == runId);

            if (run != null && run.State == SessionStates.Starting)
            {
                run.State = SessionStates.Failed;
                run.EndedAt = DateTimeOffset.UtcNow;
                record.State = SessionStates.Failed;
                record.Error = new ErrorRecord { Code = code.ToToken(), Message = message };
                store.Save(paths, record);
                RunArtifacts.WriteResult(paths.RunDirectory(runId), record, run, $"The run could not start: {message}");
                RunArtifacts.WritePending(paths.RunDirectory(runId), record);
            }
        }
        catch (Exception ex) when (ex is IOException or SubordinateException or UnauthorizedAccessException)
        {
        }
    }

    private static string? ReadText(CommandLine commandLine, string textOption, string fileOption, ErrorCode errorCode, string label)
    {
        var text = commandLine.Single(textOption);
        var file = commandLine.Single(fileOption);

        if (text != null && file != null)
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, $"Pass either --{textOption} or --{fileOption}, not both.");
        }

        if (file != null)
        {
            try
            {
                if (file == "-")
                {
                    using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                    text = input.ReadToEnd();
                }
                else
                {
                    text = File.ReadAllText(file, Encoding.UTF8);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SubordinateException(errorCode, $"The {label} file could not be read: {ex.Message}");
            }
        }

        if (text == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SubordinateException(errorCode, $"The {label} is empty.");
        }

        if (text.Length > MaxPromptCharacters)
        {
            throw new SubordinateException(errorCode, $"The {label} is larger than {MaxPromptCharacters} characters.");
        }

        return text;
    }

    private static IEnumerable<string> SplitIds(IReadOnlyList<string> values)
    {
        foreach (var value in values)
        {
            foreach (var id in value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return id;
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string Shorten(string text, int length)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= length ? singleLine : singleLine[..length] + "...";
    }
}
