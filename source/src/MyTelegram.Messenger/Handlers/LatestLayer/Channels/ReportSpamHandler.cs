using MyTelegram.Messenger.Services.AntiSpam;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Records spam reports from a supergroup administrator; does not delete or ban.
/// See https://core.telegram.org/method/channels.reportSpam
/// </summary>
internal sealed class ReportSpamHandler(
    IAntiSpamAccessService accessService,
    IAntiSpamReportStore reportStore,
    IQueryProcessor queryProcessor) : RpcResultObjectHandler<Schema.Channels.RequestReportSpam, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, Schema.Channels.RequestReportSpam obj)
    {
        var channel = await accessService.RequireAdminAsync(input, obj.Channel, requireDeleteMessages: false);
        if (obj.Id == null || obj.Id.Count is 0 or > 100 || obj.Id.Any(id => id <= 0))
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        var participant = await accessService.ResolveParticipantAsync(input, obj.Participant);
        var ids = obj.Id!.Distinct().ToArray();
        var messageIds = ids.Select(id => MessageId.Create(channel.ChannelId, id).Value).ToList();
        var messages = await queryProcessor.ProcessAsync(new GetMessagesByIdListQuery(messageIds));
        if (messages.Count != ids.Length || messages.Any(message =>
                message.OwnerPeerId != channel.ChannelId || message.ToPeerId != channel.ChannelId ||
                message.ToPeerType != PeerType.Channel || !ids.Contains(message.MessageId) ||
                !MatchesSender(message, participant)))
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        foreach (var id in ids)
            await reportStore.SaveAsync(input.UserId, channel.ChannelId, id, false, participant.PeerId);
        return new TBoolTrue();
    }

    internal static bool MatchesSender(IMessageReadModel message, Peer participant)
    {
        var sender = message.SendAs ?? new Peer(PeerType.User, message.SenderPeerId);
        var type = participant.PeerType == PeerType.Self ? PeerType.User : participant.PeerType;
        return sender.PeerId == participant.PeerId && sender.PeerType == type;
    }
}
