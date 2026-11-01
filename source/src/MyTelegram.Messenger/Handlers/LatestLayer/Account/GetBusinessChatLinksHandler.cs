using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// List all created <a href="https://corefork.telegram.org/api/business#business-chat-links">business chat deep links »</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.getBusinessChatLinks"/> </c></para>
/// </summary>
internal sealed class GetBusinessChatLinksHandler(
    IBusinessChatLinkStore chatLinkStore,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<RequestGetBusinessChatLinks, IBusinessChatLinks>
{
    protected override async Task<IBusinessChatLinks> HandleCoreAsync(IRequestInput input,
        RequestGetBusinessChatLinks obj)
    {
        var links = await chatLinkStore.GetByUserAsync(input.UserId);
        var domain = options.CurrentValue.JoinChatDomain;
        return new TBusinessChatLinks
        {
            Links = new TVector<IBusinessChatLink>(links.Select(p =>
                (IBusinessChatLink)BusinessChatLinkHelper.ToTl(p, domain))),
            Chats = [],
            Users = []
        };
    }
}
