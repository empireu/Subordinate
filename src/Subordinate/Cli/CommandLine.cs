using Subordinate.Core;

namespace Subordinate.Cli;

/// <summary>
///     A parsed command line: the subcommand, its options and its switches.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _switches = new(StringComparer.Ordinal);

    private CommandLine(string command)
    {
        Command = command;
    }

    public string Command { get; }

    /// <summary>
    ///     Parses arguments against the options and switches a command accepts. Unknown options fail instead of being ignored.
    /// </summary>
    public static CommandLine Parse(IReadOnlyList<string> arguments, IReadOnlySet<string> options, IReadOnlySet<string> switches)
    {
        var commandLine = new CommandLine(arguments.Count > 0 ? arguments[0] : "help");

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new SubordinateException(ErrorCode.InvalidArguments, $"Unexpected argument \"{argument}\". Options are written as --name value.");
            }

            var name = argument[2..];
            string? inlineValue = null;
            var equals = name.IndexOf('=');

            if (equals >= 0)
            {
                inlineValue = name[(equals + 1)..];
                name = name[..equals];
            }

            if (switches.Contains(name))
            {
                if (inlineValue != null)
                {
                    throw new SubordinateException(ErrorCode.InvalidArguments, $"--{name} does not take a value.");
                }

                commandLine._switches.Add(name);
                continue;
            }

            if (!options.Contains(name))
            {
                var hint = name == "policy" && commandLine.Command == "resume"
                    ? " The policy is fixed when a session starts and cannot be changed on resume; approve individual items by id instead."
                    : string.Empty;
                throw new SubordinateException(ErrorCode.InvalidArguments, $"Unknown option --{name} for '{commandLine.Command}'.{hint} Run 'subordinate help' for usage.");
            }

            var value = inlineValue;

            if (value == null)
            {
                if (index + 1 >= arguments.Count)
                {
                    throw new SubordinateException(ErrorCode.InvalidArguments, $"--{name} needs a value.");
                }

                value = arguments[++index];
            }

            if (!commandLine._options.TryGetValue(name, out var values))
            {
                values = [];
                commandLine._options[name] = values;
            }

            values.Add(value);
        }

        return commandLine;
    }

    public bool Has(string switchName)
    {
        return _switches.Contains(switchName);
    }

    public string? Single(string option)
    {
        if (!_options.TryGetValue(option, out var values))
        {
            return null;
        }

        if (values.Count > 1)
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, $"--{option} was given more than once.");
        }

        return values[0];
    }

    public string Required(string option)
    {
        return Single(option) ?? throw new SubordinateException(ErrorCode.InvalidArguments, $"--{option} is required for '{Command}'.");
    }

    public IReadOnlyList<string> All(string option)
    {
        return _options.TryGetValue(option, out var values) ? values : [];
    }

    public int? Integer(string option, int minimum, int maximum)
    {
        var text = Single(option);

        if (text == null)
        {
            return null;
        }

        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, $"--{option} must be a whole number from {minimum} to {maximum}.");
        }

        return value;
    }
}
