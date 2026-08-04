using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Open a <a href="https://corefork.telegram.org/bots/webapps">bot mini app</a>, sending over user information after user confirmation.After calling this method, until the user closes the webview, <a href="https://corefork.telegram.org/method/messages.prolongWebView">messages.prolongWebView</a> must be called every 60 seconds.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// 400 BOT_WEBVIEW_DISABLED A webview cannot be opened in the specified conditions: emitted for example if <code>from_bot_menu</code> or <code>url</code> are set and <code>peer</code> is not the chat with the bot.
/// 403 CHAT_WRITE_FORBIDDEN You can't write in this chat.
/// 400 INPUT_USER_DEACTIVATED The specified user was deleted.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 403 PRIVACY_PREMIUM_REQUIRED You need a <a href="https://corefork.telegram.org/api/premium">Telegram Premium subscription</a> to send a message to this user.
/// 400 SEND_AS_PEER_INVALID You can't send messages as the specified peer.
/// 400 THEME_PARAMS_INVALID The specified <code>theme_params</code> field is invalid.
/// 400 URL_INVALID Invalid URL provided.
/// 400 YOU_BLOCKED_USER You blocked this user.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.requestWebView"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class RequestWebViewHandler(
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IAccessHashHelper accessHashHelper,
    IWebViewSessionStore webViewSessionStore,
    ILayeredService<IWebViewResultUrlResponseConverter> webViewResultsLayeredService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestRequestWebView, MyTelegram.Schema.IWebViewResult>
{
    protected override async Task<MyTelegram.Schema.IWebViewResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestRequestWebView obj)
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

        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        // from_bot_menu and an explicit url are only meaningful in the chat with the bot itself.
        if ((obj.FromBotMenu || !string.IsNullOrEmpty(obj.Url)) &&
            !(peer.PeerType == PeerType.User && peer.PeerId == inputBot.UserId))
        {
            RpcErrors.RpcErrors400.BotWebviewDisabled.ThrowRpcError();
        }

        if (!string.IsNullOrEmpty(obj.Url) && !IsValidWebViewUrl(obj.Url))
        {
            RpcErrors.RpcErrors400.UrlInvalid.ThrowRpcError();
        }

        var url = await webViewSessionStore.ResolveBotUrlAsync(inputBot.UserId, obj.Url);
        if (url == null)
        {
            // The bot's owner has not set a mini app URL through BotFather.
            RpcErrors.RpcErrors400.BotWebviewDisabled.ThrowRpcError();
        }

        var queryId = await webViewSessionStore.CreateSessionAsync(inputBot.UserId, input.UserId, peer, url!);

        var result = new TWebViewResultUrl
        {
            QueryId = queryId,
            Url = url!,
            Fullscreen = obj.Fullscreen
        };

        return webViewResultsLayeredService.GetConverter(input.Layer).ToLayeredData(result);
    }

    /// <summary>
    /// Clients refuse to load a mini app over anything but HTTPS, so reject other schemes here
    /// rather than handing back a URL that cannot open.
    /// </summary>
    private static bool IsValidWebViewUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
    }
}
