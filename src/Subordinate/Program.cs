using System.Text;
using Subordinate.Cli;
using Subordinate.Core;

namespace Subordinate;

internal static class Program
{
    private static readonly Dictionary<string, (string[] Options, string[] Switches)> Commands = new(StringComparer.Ordinal)
    {
        ["start"] = (["name", "workdir", "policy", "prompt", "prompt-file", "system-prompt", "system-prompt-file", "shell", "model", "max-turns", "max-minutes", "shell-timeout"], ["wait"]),
        ["resume"] = (["name", "workdir", "approve", "approve-session", "deny", "message", "message-file", "max-turns", "max-minutes"], ["wait"]),
        ["status"] = (["name", "workdir"], []),
        ["wait"] = (["name", "workdir", "timeout-seconds"], []),
        ["result"] = (["name", "workdir", "output"], []),
        ["close"] = (["name", "workdir"], []),
        ["stop"] = (["name", "workdir"], []),
        ["delete"] = (["name", "workdir"], []),
        ["list"] = (["workdir"], []),
        ["prune"] = (["older-than-days"], ["dry-run"]),
        ["usage"] = (["since", "session", "model", "workdir"], []),
        ["classify"] = (["command", "shell", "workdir", "policy"], []),
        ["doctor"] = (["model"], []),
        ["init"] = ([], []),
        ["help"] = ([], []),
        ["__worker"] = (["session-dir", "run-id"], [])
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var commandName = args.Length > 0 ? args[0] : "help";

        if (commandName is "--help" or "-h" or "/?")
        {
            commandName = "help";
            args = ["help"];
        }

        try
        {
            if (!Commands.TryGetValue(commandName, out var definition))
            {
                throw new SubordinateException(ErrorCode.InvalidArguments, $"Unknown command \"{commandName}\". Run 'subordinate help' for usage.");
            }

            var commandLine = CommandLine.Parse(args.Length > 0 ? args : ["help"], definition.Options.ToHashSet(), definition.Switches.ToHashSet());

            if (commandName == "__worker")
            {
                return await SessionCommands.WorkerAsync(commandLine);
            }

            var result = commandName switch
            {
                "start" => await SessionCommands.StartAsync(commandLine),
                "resume" => await SessionCommands.ResumeAsync(commandLine),
                "status" => SessionCommands.Status(commandLine),
                "wait" => await SessionCommands.WaitAsync(commandLine),
                "result" => SessionCommands.Result(commandLine),
                "close" => SessionCommands.Close(commandLine),
                "stop" => SessionCommands.Stop(commandLine),
                "delete" => SessionCommands.Delete(commandLine),
                "list" => SessionCommands.List(commandLine),
                "prune" => SessionCommands.Prune(commandLine),
                "usage" => SessionCommands.Usage(commandLine),
                "classify" => UtilityCommands.Classify(commandLine),
                "doctor" => UtilityCommands.Doctor(commandLine),
                "init" => UtilityCommands.Init(commandLine),
                _ => UtilityCommands.Help()
            };

            Write(result.Envelope, result.Summary);
            return result.ExitCode;
        }
        catch (SubordinateException ex)
        {
            Write(Envelope.ForError(commandName, ex), "error: " + ex.Message);
            return ex.Code.ToExitCode();
        }
        catch (Exception ex)
        {
            var internalError = new SubordinateException(ErrorCode.InternalError, $"Unexpected failure: {ex.GetType().Name}: {ex.Message}");
            Write(Envelope.ForError(commandName, internalError), "error: " + internalError.Message);
            return ErrorCode.InternalError.ToExitCode();
        }
    }

    private static void Write(Envelope envelope, string summary)
    {
        var output = new StringBuilder();
        output.Append(envelope.ToJson()).Append('\n');

        if (!string.IsNullOrEmpty(summary))
        {
            output.Append(summary.ReplaceLineEndings("\n")).Append('\n');
        }

        using var stdout = Console.OpenStandardOutput();
        var bytes = new UTF8Encoding(false).GetBytes(output.ToString());
        stdout.Write(bytes);
        stdout.Flush();
    }
}
