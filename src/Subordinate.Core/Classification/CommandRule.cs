using ShellSyntaxTree;
using Subordinate.Core.Policy;

namespace Subordinate.Core.Classification;

/// <summary>
///     How a command treats its non-flag operands.
/// </summary>
public enum OperandAccess
{
    /// <summary>
    ///     Operands that look like paths are read.
    /// </summary>
    Read,
    /// <summary>
    ///     Operands that look like paths are written, created or deleted.
    /// </summary>
    Write,
    /// <summary>
    ///     Operands are data, never file system locations.
    /// </summary>
    Data
}

/// <summary>
///     The classification of one command identity in the command database.
/// </summary>
public sealed class CommandRule
{
    /// <summary>
    ///     Capabilities the command needs in addition to read access.
    /// </summary>
    public Capability[] Capabilities { get; init; } = [];

    public OperandAccess Operands { get; init; } = OperandAccess.Read;

    /// <summary>
    ///     True when the command can modify its working directory even without path operands, for example a build.
    /// </summary>
    public bool WritesWorkingDirectory { get; init; }

    /// <summary>
    ///     True for programs that execute code given as an operand or on standard input.
    /// </summary>
    public bool IsInterpreter { get; init; }

    /// <summary>
    ///     Rules for the second command word.
    /// </summary>
    public IReadOnlyDictionary<string, CommandRule>? Subcommands { get; init; }

    /// <summary>
    ///     The rule used when the command has subcommands but none was given.
    /// </summary>
    public CommandRule? WithoutSubcommand { get; init; }

    /// <summary>
    ///     The rule used for a subcommand missing from <see cref="Subcommands"/>. When null, the command is unclassified.
    /// </summary>
    public CommandRule? OtherSubcommand { get; init; }

    /// <summary>
    ///     Flags that add capabilities, such as sed -i. A two character flag also matches inside a cluster of short flags.
    /// </summary>
    public IReadOnlyDictionary<string, Capability[]>? FlagCapabilities { get; init; }

    /// <summary>
    ///     Flags whose value is a path the command writes, such as sort -o.
    /// </summary>
    public IReadOnlySet<string>? WriteValueFlags { get; init; }

    /// <summary>
    ///     Command specific logic applied after the generic rule.
    /// </summary>
    public Action<RuleContext>? Refine { get; init; }
}

/// <summary>
///     A view of one analyzed argument, for command specific refinement.
/// </summary>
public sealed class ArgumentView
{
    public required int Index { get; init; }

    public required string Raw { get; init; }

    /// <summary>
    ///     The statically proved value, or null when the value is not a single exact string.
    /// </summary>
    public required string? Value { get; init; }

    public required bool IsFlag { get; init; }

    public string Text => Value ?? Raw;
}

/// <summary>
///     The facts a command rule refines. Rules add capabilities with reasons, or mark arguments as write targets.
/// </summary>
public sealed class RuleContext
{
    private readonly List<(Capability Capability, string Reason)> _added = [];
    private readonly HashSet<int> _writeArguments = [];
    private readonly HashSet<int> _dataArguments = [];
    private readonly List<string> _nestedCommands = [];

    public required string Program { get; init; }

    /// <summary>
    ///     The command words, lowercased, or empty when the parser could not prove them.
    /// </summary>
    public required IReadOnlyList<string> Words { get; init; }

    /// <summary>
    ///     The raw operands after the command word, excluding redirects.
    /// </summary>
    public required IReadOnlyList<string> RawOperands { get; init; }

    public required IReadOnlyList<string> RawElements { get; init; }

    public required IReadOnlyList<ArgumentView> Arguments { get; init; }

    public required CommandOccurrenceRole Role { get; init; }

    /// <summary>
    ///     True when standard input comes from a here-document or here-string written in the command.
    /// </summary>
    public required bool HasAuthoredStandardInput { get; init; }

    public string? Subcommand => Words.Count > 1 ? Words[1] : null;

    public IReadOnlyList<(Capability Capability, string Reason)> Added => _added;

    public IReadOnlySet<int> WriteArguments => _writeArguments;

    public IReadOnlySet<int> DataArguments => _dataArguments;

    public IReadOnlyList<string> NestedCommands => _nestedCommands;

    /// <summary>
    ///     True when the nested commands also receive operands from standard input, which cannot be checked statically.
    /// </summary>
    public bool NestedOperandsFromInput { get; private set; }

    public IEnumerable<ArgumentView> Operands => Arguments.Where(argument => !argument.IsFlag);

    public void AddNestedCommand(string command)
    {
        _nestedCommands.Add(command);
    }

    public void MarkNestedOperandsFromInput()
    {
        NestedOperandsFromInput = true;
    }

    public void Add(Capability capability, string reason)
    {
        _added.Add((capability, reason));
    }

    public void MarkWrite(ArgumentView argument)
    {
        _writeArguments.Add(argument.Index);
    }

    public void MarkData(ArgumentView argument)
    {
        _dataArguments.Add(argument.Index);
    }

    public bool HasFlag(params string[] flags)
    {
        foreach (var argument in Arguments)
        {
            if (!argument.IsFlag)
            {
                continue;
            }

            foreach (var flag in flags)
            {
                if (FlagMatches(argument.Text, flag))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Returns the argument following the first matching flag.
    /// </summary>
    public ArgumentView? ValueOf(params string[] flags)
    {
        for (var index = 0; index < Arguments.Count - 1; index++)
        {
            var argument = Arguments[index];

            if (!argument.IsFlag)
            {
                continue;
            }

            foreach (var flag in flags)
            {
                if (string.Equals(argument.Text, flag, StringComparison.Ordinal))
                {
                    return Arguments[index + 1];
                }
            }
        }

        return null;
    }

    public static bool FlagMatches(string argument, string flag)
    {
        if (string.Equals(argument, flag, StringComparison.Ordinal))
        {
            return true;
        }

        if (argument.StartsWith(flag + "=", StringComparison.Ordinal))
        {
            return true;
        }

        if (flag.Length == 2 && flag[0] == '-' && flag[1] != '-' && argument.Length > 2 && argument[0] == '-' && argument[1] != '-')
        {
            for (var index = 1; index < argument.Length; index++)
            {
                if (!char.IsAsciiLetter(argument[index]))
                {
                    return argument[1..index].Contains(flag[1]);
                }
            }

            return argument.Contains(flag[1]);
        }

        return false;
    }
}
