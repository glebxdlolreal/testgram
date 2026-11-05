using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// "Log in with Telegram" buttons (keyboardButtonUrlAuth): a bot whose owner linked a domain with
/// @BotFather's /setdomain can log users in to that website. The accepted URL carries the user's data
/// and the Login Widget signature, keyed with the bot's token.
/// See https://corefork.telegram.org/api/url-authorization and https://core.telegram.org/bots/api#loginurl
/// </summary>
public interface IUrlAuthService
{
    /// <summary>Turns the inputKeyboardButtonUrlAuth buttons a bot sends into the keyboardButtonUrlAuth clients get.</summary>
    Task<IReplyMarkup?> NormalizeAsync(long senderUserId, IReplyMarkup? markup);

    Task<IUrlAuthResult> RequestAsync(IRequestInput input, IInputPeer? peer, int? msgId, int? buttonId, string? url);

    Task<IUrlAuthResult> AcceptAsync(IRequestInput input, IInputPeer? peer, int? msgId, int? buttonId, string? url,
        bool writeAllowed);
}

public class UrlAuthService(
    IMongoDatabase mongoDatabase,
    IQueryProcessor queryProcessor,
    IPeerHelper peerHelper,
    IUserAppService userAppService,
    IUserConverterService userConverterService) : IUrlAuthService, ITransientDependency
{
    private const string ButtonCollection = "bot-url-auth-buttons";
    private const string WriteAccessCollection = "bot-url-auth-write-access";

    public async Task<IReplyMarkup?> NormalizeAsync(long senderUserId, IReplyMarkup? markup)
    {
        if (markup is not TReplyInlineMarkup inline ||
            !inline.Rows.OfType<TKeyboardButtonRow>().Any(r => r.Buttons.OfType<TInputKeyboardButtonUrlAuth>().Any()))
        {
            return markup;
        }

        var buttonId = 0;
        var rows = new TVector<IKeyboardButtonRow>();
        foreach (var row in inline.Rows)
        {
            if (row is not TKeyboardButtonRow buttonRow)
            {
                rows.Add(row);
                continue;
            }

            var buttons = new TVector<IKeyboardButton>();
            foreach (var button in buttonRow.Buttons)
            {
                buttonId++;
                if (button is not TInputKeyboardButtonUrlAuth input)
                {
                    buttons.Add(button);
                    continue;
                }

                // "Username of a bot, which will be used for user authorization. If not specified, the
                // current bot's username will be assumed."
                var authBotId = input.Bot switch
                {
                    TInputUser inputUser => inputUser.UserId,
                    _ => senderUserId
                };

                await mongoDatabase.GetCollection<BsonDocument>(ButtonCollection).UpdateOneAsync(
                    Builders<BsonDocument>.Filter.Eq("_id", ButtonKey(senderUserId, input.Url)),
                    Builders<BsonDocument>.Update
                        .Set("SenderId", senderUserId)
                        .Set("Url", input.Url)
                        .Set("BotId", authBotId)
                        .Set("RequestWriteAccess", input.RequestWriteAccess),
                    new UpdateOptions { IsUpsert = true });

                buttons.Add(new TKeyboardButtonUrlAuth
                {
                    Text = input.Text,
                    FwdText = input.FwdText,
                    Url = input.Url,
                    Style = input.Style,
                    ButtonId = buttonId
                });
            }

            rows.Add(new TKeyboardButtonRow { Buttons = buttons });
        }

        return new TReplyInlineMarkup { Rows = rows };
    }

    public async Task<IUrlAuthResult> RequestAsync(IRequestInput input, IInputPeer? peer, int? msgId, int? buttonId,
        string? url)
    {
        var target = await ResolveAsync(input, peer, msgId, buttonId, url);
        if (target == null)
        {
            return new TUrlAuthResultDefault();
        }

        var bot = await userAppService.GetAsync(target.BotId);
        return new TUrlAuthResultRequest
        {
            Bot = userConverterService.ToUser(input, bot, layer: input.Layer),
            Domain = target.Domain,
            RequestWriteAccess = target.RequestWriteAccess && !await HasWriteAccessAsync(input.UserId, target.BotId)
        };
    }

    public async Task<IUrlAuthResult> AcceptAsync(IRequestInput input, IInputPeer? peer, int? msgId, int? buttonId,
        string? url, bool writeAllowed)
    {
        var target = await ResolveAsync(input, peer, msgId, buttonId, url);
        if (target == null)
        {
            return new TUrlAuthResultDefault();
        }

        if (writeAllowed && target.RequestWriteAccess)
        {
            await mongoDatabase.GetCollection<BsonDocument>(WriteAccessCollection).UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", $"{input.UserId}:{target.BotId}"),
                Builders<BsonDocument>.Update
                    .Set("UserId", input.UserId)
                    .Set("BotId", target.BotId)
                    .Set("Date", DateTime.UtcNow.ToTimestamp()),
                new UpdateOptions { IsUpsert = true });
        }

        var user = await userAppService.GetAsync(input.UserId);
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["id"] = input.UserId.ToString(),
            ["first_name"] = user.FirstName,
            ["auth_date"] = DateTime.UtcNow.ToTimestamp().ToString()
        };
        if (!string.IsNullOrEmpty(user.LastName))
        {
            fields["last_name"] = user.LastName;
        }

        if (!string.IsNullOrEmpty(user.UserName))
        {
            fields["username"] = user.UserName;
        }

        fields["hash"] = Sign(fields, target.BotToken);

        var query = string.Join("&", fields.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var separator = target.Url.Contains('?') ? '&' : '?';
        return new TUrlAuthResultAccepted { Url = target.Url + separator + query };
    }

    /// <summary>
    /// The Login Widget signature: hex HMAC-SHA-256 of the sorted "key=value" lines, keyed with
    /// SHA-256 of the bot token.
    /// </summary>
    internal static string Sign(IReadOnlyDictionary<string, string> fields, string botToken)
    {
        var dataCheckString = string.Join("\n", fields
            .Where(p => p.Key != "hash")
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
        var secret = SHA256.HashData(Encoding.UTF8.GetBytes(botToken));
        return Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(dataCheckString)));
    }

    /// <summary>"The domain of the url must be the same as the domain linked with the bot."</summary>
    internal static bool DomainMatches(string url, string domain) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private async Task<UrlAuthTarget?> ResolveAsync(IRequestInput input, IInputPeer? inputPeer, int? msgId,
        int? buttonId, string? url)
    {
        // A bare link is only ever authorized for the url_auth_domains the server advertises in the app
        // config, and it advertises none: clients open such links as they are.
        if (inputPeer == null || msgId is not > 0 || buttonId == null)
        {
            return null;
        }

        var peer = peerHelper.GetPeer(inputPeer, input.UserId);
        var ownerPeerId = peer.PeerType == PeerType.Channel ? peer.PeerId : input.UserId;
        var message = await queryProcessor.ProcessAsync(
            new GetMessageByIdQuery(MessageId.Create(ownerPeerId, msgId.Value).Value));
        var markup = message?.ReplyMarkup2;
        if (markup == null && message?.ReplyMarkup is { Length: > 0 } bytes)
        {
            var buffer = new ReadOnlyMemory<byte>(bytes);
            markup = buffer.Read<IReplyMarkup>();
        }

        var button = (markup as TReplyInlineMarkup)?.Rows.OfType<TKeyboardButtonRow>()
            .SelectMany(p => p.Buttons).OfType<TKeyboardButtonUrlAuth>()
            .FirstOrDefault(p => p.ButtonId == buttonId);
        if (message == null || button == null)
        {
            return null;
        }

        var stored = await mongoDatabase.GetCollection<BsonDocument>(ButtonCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ButtonKey(message.SenderPeerId, button.Url)))
            .FirstOrDefaultAsync();
        var botId = stored != null && stored.TryGetValue("BotId", out var storedBotId) ? storedBotId.ToInt64() : message.SenderPeerId;
        var requestWriteAccess = stored != null && stored.TryGetValue("RequestWriteAccess", out var write) &&
                                 write.IsBoolean && write.AsBoolean;

        var botState = await mongoDatabase.GetCollection<BsonDocument>("botfather-bot-state")
            .Find(Builders<BsonDocument>.Filter.Eq("BotUserId", botId)).FirstOrDefaultAsync();
        var domain = botState != null && botState.TryGetValue("Domain", out var domainValue) && domainValue.IsString
            ? domainValue.AsString
            : null;
        var token = botState != null && botState.TryGetValue("Token", out var tokenValue) && tokenValue.IsString
            ? tokenValue.AsString
            : null;

        // Without a linked domain, or for another website, the button is an ordinary link.
        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(token) || !DomainMatches(button.Url, domain))
        {
            return null;
        }

        return new UrlAuthTarget(botId, domain, token, button.Url, requestWriteAccess);
    }

    private async Task<bool> HasWriteAccessAsync(long userId, long botId) =>
        await mongoDatabase.GetCollection<BsonDocument>(WriteAccessCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", $"{userId}:{botId}")).AnyAsync();

    private static string ButtonKey(long senderUserId, string url) =>
        $"{senderUserId}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))}";

    private sealed record UrlAuthTarget(long BotId, string Domain, string BotToken, string Url, bool RequestWriteAccess);
}
