using System.Text.RegularExpressions;

namespace Subordinate.Core.Classification;

public enum ResolvedPathKind
{
    /// <summary>
    ///     An absolute local Windows path.
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
///     Normalizes paths written for Git Bash or PowerShell into absolute Windows paths.
/// </summary>
public static partial class ShellPaths
{
    [GeneratedRegex(@"^/([A-Za-z])(/.*)?$")]
    private static partial Regex BashDrivePath();

    [GeneratedRegex(@"^[A-Za-z]:/")]
    private static partial Regex WindowsDrivePath();

    [GeneratedRegex(@"^[A-Za-z]:")]
    private static partial Regex DriveRelativePath();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]+:")]
    private static partial Regex ProviderPath();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.-]*://")]
    private static partial Regex UrlPrefix();

    /// <summary>
    ///     Converts an absolute Windows path to the form Git Bash uses, for example D:\repos\x to /d/repos/x.
    /// </summary>
    public static string? ToBashPath(string windowsPath)
    {
        var normalized = windowsPath.Replace('\\', '/');

        if (normalized.Length < 2 || !char.IsAsciiLetter(normalized[0]) || normalized[1] != ':')
        {
            return null;
        }

        var rest = normalized[2..].TrimEnd('/');
        return "/" + char.ToLowerInvariant(normalized[0]) + rest;
    }

    public static bool IsUrl(string value)
    {
        return UrlPrefix().IsMatch(value);
    }

    /// <summary>
    ///     Resolves an authored path against an absolute Windows base directory.
    /// </summary>
    /// <param name="baseDirectory">The absolute Windows directory relative paths resolve against, or null when it is unknown.</param>
    public static ResolvedPath Resolve(string value, string? baseDirectory)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0 || trimmed.Contains('\0'))
        {
            return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
        }

        if (trimmed == "/dev/null" || trimmed.Equals("nul", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("$null", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedPath(ResolvedPathKind.NullDevice, null, value);
        }

        if (UrlPrefix().IsMatch(trimmed))
        {
            return new ResolvedPath(ResolvedPathKind.Foreign, null, value);
        }

        var slashed = trimmed.Replace('\\', '/');

        if (slashed.StartsWith("//", StringComparison.Ordinal))
        {
            return new ResolvedPath(ResolvedPathKind.Foreign, null, value);
        }

        string combined;
        var bashDrive = BashDrivePath().Match(slashed);

        if (bashDrive.Success)
        {
            var rest = bashDrive.Groups[2].Success ? bashDrive.Groups[2].Value : "/";
            combined = char.ToUpperInvariant(bashDrive.Groups[1].Value[0]) + ":" + rest;
        }
        else if (WindowsDrivePath().IsMatch(slashed))
        {
            combined = char.ToUpperInvariant(slashed[0]) + slashed[1..];
        }
        else if (DriveRelativePath().IsMatch(slashed) && (slashed.Length == 2 || slashed[2] != '/'))
        {
            if (ProviderPath().IsMatch(slashed))
            {
                return new ResolvedPath(ResolvedPathKind.Foreign, null, value);
            }

            return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
        }
        else if (ProviderPath().IsMatch(slashed))
        {
            return new ResolvedPath(ResolvedPathKind.Foreign, null, value);
        }
        else if (slashed.StartsWith('/'))
        {
            return new ResolvedPath(ResolvedPathKind.Foreign, null, value);
        }
        else if (slashed.StartsWith('~'))
        {
            return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
        }
        else
        {
            if (baseDirectory == null)
            {
                return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
            }

            combined = baseDirectory.Replace('\\', '/').TrimEnd('/') + "/" + slashed;
        }

        var canonical = Canonicalize(combined);

        return canonical == null
            ? new ResolvedPath(ResolvedPathKind.Unknown, null, value)
            : new ResolvedPath(ResolvedPathKind.Local, canonical, value);
    }

    /// <summary>
    ///     Canonicalizes an absolute Windows path without touching the file system.
    /// </summary>
    public static string? Canonicalize(string absolutePath)
    {
        var windows = absolutePath.Replace('/', '\\');

        if (windows.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0)
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(windows);

            if (full.Length > 3)
            {
                full = full.TrimEnd('\\');
            }

            if (full.Length >= 2 && full[1] == ':')
            {
                full = char.ToUpperInvariant(full[0]) + full[1..];
            }

            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool IsInside(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
