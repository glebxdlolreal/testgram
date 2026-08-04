using MyTelegram.Converters;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get poll results
/// Possible errors
/// Code Type Description
/// 400 MESSAGE_ID_INVALID The provided message id is invalid.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getPollResults"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPollResultsHandler(IQueryProcessor queryProcessor, IPeerHelper peerHelper, //ILayeredService<IPollConverter> layeredService,
 IPollConverterService pollConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetPollResults, MyTelegram.Schema.IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestGetPollResults obj)
    {
        var peer = peerHelper.GetPeer(obj.Peer);
        var pollId = await queryProcessor.ProcessAsync(new GetPollIdByMessageIdQuery(peer.PeerId, obj.MsgId));
        if (pollId == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        var pollReadModel = await queryProcessor.ProcessAsync(new GetPollQuery(pollId!.Value));
        if (pollReadModel == null)
        {
            RpcErrors.RpcErrors400.MessageIdInvalid.ThrowRpcError();
        }

        // The client already holds this exact state, so there is nothing to send back.
        if (obj.PollHash != 0 && obj.PollHash == PollHashHelper.ComputeHash(pollReadModel!))
        {
            return new TUpdates
            {
                Updates = [],
                Users = [],
                Chats = [],
                Date = CurrentDate,
                Seq = 0
            };
        }

        var pollAnswers = await queryProcessor.ProcessAsync(new GetPollAnswerVotersQuery(pollId.Value, input.UserId), default);

        IReadOnlyCollection<long>? recentVoterPeerIds = null;
        if (pollReadModel!.PublicVoters)
        {
            var recentVoters = await queryProcessor.ProcessAsync(
                new GetRecentPollVotersQuery(pollReadModel.PollId, MyTelegramConsts.MaxPollRecentVoters));
            recentVoterPeerIds = recentVoters.Select(p => p.VoterPeerId).Distinct().ToList();
        }

        // min: false — this answer goes to the requesting user, so the chosen flags matter.
        // A min result makes clients discard them.
        var updates = pollConverterService.ToPollUpdates(pollReadModel,
            pollAnswers?.Select(p => p.Option).ToArray() ?? [],
            input.Layer,
            min: false,
            peer: peer,
            msgId: obj.MsgId,
            recentVoterPeerIds: recentVoterPeerIds,
            selfUserId: input.UserId);
        return updates;
    }
}
