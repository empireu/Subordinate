using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;
using Subordinate.Core.Platform;

namespace Subordinate.Core.Shells;

/// <summary>
///     A located and version-checked shell executable.
/// </summary>
public sealed record ShellInfo(ShellKind Kind, string ExecutablePath, string Version);

/// <summary>
///     Finds the executable for a shell and checks that its version matches the parser dialect. Never falls back to another shell.
/// </summary>
public static class ShellLocator
{
    public static ShellInfo Locate(ShellKind kind, ShellPathsEntry configured)
    {
        return PlatformServices.Current.Shells.Locate(kind, configured);
    }

    /// <summary>
    ///     Checks that a shell recorded in a session is still present with the same version.
    /// </summary>
    public static void VerifyUnchanged(ShellInfo recorded)
    {
        if (!File.Exists(recorded.ExecutablePath))
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, $"The {recorded.Kind.ToToken()} executable recorded for this session no longer exists at {recorded.ExecutablePath}.");
        }

        var current = PlatformServices.Current.Shells.ProbeVersion(recorded.Kind, recorded.ExecutablePath);

        if (current == null || !string.Equals(current, recorded.Version, StringComparison.Ordinal))
        {
            throw new SubordinateException(ErrorCode.ShellUnavailable, $"The {recorded.Kind.ToToken()} executable at {recorded.ExecutablePath} changed version from {recorded.Version} to {current ?? "unknown"}. Start a new session.");
        }
    }
}
