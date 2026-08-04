using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Fetch a default recommended list of <a href="https://corefork.telegram.org/api/saved-messages#tags">saved message tag reactions</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getDefaultTagReactions"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetDefaultTagReactionsHandler(IReactionListAppService reactionListAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetDefaultTagReactions, MyTelegram.Schema.Messages.IReactions>
{
    private const int DefaultTagReactionsLimit = 12;

    protected override async Task<MyTelegram.Schema.Messages.IReactions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetDefaultTagReactions obj)
    {
        var reactions = await reactionListAppService.GetActiveEmojiReactionsAsync(DefaultTagReactionsLimit);
        var hash = TelegramHashHelper.GetVectorHash(reactions.Select(r => r.GetReactionId()));

        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TReactionsNotModified();
        }

        return new TReactions
        {
            Hash = hash,
            Reactions = new TVector<IReaction>(reactions)
        };
    }
}
