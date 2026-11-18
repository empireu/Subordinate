using System.Globalization;

namespace Subordinate.Core.Configuration;

/// <summary>
///     The built-in texts of the compaction prompt files and the placeholder they share.
/// </summary>
public static class CompactionPrompts
{
    public const string TurnsPlaceholder = "{turns}";

    public const string DefaultNotice = """
        [Context compaction notice]
        The context of this session is nearly full. After at most {turns} more model turns, Subordinate will ask you to summarize your work. It will then continue this same task from your summary, with the older messages removed. This is not the end of the task.

        Use these turns to reach a safe stopping point. Finish an edit or a step you have already started, but do not start new exploration or new changes. Do not give your final answer because of this notice. If you are already at a safe stopping point, reply with one short sentence and no tool calls.
        """;

    public const string DefaultRequest = """
        [Context compaction request]
        Write a summary of this session so far. The older messages will be removed and replaced by your summary, so it must let you continue the task without them. Tools are disabled for this turn. Do not give your final answer and do not ask questions. Only write the summary.

        Your earlier reasoning is the best record of why things were done, so carry its conclusions over. Prefer exact facts over prose: file paths with line numbers, function and type names, commands and their key results, error messages quoted exactly, and the ids of pending or denied approvals.

        Use these sections:

        ## Task
        The task as you understand it now, including every instruction, constraint and preference from the supervisor, and any clarifications or changes that came later.

        ## Done
        Work completed, with paths and how it was verified.

        ## Current state
        What is in progress or half done, what the files and environment look like now, and anything changed but not yet checked.

        ## Findings and decisions
        Facts you established and decisions you made, each with its reason.

        ## Failed attempts
        Approaches that did not work and why, so they are not repeated. Include denied tool calls.

        ## Next steps
        The concrete next actions, in order. The first item is what you will do right after this summary.

        Your messages after the compaction notice stay visible after the summary, so mention them only briefly. Keep the summary as short as you can without losing anything you will need. It should rarely exceed 2000 words.
        """;

    public static string FormatNotice(string noticeText, int windDownTurns)
    {
        return noticeText.Replace(TurnsPlaceholder, windDownTurns.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
