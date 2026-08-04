using MyTelegram.Schema;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Converts the bot-supplied <c>inputBotInlineResult*</c> family into the client-facing
/// <c>botInlineResult*</c> family, and the matching <c>inputBotInlineMessage*</c> payloads into
/// <c>botInlineMessage*</c>.
/// </summary>
/// <remarks>
/// The input and output constructors are field-for-field mirrors, except that the input side
/// references media by <c>InputPhoto</c>/<c>InputDocument</c> while the output side carries the
/// resolved <c>Photo</c>/<c>Document</c>. Media resolution is done by the caller, which has the
/// query processor; this helper only reshapes what it is given.
/// </remarks>
public static class InlineResultConverter
{
    /// <summary>
    /// Reshapes one bot-supplied result for delivery to the client.
    /// </summary>
    /// <param name="photo">Resolved photo for <c>inputBotInlineResultPhoto</c>, if the caller found one.</param>
    /// <param name="document">Resolved document for <c>inputBotInlineResultDocument</c>, if the caller found one.</param>
    public static IBotInlineResult? ToBotInlineResult(
        IInputBotInlineResult input,
        IPhoto? photo = null,
        IDocument? document = null)
    {
        switch (input)
        {
            case TInputBotInlineResult generic:
                return new TBotInlineResult
                {
                    Id = generic.Id,
                    Type = generic.Type,
                    Title = generic.Title,
                    Description = generic.Description,
                    Url = generic.Url,
                    Thumb = ToWebDocument(generic.Thumb),
                    Content = ToWebDocument(generic.Content),
                    SendMessage = ToBotInlineMessage(generic.SendMessage)
                };

            case TInputBotInlineResultPhoto photoResult:
                return new TBotInlineMediaResult
                {
                    Id = photoResult.Id,
                    Type = photoResult.Type,
                    Photo = photo,
                    SendMessage = ToBotInlineMessage(photoResult.SendMessage)
                };

            case TInputBotInlineResultDocument documentResult:
                return new TBotInlineMediaResult
                {
                    Id = documentResult.Id,
                    Type = documentResult.Type,
                    Title = documentResult.Title,
                    Description = documentResult.Description,
                    Document = document,
                    SendMessage = ToBotInlineMessage(documentResult.SendMessage)
                };

            case TInputBotInlineResultGame gameResult:
                return new TBotInlineResult
                {
                    Id = gameResult.Id,
                    Type = "game",
                    Title = gameResult.ShortName,
                    SendMessage = ToBotInlineMessage(gameResult.SendMessage)
                };

            default:
                return null;
        }
    }

    /// <summary>
    /// Reshapes the message a result will turn into once the user picks it.
    /// </summary>
    public static IBotInlineMessage ToBotInlineMessage(IInputBotInlineMessage input)
    {
        switch (input)
        {
            case TInputBotInlineMessageText text:
                return new TBotInlineMessageText
                {
                    NoWebpage = text.NoWebpage,
                    InvertMedia = text.InvertMedia,
                    Message = text.Message,
                    Entities = text.Entities,
                    ReplyMarkup = text.ReplyMarkup
                };

            case TInputBotInlineMessageMediaAuto auto:
                return new TBotInlineMessageMediaAuto
                {
                    InvertMedia = auto.InvertMedia,
                    Message = auto.Message,
                    Entities = auto.Entities,
                    ReplyMarkup = auto.ReplyMarkup
                };

            case TInputBotInlineMessageMediaGeo geo:
                return new TBotInlineMessageMediaGeo
                {
                    Geo = ToGeoPoint(geo.GeoPoint),
                    Heading = geo.Heading,
                    Period = geo.Period,
                    ProximityNotificationRadius = geo.ProximityNotificationRadius,
                    ReplyMarkup = geo.ReplyMarkup
                };

            case TInputBotInlineMessageMediaVenue venue:
                return new TBotInlineMessageMediaVenue
                {
                    Geo = ToGeoPoint(venue.GeoPoint),
                    Title = venue.Title,
                    Address = venue.Address,
                    Provider = venue.Provider,
                    VenueId = venue.VenueId,
                    VenueType = venue.VenueType,
                    ReplyMarkup = venue.ReplyMarkup
                };

            case TInputBotInlineMessageMediaContact contact:
                return new TBotInlineMessageMediaContact
                {
                    PhoneNumber = contact.PhoneNumber,
                    FirstName = contact.FirstName,
                    LastName = contact.LastName,
                    Vcard = contact.Vcard,
                    ReplyMarkup = contact.ReplyMarkup
                };

            case TInputBotInlineMessageGame game:
                // A game message carries no text of its own; the client renders the game itself.
                return new TBotInlineMessageMediaAuto
                {
                    Message = string.Empty,
                    ReplyMarkup = game.ReplyMarkup
                };

            default:
                return new TBotInlineMessageText { Message = string.Empty };
        }
    }

    /// <summary>
    /// Extracts the plain text a result should post, used when actually sending the message.
    /// </summary>
    public static string GetMessageText(IBotInlineMessage message)
    {
        return message switch
        {
            TBotInlineMessageText text => text.Message,
            TBotInlineMessageMediaAuto auto => auto.Message,
            TBotInlineMessageMediaVenue venue => $"{venue.Title}\n{venue.Address}",
            TBotInlineMessageMediaContact contact =>
                $"{contact.FirstName} {contact.LastName}".Trim() + $"\n{contact.PhoneNumber}",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Extracts the entities attached to a result's message, if any.
    /// </summary>
    public static TVector<IMessageEntity>? GetMessageEntities(IBotInlineMessage message)
    {
        return message switch
        {
            TBotInlineMessageText text => text.Entities,
            TBotInlineMessageMediaAuto auto => auto.Entities,
            _ => null
        };
    }

    /// <summary>
    /// Extracts the inline keyboard attached to a result's message, if any.
    /// </summary>
    public static IReplyMarkup? GetReplyMarkup(IBotInlineMessage message)
    {
        return message switch
        {
            TBotInlineMessageText text => text.ReplyMarkup,
            TBotInlineMessageMediaAuto auto => auto.ReplyMarkup,
            TBotInlineMessageMediaGeo geo => geo.ReplyMarkup,
            TBotInlineMessageMediaVenue venue => venue.ReplyMarkup,
            TBotInlineMessageMediaContact contact => contact.ReplyMarkup,
            _ => null
        };
    }

    private static IWebDocument? ToWebDocument(IInputWebDocument? input)
    {
        if (input is not TInputWebDocument document)
        {
            return null;
        }

        // The server does not proxy inline thumbnails, so the no-proxy variant is the honest one.
        return new TWebDocumentNoProxy
        {
            Url = document.Url,
            Size = document.Size,
            MimeType = document.MimeType,
            Attributes = document.Attributes
        };
    }

    private static IGeoPoint ToGeoPoint(IInputGeoPoint? input)
    {
        if (input is not TInputGeoPoint point)
        {
            return new TGeoPointEmpty();
        }

        return new TGeoPoint
        {
            Lat = point.Lat,
            Long = point.Long,
            AccessHash = 0,
            AccuracyRadius = point.AccuracyRadius
        };
    }
}
