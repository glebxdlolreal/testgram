using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Report a <a href="https://corefork.telegram.org/api/reactions">message reaction</a>
/// Possible errors
/// Code Type Description
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USER_ID_INVALID The provided user ID is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.reportReaction"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReportReactionHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IMongoDatabase database) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReportReaction, IBool>
{
    private const string CollectionName = "reaction_reports";

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestReportReaction obj)
    {
        // Only reactions in channels/supergroups can be reported.
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        if (peer.PeerType != PeerType.Channel)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
        }

        var reportedPeer = peerHelper.GetPeer(obj.ReactionPeer, input.UserId);
        if (reportedPeer.PeerId == input.UserId)
        {
            // Reporting your own reaction is meaningless.
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        var messageReadModel = await queryProcessor.ProcessAsync(
            new GetMessageByPeerIdAndMessageIdQuery(peer.PeerId, obj.Id)) as MessageReadModel;
        if (messageReadModel == null)
        {
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        }

        // The reported peer must actually have reacted to this message.
        var hasReaction = messageReadModel!.RecentReactions2?
            .Any(r => r.SenderUserId == reportedPeer.PeerId) ?? false;
        if (!hasReaction)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        // Upsert keyed on reporter+message+target, so repeated reports do not pile up.
        await database.GetCollection<BsonDocument>(CollectionName).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id",
                $"reaction-report-{input.UserId}-{peer.PeerId}-{obj.Id}-{reportedPeer.PeerId}"),
            Builders<BsonDocument>.Update
                .Set("ReporterUserId", input.UserId)
                .Set("PeerId", peer.PeerId)
                .Set("MsgId", obj.Id)
                .Set("ReactedPeerId", reportedPeer.PeerId)
                .Set("Date", CurrentDate),
            new UpdateOptions { IsUpsert = true });

        return new TBoolTrue();
    }
}
