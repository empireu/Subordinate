namespace Subordinate.Core.Platform;

public sealed class LinuxPlatform : IPlatform
{
    public static readonly LinuxPlatform Instance = new();

    private LinuxPlatform()
    {
    }

    public string Name => "linux";

    public IPathSemantics Paths { get; } = LinuxPathSemantics.Instance;

    public IShellEnvironment Shells { get; } = LinuxShellEnvironment.Instance;
}
