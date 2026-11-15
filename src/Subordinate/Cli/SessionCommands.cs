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
            record.FinalText = null;
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
            builder.Append(CultureInfo.InvariantCulture, $"Run {record.CurrentRun?.RunId}: {Read(element, "run", "model_calls")} model calls, {Read(element, "run", "tool_calls")} tool calls, {Read(element, "run", "input_tokens")} input tokens ({Read(element, "run", "cached_input_tokens")} cached), {Read(element, "run", "output_tokens")} output tokens, {Read(element, "elapsed_seconds")} s elapsed, {Read(element, "turns_remaining")} turns remaining, last activity: {Read(element, "last_activity")}.\n");
        }

        if (record.PendingCount > 0)
        {
            var pendingFile = runDirectory != null ? RunArtifacts.PendingFile(runDirectory) : null;
            var pendingText = pendingFile != null && File.Exists(pendingFile)
                ? string.Create(CultureInfo.InvariantCulture, $"{record.PendingCount} pending approval(s); see {pendingFile}.\n")
                : string.Create(CultureInfo.InvariantCulture, $"{record.PendingCount} pending approval(s).\n");
            builder.Append(pendingText);
        }

        return new CommandResult(envelope, builder.ToString().TrimEnd('\n'));
    }

    public static async Task<CommandResult> WaitAsync(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var names = SplitNames(commandLine.All("name")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (names.Count == 0)
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, "--name is required for 'wait'.");
        }

        var workdir = commandLine.Single("workdir");
        var timeout = commandLine.Integer("timeout-seconds", 1, 7 * 24 * 3600);
        var deadline = timeout.HasValue ? DateTime.UtcNow.AddSeconds(timeout.Value) : DateTime.MaxValue;

        if (names.Count == 1)
        {
            var (paths, record) = SessionResolver.Resolve(store, names[0], workdir);
            var state = SessionResolver.LiveState(paths, record);

            while (SessionStates.IsActive(state) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
                record = store.Load(paths);
                state = SessionResolver.LiveState(paths, record);
            }

            return FinishedResult("wait", paths, record, state);
        }

        var targets = names.Select(name => SessionResolver.Resolve(store, name, workdir)).ToList();
        var states = targets.Select(target => SessionResolver.LiveState(target.Paths, target.Record)).ToList();

        while (states.Any(SessionStates.IsActive) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000);

            for (var index = 0; index < targets.Count; index++)
            {
                if (!SessionStates.IsActive(states[index]))
                {
                    continue;
                }

                var record = store.Load(targets[index].Paths);
                targets[index] = (targets[index].Paths, record);
                states[index] = SessionResolver.LiveState(targets[index].Paths, record);
            }
        }

        var envelope = new Envelope
        {
            Command = "wait",
            Workdir = workdir != null ? SessionStore.NormalizeWorkdir(workdir) : null,
            State = states.All(state => string.Equals(state, states[0], StringComparison.Ordinal)) ? states[0] : "mixed",
            PendingCount = targets.Sum(target => target.Record.PendingCount),
            Resumable = states.Any(RunArtifacts.IsResumable),
            Extra = writer =>
            {
                writer.WriteNumber("count", targets.Count);
                writer.WriteStartArray("sessions");

                for (var index = 0; index < targets.Count; index++)
                {
                    var (paths, record) = targets[index];
                    var state = states[index];
                    var runDirectory = SessionResolver.CurrentRunDirectory(paths, record);

                    writer.WriteStartObject();
                    writer.WriteString("session", record.Name);
                    writer.WriteString("workdir", record.Workdir);
                    writer.WriteString("state", state);
                    writer.WriteNullableString("run_id", record.CurrentRun?.RunId);
                    writer.WriteNullableString("run_dir", runDirectory);
                    writer.WriteNullableString("result_file", Envelope.ExistingFile(runDirectory, RunArtifacts.ResultFile));
                    writer.WriteNullableString("text_file", Envelope.ExistingFile(runDirectory, RunArtifacts.TextFile));
                    writer.WriteNullableString("pending_file", Envelope.ExistingFile(runDirectory, RunArtifacts.PendingFile));
                    writer.WriteNumber("pending_count", record.PendingCount);
                    writer.WriteBoolean("resumable", RunArtifacts.IsResumable(state));
                    writer.WriteBoolean("final_answer", record.FinalText != null);
                    WriteCounters(writer, "run", record.CurrentRun?.Counters ?? new CountersRecord());
                    WriteCounters(writer, "session_totals", record.Counters);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
        };

        var summary = string.Join("\n\n", targets.Select((target, index) => DescribeOutcome(target.Record, states[index], SessionResolver.CurrentRunDirectory(target.Paths, target.Record))));
        var exitCode = states.Any(state => state == SessionStates.Failed) ? ErrorCode.RunFailed.ToExitCode() : 0;
        return new CommandResult(envelope, summary, exitCode);
    }

    public static CommandResult Result(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var (paths, record) = SessionResolver.Resolve(store, commandLine.Required("name"), commandLine.Single("workdir"));
        var state = SessionResolver.LiveState(paths, record);
        var runDirectory = SessionResolver.CurrentRunDirectory(paths, record);
        var text = runDirectory != null && File.Exists(RunArtifacts.TextFile(runDirectory))
            ? File.ReadAllText(RunArtifacts.TextFile(runDirectory), Encoding.UTF8)
            : record.FinalText ?? string.Empty;

        string? outputFile = null;

        if (commandLine.Single("output") is { } output)
        {
            try
            {
                outputFile = Path.GetFullPath(output);
                File.WriteAllText(outputFile, text, new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new SubordinateException(ErrorCode.InvalidArguments, $"The output file {output} could not be written: {ex.Message}");
            }
        }

        var result = FinishedResult("result", paths, record, state, outputFile != null ? writer => writer.WriteString("output_file", outputFile) : null);

        if (outputFile != null)
        {
            return result with { Summary = string.Create(CultureInfo.InvariantCulture, $"Wrote {text.Length} characters to {outputFile}.") };
        }

        if (text.Length > MaxInlineAnswerCharacters)
        {
            var textFile = runDirectory != null ? RunArtifacts.TextFile(runDirectory) : "result.md";
            var summary = string.Create(CultureInfo.InvariantCulture, $"{text[..MaxInlineAnswerCharacters]}\n[Truncated at {MaxInlineAnswerCharacters} of {text.Length} characters. The full text is in {textFile}.]");
            return result with { Summary = summary };
        }

        return result with { Summary = text.Length > 0 ? text : result.Summary };
    }

    public static CommandResult Usage(CommandLine commandLine)
    {
        var home = SubordinateConfig.ResolveHome();
        var since = ParseSince(commandLine.Single("since"));
        var sessionFilter = commandLine.Single("session");
        var modelFilter = commandLine.Single("model");
        var workdirFilter = commandLine.Single("workdir") is { } workdir ? SessionStore.NormalizeWorkdir(workdir) : null;

        var totals = new UsageTotals();
        var byModel = new Dictionary<string, UsageTotals>(StringComparer.OrdinalIgnoreCase);
        var byWorkdir = new Dictionary<string, UsageTotals>(StringComparer.OrdinalIgnoreCase);
        var modelDetails = new Dictionary<string, (string Provider, string ModelId)>(StringComparer.OrdinalIgnoreCase);
        var seenRuns = new HashSet<(string Session, string Workdir, string RunId)>();
        var count = 0;
        var skipped = 0;
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        var path = UsageLog.FilePath(home);

        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                UsageEntry? entry;

                try
                {
                    entry = ParseUsageEntry(line);
                }
                catch (JsonException)
                {
                    skipped++;
                    continue;
                }

                if (entry == null)
                {
                    skipped++;
                    continue;
                }

                if (!seenRuns.Add((entry.Session, entry.Workdir, entry.RunId)))
                {
                    continue;
                }

                if (since.HasValue && entry.Timestamp < since.Value)
                {
                    continue;
                }

                if (sessionFilter != null && !string.Equals(entry.Session, sessionFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (modelFilter != null && !string.Equals(entry.ModelAlias, modelFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (workdirFilter != null && !string.Equals(entry.Workdir, workdirFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                count++;
                totals.Add(entry);
                first = first == null || entry.Timestamp < first ? entry.Timestamp : first;
                last = last == null || entry.Timestamp > last ? entry.Timestamp : last;
                modelDetails[entry.ModelAlias] = (entry.ModelProvider, entry.ModelId);

                if (!byModel.TryGetValue(entry.ModelAlias, out var modelTotals))
                {
                    modelTotals = new UsageTotals();
                    byModel[entry.ModelAlias] = modelTotals;
                }

                modelTotals.Add(entry);

                if (!byWorkdir.TryGetValue(entry.Workdir, out var workdirTotals))
                {
                    workdirTotals = new UsageTotals();
                    byWorkdir[entry.Workdir] = workdirTotals;
                }

                workdirTotals.Add(entry);
            }
        }

        var envelope = new Envelope
        {
            Command = "usage",
            Workdir = workdirFilter,
            Extra = writer =>
            {
                writer.WriteNullableString("since", since?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                writer.WriteNullableString("session_filter", sessionFilter);
                writer.WriteNullableString("model_filter", modelFilter);
                writer.WriteNullableString("workdir_filter", workdirFilter);
                writer.WriteNumber("count", count);
                writer.WriteNumber("skipped_lines", skipped);
                writer.WriteNullableString("first", first?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                writer.WriteNullableString("last", last?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                WriteUsageTotals(writer, "totals", totals);
                writer.WriteStartArray("models");

                foreach (var (alias, modelTotals) in byModel.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteStartObject();
                    writer.WriteString("model", alias);

                    if (modelDetails.TryGetValue(alias, out var details))
                    {
                        writer.WriteString("provider", details.Provider);
                        writer.WriteString("model_id", details.ModelId);
                    }

                    WriteUsageTotalsProperties(writer, modelTotals);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray("workdirs");

                foreach (var (workdir, workdirTotals) in byWorkdir.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteStartObject();
                    writer.WriteString("workdir", workdir);
                    WriteUsageTotalsProperties(writer, workdirTotals);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
        };

        var builder = new StringBuilder();

        if (count == 0)
        {
            builder.Append("No usage matches the filters.");
        }
        else
        {
            builder.Append(CultureInfo.InvariantCulture, $"{count} run(s), {totals.ModelCalls} model calls, {totals.ToolCalls} tool calls, {totals.InputTokens} input tokens ({totals.CachedInputTokens} cached), {totals.OutputTokens} output tokens, {FormatDuration(totals.DurationSeconds)}.\n");
            builder.Append("By model:\n");

            foreach (var (alias, modelTotals) in byModel.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(CultureInfo.InvariantCulture, $"  {alias}: {modelTotals.Runs} run(s), {modelTotals.ModelCalls} model calls, {modelTotals.InputTokens} input tokens ({modelTotals.CachedInputTokens} cached), {modelTotals.OutputTokens} output tokens\n");
            }
        }

        return new CommandResult(envelope, builder.ToString().TrimEnd('\n'));
    }

    public static CommandResult Close(CommandLine commandLine)
    {
        var store = new SessionStore(SubordinateConfig.ResolveHome());
        var (paths, _) = SessionResolver.Resolve(store, commandLine.Required("name"), commandLine.Single("workdir"));

        using (var sessionLock = SessionLock.TryAcquire(paths) ?? throw new SubordinateException(ErrorCode.SessionBusy, "The session is running. Stop it before closing it."))
        {
            var record = store.Load(paths);
            var ledger = new PendingLedger(record);
            ledger.Close();
            record.State = SessionStates.Finished;
            record.Error = null;
            record.QueuedMessage ??= "The session was closed by the supervisor. Pending items were dropped.";

            if (record.CurrentRun is { } run)
            {
                run.State = SessionStates.Finished;
                run.EndedAt ??= DateTimeOffset.UtcNow;
            }

            store.Save(paths, record);

            var runDirectory = SessionResolver.CurrentRunDirectory(paths, record);

            if (runDirectory != null)
            {
                RunArtifacts.WritePending(runDirectory, record);

                if (record.CurrentRun is { } currentRun)
                {
                    var text = record.FinalText ?? "The session was closed by the supervisor. Pending items were dropped.";
                    RunArtifacts.WriteResult(runDirectory, record, currentRun, text);
                    WriteClosedStatus(runDirectory, record, currentRun);
                }
            }

            var closed = record.Pending.Count(item => item.Status == PendingStatuses.Closed);
            var envelope = Envelope.ForSession("close", record, runDirectory, writer => writer.WriteNumber("closed_pending", closed), record.State);
            return new CommandResult(envelope, $"Closed session \"{record.Name}\". {closed} pending item(s) were dropped.");
        }
    }

    private static void WriteClosedStatus(string runDirectory, SessionRecord record, RunRecord run)
    {
        var endedAt = run.EndedAt ?? DateTimeOffset.UtcNow;

        RunArtifacts.WriteStatus(runDirectory, new StatusSnapshot
        {
            Session = record.Name,
            Workdir = record.Workdir,
            RunId = run.RunId,
            State = SessionStates.Finished,
            ProcessId = Environment.ProcessId,
            StartedAt = run.StartedAt,
            ElapsedSeconds = (endedAt - run.StartedAt).TotalSeconds,
            PendingCount = record.PendingCount,
            RunCounters = run.Counters,
            SessionCounters = record.Counters,
            TurnsRemaining = record.Limits.MaxTurns > 0 ? Math.Max(0, record.Limits.MaxTurns - run.Counters.ModelCalls) : 0,
            LastActivity = "closed by the supervisor"
        });
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
            File.Delete(paths.RecordFile);
            File.Delete(paths.TranscriptFile);
            File.Delete(paths.StopFile);

            if (Directory.Exists(paths.RunsDirectory))
            {
                Directory.Delete(paths.RunsDirectory, true);
            }
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

    private static CommandResult FinishedResult(string command, SessionPaths paths, SessionRecord record, string state, Action<Utf8JsonWriter>? extra = null)
    {
        var runDirectory = SessionResolver.CurrentRunDirectory(paths, record);
        var errorRecord = record.Error;
        var envelope = Envelope.ForSession(command, record, runDirectory, writer =>
        {
            WriteCounters(writer, "run", record.CurrentRun?.Counters ?? new CountersRecord());
            WriteCounters(writer, "session_totals", record.Counters);
            writer.WriteBoolean("final_answer", record.FinalText != null);
            writer.WriteStringArray("files_written", record.FilesWritten);
            extra?.Invoke(writer);

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
            case SessionStates.Starting:
            case SessionStates.Running:
            {
                builder.Append(" The session is still running. Call 'subordinate wait' again to keep blocking.");
                break;
            }
            case SessionStates.Finished:
            {
                builder.Append(record.FinalText != null
                    ? " The final answer follows and is also in result.md."
                    : " The run finished. See result.md for details.");
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

        builder.Append(CultureInfo.InvariantCulture, $"\nUsage: {record.Counters.ModelCalls} model calls, {record.Counters.InputTokens} input tokens ({record.Counters.CachedInputTokens} cached), {record.Counters.OutputTokens} output tokens.");

        if (runDirectory != null)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\nRun directory: {runDirectory}");
        }

        if (!SessionStates.IsActive(state) && !string.IsNullOrWhiteSpace(record.FinalText))
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

        if (status is { ValueKind: JsonValueKind.Object } element)
        {
            WriteStatusCounters(writer, element, "run", record.CurrentRun?.Counters ?? new CountersRecord());
            WriteStatusCounters(writer, element, "session_totals", record.Counters);

            foreach (var property in new[] { "elapsed_seconds", "last_activity", "updated_at", "pid", "turns_remaining" })
            {
                if (element.TryGetProperty(property, out var value))
                {
                    writer.WritePropertyName(property);
                    value.WriteTo(writer);
                }
            }
        }
        else
        {
            WriteCounters(writer, "run", record.CurrentRun?.Counters ?? new CountersRecord());
            WriteCounters(writer, "session_totals", record.Counters);
        }

        writer.WriteEndObject();
    }

    private static void WriteStatusCounters(Utf8JsonWriter writer, JsonElement status, string propertyName, CountersRecord fallback)
    {
        writer.WriteStartObject(propertyName);

        if (status.TryGetProperty(propertyName, out var counters) && counters.ValueKind == JsonValueKind.Object)
        {
            writer.WriteNumber("model_calls", GetInt32(counters, "model_calls"));
            writer.WriteNumber("tool_calls", GetInt32(counters, "tool_calls"));
            writer.WriteNumber("input_tokens", GetInt64(counters, "input_tokens"));
            writer.WriteNumber("cached_input_tokens", GetInt64(counters, "cached_input_tokens"));
            writer.WriteNumber("output_tokens", GetInt64(counters, "output_tokens"));
        }
        else
        {
            writer.WriteNumber("model_calls", fallback.ModelCalls);
            writer.WriteNumber("tool_calls", fallback.ToolCalls);
            writer.WriteNumber("input_tokens", fallback.InputTokens);
            writer.WriteNumber("cached_input_tokens", fallback.CachedInputTokens);
            writer.WriteNumber("output_tokens", fallback.OutputTokens);
        }

        writer.WriteEndObject();
    }

    private static void WriteCounters(Utf8JsonWriter writer, string propertyName, CountersRecord counters)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteNumber("model_calls", counters.ModelCalls);
        writer.WriteNumber("tool_calls", counters.ToolCalls);
        writer.WriteNumber("input_tokens", counters.InputTokens);
        writer.WriteNumber("cached_input_tokens", counters.CachedInputTokens);
        writer.WriteNumber("output_tokens", counters.OutputTokens);
        writer.WriteEndObject();
    }

    private static string Read(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) ? value.ToString() : "?";
    }

    private static string Read(JsonElement element, string objectName, string property)
    {
        return element.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty(property, out var value)
            ? value.ToString()
            : "?";
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

    private static IEnumerable<string> SplitNames(IReadOnlyList<string> values)
    {
        foreach (var value in values)
        {
            foreach (var name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return name;
            }
        }
    }

    private static DateTimeOffset? ParseSince(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();

        if (value.Length >= 2 && char.IsAsciiDigit(value[0]))
        {
            var unit = char.ToLowerInvariant(value[^1]);

            if (unit is 'm' or 'h' or 'd' or 'w' && int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            {
                if (amount > 100_000)
                {
                    throw new SubordinateException(ErrorCode.InvalidArguments, "--since is too far in the past.");
                }

                var duration = unit switch
                {
                    'm' => TimeSpan.FromMinutes(amount),
                    'h' => TimeSpan.FromHours(amount),
                    'd' => TimeSpan.FromDays(amount),
                    'w' => TimeSpan.FromDays(amount * 7),
                    _ => TimeSpan.Zero
                };

                return DateTimeOffset.UtcNow - duration;
            }
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return timestamp;
        }

        throw new SubordinateException(ErrorCode.InvalidArguments, "--since expects an ISO 8601 time such as 2026-11-01 or 2026-11-01T12:00:00Z, or a relative duration such as 30m, 24h, 7d or 2w.");
    }

    private static UsageEntry? ParseUsageEntry(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var timestampText = GetString(root, "timestamp");
        var runId = GetString(root, "run_id");
        var session = GetString(root, "session");
        var workdir = GetString(root, "workdir");
        var modelAlias = GetString(root, "model_alias");
        var modelProvider = GetString(root, "model_provider");
        var modelId = GetString(root, "model_id");

        if (timestampText == null || runId == null || session == null || workdir == null || modelAlias == null || modelProvider == null || modelId == null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return null;
        }

        return new UsageEntry(
            timestamp,
            runId,
            session,
            workdir,
            modelAlias,
            modelProvider,
            modelId,
            GetDouble(root, "duration_seconds"),
            GetInt32(root, "model_calls"),
            GetInt32(root, "tool_calls"),
            GetInt64(root, "input_tokens"),
            GetInt64(root, "cached_input_tokens"),
            GetInt64(root, "output_tokens"));
    }

    private static string? GetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int GetInt32(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    }

    private static long GetInt64(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }

    private static double GetDouble(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : 0;
    }

    private static void WriteUsageTotals(Utf8JsonWriter writer, string propertyName, UsageTotals totals)
    {
        writer.WriteStartObject(propertyName);
        WriteUsageTotalsProperties(writer, totals);
        writer.WriteEndObject();
    }

    private static void WriteUsageTotalsProperties(Utf8JsonWriter writer, UsageTotals totals)
    {
        writer.WriteNumber("runs", totals.Runs);
        writer.WriteNumber("model_calls", totals.ModelCalls);
        writer.WriteNumber("tool_calls", totals.ToolCalls);
        writer.WriteNumber("input_tokens", totals.InputTokens);
        writer.WriteNumber("cached_input_tokens", totals.CachedInputTokens);
        writer.WriteNumber("output_tokens", totals.OutputTokens);
        writer.WriteNumber("duration_seconds", Math.Round(totals.DurationSeconds, 1));
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds:F1} s");
        }

        if (seconds < 3600)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:F1} min");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:F1} h");
    }

    private sealed record UsageEntry(
        DateTimeOffset Timestamp,
        string RunId,
        string Session,
        string Workdir,
        string ModelAlias,
        string ModelProvider,
        string ModelId,
        double DurationSeconds,
        int ModelCalls,
        int ToolCalls,
        long InputTokens,
        long CachedInputTokens,
        long OutputTokens);

    private sealed class UsageTotals
    {
        public int Runs { get; private set; }

        public int ModelCalls { get; private set; }

        public int ToolCalls { get; private set; }

        public long InputTokens { get; private set; }

        public long CachedInputTokens { get; private set; }

        public long OutputTokens { get; private set; }

        public double DurationSeconds { get; private set; }

        public void Add(UsageEntry entry)
        {
            Runs++;
            ModelCalls += entry.ModelCalls;
            ToolCalls += entry.ToolCalls;
            InputTokens += entry.InputTokens;
            CachedInputTokens += entry.CachedInputTokens;
            OutputTokens += entry.OutputTokens;
            DurationSeconds += entry.DurationSeconds;
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
