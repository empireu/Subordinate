using System.Text.RegularExpressions;
using Subordinate.Core.Platform;

namespace Subordinate.Core.Classification;

public enum ResolvedPathKind
{
    /// <summary>
    ///     An absolute local path.
    /// </summary>
    Local,
    /// <summary>
    ///     A discard sink such as /dev/null.
    /// </summary>
    NullDevice,
    /// <summary>
    ///     A location that is never inside a workdir, such as a Git Bash root path, a UNC share or a provider path.
    /// </summary>
    Foreign,
    /// <summary>
    ///     A location that cannot be determined statically.
    /// </summary>
    Unknown
}

public readonly record struct ResolvedPath(ResolvedPathKind Kind, string? Path, string Authored)
{
    public string Display => Kind == ResolvedPathKind.Local ? Path! : Authored;
}

/// <summary>
///     Normalizes paths written for Git Bash or PowerShell into absolute paths.
/// </summary>
public static partial class ShellPaths
{
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://")]
    private static partial Regex UrlPrefix();

    public static bool IsUrl(string value)
    {
        return UrlPrefix().IsMatch(value);
    }

    /// <summary>
    ///     Resolves an authored path against an absolute base directory.
    /// </summary>
    /// <param name="baseDirectory">The absolute directory relative paths resolve against, or null when it is unknown.</param>
    public static ResolvedPath Resolve(string value, string? baseDirectory)
    {
        return PlatformServices.Current.Paths.Resolve(value, baseDirectory);
    }

    /// <summary>
    ///     Canonicalizes an absolute path without touching the file system.
    /// </summary>
    public static string? Canonicalize(string absolutePath)
    {
        return PlatformServices.Current.Paths.Canonicalize(absolutePath);
    }

    public static bool IsInside(string path, string root)
    {
        return PlatformServices.Current.Paths.IsInside(path, root);
    }

    /// <summary>
    ///     Returns the directory part of a glob word that precedes its first wildcard segment, for example src for src/*.cs.
    /// </summary>
    public static string GlobCoveringPrefix(string glob)
    {
        var slashed = glob.Replace('\\', '/');
        var wildcardIndex = slashed.IndexOfAny(['*', '?', '[']);

        if (wildcardIndex < 0)
        {
            return slashed;
        }

        var lastSlash = slashed.LastIndexOf('/', wildcardIndex);

        if (lastSlash < 0)
        {
            return ".";
        }

        if (lastSlash == 0)
        {
            return "/";
        }

        return slashed[..lastSlash];
    }
}
