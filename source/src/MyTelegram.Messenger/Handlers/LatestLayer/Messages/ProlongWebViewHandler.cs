using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Indicate to the server (from the user side) that the user is still using a web app.If the method returns a <code>QUERY_ID_INVALID</code> error, the webview must be closed.
/// Possible errors
/// Code Type Description
/// 400 BOT_INVALID This is not a valid bot.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.prolongWebView"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class ProlongWebViewHandler(
    IQueryProcessor queryProcessor,
    IAccessHashHelper accessHashHelper,
    IWebViewSessionStore webViewSessionStore) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestProlongWebView, IBool>
{
    protected override async Task<IBool> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestProlongWebView obj)
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

        // Clients treat QUERY_ID_INVALID as "close the webview", which is exactly what should
        // happen once the session has lapsed.
        if (!await webViewSessionStore.ProlongSessionAsync(obj.QueryId, input.UserId))
        {
            RpcErrors.RpcErrors400.QueryIdInvalid.ThrowRpcError();
        }

        return new TBoolTrue();
    }
}
