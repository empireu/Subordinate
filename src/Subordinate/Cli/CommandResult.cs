namespace Subordinate.Cli;

/// <summary>
///     What a command prints: the JSON envelope line, then a human readable summary.
/// </summary>
internal sealed record CommandResult(Envelope Envelope, string Summary, int ExitCode = 0);
