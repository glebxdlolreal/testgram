using System.Diagnostics.CodeAnalysis;

namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class UserFullMapper
    : IObjectMapper<IUserFullReadModel, TUserFull>,
        IObjectMapper<IUserReadModel, TUserFull>,
        ILayeredMapper,
        ITransientDependency
{
    private const int MediaDcId = 2;
    private const int MinAdvertisedDcId = 1;
    private const int MaxAdvertisedDcId = 5;

    public int Layer => Layers.LayerLatest;
    

    public TUserFull Map(IUserFullReadModel source)
    {
        return Map(source, new TUserFull());
    }

    public TUserFull Map(
        IUserFullReadModel source,
        TUserFull destination
    )
    {
        destination.BusinessWorkHours = source.BusinessWorkHours;
        destination.BusinessLocation = source.BusinessLocation;
        destination.BusinessGreetingMessage = source.BusinessGreetingMessage;
        destination.BusinessAwayMessage = source.BusinessAwayMessage;
        destination.BusinessIntro = NormalizeBusinessIntro(source.BusinessIntro);

        destination.Id = source.UserId;
        destination.Settings = new TPeerSettings();

        return destination;
    }

    [return: NotNullIfNotNull("source")]
    public TUserFull? Map(IUserReadModel source)
    {
        return Map(source, new TUserFull());
    }

    [return: NotNullIfNotNull("source")]
    public TUserFull? Map(IUserReadModel source, TUserFull destination)
    {
        destination.Id = source.UserId;
        destination.About = source.About;
        destination.Settings = new TPeerSettings();
        destination.ReadDatesPrivate = source.GlobalPrivacySettings?.HideReadMarks ?? false;
        if (source.GlobalPrivacySettings?.NoncontactPeersPaidStars > 0)
            destination.SendPaidMessagesStars = source.GlobalPrivacySettings.NoncontactPeersPaidStars;
        if (source.Birthday != null)
        {
            destination.Birthday = new TBirthday
            {
                Day = source.Birthday.Day,
                Month = source.Birthday.Month,
                Year = source.Birthday.Year
            };
        }

        // Business fields are filled by IBusinessProfileService: they depend on who is asking and on the
        // current time (open_now).

        // Map MainProfileTab (flags2.20)
        if (!string.IsNullOrEmpty(source.MainProfileTab))
        {
            destination.MainTab = source.MainProfileTab switch
            {
                "Posts" => new TProfileTabPosts(),
                "Gifts" => new TProfileTabGifts(),
                "Media" => new TProfileTabMedia(),
                "Files" => new TProfileTabFiles(),
                "Music" => new TProfileTabMusic(),
                "Voice" => new TProfileTabVoice(),
                "Links" => new TProfileTabLinks(),
                "Gifs" => new TProfileTabGifs(),
                _ => null
            };

            if (destination.MainTab != null)
            {
                destination.Flags2 = destination.Flags2.SetBit(20);
            }
        }

        return destination;
    }

    private static TBusinessIntro? NormalizeBusinessIntro(TBusinessIntro? intro)
    {
        if (intro?.Sticker is TDocument { DcId: < MinAdvertisedDcId or > MaxAdvertisedDcId } sticker)
        {
            sticker.DcId = MediaDcId;
        }

        return intro;
    }
}
