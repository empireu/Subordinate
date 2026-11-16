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

    public bool RejectsRawPath(string raw)
    {
        return raw.Contains('\\');
    }

    public bool HasParentTraversalThroughLink(string raw, string? baseDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        var trimmed = raw.Trim();

        if (trimmed.StartsWith('/'))
        {
            return LinuxRealPaths.HasParentTraversalThroughLink(trimmed);
        }

        return baseDirectory != null && LinuxRealPaths.HasParentTraversalThroughLink(baseDirectory + "/" + trimmed);
    }

    public bool RejectsUnknownWorkingDirectory => true;

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
            if (OperatingSystem.IsLinux() && LinuxRealPaths.HasParentTraversalThroughLink(trimmed))
            {
                return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
            }

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

        var combinedPath = baseDirectory + "/" + trimmed;

        if (OperatingSystem.IsLinux() && LinuxRealPaths.HasParentTraversalThroughLink(combinedPath))
        {
            return new ResolvedPath(ResolvedPathKind.Unknown, null, value);
        }

        var combined = Canonicalize(combinedPath);

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
        if (!IsInsideLexically(path, root))
        {
            return false;
        }

        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        var realRoot = LinuxRealPaths.Resolve(root);

        if (realRoot == null)
        {
            return false;
        }

        var realPath = LinuxRealPaths.Resolve(path);

        if (realPath == null)
        {
            return false;
        }

        return IsInsideLexically(realPath, realRoot);
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
