using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Resolve a <a href="https://corefork.telegram.org/api/business#business-chat-links">business chat deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 CHATLINK_SLUG_EMPTY The specified slug is empty.
/// 400 CHATLINK_SLUG_EXPIRED The specified business chat link has expired.
/// <para>Anyone may resolve a link; the answer carries the business account so the client can open the chat
/// with the message pre-filled. The owner opening their own link does not count as a view.</para>
/// <para><c>See <a href="https://corefork.telegram.org/method/account.resolveBusinessChatLink"/> </c></para>
/// </summary>
internal sealed class ResolveBusinessChatLinkHandler(
    IBusinessChatLinkStore chatLinkStore,
    IUserConverterService userConverterService)
    : RpcResultObjectHandler<RequestResolveBusinessChatLink, IResolvedBusinessChatLinks>
{
    protected override async Task<IResolvedBusinessChatLinks> HandleCoreAsync(IRequestInput input,
        RequestResolveBusinessChatLink obj)
    {
        var slug = chatLinkStore.ParseSlug(obj.Slug);
        if (slug.Length == 0)
        {
            RpcErrors.RpcErrors400.ChatlinkSlugEmpty.ThrowRpcError();
        }

        var existing = await chatLinkStore.GetAsync(slug);
        if (existing == null)
        {
            RpcErrors.RpcErrors400.ChatlinkSlugExpired.ThrowRpcError();
        }

        var link = existing!.UserId == input.UserId
            ? existing
            : await chatLinkStore.ResolveAsync(slug, true) ?? existing;
        var owner = await userConverterService.GetUserAsync(input, link.UserId, layer: input.Layer);

        return new TResolvedBusinessChatLinks
        {
            Peer = new TPeerUser { UserId = link.UserId },
            Message = link.Message,
            Entities = link.Entities,
            Chats = [],
            Users = new TVector<IUser>(owner)
        };
    }
}
