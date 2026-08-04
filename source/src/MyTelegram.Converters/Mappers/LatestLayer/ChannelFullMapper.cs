namespace MyTelegram.Converters.Mappers.LatestLayer;

internal sealed class ChannelFullMapper
    : IObjectMapper<IChannelFullReadModel, TChannelFull>,
        ILayeredMapper,
        ITransientDependency
{
    public int Layer => Layers.LayerLatest;


    public TChannelFull Map(IChannelFullReadModel source)
    {
        return Map(source, new TChannelFull());
    }

    public TChannelFull Map(
        IChannelFullReadModel source,
        TChannelFull destination
    )
    {
        destination.Id = source.ChannelId;
        destination.About = source.About ?? string.Empty;

        destination.CanViewParticipants = source.CanViewParticipants;
        destination.CanSetUsername = source.CanSetUserName;
        destination.CanSetStickers = source.CanSetStickers;
        destination.HiddenPrehistory = source.HiddenPreHistory;
        destination.CanViewStats = source.CanViewStats;
        destination.StatsDc = MyTelegramConsts.MainDcId;
        destination.CanSetLocation = source.CanSetLocation;
        destination.AdminsCount = source.AdminsCount;
        destination.KickedCount = source.KickedCount;
        destination.BannedCount = source.BannedCount;
        destination.OnlineCount = source.OnlineCount;
        destination.ReadInboxMaxId = source.ReadInboxMaxId;
        destination.ReadOutboxMaxId = source.ReadOutboxMaxId;
        destination.UnreadCount = source.UnreadCount;
        destination.MigratedFromChatId = source.MigratedFromChatId;
        destination.MigratedFromMaxId = source.MigratedFromMaxId;
        destination.PinnedMsgId = source.PinnedMsgId;
        destination.AvailableMinId = source.AvailableMinId;
        destination.FolderId = source.FolderId;
        destination.LinkedChatId = source.LinkedChatId;
        destination.SlowmodeSeconds = source.SlowModeSeconds;
        destination.SlowmodeNextSendDate = source.SlowModeNextSendDate;
        switch (source.ReactionType)
        {
            case ReactionType.ReactionNone:
                // Default to all reactions enabled (ReactionNone means not explicitly set)
                destination.AvailableReactions = new TChatReactionsAll();
                break;
            case ReactionType.ReactionAll:
                destination.AvailableReactions = new TChatReactionsAll
                {
                    AllowCustom = source.AllowCustomReaction
                };
                break;
            case ReactionType.ReactionSome:
                // An empty whitelist means reactions are disabled entirely (chatReactionsNone).
                // Leaving the field null would be read by clients as "no restriction".
                destination.AvailableReactions = source.AvailableReactions?.Count > 0
                    ? new TChatReactionsSome
                    {
                        Reactions = new TVector<IReaction>(source.AvailableReactions.Select(p => new TReactionEmoji
                        {
                            Emoticon = p
                        }))
                    }
                    : new TChatReactionsNone();

                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        destination.Antispam = source.AntiSpam;
        destination.TranslationsDisabled = false;
        if (source.TtlPeriod != 0)
        {
            destination.TtlPeriod = source.TtlPeriod;
        }

        destination.ParticipantsHidden = source.ParticipantsHidden;

        // Map MainProfileTab (flags2.22)
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
                destination.Flags2 = destination.Flags2.SetBit(22);
            }
        }

        return destination;
    }
}