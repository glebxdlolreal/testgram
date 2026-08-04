using System.Globalization;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Domain.Aggregates.UserConfig;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Mark the votes cast on your own polls as read, clearing <c>unread_poll_votes_count</c>.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.readPollVotes"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ReadPollVotesHandler(
    IPtsHelper ptsHelper,
    IPeerHelper peerHelper,
    ICommandBus commandBus)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestReadPollVotes, MyTelegram.Schema.Messages.IAffectedHistory>
{
    protected override async Task<MyTelegram.Schema.Messages.IAffectedHistory> HandleCoreAsync(IRequestInput input,
        MyTelegram.Schema.Messages.RequestReadPollVotes obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // Reading is recorded as a timestamp cutoff rather than a flag per vote, so a vote
        // counts as unread precisely when it is newer than this value.
        var key = PollVoteReadState.GetKey(peer, obj.TopMsgId);
        var command = new UpdateUserConfigCommand(
            UserConfigId.Create(input.UserId, key),
            input.ToRequestInfo(),
            input.UserId,
            key,
            CurrentDate.ToString(CultureInfo.InvariantCulture));
        await commandBus.PublishAsync(command);

        // Clear the dialog badge as well. Topic-scoped reads leave it alone: the counter is
        // per dialog, so zeroing it from one topic would hide unread votes in the others.
        if (obj.TopMsgId == null)
        {
            await commandBus.PublishAsync(new ReadPollVotesCommand(DialogId.Create(input.UserId, peer)));
        }

        return new TAffectedHistory
        {
            Pts = ptsHelper.GetCachedPts(peer.PeerId),
            PtsCount = 0
        };
    }
}
