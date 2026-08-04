using System.Globalization;
using EventFlow.Exceptions;
using MyTelegram.Domain.Aggregates.UserConfig;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Mark <a href="https://corefork.telegram.org/api/reactions">message reactions »</a> as read
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readReactions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadReactionsHandler(
    IPtsHelper ptsHelper,
    IPeerHelper peerHelper,
    ICommandBus commandBus) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadReactions, MyTelegram.Schema.Messages.IAffectedHistory>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedHistory> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestReadReactions obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);
        var savedPeer = obj.SavedPeerId == null ? null : peerHelper.GetPeer(obj.SavedPeerId, input.UserId);
        var key = ReactionReadState.GetKey(peer, obj.TopMsgId, savedPeer);
        var command = new UpdateUserConfigCommand(
            UserConfigId.Create(input.UserId, key),
            input.ToRequestInfo(),
            input.UserId,
            key,
            CurrentDate.ToString(CultureInfo.InvariantCulture));
        await commandBus.PublishAsync(command);

        // Clear the dialog badge. A per-topic or per-saved-dialog read still clears the whole dialog
        // counter, because the counter itself is not tracked per topic.
        try
        {
            await commandBus.PublishAsync(new ReadUnreadReactionsCommand(DialogId.Create(input.UserId, peer)));
        }
        catch (DomainError)
        {
            // No dialog aggregate (for example a legacy chat): nothing to clear.
        }

        // Advance pts so the client's difference loop notices the read state on other sessions.
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var currentPts = ptsHelper.GetCachedPts(ownerPeerId);
        var pts = await ptsHelper.IncrementPtsAsync(ownerPeerId, currentPts, 1, input.PermAuthKeyId);

        return new TAffectedHistory
        {
            Pts = pts,
            PtsCount = 1
        };
    }
}
