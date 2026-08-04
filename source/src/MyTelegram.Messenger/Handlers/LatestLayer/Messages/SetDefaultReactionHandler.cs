using MyTelegram.Domain.Aggregates.UserConfig;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Change default emoji reaction to use in the quick reaction menu.
/// Possible errors
/// Code Type Description
/// 400 REACTION_INVALID The specified reaction is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.setDefaultReaction"/> </c></para>
/// </summary>
internal sealed class SetDefaultReactionHandler(
    ICommandBus commandBus,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestSetDefaultReaction, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestSetDefaultReaction obj)
    {
        // An unusable reaction is rejected rather than silently replaced with a thumbs up.
        var value = obj.Reaction switch
        {
            TReactionEmoji { Emoticon: { Length: > 0 } emoticon } => emoticon,
            TReactionCustomEmoji custom => $"custom:{custom.DocumentId}",
            _ => null
        };

        if (value == null)
        {
            RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
        }

        var key = ((int)UserConfigType.DefaultReaction).ToString();
        var command = new UpdateUserConfigCommand(UserConfigId.Create(input.UserId, key), input.ToRequestInfo(), input.UserId, key, value!);
        await commandBus.PublishAsync(command);

        // The default reaction is served through appConfig, so other sessions are told to refetch it.
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateConfig()),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Seq = 0
        };
        await objectMessageSender.PushMessageToPeerAsync(new Peer(PeerType.User, input.UserId), updates,
            excludeAuthKeyId: input.PermAuthKeyId);

        return new TBoolTrue();
    }
}
