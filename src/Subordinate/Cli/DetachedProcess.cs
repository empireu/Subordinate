using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Subordinate.Cli;

/// <summary>
///     Starts a process that inherits no handles and has no console, so the caller's pipes close as soon as the caller exits.
/// </summary>
internal static partial class DetachedProcess
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ErrorAccessDenied = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(
        string? applicationName,
        char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    public static int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, executable);

        foreach (var argument in arguments)
        {
            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        builder.Append('\0');
        var flags = CreateNoWindow | CreateNewProcessGroup | CreateUnicodeEnvironment;

        if (TryCreate(executable, builder, flags | CreateBreakawayFromJob, workingDirectory, out var processId, out var error))
        {
            return processId;
        }

        if (error == ErrorAccessDenied && TryCreate(executable, builder, flags, workingDirectory, out processId, out error))
        {
            return processId;
        }

        throw new Win32Exception(error);
    }

    private static bool TryCreate(string executable, StringBuilder commandLine, uint flags, string workingDirectory, out int processId, out int error)
    {
        var startupInfo = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>() };
        var buffer = commandLine.ToString().ToCharArray();

        if (!CreateProcess(executable, buffer, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workingDirectory, ref startupInfo, out var information))
        {
            processId = 0;
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        CloseHandle(information.Thread);
        CloseHandle(information.Process);
        processId = information.ProcessId;
        error = 0;
        return true;
    }

    /// <summary>
    ///     Quotes one argument so the C runtime parses it back unchanged.
    /// </summary>
    public static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var backslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
    }
}
