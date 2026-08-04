using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Fetch the full list of <a href="https://corefork.telegram.org/api/saved-messages#tags">saved message tags</a> created by the user.
/// Possible errors
/// Code Type Description
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getSavedReactionTags"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetSavedReactionTagsHandler(
    IPeerHelper peerHelper,
    ISavedReactionTagAppService savedReactionTagAppService)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetSavedReactionTags, MyTelegram.Schema.Messages.ISavedReactionTags>
{
    protected override async Task<MyTelegram.Schema.Messages.ISavedReactionTags> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetSavedReactionTags obj)
    {
        // The optional peer selects a single saved dialog; tags themselves are owned by the user,
        // so it only needs validating.
        if (obj.Peer != null)
        {
            _ = peerHelper.GetPeer(obj.Peer, input.UserId);
        }

        var tags = await savedReactionTagAppService.GetTagsAsync(input.UserId);

        var hash = TelegramHashHelper.GetVectorHash(tags.SelectMany(t => new[]
        {
            t.Reaction.GetReactionId(),
            t.Title == null ? 0L : TelegramHashHelper.GetStringNumber(t.Title),
            t.Count
        }));

        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TSavedReactionTagsNotModified();
        }

        return new TSavedReactionTags
        {
            Tags = new TVector<ISavedReactionTag>(tags.Select(t => (ISavedReactionTag)new TSavedReactionTag
            {
                Reaction = t.Reaction,
                Title = t.Title,
                Count = t.Count
            })),
            Hash = hash
        };
    }
}
