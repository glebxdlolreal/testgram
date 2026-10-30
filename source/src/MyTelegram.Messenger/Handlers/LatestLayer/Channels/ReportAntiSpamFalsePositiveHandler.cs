using MyTelegram.Messenger.Services.AntiSpam;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Report a native antispam false positive without restoring the message.
/// See https://core.telegram.org/method/channels.reportAntiSpamFalsePositive
/// </summary>
internal sealed class ReportAntiSpamFalsePositiveHandler(
    IAntiSpamAccessService accessService,
    IAntiSpamActorService actorService,
    IAntiSpamReportStore reportStore) : RpcResultObjectHandler<RequestReportAntiSpamFalsePositive, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestReportAntiSpamFalsePositive obj)
    {
        var channel = await accessService.RequireAdminAsync(input, obj.Channel);
        if (obj.MsgId <= 0) RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        await actorService.EnsureAvailableAsync();
        if (!await reportStore.HasAntiSpamDeletionAsync(channel.ChannelId, obj.MsgId))
            RpcErrors.RpcErrors400.MsgIdInvalid.ThrowRpcError();
        await reportStore.SaveAsync(input.UserId, channel.ChannelId, obj.MsgId, true);
        return new TBoolTrue();
    }
}
