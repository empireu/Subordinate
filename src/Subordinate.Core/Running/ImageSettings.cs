namespace Subordinate.Core.Running;

/// <summary>
///     The image settings of one session, taken from the model entry of its alias.
/// </summary>
public sealed class ImageSettings
{
    public bool Enabled { get; init; }

    public int? MaxImages { get; init; }

    public static ImageSettings Disabled { get; } = new()
    {
        Enabled = false,
        MaxImages = null
    };
}
