using System.Text.RegularExpressions;
using Subordinate.Core.Classification;

namespace Subordinate.Core.Platform;

public sealed partial class WindowsPathSemantics : IPathSemantics
{
    public static readonly WindowsPathSemantics Instance = new();

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

    private WindowsPathSemantics()
    {
    }

    public StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    public string NormalizeDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);

        if (full.Length > 3)
        {
            full = full.TrimEnd('\\', '/');
        }

        if (full.Length >= 2 && full[1] == ':')
        {
            full = char.ToUpperInvariant(full[0]) + full[1..];
        }

        return full;
    }

    public string IdentityKey(string normalizedDirectory)
    {
        return normalizedDirectory.ToLowerInvariant();
    }

    public string GrantKey(string canonicalPath)
    {
        return canonicalPath.ToLowerInvariant();
    }

    public bool SupportsShellWorkingDirectory(ShellKind shell, string directory)
    {
        return shell != ShellKind.Bash || ToBashPath(directory) != null;
    }

    public ResolvedPath Resolve(string value, string? baseDirectory)
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

    public string? Canonicalize(string absolutePath)
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

    public bool IsInside(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

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
}
