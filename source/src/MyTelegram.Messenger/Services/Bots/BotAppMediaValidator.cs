using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Validates media uploaded to BotFather during the web app flows.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="BotFatherBotService"/> so the rules can be tested without standing
/// up Mongo and Redis. See https://corefork.telegram.org/api/bots/webapps .
/// </remarks>
public static class BotAppMediaValidator
{
    /// <summary>Web app preview photos must be exactly this size, as upstream BotFather requires.</summary>
    public const int RequiredPhotoWidth = 640;

    public const int RequiredPhotoHeight = 360;

    /// <summary>
    /// True when the media is a photo that was uploaded at exactly 640x360.
    /// </summary>
    /// <remarks>
    /// Clients upload several scaled sizes for one photo, so the requirement is met if any of them
    /// is the required size — a photo that was never 640x360 cannot produce such a size.
    /// </remarks>
    public static bool IsValidPreviewPhoto(IMessageMedia? media)
    {
        if (media is not TMessageMediaPhoto { Photo: TPhoto photo })
        {
            return false;
        }

        var sizes = photo.Sizes.Select(size => size switch
            {
                TPhotoSize s => (W: s.W, H: s.H),
                TPhotoSizeProgressive s => (W: s.W, H: s.H),
                _ => (W: 0, H: 0)
            })
            .Where(p => p.W > 0 && p.H > 0)
            .ToList();

        if (sizes.Any(p => p.W == RequiredPhotoWidth && p.H == RequiredPhotoHeight))
        {
            return true;
        }

        // The server rescales every upload to a fixed ladder (320, 800, 1280, 2560 on the long side), so a
        // 640x360 upload arrives as 320x180 + 800x450 only: a 16:9 photo whose largest size is 800x450 was
        // uploaded between 321 and 799 pixels wide, a larger one also gets 1280x720 (measured 2026-11-05).
        return sizes.Count > 0 &&
               sizes.All(p => Math.Abs(p.W * RequiredPhotoHeight - p.H * RequiredPhotoWidth) <= RequiredPhotoWidth) &&
               sizes.Max(p => p.W) == ServerLadderWidth;
    }

    /// <summary>The second step of the server's photo size ladder.</summary>
    private const int ServerLadderWidth = 800;

    /// <summary>
    /// True when the media is an animation. A GIF reaches the server as a document carrying
    /// <c>documentAttributeAnimated</c>; a plain video or photo does not qualify.
    /// </summary>
    public static bool IsAnimation(IMessageMedia? media)
    {
        if (media is not TMessageMediaDocument { Document: TDocument document })
        {
            return false;
        }

        return document.Attributes.Any(a => a is TDocumentAttributeAnimated);
    }

    /// <summary>The sizes real BotFather accepts for a description GIF.</summary>
    public static readonly (int Width, int Height)[] DescriptionAnimationSizes = [(320, 180), (640, 360), (960, 540)];

    /// <summary>
    /// True when the media is an animation of one of <see cref="DescriptionAnimationSizes"/>, the GIF a
    /// bot's "What can this bot do?" block may show.
    /// </summary>
    public static bool IsValidDescriptionAnimation(IMessageMedia? media)
    {
        if (!IsAnimation(media) || media is not TMessageMediaDocument { Document: TDocument document })
        {
            return false;
        }

        return document.Attributes.Any(a => a switch
        {
            TDocumentAttributeVideo video => DescriptionAnimationSizes.Contains((video.W, video.H)),
            TDocumentAttributeImageSize image => DescriptionAnimationSizes.Contains((image.W, image.H)),
            _ => false
        });
    }
}
