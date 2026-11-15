using System.Globalization;
using System.Text;
using Subordinate.Core;
using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;
using Subordinate.Core.Json;
using Subordinate.Core.Policy;
using Subordinate.Core.Running;
using Subordinate.Core.Sessions;
using Subordinate.Core.Shells;

namespace Subordinate.Cli;

internal static class UtilityCommands
{
    public static CommandResult Classify(CommandLine commandLine)
    {
        var command = commandLine.Required("command");
        var shellToken = commandLine.Single("shell") ?? "bash";

        if (!ShellKindNames.TryParse(shellToken, out var shell))
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, $"Unknown shell \"{shellToken}\". Use bash, powershell or pwsh.");
        }

        var workdir = SessionStore.NormalizeWorkdir(commandLine.Single("workdir") ?? Directory.GetCurrentDirectory());
        var policyValues = commandLine.All("policy").Count > 0 ? commandLine.All("policy") : ["readonly"];

        if (!PolicySet.TryParse(policyValues, out var policy, out var policyError))
        {
            throw new SubordinateException(ErrorCode.BadPolicy, policyError);
        }

        var classification = ShellClassifier.Classify(command, shell, new ClassificationScope { Workdir = workdir, WorkingDirectory = workdir });
        var decision = PolicyEvaluator.Evaluate(classification, policy!, []);
        var verdict = decision.IsAllowed ? "allow" : "defer";

        var envelope = new Envelope
        {
            Command = "classify",
            Workdir = workdir,
            Extra = writer =>
            {
                writer.WriteString("shell", shell.ToToken());
                writer.WriteStringArray("policy", policy!.Tokens);
                writer.WriteString("verdict", verdict);
                writer.WriteStringArray("capabilities", classification.AllCapabilities.ToTokens());
                writer.WriteStringArray("missing", decision.Missing.ToTokens());
                writer.WriteBoolean("needs_human", decision.NeedsHuman);
                writer.WriteString("proposed_scope", decision.ProposedScope == ApprovalScope.Session ? "session" : "once");
                writer.WriteStringArray("reasons", decision.IsAllowed ? classification.AllReasons : decision.Reasons);
                writer.WriteStartArray("units");

                foreach (var unit in classification.Units)
                {
                    writer.WriteStartObject();
                    writer.WriteString("display", unit.Display);
                    writer.WriteNullableString("key", unit.Key);
                    writer.WriteStringArray("capabilities", unit.Capabilities.ToTokens());
                    writer.WriteStringArray("paths", unit.Paths.Select(path => (path.IsWrite ? "write " : "read ") + path.Path.Display));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
        };

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{verdict.ToUpperInvariant()} under policy [{string.Join(", ", policy!.Tokens)}]: capabilities {string.Join(", ", classification.AllCapabilities.ToTokens())}");

        if (!decision.IsAllowed)
        {
            builder.Append(CultureInfo.InvariantCulture, $"; missing {string.Join(", ", decision.Missing.ToTokens())}; scope {(decision.ProposedScope == ApprovalScope.Session ? "session" : "once")}{(decision.NeedsHuman ? "; needs a human" : string.Empty)}");

            foreach (var reason in decision.Reasons)
            {
                builder.Append("\n  - ").Append(reason);
            }
        }

        return new CommandResult(envelope, builder.ToString());
    }

    public static CommandResult Init(CommandLine commandLine)
    {
        var home = SubordinateConfig.ResolveHome();
        var path = Path.Combine(home, SubordinateConfig.FileName);
        var promptPath = Path.Combine(home, SubordinateConfig.BasePromptFileName);
        Directory.CreateDirectory(home);

        var configCreated = !File.Exists(path);
        if (configCreated)
        {
            File.WriteAllText(path, SubordinateConfig.CreateTemplate(), new UTF8Encoding(false));
        }

        var promptCreated = !File.Exists(promptPath);
        if (promptCreated)
        {
            File.WriteAllText(promptPath, SubordinateAgent.DefaultBasePrompt.ReplaceLineEndings("\n") + "\n", new UTF8Encoding(false));
        }

        var summary = new StringBuilder();
        summary.Append(configCreated
            ? $"Created {path}. Set the model id, and set the environment variable named by api_key_env to your API key."
            : $"The configuration already exists at {path}.");
        summary.Append('\n').Append(promptCreated
            ? $"Created {promptPath} with the built-in base system prompt. Edit it to change the base prompt of new sessions."
            : $"The base system prompt already exists at {promptPath}.");

        return new CommandResult(new Envelope
        {
            Command = "init",
            Extra = writer =>
            {
                writer.WriteString("config_file", path);
                writer.WriteBoolean("created", configCreated);
                writer.WriteString("system_prompt_file", promptPath);
                writer.WriteBoolean("system_prompt_created", promptCreated);
            }
        }, summary.ToString());
    }

    public static CommandResult Doctor(CommandLine commandLine)
    {
        var home = SubordinateConfig.ResolveHome();
        var checks = new List<(string Name, bool Ok, string Detail)>();
        SubordinateConfig? config = null;

        try
        {
            config = SubordinateConfig.Load(home);
            checks.Add(("config", true, Path.Combine(home, SubordinateConfig.FileName)));
        }
        catch (SubordinateException ex)
        {
            checks.Add(("config", false, ex.Message));
        }

        if (config != null)
        {
            var requested = commandLine.Single("model");
            var aliases = requested != null
                ? new List<string> { requested }
                : config.Models.Keys.Order(StringComparer.Ordinal).ToList();

            if (aliases.Count == 0)
            {
                checks.Add(("model", false, "No models are configured."));
            }

            foreach (var alias in aliases)
            {
                try
                {
                    var (resolved, model, provider) = config.ResolveModel(alias);
                    SubordinateConfig.CreateProvider(model.Provider, provider, model);
                    var reasoning = string.IsNullOrWhiteSpace(model.ReasoningEffort) ? "default" : model.ReasoningEffort;
                    var isDefault = string.Equals(config.DefaultModel, resolved, StringComparison.Ordinal);
                    checks.Add(($"model:{alias}", true, $"{model.Model} via {model.Provider}, reasoning effort {reasoning}, API key present{(isDefault ? ", default" : string.Empty)}"));
                }
                catch (SubordinateException ex)
                {
                    checks.Add(($"model:{alias}", false, ex.Message));
                }
            }

            if (string.IsNullOrWhiteSpace(config.DefaultModel))
            {
                checks.Add(("default_model", false, "No default_model is set; pass --model to start."));
            }
        }

        foreach (var shell in new[] { ShellKind.Bash, ShellKind.WindowsPowerShell, ShellKind.PowerShell7 })
        {
            try
            {
                var info = ShellLocator.Locate(shell, config?.Shells ?? new ShellPathsEntry());
                checks.Add(($"shell:{shell.ToToken()}", true, $"{info.ExecutablePath} {info.Version}"));
            }
            catch (SubordinateException ex)
            {
                checks.Add(($"shell:{shell.ToToken()}", false, ex.Message));
            }
        }

        try
        {
            var (_, promptPath) = SubordinateConfig.LoadBasePrompt(home);
            checks.Add(("system_prompt", true, promptPath ?? "built-in default (no system-prompt.md)"));
        }
        catch (SubordinateException ex)
        {
            checks.Add(("system_prompt", false, ex.Message));
        }

        checks.Add(("commands", true, $"{CommandDatabase.Count} entries in the command database"));

        var envelope = new Envelope
        {
            Command = "doctor",
            Extra = writer =>
            {
                writer.WriteString("home", home);
                writer.WriteStartArray("checks");

                foreach (var (name, ok, detail) in checks)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WriteBoolean("ok", ok);
                    writer.WriteString("detail", detail);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
        };

        var summary = string.Join("\n", checks.Select(check => $"{(check.Ok ? "ok  " : "FAIL")} {check.Name}: {check.Detail}"));
        return new CommandResult(envelope, summary);
    }

    public static CommandResult Help()
    {
        return new CommandResult(new Envelope { Command = "help" }, HelpText);
    }

    public const string HelpText = """
        subordinate - start and resume headless coding subagents with a fixed permission policy

        Every command prints one JSON line on stdout first, then a human summary. Exit code 0 means the
        command worked, whatever state the session is in; see the README for the other exit codes.

          start   --name N --policy P [--policy P2] (--prompt-file F | --prompt-file - | --prompt TEXT)
                  [--system-prompt TEXT | --system-prompt-file F] [--workdir D] [--shell bash|powershell|pwsh] [--model ALIAS]
                  [--max-turns N] [--max-minutes M] [--shell-timeout S] [--wait]
          resume  --name N [--workdir D] [--approve ID,ID] [--approve-session ID] [--deny ID[:reason]]
                  [--message TEXT | --message-file F] [--max-turns N] [--max-minutes M] [--wait]
          status  --name N [--workdir D]
          wait    --name N[,N...] [--workdir D] [--timeout-seconds S]
          result  --name N [--workdir D] [--output FILE]
          close   --name N [--workdir D]
          stop    --name N [--workdir D]
          list    [--workdir D]
          delete  --name N [--workdir D]
          prune   [--older-than-days N] [--dry-run]
          usage   [--since TIME] [--session N] [--model ALIAS] [--workdir D]
          classify --command TEXT [--shell bash] [--workdir D] [--policy P]
          doctor  [--model ALIAS]
          init

        Policy tokens: readonly, build, edit, read, read-outside, write, write-outside, exec, network,
        cmd:<command words>. Comma separated; repeat --policy to add more. Fixed for the life of the session.

        Usage is appended to usage.jsonl in the Subordinate home and survives session deletion.
        --since accepts an ISO 8601 time or a relative duration such as 30m, 24h, 7d or 2w.
        """;
}
