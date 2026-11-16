using Subordinate.Core.Classification;

namespace Subordinate.Core.Platform;

public interface IPathSemantics
{
    StringComparer Comparer { get; }

    StringComparison Comparison { get; }

    string NormalizeDirectory(string directory);

    string IdentityKey(string normalizedDirectory);

    string GrantKey(string canonicalPath);

    bool SupportsShellWorkingDirectory(ShellKind shell, string directory);

    ResolvedPath Resolve(string value, string? baseDirectory);

    string? Canonicalize(string absolutePath);

    bool IsInside(string path, string root);
}
