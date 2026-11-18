using Subordinate.Core.Configuration;

namespace Subordinate.Core.Running;

/// <summary>
///     The autocompaction configuration of one session.
/// </summary>
public sealed class CompactionSettings
{
    public int? Threshold { get; init; }

    public int WindDownTurns { get; init; }

    public required string NoticeText { get; init; }

    public required string RequestText { get; init; }

    public static CompactionSettings Disabled { get; } = new()
    {
        Threshold = null,
        WindDownTurns = 0,
        NoticeText = CompactionPrompts.DefaultNotice,
        RequestText = CompactionPrompts.DefaultRequest
    };
}
