using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Subordinate.Core.Platform;

[SupportedOSPlatform("linux")]
internal static partial class LinuxRealPaths
{
    public static string? Resolve(string path)
    {
        var current = path;
        var suffix = new List<string>();

        while (true)
        {
            var resolved = RealPath(current);

            if (resolved != null)
            {
                if (suffix.Count == 0)
                {
                    return resolved;
                }

                suffix.Reverse();
                return resolved.TrimEnd('/') + "/" + string.Join('/', suffix);
            }

            if (current == "/")
            {
                return null;
            }

            var slash = current.LastIndexOf('/');

            if (slash < 0 || IsSymbolicLink(current))
            {
                return null;
            }

            suffix.Add(current[(slash + 1)..]);
            current = slash == 0 ? "/" : current[..slash];
        }
    }

    public static bool HasParentTraversalThroughLink(string path)
    {
        if (!path.StartsWith('/'))
        {
            return false;
        }

        var current = "/";

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (IsSymbolicLink(current))
                {
                    return true;
                }

                current = Parent(current);
                continue;
            }

            current = current == "/" ? "/" + segment : current + "/" + segment;
        }

        return false;
    }

    public static bool IsSymbolicLink(string path)
    {
        var buffer = (byte)0;
        return ReadLink(path, ref buffer, 1) > 0;
    }

    private static string Parent(string path)
    {
        if (path == "/")
        {
            return "/";
        }

        var slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ResolveRealPath(string path, IntPtr resolvedPath);

    [LibraryImport("libc", EntryPoint = "readlink", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint ReadLink(string path, ref byte buffer, nint bufferSize);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(IntPtr pointer);

    private static string? RealPath(string path)
    {
        var pointer = ResolveRealPath(path, IntPtr.Zero);

        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            Free(pointer);
        }
    }
}
