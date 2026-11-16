using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Subordinate.Cli;

[SupportedOSPlatform("linux")]
internal sealed partial class LinuxDetachedProcessLauncher : IDetachedProcessLauncher
{
    public static readonly LinuxDetachedProcessLauncher Instance = new();

    private static PosixSignalRegistration? _sighupRegistration;

    private LinuxDetachedProcessLauncher()
    {
    }

    public int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The background worker could not be started.");
        var processId = process.Id;
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();
        return processId;
    }

    public void DetachCurrentProcess()
    {
        CreateNewSession();
        _sighupRegistration ??= PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;
        });
    }

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int CreateNewSession();
}
