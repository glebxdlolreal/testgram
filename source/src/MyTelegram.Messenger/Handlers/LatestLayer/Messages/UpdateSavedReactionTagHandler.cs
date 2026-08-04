namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Update the <a href="https://corefork.telegram.org/api/saved-messages#tags">description of a saved message tag »</a>.
/// Possible errors
/// Code Type Description
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// 400 REACTION_INVALID The specified reaction is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.updateSavedReactionTag"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class UpdateSavedReactionTagHandler(
    IUserAppService userAppService,
    ISavedReactionTagAppService savedReactionTagAppService,
    IObjectMessageSender objectMessageSender)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestUpdateSavedReactionTag, IBool>
{
    private const int TagTitleMaxLength = 12;

    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestUpdateSavedReactionTag obj)
    {
        if (obj.Reaction is not (TReactionEmoji or TReactionCustomEmoji))
        {
            RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
        }

        if (obj.Reaction is TReactionEmoji { Emoticon: null or "" })
        {
            RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
        }

        // Naming a tag is a Premium-only feature.
        var user = await userAppService.GetAsync(input.UserId);
        if (user == null)
        {
            RpcErrors.RpcErrors400.UserIdInvalid.ThrowRpcError();
        }

        if (!user!.Premium)
        {
            RpcErrors.RpcErrors403.PremiumAccountRequired.ThrowRpcError();
        }

        var title = obj.Title;
        if (title != null && title.Length > TagTitleMaxLength)
        {
            RpcErrors.RpcErrors400.ReactionInvalid.ThrowRpcError();
        }

        // An absent or empty title clears the tag name.
        await savedReactionTagAppService.SetTitleAsync(input.UserId, obj.Reaction, title);

        // Tell the user's other sessions to refetch the tag list.
        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateSavedReactionTags()),
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
