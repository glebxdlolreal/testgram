using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get most used message reactions.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getTopReactions"/> </c></para>
/// </summary>
internal sealed class GetTopReactionsHandler(IReactionListAppService reactionListAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetTopReactions, MyTelegram.Schema.Messages.IReactions>
{
    protected override async Task<MyTelegram.Schema.Messages.IReactions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetTopReactions obj)
    {
        var limit = obj.Limit > 0 ? obj.Limit : 100;
        var reactions = await reactionListAppService.GetActiveEmojiReactionsAsync(limit);
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
