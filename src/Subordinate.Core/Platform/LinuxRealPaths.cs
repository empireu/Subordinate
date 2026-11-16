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

            if (slash < 0)
            {
                return null;
            }

            suffix.Add(current[(slash + 1)..]);
            current = slash == 0 ? "/" : current[..slash];
        }
    }

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr ResolveRealPath(string path, IntPtr resolvedPath);

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
