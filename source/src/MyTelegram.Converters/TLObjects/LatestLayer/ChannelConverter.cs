using MyTelegram.Converters.Mappers;

namespace MyTelegram.Converters.TLObjects.LatestLayer;

internal sealed class ChannelConverter : IChannelConverter, ITransientDependency
{
    public int Layer => Layers.LayerLatest;

    public ILayeredChannel ToChannel(IChannelReadModel channelReadModel)
    {
        var channel = new TChannel();
        channel.Id = channelReadModel.ChannelId;
        channel.Title = channelReadModel.Title;
        channel.ParticipantsCount = channelReadModel.ParticipantsCount;
        channel.Broadcast = channelReadModel.Broadcast;
        // Fix for old channels without Megagroup flag: if not broadcast, it's a megagroup
        channel.Megagroup = channelReadModel.MegaGroup || (!channelReadModel.Broadcast && !channelReadModel.MegaGroup);

        channel.Verified = channelReadModel.Verified;
        channel.Fake = channelReadModel.Fake;
        channel.Scam = channelReadModel.Scam;
        channel.Signatures = channelReadModel.Signatures;
        channel.AccessHash = channelReadModel.AccessHash;
        channel.Username = channelReadModel.UserName;
        channel.Date = channelReadModel.Date;
        channel.SlowmodeEnabled = channelReadModel.SlowModeEnabled;

        channel.DefaultBannedRights = (channelReadModel.DefaultBannedRights ?? ChatBannedRights.CreateDefaultBannedRights())
            .ToChatBannedRights();

        channel.HasLink = channelReadModel.HasLink;
        channel.Noforwards = channelReadModel.NoForwards;
        channel.Color = channelReadModel.Color.ToPeerColor();
        channel.ProfileColor = channelReadModel.ProfileColor.ToPeerColor();
        channel.Forum = channelReadModel.Forum;

        // NOTE: channelReadModel.BackgroundEmojiId is the peer color's pattern emoji
        // (https://core.telegram.org/api/colors) and is already exposed to clients
        // inside channel.Color / channel.ProfileColor. It must NOT be mapped
        // onto channel.EmojiStatus, which is the channel's status emoji badge.

        channel.Level = 10;

        if (channelReadModel.EmojiStatus != null)
        {
            channel.EmojiStatus = new TEmojiStatus
            {
                DocumentId = channelReadModel.EmojiStatus.DocumentId,
                Until = channelReadModel.EmojiStatus.Until
            };
        }

        channel.SignatureProfiles = channelReadModel.SignatureProfiles;
        channel.SubscriptionUntilDate = channelReadModel.SubscriptionUntilDate;

        // The owner's view, disabled collectibles included; the converter service hides those from
        // everyone else.
        (channel.Username, channel.Usernames) =
            UsernameVectorBuilder.Build(channelReadModel.Usernames, channelReadModel.UserName, includeDisabled: true);

        channel.JoinRequest = channelReadModel.JoinRequest;
        channel.JoinToSend = channelReadModel.JoinToSend;

        // channel.autotranslation (flags2.15). Nothing set it before, so the toggle was invisible to
        // every client whatever the server stored, and it is this flag that lets a non-Premium member
        // translate the channel at all (Android TranslateController.isFeatureAvailable).
        // See https://corefork.telegram.org/api/translation#autotranslation-for-channels
        channel.Autotranslation = channelReadModel.Autotranslation;
        channel.Monoforum = channelReadModel.IsMonoforum;
        channel.BroadcastMessagesAllowed = channelReadModel.BroadcastMessagesAllowed;
        if (channelReadModel.LinkedMonoforumId.HasValue) channel.LinkedMonoforumId = channelReadModel.LinkedMonoforumId;
        if (channelReadModel.SendPaidMessagesStars.HasValue) channel.SendPaidMessagesStars = channelReadModel.SendPaidMessagesStars.Value;

        return channel;
    }
}
