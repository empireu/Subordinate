namespace Subordinate.Core.Platform;

public sealed class UnsupportedPlatform : IPlatform
{
    public static readonly UnsupportedPlatform Instance = new();

    private UnsupportedPlatform()
    {
    }

    public string Name => throw CreateException();

    public IPathSemantics Paths => throw CreateException();

    public IShellEnvironment Shells => throw CreateException();

    private static SubordinateException CreateException()
    {
        return new SubordinateException(ErrorCode.InternalError, "Subordinate runs on Windows and Linux. This operating system is not supported.");
    }
}
