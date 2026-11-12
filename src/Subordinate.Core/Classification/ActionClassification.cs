using Subordinate.Core.Policy;

namespace Subordinate.Core.Classification;

public sealed record PathAccess(bool IsWrite, ResolvedPath Path);

/// <summary>
///     One independently authorized unit of a tool call, such as one command occurrence of a shell command.
/// </summary>
public sealed class ActionUnit
{
    public required string Display { get; init; }

    /// <summary>
    ///     The identity used for session grants and cmd: policy tokens, or null when it cannot be proved.
    /// </summary>
    public required string? Key { get; init; }

    public required IReadOnlySet<Capability> Capabilities { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    public required IReadOnlyList<PathAccess> Paths { get; init; }
}

/// <summary>
///     The classification facts of one tool call.
/// </summary>
public sealed class ActionClassification
{
    public required string Tool { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<ActionUnit> Units { get; init; }

    /// <summary>
    ///     Capabilities that apply to the call as a whole, such as a credential path anywhere in the command text.
    /// </summary>
    public required IReadOnlySet<Capability> SharedCapabilities { get; init; }

    public required IReadOnlyList<string> SharedReasons { get; init; }

    public IReadOnlySet<Capability> AllCapabilities
    {
        get
        {
            var all = new HashSet<Capability>(SharedCapabilities);

            foreach (var unit in Units)
            {
                all.UnionWith(unit.Capabilities);
            }

            return all;
        }
    }

    public IReadOnlyList<string> AllReasons
    {
        get
        {
            var reasons = new List<string>(SharedReasons);

            foreach (var unit in Units)
            {
                foreach (var reason in unit.Reasons)
                {
                    if (!reasons.Contains(reason))
                    {
                        reasons.Add(reason);
                    }
                }
            }

            return reasons;
        }
    }
}

/// <summary>
///     The directories a classification resolves paths against.
/// </summary>
public sealed class ClassificationScope
{
    /// <summary>
    ///     The absolute Windows workdir of the session.
    /// </summary>
    public required string Workdir { get; init; }

    /// <summary>
    ///     The absolute Windows directory the command starts in.
    /// </summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    ///     Extra directories that may be read without read-outside, such as the run directory holding full tool output.
    /// </summary>
    public IReadOnlyList<string> ReadableRoots { get; init; } = [];

    public bool IsInsideWorkdir(string path)
    {
        return ShellPaths.IsInside(path, Workdir);
    }

    public bool IsReadable(string path)
    {
        if (IsInsideWorkdir(path))
        {
            return true;
        }

        foreach (var root in ReadableRoots)
        {
            if (ShellPaths.IsInside(path, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Converts path facts into capabilities and reasons.
    /// </summary>
    public void ApplyPathCapabilities(IEnumerable<PathAccess> paths, HashSet<Capability> capabilities, List<string> reasons)
    {
        foreach (var access in paths)
        {
            var path = access.Path;

            if (path.Kind == ResolvedPathKind.NullDevice)
            {
                continue;
            }

            if (path.Kind == ResolvedPathKind.Local)
            {
                var inside = access.IsWrite ? IsInsideWorkdir(path.Path!) : IsReadable(path.Path!);

                if (inside)
                {
                    capabilities.Add(access.IsWrite ? Capability.Write : Capability.Read);
                }
                else
                {
                    capabilities.Add(access.IsWrite ? Capability.WriteOutside : Capability.ReadOutside);
                    AddReason(reasons, access.IsWrite ? $"writes {path.Path} outside the workdir" : $"reads {path.Path} outside the workdir");
                }

                foreach (var match in SensitivePaths.FindMatches(path.Path!))
                {
                    capabilities.Add(Capability.Sensitive);
                    AddReason(reasons, $"touches a credential store ({match}): {path.Path}");
                }

                continue;
            }

            capabilities.Add(access.IsWrite ? Capability.WriteOutside : Capability.ReadOutside);
            AddReason(reasons, path.Kind == ResolvedPathKind.Foreign
                ? $"{(access.IsWrite ? "writes" : "reads")} {path.Authored}, which is outside the workdir"
                : $"{(access.IsWrite ? "writes" : "reads")} {path.Authored}, whose location cannot be determined");
        }
    }

    private static void AddReason(List<string> reasons, string reason)
    {
        if (!reasons.Contains(reason))
        {
            reasons.Add(reason);
        }
    }
}
