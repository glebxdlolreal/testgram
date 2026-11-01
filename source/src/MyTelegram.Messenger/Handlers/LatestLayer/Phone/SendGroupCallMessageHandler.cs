using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;
using MyTelegram.Services.Services;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class SendGroupCallMessageHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IIdGenerator idGenerator,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<RequestSendGroupCallMessage, IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendGroupCallMessage obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Active || !groupCall.MessagesEnabled)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        var fromPeer = obj.SendAs != null
            ? peerHelper.GetPeer(obj.SendAs, input.UserId)
            : new Peer(PeerType.User, input.UserId);
        if (fromPeer == null)
        {
            RpcErrors.RpcErrors400.PeerIdInvalid.ThrowRpcError();
            return null!;
        }

        // The message is styled text, so its entities are checked and legally nested before being
        // stored and echoed to every participant. See https://corefork.telegram.org/api/entities
        MessageEntityValidator.Validate(obj.Message.Text, obj.Message.Entities);
        obj.Message.Entities = new TVector<IMessageEntity>(
            MessageEntityNormalizer.Normalize(obj.Message.Text, obj.Message.Entities));

        var existing = groupCall.Messages.FirstOrDefault(message =>
            message.RandomId == obj.RandomId &&
            message.FromPeerId == fromPeer.PeerId &&
            message.FromPeerType == (int)fromPeer.PeerType);
        if (existing != null && !existing.Deleted)
        {
            return GroupCallStateHelper.Updates(GroupCallStateHelper.CreateMessageUpdate(
                groupCall,
                ToGroupCallMessage(existing, obj.Message)));
        }

        var messageId = (int)await idGenerator.NextIdAsync(IdType.MessageId, input.UserId);
        var currentDate = GroupCallStateHelper.CurrentDate();
        var groupCallMessage = new TGroupCallMessage
        {
            Id = messageId,
            FromId = GroupCallStateHelper.ToPeer(fromPeer.PeerType, fromPeer.PeerId),
            Date = currentDate,
            Message = obj.Message,
            PaidMessageStars = obj.AllowPaidStars,
            FromAdmin = groupCall.CreatorId == input.UserId
        };

        groupCall.Messages.Add(new GroupCallMessageDoc
        {
            Id = messageId,
            RandomId = obj.RandomId,
            FromPeerId = fromPeer.PeerId,
            FromPeerType = (int)fromPeer.PeerType,
            Date = currentDate,
            Text = obj.Message.Text,
            MessageBytes = obj.Message.ToBytes(),
            PaidMessageStars = obj.AllowPaidStars,
            FromAdmin = groupCall.CreatorId == input.UserId
        });
        groupCall.Version++;
        await _groupCallCollection.ReplaceOneAsync(filter, groupCall);

        var updates = GroupCallStateHelper.Updates(GroupCallStateHelper.CreateMessageUpdate(groupCall, groupCallMessage));
        await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
            objectMessageSender,
            groupCall,
            updates,
            input.UserId);

        return updates;
    }

    private static TGroupCallMessage ToGroupCallMessage(GroupCallMessageDoc doc, ITextWithEntities fallbackMessage)
    {
        return new TGroupCallMessage
        {
            Id = doc.Id,
            FromId = GroupCallStateHelper.ToPeer((PeerType)doc.FromPeerType, doc.FromPeerId),
            Date = doc.Date,
            Message = fallbackMessage,
            PaidMessageStars = doc.PaidMessageStars,
            FromAdmin = doc.FromAdmin
        };
    }
}
