using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema.Account;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Account;

/// <summary>
/// Create a <a href="https://corefork.telegram.org/api/business#business-chat-links">business chat deep link »</a>.
/// Possible errors
/// Code Type Description
/// 400 CHATLINKS_TOO_MUCH Too many <a href="https://corefork.telegram.org/api/business#business-chat-links">business chat links</a> were created, please delete some older links.
/// 403 PREMIUM_ACCOUNT_REQUIRED A premium account is required to execute this action.
/// <para><c>See <a href="https://corefork.telegram.org/method/account.createBusinessChatLink"/> </c></para>
/// </summary>
internal sealed class CreateBusinessChatLinkHandler(
    IUserAppService userAppService,
    IMessageAppService messageAppService,
    IBusinessChatLinkStore chatLinkStore,
    IOptionsMonitor<MyTelegramMessengerServerOptions> options)
    : RpcResultObjectHandler<RequestCreateBusinessChatLink, IBusinessChatLink>
{
    /// <summary>appConfig <c>business_chat_links_limit</c>.</summary>
    public const int ChatLinksLimit = 100;

    protected override async Task<IBusinessChatLink> HandleCoreAsync(IRequestInput input,
        RequestCreateBusinessChatLink obj)
    {
        await userAppService.CheckAccountPremiumStatusAsync(input.UserId);
        if (await chatLinkStore.CountAsync(input.UserId) >= ChatLinksLimit)
        {
            RpcErrors.RpcErrors400.ChatlinksTooMuch.ThrowRpcError();
        }

        var (title, message, entities) = await BusinessChatLinkHelper.ReadAsync(input, obj.Link, messageAppService);
        var link = await chatLinkStore.CreateAsync(input.UserId, title, message, entities);
        return BusinessChatLinkHelper.ToTl(link, options.CurrentValue.JoinChatDomain);
    }
}

internal static class BusinessChatLinkHelper
{
    /// <summary>The pre-filled text is an ordinary message draft, so the ordinary message limit applies.</summary>
    private const int MaxMessageLength = 4096;

    private const int MaxTitleLength = 32;

    public static async Task<(string? Title, string Message, TVector<IMessageEntity>? Entities)> ReadAsync(
        IRequestInput input, IInputBusinessChatLink link, IMessageAppService messageAppService)
    {
        if (link is not TInputBusinessChatLink l)
        {
            RpcErrors.RpcErrors400.MessageEmpty.ThrowRpcError();
            return default;
        }

        var message = l.Message ?? string.Empty;
        if (message.Length > MaxMessageLength)
        {
            RpcErrors.RpcErrors400.MessageTooLong.ThrowRpcError();
        }

        var title = string.IsNullOrWhiteSpace(l.Title) ? null : l.Title.Trim();
        if (title?.Length > MaxTitleLength)
        {
            title = title[..MaxTitleLength];
        }

        TVector<IMessageEntity>? entities = null;
        if (l.Entities?.Count > 0)
        {
            var processed = await messageAppService.ProcessMessageEntitiesAsync(message, l.Entities,
                input.UserId.ToUserPeer());
            entities = processed.Entities;
        }

        return (title, message, entities);
    }

    public static string ToLink(string domain, string slug)
    {
        var baseUrl = string.IsNullOrWhiteSpace(domain) ? "https://t.me" : domain.TrimEnd('/');
        return $"{baseUrl}/m/{slug}";
    }

    public static TBusinessChatLink ToTl(BusinessChatLinkItem link, string domain) => new()
    {
        Link = ToLink(domain, link.Slug),
        Message = link.Message,
        Entities = link.Entities,
        Title = link.Title,
        Views = link.Views
    };
}
