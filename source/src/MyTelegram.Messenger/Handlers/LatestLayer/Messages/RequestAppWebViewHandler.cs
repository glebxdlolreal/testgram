using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Open a <a href="https://corefork.telegram.org/bots/webapps">bot mini app</a> from a <a href="https://corefork.telegram.org/api/links#direct-mini-app-links">direct Mini App deep link</a>, sending over user information after user confirmation.After calling this method, until the user closes the webview, <a href="https://corefork.telegram.org/method/messages.prolongWebView">messages.prolongWebView</a> must be called every 60 seconds.
/// Possible errors
/// Code Type Description
/// 400 BOT_APP_BOT_INVALID The bot_id passed in the inputBotAppShortName constructor is invalid.
/// 400 BOT_APP_INVALID The specified bot app is invalid.
/// 400 BOT_APP_SHORTNAME_INVALID The specified bot app short name is invalid.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 THEME_PARAMS_INVALID The specified <code>theme_params</code> field is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.requestAppWebView"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class RequestAppWebViewHandler(
    IPeerHelper peerHelper,
    IBotAppStore botAppStore,
    IWebViewSessionStore webViewSessionStore,
    ILayeredService<IWebViewResultUrlResponseConverter> webViewResultsLayeredService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestRequestAppWebView, MyTelegram.Schema.IWebViewResult>
{
    protected override async Task<MyTelegram.Schema.IWebViewResult> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestRequestAppWebView obj)
    {
        if (obj.App is TInputBotAppShortName { ShortName.Length: 0 })
        {
            RpcErrors.RpcErrors400.BotAppShortnameInvalid.ThrowRpcError();
        }

        var lookup = await botAppStore.ResolveAsync(obj.App, ResolveBotId);
        if (lookup == null)
        {
            RpcErrors.RpcErrors400.BotAppInvalid.ThrowRpcError();
        }

        var shortName = obj.App is TInputBotAppShortName name ? name.ShortName : null;
        var peer = peerHelper.GetPeer(obj.Peer, input.UserId);

        var url = await webViewSessionStore.ResolveBotUrlAsync(lookup!.Value.BotId, shortName: shortName);
        if (url == null)
        {
            RpcErrors.RpcErrors400.BotAppInvalid.ThrowRpcError();
        }

        var queryId = await webViewSessionStore.CreateSessionAsync(lookup.Value.BotId, input.UserId, peer, url!);

        var result = new TWebViewResultUrl
        {
            QueryId = queryId,
            Url = url!,
            Fullscreen = obj.Fullscreen
        };

        return webViewResultsLayeredService.GetConverter(input.Layer).ToLayeredData(result);
    }

    private static long? ResolveBotId(IInputUser inputUser)
    {
        return inputUser is TInputUser user ? user.UserId : null;
    }
}
