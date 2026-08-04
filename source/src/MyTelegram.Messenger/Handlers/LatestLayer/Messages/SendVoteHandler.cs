using MyTelegram.Domain.Aggregates.Poll;
using MyTelegram.Domain.Aggregates.UserConfig;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Vote in a <a href="https://corefork.telegram.org/constructor/poll">poll</a>Starting from layer 159, the vote will be sent from the peer specified using <a href="https://corefork.telegram.org/method/messages.saveDefaultSendAs">messages.saveDefaultSendAs</a>.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 MESSAGE_POLL_CLOSED Poll closed.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 OPTIONS_TOO_MUCH Too many options provided.
/// 400 OPTION_INVALID Invalid option selected.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 REVOTE_NOT_ALLOWED You cannot change your vote.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.sendVote"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class SendVoteHandler(
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IMessageAppService messageAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSendVote, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestSendVote obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer);
        var pollId = await queryProcessor.ProcessAsync(new GetPollIdByMessageIdQuery(peer.PeerId, obj.MsgId), default);
        if (pollId == null)
        {
            RpcErrors.RpcErrors400.PollQuestionInvalid.ThrowRpcError();
        }

        var pollReadModel = await queryProcessor.ProcessAsync(new GetPollQuery(pollId!.Value));
        if (pollReadModel == null)
        {
            RpcErrors.RpcErrors400.PollQuestionInvalid.ThrowRpcError();
        }

        if (pollReadModel!.Closed)
        {
            RpcErrors.RpcErrors400.MessagePollClosed.ThrowRpcError();
        }

        var voterPeerId = await GetVoterPeerIdAsync(input, peer);

        var command = new VoteCommand(PollId.With(pollReadModel.Id), input.ToRequestInfo(), voterPeerId, obj.Options.Select(p => p).ToList());
        await commandBus.PublishAsync(command, default);
        return null!;
    }

    /// <summary>
    /// Resolves who the vote is cast as. Since layer 159 a vote in a group follows the peer
    /// picked with <c>messages.saveDefaultSendAs</c>, so a user voting as a channel is recorded
    /// under that channel. Falls back to the user when nothing valid is configured.
    /// </summary>
    private async Task<long> GetVoterPeerIdAsync(IRequestInput input, Peer toPeer)
    {
        if (toPeer.PeerType != PeerType.Channel)
        {
            return input.UserId;
        }

        var userConfigReadModel = await queryProcessor.ProcessAsync(
            new GetUserConfigByKeyQuery(input.UserId, ((int)UserConfigType.SendAsPeer).ToString()));
        if (userConfigReadModel == null || !long.TryParse(userConfigReadModel.Value, out var sendAsPeerId))
        {
            return input.UserId;
        }

        var sendAsPeer = peerHelper.GetPeer(sendAsPeerId);

        // The stored peer may have gone stale (rights revoked, channel left), so it is
        // re-validated on every vote rather than trusted outright.
        return await messageAppService.IsValidSendAsPeerAsync(input.UserId, toPeer, sendAsPeer)
            ? sendAsPeer.PeerId
            : input.UserId;
    }
}
