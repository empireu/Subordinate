using Subordinate.Core.Classification;
using Subordinate.Core.Configuration;
using Subordinate.Core.Shells;

namespace Subordinate.Core.Platform;

public interface IShellEnvironment
{
    IReadOnlyList<ShellKind> SupportedShells { get; }

    ShellInfo Locate(ShellKind kind, ShellPathsEntry configured);

    string? ProbeVersion(ShellKind kind, string executablePath);

    string ToolDescription(ShellKind kind);

    string PromptDescription(ShellKind kind);
}
