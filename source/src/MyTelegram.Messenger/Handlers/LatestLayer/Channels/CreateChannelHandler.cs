using MyTelegram.Messenger.Services.Premium;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Create a <a href="https://corefork.telegram.org/api/channel">supergroup/channel</a>.
/// Possible errors
/// Code Type Description
/// 400 ADDRESS_INVALID The specified geopoint address is invalid.
/// 400 CHANNELS_ADMIN_LOCATED_TOO_MUCH The user has reached the limit of public geogroups.
/// 400 CHANNELS_TOO_MUCH You have joined too many channels/supergroups.
/// 400 CHAT_ABOUT_TOO_LONG Chat about too long.
/// 500 CHAT_INVALID Invalid chat.
/// 400 CHAT_TITLE_EMPTY No chat title provided.
/// 400 TTL_PERIOD_INVALID The specified TTL period is invalid.
/// 406 USER_RESTRICTED You're spamreported, you can't create channels or chats.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.createChannel"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class CreateChannelHandler(ICommandBus commandBus, IIdGenerator idGenerator, IRandomHelper randomHelper, IMongoDatabase mongoDatabase, IPremiumLimitGuard premiumLimitGuard) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestCreateChannel, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestCreateChannel obj)
    {
        // The creator becomes a member, so channels_limit applies.
        await premiumLimitGuard.EnsureCanJoinChannelAsync(input.UserId);

        var ttl = obj.TtlPeriod;
        var ttlFromDefaultSetting = false;
        if (ttl == null || ttl == 0)
        {
            ttlFromDefaultSetting = ttl.HasValue;
        }

        var channelId = await idGenerator.NextLongIdAsync(IdType.ChannelId);
        var accessHash = randomHelper.NextInt64();
        var date = DateTime.UtcNow.ToTimestamp();
        var megagroup = obj.Megagroup;
        if (!obj.Broadcast)
        {
            megagroup = true;
        }

        GeoPoint? geoPoint = null;
        switch (obj.GeoPoint)
        {
            case TInputGeoPoint inputGeoPoint:
                geoPoint = new GeoPoint(inputGeoPoint.Lat, inputGeoPoint.Long, inputGeoPoint.AccuracyRadius);
                break;
        }

        var command = new CreateChannelCommand(ChannelId.Create(channelId), input.ToRequestInfo(), channelId, input.UserId, obj.Broadcast, megagroup, obj.Title, obj.About ?? string.Empty, geoPoint, obj.Address, accessHash, date, randomHelper.NextInt64(), new TMessageActionChannelCreate { Title = obj.Title }, ttl, false, null, null, null, ttlFromDefaultSetting: ttlFromDefaultSetting, forum: obj.Forum);
        await commandBus.PublishAsync(command);
        if (obj.Forum)
        {
            await ForumTopicHelper.EnsureGeneralTopicAsync(mongoDatabase, channelId, input.UserId);
        }

        return null !;
    }
}
