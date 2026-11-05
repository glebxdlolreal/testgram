using MyTelegram.Messenger.Services.Bots;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.Bots;

/// <summary>
/// Feature: media uploads to BotFather during the web app flows (<c>/newapp</c>, <c>/editapp</c>).
///
/// <para>
/// BotFather previously received only text — <c>messages.sendMedia</c> was never routed to it — so
/// the flows could not ask for the 640x360 preview photo or the demo GIF that upstream BotFather
/// requires. Now that uploads reach it, these are the rules that decide whether a file is accepted:
/// a preview photo must really have been uploaded at 640x360, and a demo GIF must be an animation
/// rather than any old video or photo. See https://corefork.telegram.org/api/bots/webapps .
/// </para>
/// </summary>
public class BotAppMediaValidatorTests
{
    private static TMessageMediaPhoto PhotoWithSizes(params (int W, int H)[] sizes)
    {
        var photoSizes = new TVector<IPhotoSize>();
        foreach (var (w, h) in sizes)
        {
            photoSizes.Add(new TPhotoSize { Type = "x", W = w, H = h, Size = w * h });
        }

        return new TMessageMediaPhoto
        {
            Photo = new TPhoto
            {
                Id = 1,
                AccessHash = 2,
                FileReference = [],
                Date = 0,
                Sizes = photoSizes,
                DcId = 1
            }
        };
    }

    private static TMessageMediaDocument DocumentWith(params IDocumentAttribute[] attributes)
    {
        return new TMessageMediaDocument
        {
            Document = new TDocument
            {
                Id = 1,
                AccessHash = 2,
                FileReference = ReadOnlyMemory<byte>.Empty,
                Date = 0,
                MimeType = "video/mp4",
                Size = 1024,
                DcId = 1,
                Attributes = new TVector<IDocumentAttribute>(attributes.ToList())
            }
        };
    }

    [Fact]
    public void Photo_uploaded_at_the_required_size_is_accepted()
    {
        var media = PhotoWithSizes((640, 360));

        BotAppMediaValidator.IsValidPreviewPhoto(media).ShouldBeTrue();
    }

    [Fact]
    public void Photo_of_any_other_size_is_rejected()
    {
        // The exact case from the transcript: users kept sending arbitrary screenshots.
        var media = PhotoWithSizes((1280, 720), (320, 180));

        BotAppMediaValidator.IsValidPreviewPhoto(media).ShouldBeFalse();
    }

    [Fact]
    public void Required_size_among_several_scaled_sizes_is_accepted()
    {
        // Clients upload a whole ladder of sizes for one photo; only one has to match.
        var media = PhotoWithSizes((160, 90), (640, 360), (1280, 720));

        BotAppMediaValidator.IsValidPreviewPhoto(media).ShouldBeTrue();
    }

    [Fact]
    public void A_640x360_upload_as_the_server_stores_it_is_accepted()
    {
        // Measured: the server keeps a 640x360 upload as 320x180 + 800x450, never 640x360 itself.
        BotAppMediaValidator.IsValidPreviewPhoto(PhotoWithSizes((320, 180), (800, 450))).ShouldBeTrue();
    }

    [Fact]
    public void A_larger_16_by_9_upload_is_rejected()
    {
        // 1280x720 gets the 1280 step of the ladder too.
        BotAppMediaValidator.IsValidPreviewPhoto(PhotoWithSizes((320, 180), (800, 450), (1280, 720))).ShouldBeFalse();
    }

    [Fact]
    public void A_4_by_3_upload_is_rejected()
    {
        BotAppMediaValidator.IsValidPreviewPhoto(PhotoWithSizes((320, 240), (800, 600))).ShouldBeFalse();
    }

    [Fact]
    public void Transposed_dimensions_are_rejected()
    {
        // 360x640 is portrait, not the landscape banner the profile page expects.
        var media = PhotoWithSizes((360, 640));

        BotAppMediaValidator.IsValidPreviewPhoto(media).ShouldBeFalse();
    }

    [Fact]
    public void Photo_without_sizes_is_rejected()
    {
        BotAppMediaValidator.IsValidPreviewPhoto(PhotoWithSizes()).ShouldBeFalse();
    }

    [Fact]
    public void A_document_is_not_a_preview_photo()
    {
        // Sending the GIF at the photo step must not silently pass.
        var media = DocumentWith(new TDocumentAttributeAnimated());

        BotAppMediaValidator.IsValidPreviewPhoto(media).ShouldBeFalse();
    }

    [Fact]
    public void Null_media_is_rejected()
    {
        BotAppMediaValidator.IsValidPreviewPhoto(null).ShouldBeFalse();
        BotAppMediaValidator.IsAnimation(null).ShouldBeFalse();
    }

    [Fact]
    public void Animated_document_is_accepted_as_a_gif()
    {
        var media = DocumentWith(
            new TDocumentAttributeAnimated(),
            new TDocumentAttributeVideo { W = 320, H = 180, Duration = 3 });

        BotAppMediaValidator.IsAnimation(media).ShouldBeTrue();
    }

    [Fact]
    public void Plain_video_is_not_a_gif()
    {
        // A video without documentAttributeAnimated is an ordinary clip, not a demo GIF.
        var media = DocumentWith(new TDocumentAttributeVideo { W = 320, H = 180, Duration = 3 });

        BotAppMediaValidator.IsAnimation(media).ShouldBeFalse();
    }

    [Fact]
    public void A_photo_is_not_a_gif()
    {
        BotAppMediaValidator.IsAnimation(PhotoWithSizes((640, 360))).ShouldBeFalse();
    }
}
