using MyTelegram.Messenger.Services.AntiSpam;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Channels;

/// <summary>
/// Enable or disable native antispam. See https://core.telegram.org/method/channels.toggleAntiSpam
/// </summary>
internal sealed class ToggleAntiSpamHandler(
    IAntiSpamAccessService accessService,
    IAntiSpamActorService actorService,
    IAppConfigHelper appConfig,
    ICommandBus commandBus) : RpcResultObjectHandler<RequestToggleAntiSpam, IUpdates>
{
    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestToggleAntiSpam obj)
    {
        var channel = await accessService.RequireAdminAsync(input, obj.Channel);
        if (obj.Enabled)
        {
            if ((channel.ParticipantsCount ?? 0) < appConfig.GetInt32Value("telegram_antispam_group_size_min", 200))
                RpcErrors.RpcErrors400.ParticipantsTooFew.ThrowRpcError();
            await actorService.EnsureAvailableAsync();
        }
        await commandBus.PublishAsync(new ToggleAntiSpamCommand(ChannelId.Create(channel.ChannelId), input.ToRequestInfo(), obj.Enabled));
        // The aggregate checks duplicate state; its event is answered by QueryServer.
        return null!;
    }
}
