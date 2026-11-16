namespace Subordinate.Core.Platform;

public sealed class WindowsPlatform : IPlatform
{
    public static readonly WindowsPlatform Instance = new();

    private WindowsPlatform()
    {
    }

    public string Name => "windows";

    public IPathSemantics Paths { get; } = WindowsPathSemantics.Instance;

    public IShellEnvironment Shells { get; } = WindowsShellEnvironment.Instance;
}
