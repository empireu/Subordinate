namespace Subordinate.Core.Images;

/// <summary>
///     One of the image formats that the read tool can show, with the media type and file extension it uses.
/// </summary>
public sealed class ImageFormat
{
    public static ImageFormat Png { get; } = new("image/png", "png", "PNG");

    public static ImageFormat Jpeg { get; } = new("image/jpeg", "jpg", "JPEG");

    public static ImageFormat Gif { get; } = new("image/gif", "gif", "GIF");

    public static ImageFormat Webp { get; } = new("image/webp", "webp", "WebP");

    public static IReadOnlyList<ImageFormat> All { get; } = [Png, Jpeg, Gif, Webp];

    private ImageFormat(string mediaType, string extension, string displayName)
    {
        MediaType = mediaType;
        Extension = extension;
        DisplayName = displayName;
    }

    public string MediaType { get; }

    public string Extension { get; }

    public string DisplayName { get; }

    public static ImageFormat? FromExtension(string extension)
    {
        foreach (var format in All)
        {
            if (string.Equals(format.Extension, extension, StringComparison.Ordinal))
            {
                return format;
            }
        }

        return null;
    }
}
