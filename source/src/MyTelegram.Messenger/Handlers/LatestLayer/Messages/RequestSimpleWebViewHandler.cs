using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Open a <a href="https://corefork.telegram.org/api/bots/webapps">bot mini app</a>.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 URL_INVALID Invalid URL provided.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.requestSimpleWebView"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class RequestSimpleWebViewHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper accessHashHelper,
    IWebViewSessionStore webViewSessionStore,
    ILayeredService<IWebViewResultUrlResponseConverter> webViewResultsLayeredService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestRequestSimpleWebView, MyTelegram.Schema.IWebViewResult>
{
    protected override async Task<MyTelegram.Schema.IWebViewResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestRequestSimpleWebView obj)
    {
        if (obj.Bot is not TInputUser inputBot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            return null!;
        }

        await accessHashHelper.CheckAccessHashAsync(input, inputBot.UserId, inputBot.AccessHash);

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(inputBot.UserId));
        if (botReadModel == null || !botReadModel.Bot)
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        if (!string.IsNullOrEmpty(obj.Url) &&
            !(Uri.TryCreate(obj.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
        {
            RpcErrors.RpcErrors400.UrlInvalid.ThrowRpcError();
        }

        var url = await webViewSessionStore.ResolveBotUrlAsync(inputBot.UserId, obj.Url);
        if (url == null)
        {
            // The bot's owner has not set a mini app URL through BotFather.
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        // A simple webview cannot post messages back to a chat, so it gets no query id: there is
        // nothing for messages.prolongWebView to keep alive.
        var result = new TWebViewResultUrl
        {
            Url = url!,
            Fullscreen = obj.Fullscreen
        };

        return webViewResultsLayeredService.GetConverter(input.Layer).ToLayeredData(result);
    }
}
