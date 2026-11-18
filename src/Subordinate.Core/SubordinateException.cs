namespace Subordinate.Core;

/// <summary>
///     Machine readable failure categories. Each maps to a distinct process exit code.
/// </summary>
public enum ErrorCode
{
    InternalError,
    InvalidArguments,
    UnknownSession,
    SessionExists,
    SessionBusy,
    BadPolicy,
    WorkdirMissing,
    ConfigError,
    BadModel,
    BadApproval,
    StateConflict,
    ShellUnavailable,
    PromptError,
    AmbiguousSession,
    NothingToResume,
    RunFailed,
    CompactionFailed
}

public static class ErrorCodes
{
    public static string ToToken(this ErrorCode code)
    {
        return code switch
        {
            ErrorCode.InternalError => "internal_error",
            ErrorCode.InvalidArguments => "invalid_arguments",
            ErrorCode.UnknownSession => "unknown_session",
            ErrorCode.SessionExists => "session_exists",
            ErrorCode.SessionBusy => "session_busy",
            ErrorCode.BadPolicy => "bad_policy",
            ErrorCode.WorkdirMissing => "workdir_missing",
            ErrorCode.ConfigError => "config_error",
            ErrorCode.BadModel => "bad_model",
            ErrorCode.BadApproval => "bad_approval",
            ErrorCode.StateConflict => "state_conflict",
            ErrorCode.ShellUnavailable => "shell_unavailable",
            ErrorCode.PromptError => "prompt_error",
            ErrorCode.AmbiguousSession => "ambiguous_session",
            ErrorCode.NothingToResume => "nothing_to_resume",
            ErrorCode.RunFailed => "run_failed",
            ErrorCode.CompactionFailed => "compaction_failed",
            _ => "internal_error"
        };
    }

    public static int ToExitCode(this ErrorCode code)
    {
        return code switch
        {
            ErrorCode.InternalError => 1,
            ErrorCode.InvalidArguments => 2,
            ErrorCode.UnknownSession => 3,
            ErrorCode.SessionExists => 4,
            ErrorCode.SessionBusy => 5,
            ErrorCode.BadPolicy => 6,
            ErrorCode.WorkdirMissing => 7,
            ErrorCode.ConfigError => 8,
            ErrorCode.BadModel => 9,
            ErrorCode.BadApproval => 10,
            ErrorCode.StateConflict => 11,
            ErrorCode.ShellUnavailable => 12,
            ErrorCode.PromptError => 13,
            ErrorCode.AmbiguousSession => 14,
            ErrorCode.NothingToResume => 15,
            ErrorCode.RunFailed => 20,
            ErrorCode.CompactionFailed => 21,
            _ => 1
        };
    }
}

/// <summary>
///     A failure with a stable code and a message that is safe to show. Messages never contain secrets.
/// </summary>
public sealed class SubordinateException : Exception
{
    public SubordinateException(ErrorCode code, string message) : base(message)
    {
        Code = code;
    }

    public ErrorCode Code { get; }
}
