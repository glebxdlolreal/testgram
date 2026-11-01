using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Delete a <a href="https://corefork.telegram.org/api/business#business-chat-links">business chat deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 CHATLINK_SLUG_EMPTY The specified slug is empty.
/// 400 CHATLINK_SLUG_EXPIRED The specified business chat link has expired.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.deleteBusinessChatLink"/> </c></para>
/// </summary>
internal sealed class DeleteBusinessChatLinkHandler(IBusinessChatLinkStore chatLinkStore)
    : RpcResultObjectHandler<RequestDeleteBusinessChatLink, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, RequestDeleteBusinessChatLink obj)
    {
        var slug = chatLinkStore.ParseSlug(obj.Slug);
        if (slug.Length == 0)
        {
            RpcErrors.RpcErrors400.ChatlinkSlugEmpty.ThrowRpcError();
        }

        // Deleting needs no Premium: a lapsed subscriber must still be able to remove old links.
        if (!await chatLinkStore.DeleteAsync(input.UserId, slug))
        {
            RpcErrors.RpcErrors400.ChatlinkSlugExpired.ThrowRpcError();
        }

        return new TBoolTrue();
    }
}
