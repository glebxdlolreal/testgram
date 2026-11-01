using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;
/// <summary>
/// Enable or disable <a href="https://corefork.telegram.org/api/forum">forum functionality</a> in a supergroup.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHAT_ADMIN_REQUIRED You must be an admin in this chat to do this.
/// 400 CHAT_DISCUSSION_UNALLOWED You can't enable forum topics in a discussion group linked to a channel.
/// 400 CHAT_ID_INVALID The provided chat id is invalid.
/// 400 CHAT_NOT_MODIFIED No changes were made to chat information because the new information you passed is identical to the current information.
/// <para><c>See <a href="https://corefork.telegram.org/method/channels.toggleForum"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ToggleForumHandler(
    IMongoDatabase mongoDatabase,
    IPeerHelper peerHelper,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IChatConverterService chatConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Channels.RequestToggleForum, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Channels.RequestToggleForum obj)
    {
        await channelAdminRightsChecker.ThrowIfNotChannelOwnerAsync(obj.Channel, input.UserId);

        IInputPeer inputPeer;
        if (obj.Channel is TInputChannel inputChannel)
        {
            inputPeer = new TInputPeerChannel { ChannelId = inputChannel.ChannelId, AccessHash = inputChannel.AccessHash };
        }
        else if (obj.Channel is TInputChannelFromMessage inputChannelFromMessage)
        {
            inputPeer = new TInputPeerChannelFromMessage
            {
                Peer = inputChannelFromMessage.Peer,
                MsgId = inputChannelFromMessage.MsgId,
                ChannelId = inputChannelFromMessage.ChannelId
            };
        }
        else
        {
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();
            return null!;
        }

        var peer = peerHelper.GetPeer(inputPeer, input.UserId);
        if (peer == null || peer.PeerType != PeerType.Channel)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        var channelId = peer.PeerId;
        var collection = mongoDatabase.GetCollection<BsonDocument>("eventflow-channelreadmodel");
        var channelDoc = await collection.Find(Builders<BsonDocument>.Filter.Eq("ChannelId", channelId)).FirstOrDefaultAsync();

        if (channelDoc == null)
            RpcErrors.RpcErrors400.ChannelInvalid.ThrowRpcError();

        if (channelDoc.Contains("Broadcast") && channelDoc["Broadcast"].AsBoolean)
            RpcErrors.RpcErrors400.ChatIdInvalid.ThrowRpcError();

        if (channelDoc.Contains("LinkedChatId") && !channelDoc["LinkedChatId"].IsBsonNull)
            RpcErrors.RpcErrors400.ChatDiscussionUnallowed.ThrowRpcError();

        var currentForum = channelDoc.Contains("Forum") && channelDoc["Forum"].AsBoolean;
        var currentTabs = channelDoc.Contains("ForumTabs") && channelDoc["ForumTabs"].AsBoolean;
        var newTabs = obj.Enabled && obj.Tabs;
        if (currentForum == obj.Enabled && currentTabs == newTabs)
            RpcErrors.RpcErrors400.ChatNotModified.ThrowRpcError();

        var filter = Builders<BsonDocument>.Filter.Eq("ChannelId", channelId);
        var update = Builders<BsonDocument>.Update
            .Set("Forum", obj.Enabled)
            .Set("ForumTabs", newTabs);
        await collection.UpdateOneAsync(filter, update);

        // Create General topic when enabling forum
        if (obj.Enabled)
        {
            await ForumTopicHelper.EnsureGeneralTopicAsync(mongoDatabase, channelId, input.UserId);
        }

        // Log to admin log
        await AdminLogHelper.LogToggleForum(mongoDatabase, channelId, input.UserId, obj.Enabled);

        // Get updated channel; Forum and ForumTabs are set below because the cached read model may
        // not have seen the direct write yet.
        var channel = await chatConverterService.GetChannelAsync(input, channelId, false, null, input.Layer);
        if (channel is TChannel tChannel)
        {
            tChannel.Forum = obj.Enabled;
            tChannel.ForumTabs = newTabs;
        }

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat> { channel },
            Date = CurrentDate
        };
    }
}
