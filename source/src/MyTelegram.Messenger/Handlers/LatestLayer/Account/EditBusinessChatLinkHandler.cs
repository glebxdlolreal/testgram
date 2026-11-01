using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Edit a created <a href="https://corefork.telegram.org/api/business#business-chat-links">business chat deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 CHATLINK_SLUG_EMPTY The specified slug is empty.
/// 400 CHATLINK_SLUG_EXPIRED The specified business chat link has expired.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.editBusinessChatLink"/> </c></para>
/// </summary>
internal sealed class EditBusinessChatLinkHandler(
    IUserAppService userAppService,
    IMessageAppService messageAppService,
    IBusinessChatLinkStore chatLinkStore,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<RequestEditBusinessChatLink, IBusinessChatLink>
{
    protected override async Task<IBusinessChatLink> HandleCoreAsync(IRequestInput input,
        RequestEditBusinessChatLink obj)
    {
        await userAppService.CheckAccountPremiumStatusAsync(input.UserId);
        var slug = chatLinkStore.ParseSlug(obj.Slug);
        if (slug.Length == 0)
        {
            RpcErrors.RpcErrors400.ChatlinkSlugEmpty.ThrowRpcError();
        }

        var (title, message, entities) = await BusinessChatLinkHelper.ReadAsync(input, obj.Link, messageAppService);
        var link = await chatLinkStore.UpdateAsync(input.UserId, slug, title, message, entities);
        if (link == null)
        {
            RpcErrors.RpcErrors400.ChatlinkSlugExpired.ThrowRpcError();
        }

        return BusinessChatLinkHelper.ToTl(link!, options.CurrentValue.JoinChatDomain);
    }
}
