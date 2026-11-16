using System.Globalization;
using System.Text;
using KnowledgeSystem.Agents.Orchestration;
using KnowledgeSystem.Agents.Orchestration.Tools;
using KnowledgeSystem.Agents.Tools;
using Subordinate.Core.Classification;
using Subordinate.Core.Platform;
using Subordinate.Core.Policy;
using Subordinate.Core.Running;
using Subordinate.Core.Shells;

namespace Subordinate.Core.Tools;

/// <summary>
///     Runs one shell command. Every call is classified and passes through the gate before it executes.
/// </summary>
public sealed class ShellToolHandler(AgentTool tool, StringArgument commandArgument, StringArgument workdirArgument, IntegerArgument timeoutArgument)
    : ToolHandler<SubordinateContext>.Plain(tool), IGatedTool
{
    public const int MaxTimeoutSeconds = 1800;

    public static void Register(AgentToolRegistry<SubordinateContext> registry, ShellKind shell)
    {
        var shellDescription = PlatformServices.Current.Shells.ToolDescription(shell);

        var shellTool = new ToolBuilder(ShellClassifier.ToolName)
            .WithDescription($"Runs a command in a fresh, non-interactive shell and returns its exit code, standard output and standard error. The shell is {shellDescription} Standard input is closed, so interactive programs fail. Prefer one simple command per call. Large output is truncated and the full output is saved to a file that read_file can open.")
            .WithRequiredStringArgument("command", "The command to run.", out var commandArg)
            .WithStringArgument("workdir", "The directory to run in, relative to the session workdir or absolute. Defaults to the session workdir.", out var workdirArg)
            .WithIntegerArgument("timeout_seconds", $"How long to wait before killing the command. Default 120, maximum {MaxTimeoutSeconds}.", out var timeoutArg)
            .Build();

        registry.RegisterTool(shellTool, new ShellToolHandler(shellTool, commandArg, workdirArg, timeoutArg));
    }

    public ActionClassification Classify(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        var command = commandArgument.GetValue(args);

        if (!TryResolveWorkingDirectory(args, environment, out var workingDirectory, out var error))
        {
            return new ActionClassification
            {
                Tool = ShellClassifier.ToolName,
                Summary = command,
                Units = [],
                SharedCapabilities = new HashSet<Capability> { Capability.Read, Capability.Unbounded },
                SharedReasons = [error]
            };
        }

        return ShellClassifier.Classify(command, environment.Shell, environment.CreateScope(workingDirectory));
    }

    public string Fingerprint(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        TryResolveWorkingDirectory(args, environment, out var workingDirectory, out _);
        return $"shell\n{workingDirectory}\n{commandArgument.GetValue(args)}";
    }

    public CallDescription Describe(ArgumentExtractionResult args, ToolEnvironment environment)
    {
        TryResolveWorkingDirectory(args, environment, out var workingDirectory, out _);
        var command = commandArgument.GetValue(args);

        return new CallDescription
        {
            Summary = command,
            Command = command,
            WorkingDirectory = workingDirectory
        };
    }

    public override async Task<ToolExecutionResult> ExecuteAsync(AgentRunner<SubordinateContext> runner, ArgumentExtractionResult args, CancellationToken cancellationToken)
    {
        var environment = runner.ExecutionContext.Environment;
        var command = commandArgument.GetValue(args);

        if (!TryResolveWorkingDirectory(args, environment, out var workingDirectory, out var error))
        {
            return Error(error);
        }

        if (!Directory.Exists(workingDirectory))
        {
            return Error($"The working directory {workingDirectory} does not exist.");
        }

        var timeoutSeconds = environment.DefaultShellTimeoutSeconds;
        if (timeoutArgument.TryGetValue(args, out var requested) && requested > 0)
        {
            timeoutSeconds = Math.Min(requested, MaxTimeoutSeconds);
        }

        var result = await environment.Executor.ExecuteAsync(new CommandExecutionRequest
        {
            Command = command,
            WorkingDirectory = workingDirectory,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            OutputDirectory = Path.Combine(environment.OutputDirectory, "outputs"),
            OutputStem = environment.NextOutputStem()
        }, cancellationToken);

        if (result.StartError != null)
        {
            return Error(result.StartError);
        }

        var text = Format(result, timeoutSeconds);

        if (result.Cancelled)
        {
            return Error("The command was interrupted because the session was stopped. It may have partially run.\n" + text);
        }

        return result.TimedOut || result.ExitCode != 0 ? Error(text) : Success(text);
    }

    private static string Format(CommandExecutionResult result, int timeoutSeconds)
    {
        var builder = new StringBuilder();

        if (result.TimedOut)
        {
            builder.Append(CultureInfo.InvariantCulture, $"The command timed out after {timeoutSeconds} seconds and was killed.\n");
        }
        else if (result.ExitCode.HasValue)
        {
            builder.Append(CultureInfo.InvariantCulture, $"exit_code: {result.ExitCode.Value}\n");
        }

        builder.Append(CultureInfo.InvariantCulture, $"duration_seconds: {result.Duration.TotalSeconds:F1}\n");
        AppendStream(builder, "stdout", result.StandardOutput);
        AppendStream(builder, "stderr", result.StandardError);

        return builder.ToString().TrimEnd('\n');
    }

    private static void AppendStream(StringBuilder builder, string name, CapturedOutput? output)
    {
        if (output == null || output.TotalCharacters == 0)
        {
            return;
        }

        builder.Append(name).Append(":\n").Append(output.Text.TrimEnd('\n')).Append('\n');

        if (output.IsTruncated)
        {
            builder.Append(CultureInfo.InvariantCulture, $"[{name} was {output.TotalCharacters} characters and was truncated. The full output is in {output.FullOutputPath}]\n");
        }
    }

    private bool TryResolveWorkingDirectory(ArgumentExtractionResult args, ToolEnvironment environment, out string workingDirectory, out string error)
    {
        workingDirectory = environment.Workdir;
        error = string.Empty;

        var requested = workdirArgument.GetValueOrNull(args);

        if (string.IsNullOrWhiteSpace(requested))
        {
            return true;
        }

        var resolved = ShellPaths.Resolve(requested, environment.Workdir);

        if (resolved.Kind != ResolvedPathKind.Local)
        {
            error = $"The workdir \"{requested}\" is not a local directory path.";
            return false;
        }

        workingDirectory = resolved.Path!;
        return true;
    }
}
