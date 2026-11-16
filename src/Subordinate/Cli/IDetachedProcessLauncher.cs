namespace Subordinate.Cli;

internal interface IDetachedProcessLauncher
{
    int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);

    void DetachCurrentProcess();
}
