using MongoDB.Bson;
using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Obtain information about a <a href="https://corefork.telegram.org/api/bots/webapps#direct-link-mini-apps">direct link Mini App</a>
/// Possible errors
/// Code Type Description
/// 400 BOT_APP_BOT_INVALID The bot_id passed in the inputBotAppShortName constructor is invalid.
/// 400 BOT_APP_INVALID The specified bot app is invalid.
/// 400 BOT_APP_SHORTNAME_INVALID The specified bot app short name is invalid.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getBotApp"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetBotAppHandler(
    IBotAppStore botAppStore) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetBotApp, MyTelegram.Schema.Messages.IBotApp>
{
    protected override async Task<MyTelegram.Schema.Messages.IBotApp> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetBotApp obj)
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

        var document = lookup!.Value.Document;
        var hash = document.TryGetValue("hash", out var hashValue) && hashValue.BsonType == BsonType.Int64
            ? hashValue.AsInt64
            : 0;

        // The client already holds this version of the app; botAppNotModified is a constructor of
        // the inner BotApp, so it still travels inside the messages.botApp wrapper.
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new MyTelegram.Schema.Messages.TBotApp
            {
                App = new TBotAppNotModified()
            };
        }

        return new MyTelegram.Schema.Messages.TBotApp
        {
            App = botAppStore.ToBotApp(document),
            Inactive = document.TryGetValue("inactive", out var inactive) && inactive.IsBoolean && inactive.AsBoolean,
            RequestWriteAccess = document.TryGetValue("request_write_access", out var write) && write.IsBoolean &&
                                 write.AsBoolean,
            HasSettings = document.TryGetValue("has_settings", out var settings) && settings.IsBoolean &&
                          settings.AsBoolean
        };
    }

    private static long? ResolveBotId(IInputUser inputUser)
    {
        return inputUser is TInputUser user ? user.UserId : null;
    }
}
