using System.Security.Cryptography;

namespace Subordinate.Core.Images;

/// <summary>
///     Stores the images of a session as files in its images directory and refers to them by URL.
/// </summary>
public static class ImageSnapshots
{
    public const string UrlPrefix = "subordinate-image:";

    public const int HashCharacters = 64;

    public const int HeaderLength = 12;

    private const string TemporaryExtension = ".tmp";

    public static string Save(string imagesDirectory, byte[] bytes, ImageFormat format)
    {
        var fileName = FileName(bytes, format.Extension);
        var path = Path.Combine(imagesDirectory, fileName);

        if (File.Exists(path))
        {
            return UrlPrefix + fileName;
        }

        Directory.CreateDirectory(imagesDirectory);
        var temporary = Path.Combine(imagesDirectory, Guid.NewGuid().ToString("N") + TemporaryExtension);

        try
        {
            File.WriteAllBytes(temporary, bytes);

            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
        }
        finally
        {
            TryDelete(temporary);
        }

        return UrlPrefix + fileName;
    }

    public static bool TryParse(string? url, out string fileName)
    {
        fileName = string.Empty;

        if (url == null || !url.StartsWith(UrlPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = url[UrlPrefix.Length..];
        var dot = candidate.LastIndexOf('.');

        if (dot != HashCharacters || candidate.Length <= HashCharacters + 1)
        {
            return false;
        }

        for (var index = 0; index < HashCharacters; index++)
        {
            var character = candidate[index];

            if (character is not (>= '0' and <= '9') && character is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        if (ImageFormat.FromExtension(candidate[(dot + 1)..]) == null)
        {
            return false;
        }

        fileName = candidate;
        return true;
    }

    public static ImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47
            && header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return ImageFormat.Png;
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        if (MatchesAscii(header, 0, "GIF87a") || MatchesAscii(header, 0, "GIF89a"))
        {
            return ImageFormat.Gif;
        }

        if (MatchesAscii(header, 0, "RIFF") && MatchesAscii(header, 8, "WEBP"))
        {
            return ImageFormat.Webp;
        }

        return null;
    }

    public static string Hash(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string FileName(byte[] bytes, string extension)
    {
        return Hash(bytes) + "." + extension;
    }

    private static bool MatchesAscii(ReadOnlySpan<byte> header, int offset, string text)
    {
        if (header.Length < offset + text.Length)
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (header[offset + index] != text[index])
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
