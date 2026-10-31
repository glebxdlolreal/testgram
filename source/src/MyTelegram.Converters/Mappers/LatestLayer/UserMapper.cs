namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class UserMapper
    : IObjectMapper<IUserReadModel, TUser>,
        ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;
    

    public TUser Map(IUserReadModel source)
    {
        return Map(source, new TUser());
    }

    public TUser Map(
        IUserReadModel source,
        TUser destination
    )
    {
        destination.Id = source.UserId;
        destination.Photo = new TUserProfilePhotoEmpty();
        destination.AccessHash = source.AccessHash;
        destination.Bot = source.Bot;

        // user.bot makes the constructor set the bot_info_version flag, and serializing it then
        // dereferences the value: a bot without one takes down every response it appears in. Only the
        // bots created through BotFather ever got the field, so it is defaulted here instead.
        destination.BotInfoVersion = source.BotInfoVersion ?? (source.Bot ? 1 : null);
        destination.Phone = source.PhoneNumber;
        destination.FirstName = source.FirstName;
        destination.LastName = source.LastName;
        destination.Fake = source.Fake;
        destination.Scam = source.Scam;
        destination.Verified = source.Verified;
        destination.Support = source.Support;
        destination.Premium = source.Premium;

        destination.Color = source.Color.ToPeerColor();
        destination.ProfileColor = source.ProfileColor.ToPeerColor();
        destination.ContactRequirePremium = source.GlobalPrivacySettings?.NewNoncontactPeersRequirePremium ?? false;
        if (source.GlobalPrivacySettings?.NoncontactPeersPaidStars > 0)
            destination.SendPaidMessagesStars = source.GlobalPrivacySettings.NoncontactPeersPaidStars;
        destination.BotHasMainApp = source.BotHasMainApp;
        destination.BotActiveUsers = source.BotActiveUsers;

        // The owner's view, disabled collectibles included; the converter service hides those from
        // everyone else.
        (destination.Username, destination.Usernames) =
            UsernameVectorBuilder.Build(source.Usernames, source.UserName, includeDisabled: true);

        // Read BotBusiness from MongoDB directly since it's not in IUserReadModel
        // This is a temporary solution until BotBusiness is added to the read model
        if (source.Bot)
        {
            // BotBusiness flag will be set by UserConverterService
            destination.BotBusiness = false; // Default value, will be overridden if needed
        }

        return destination;
    }
}
