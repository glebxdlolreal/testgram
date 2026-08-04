using MyTelegram.Messenger.Helpers;
using MyTelegram.Schema.Messages;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Get recently used message reactions.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getRecentReactions"/> </c></para>
/// </summary>
internal sealed class GetRecentReactionsHandler(
    IQueryProcessor queryProcessor,
    IReactionListAppService reactionListAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetRecentReactions, MyTelegram.Schema.Messages.IReactions>
{
    private const string RecentKey = "recent_reactions";

    protected override async Task<MyTelegram.Schema.Messages.IReactions> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetRecentReactions obj)
    {
        var limit = obj.Limit > 0 ? obj.Limit : 8;
        var config = await queryProcessor.ProcessAsync(new GetUserConfigByKeyQuery(input.UserId, RecentKey));

        List<IReaction> reactions;
        if (config?.Value is { Length: > 0 })
        {
            reactions = config.Value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Take(limit)
                .Select(e => (IReaction)new TReactionEmoji { Emoticon = e })
                .ToList();
        }
        else
        {
            // No personal history yet: fall back to the server catalogue.
            reactions = await reactionListAppService.GetActiveEmojiReactionsAsync(limit);
        }

        var hash = TelegramHashHelper.GetVectorHash(reactions.Select(r => r.GetReactionId()));
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TReactionsNotModified();
        }

        return new TReactions { Hash = hash, Reactions = new TVector<IReaction>(reactions) };
    }
}
