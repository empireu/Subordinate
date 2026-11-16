using System.Text.RegularExpressions;
using Subordinate.Core.Classification;

namespace Subordinate.Core.Platform;

public sealed partial class LinuxPathSemantics : IPathSemantics
{
    public static readonly LinuxPathSemantics Instance = new();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]+:")]
    private static partial Regex ProviderPath();

    private LinuxPathSemantics()
    {
    }

    public StringComparer Comparer => StringComparer.Ordinal;

    public StringComparison Comparison => StringComparison.Ordinal;

    public string NormalizeDirectory(string directory)
    {
        var canonical = Canonicalize(Path.GetFullPath(directory));

        if (canonical == null)
        {
            throw new SubordinateException(ErrorCode.InvalidArguments, $"The directory {directory} cannot be used on Linux because its path contains a backslash or a wildcard character.");
        }

        return canonical;
    }

    public string IdentityKey(string normalizedDirectory)
    {
        return normalizedDirectory;
    }

    public string GrantKey(string canonicalPath)
    {
        return canonicalPath;
    }

    public bool SupportsShellWorkingDirectory(ShellKind shell, string directory)
    {
        return directory.StartsWith('/');
    }

    public ResolvedPath Resolve(string value, string? baseDirectory)
    {
        var trimmed = value.Trim();

        if (trimmed.Length == 0 || trimmed.Contains('\0'))
        {
            return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
        }

        if (trimmed == "/dev/null" || trimmed.Equals("$null", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedPath(ResolvedPathKind.NullDevice, null, value);
        }

        if (ShellPaths.IsUrl(trimmed))
        {
            return new ResolvedPath(ResolvedPathKind.Foreign, null, value);
        }

        if (trimmed.Contains('\\'))
        {
            return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
        }

        if (ProviderPath().IsMatch(trimmed))
        {
            return new ResolvedPath(ResolvedPathKind.Foreign, null, value);
        }

        if (trimmed.StartsWith('/'))
        {
            var canonical = Canonicalize(trimmed);

            return canonical == null
                ? new ResolvedPath(ResolvedPathKind.Unknown, null, value)
                : new ResolvedPath(ResolvedPathKind.Local, canonical, value);
        }

        if (trimmed.StartsWith('~'))
        {
            return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
        }

        if (baseDirectory == null)
        {
            return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
        }

        var combined = Canonicalize(baseDirectory + "/" + trimmed);

        return combined == null
            ? new ResolvedPath(ResolvedPathKind.Unknown, null, value)
            : new ResolvedPath(ResolvedPathKind.Local, combined, value);
    }

    public string? Canonicalize(string absolutePath)
    {
        if (!absolutePath.StartsWith('/') || absolutePath.IndexOfAny(['\0', '\\', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            return null;
        }

        var segments = new List<string>();

        foreach (var part in absolutePath.Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(part);
        }

        return "/" + string.Join('/', segments);
    }

    public bool IsInside(string path, string root)
    {
        return IsInsideLexically(path, root);
    }

    public static bool IsInsideLexically(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        if (root == "/")
        {
            return path.StartsWith('/');
        }

        var prefix = root.EndsWith('/') ? root : root + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
