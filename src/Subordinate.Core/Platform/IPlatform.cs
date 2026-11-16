namespace Subordinate.Core.Platform;

public interface IPlatform
{
    string Name { get; }

    IPathSemantics Paths { get; }

    IShellEnvironment Shells { get; }
}
