using MongoDB.Bson;
using MongoDB.Driver;
using StackExchange.Redis;

namespace MyTelegram.Messenger.Services.Bots;

public interface IBotFatherBotService
{
    Task HandleMessageAsync(IRequestInput input, long fromUserId, string message);
    Task HandleCallbackAsync(IRequestInput input, long fromUserId, int msgId, string data);
}

public class BotFatherBotService(
    IMessageAppService messageAppService,
    IMongoDatabase mongoDatabase,
    IConnectionMultiplexer redis,
    ICommandBus commandBus,
    IQueryProcessor queryProcessor) : IBotFatherBotService, ISingletonDependency
{
    public const long BotUserId = MyTelegramConsts.BotFatherUserId;
    private const string BotCollection = "botfather-bot-state";

    private const string WelcomeText =
        "I can help you create and manage Xiegram bots. You can control me by sending these commands:\n\n" +
        "Managing bots\n" +
        "/newbot - create a new bot\n" +
        "/mybots - edit your bots\n\n" +
        "Edit Bots\n" +
        "/setname - change a bot's name\n" +
        "/setdescription - change bot description\n" +
        "/setabouttext - change bot about info\n" +
        "/setuserpic - change bot profile photo\n" +
        "/setcommands - change the list of commands\n" +
        "/setinline - toggle inline mode\n" +
        "/deletebot - delete a bot\n\n" +
        "Bot Settings\n" +
        "/token - get authorization token\n" +
        "/revoke - revoke bot access token";

    // --- State keys ---
    private string StateKey(long userId) => $"botfather:state:{userId}";

    private async Task SetStateAsync(long userId, string state, int ttl = 300)
        => await redis.GetDatabase().StringSetAsync(StateKey(userId), state, TimeSpan.FromSeconds(ttl));

    private async Task<string?> GetStateAsync(long userId)
        => await redis.GetDatabase().StringGetAsync(StateKey(userId));

    private async Task ClearStateAsync(long userId)
        => await redis.GetDatabase().KeyDeleteAsync(StateKey(userId));

    // --- Entry points ---

    public async Task HandleMessageAsync(IRequestInput input, long fromUserId, string message)
    {
        var text = message.Trim();
        var cmd = text.Split(' ')[0].ToLower();

        // Check FSM state first
        var state = await GetStateAsync(fromUserId);
        if (state != null && !cmd.StartsWith("/"))
        {
            await HandleStateInputAsync(input, fromUserId, state, text);
            return;
        }

        switch (cmd)
        {
            case "/start":
                await ClearStateAsync(fromUserId);
                // Check for deep link parameter
                var startParam = text.Length > 7 ? text.Substring(7).Trim() : "";
                if (startParam.StartsWith("bizChat"))
                {
                    // Handle business chat deep link: /start bizChat<user_chat_id>
                    var chatIdStr = startParam.Substring(7);
                    await SendAsync(input, fromUserId,
                        $"Managing business chat {chatIdStr}\n\n" +
                        "This is where you would manage the business connection for this specific chat. " +
                        "You can configure bot behavior, view chat history, and manage permissions.");
                }
                else
                {
                    await SendAsync(input, fromUserId, WelcomeText, entities: new TVector<IMessageEntity>(BoldEntities(WelcomeText)));
                }
                break;
            case "/newbot":
                await ClearStateAsync(fromUserId);
                await SetStateAsync(fromUserId, "newbot:name");
                await SendAsync(input, fromUserId, "Alright, a new bot. How are we going to call it? Please choose a name for your bot.");
                break;
            case "/mybots":
                await ClearStateAsync(fromUserId);
                await SendMyBotsAsync(input, fromUserId);
                break;
            case "/token":
                await ClearStateAsync(fromUserId);
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to get Bot API access token.", "token");
                break;
            case "/revoke":
                await ClearStateAsync(fromUserId);
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to generate a new token. Warning: your old token will stop working.", "revoke");
                break;
            case "/deletebot":
                await ClearStateAsync(fromUserId);
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to delete.", "deletebot");
                break;
            case "/setname":
                await ClearStateAsync(fromUserId);
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change name.", "setname");
                break;
            case "/setdescription":
                await ClearStateAsync(fromUserId);
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change description.", "setdescription");
                break;
            case "/setabouttext":
                await ClearStateAsync(fromUserId);
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change the about section.", "setabouttext");
                break;
            case "/setuserpic":
                await ClearStateAsync(fromUserId);
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change profile photo.", "setuserpic");
                break;
            case "/setcommands":
                await ClearStateAsync(fromUserId);
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change the list of commands.", "setcommands");
                break;
            case "/setinline":
                await ClearStateAsync(fromUserId);
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change inline settings.", "setinline");
                break;
        }
    }

    public async Task HandleCallbackAsync(IRequestInput input, long fromUserId, int msgId, string data)
    {
        Console.WriteLine($"[BotFather] HandleCallbackAsync called: fromUserId={fromUserId}, msgId={msgId}, data={data}");

        var parts = data.Split(':');
        if (parts.Length < 2)
        {
            Console.WriteLine($"[BotFather] Invalid callback data format: {data}");
            return;
        }
        var action = parts[0];
        var username = parts.Length > 1 ? parts[1] : "";

        Console.WriteLine($"[BotFather] Parsed action={action}, username={username}");

        switch (action)
        {
            case "bot_select":
                await SendBotMenuAsync(input, fromUserId, msgId, username);
                break;
            case "api_token":
                await SendTokenAsync(input, fromUserId, msgId, username);
                break;
            case "edit_bot":
                await SendEditBotAsync(input, fromUserId, msgId, username);
                break;
            case "bot_settings":
                await SendBotSettingsAsync(input, fromUserId, msgId, username);
                break;
            case "transfer_ownership":
                await EditMessageAsync(input, fromUserId, msgId,
                    "You can transfer bot ownership to another Telegram user.",
                    InlineRows(
                        InlineRow(Btn("Choose recipient", $"soon:{username}"), Btn("« Back to Bot", $"bot_select:{username}"))
                    ));
                break;
            case "delete_confirm":
                var bot2 = await GetBotAsync(fromUserId, username);
                if (bot2 == null) return;
                await EditMessageAsync(input, fromUserId, msgId,
                    $"You are about to delete your bot *{Esc(bot2["Name"].AsString)}* @{username}. Is that correct?",
                    InlineRows(
                        InlineRow(Btn("Nope, nevermind", $"bot_select:{username}")),
                        InlineRow(Btn("Yes, delete the bot", $"delete_do:{username}"))
                    ));
                break;
            case "delete_do":
                await DoDeleteBotAsync(fromUserId, username);
                await EditMessageAsync(input, fromUserId, msgId, $"Bot @{username} has been deleted.", null);
                break;
            case "revoke_token":
                var newToken = await DoRevokeAsync(fromUserId, username);
                await EditMessageAsync(input, fromUserId, msgId,
                    $"Done! New token for @{username}:\n`{newToken}`",
                    InlineRows(InlineRow(Btn("« Back to Bot", $"bot_select:{username}"))));
                break;
            case "business_mode":
                await SendBusinessModeMenuAsync(input, fromUserId, msgId, username);
                break;
            case "inline_mode":
                await SendInlineModeMenuAsync(input, fromUserId, msgId, username);
                break;
            case "inline_enable":
                await SetBotInlineModeAsync(fromUserId, username, true);
                await SendInlineModeMenuAsync(input, fromUserId, msgId, username);
                break;
            case "inline_disable":
                await SetBotInlineModeAsync(fromUserId, username, false);
                await SendInlineModeMenuAsync(input, fromUserId, msgId, username);
                break;
            case "business_enable":
                await EnableBotBusinessModeAsync(input, fromUserId, username);
                await SendBusinessModeMenuAsync(input, fromUserId, msgId, username);
                break;
            case "business_disable":
                await DisableBotBusinessModeAsync(input, fromUserId, username);
                await SendBusinessModeMenuAsync(input, fromUserId, msgId, username);
                break;
            case "edit_field":
                var field = parts.Length > 2 ? parts[1] : "";
                var botUsername = parts.Length > 2 ? parts[2] : "";
                await HandleEditFieldAsync(input, fromUserId, msgId, field, botUsername);
                break;
            case "soon":
                // no-op, just answer callback
                break;
        }
    }

    // --- FSM state handler ---

    private async Task HandleStateInputAsync(IRequestInput input, long fromUserId, string state, string text)
    {
        var parts = state.Split(':');
        var flow = parts[0];
        var step = parts.Length > 1 ? parts[1] : "";
        var extra = parts.Length > 2 ? parts[2] : "";

        switch (flow)
        {
            case "newbot":
                if (step == "name")
                {
                    await SetStateAsync(fromUserId, $"newbot:username:{text}");
                    await SendAsync(input, fromUserId,
                        $"Good. Now let's choose a username for your bot. It must end in `bot`. Like this, for example: TetrisBot or tetris_bot.");
                }
                else if (step == "username")
                {
                    var name = extra;
                    var username = text.TrimStart('@').ToLower();
                    await ClearStateAsync(fromUserId);
                    var result = await DoCreateBotAsync(input, fromUserId, name, username);
                    if (result != null) await SendAsync(input, fromUserId, result);
                }
                break;

            case "setname":
                if (step == "input")
                {
                    var username2 = extra;
                    await DoSetNameAsync(fromUserId, username2, text);
                    await ClearStateAsync(fromUserId);
                    await SendAsync(input, fromUserId, $"Success! Name for @{username2} changed to *{Esc(text)}*.");
                }
                else if (step == "pick")
                    await HandleBotPickAsync(input, fromUserId, text, SendBotMenuTextAsync);
                break;

            case "setdescription":
                if (step == "input")
                {
                    await DoSetFieldAsync(fromUserId, extra, "Description", text);
                    await ClearStateAsync(fromUserId);
                    await SendAsync(input, fromUserId, $"Success! Description for @{extra} updated.");
                }
                else if (step == "pick")
                    await HandleBotPickAsync(input, fromUserId, text, SendBotMenuTextAsync);
                break;

            case "setabouttext":
                if (step == "input")
                {
                    await DoSetFieldAsync(fromUserId, extra, "About", text);
                    await ClearStateAsync(fromUserId);
                    // Also update DB userreadmodel
                    await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
                        .UpdateOneAsync(new BsonDocument("UserName", extra), new BsonDocument("$set", new BsonDocument("About", text)));
                    await SendAsync(input, fromUserId, $"Success! About text for @{extra} updated.");
                }
                else if (step == "pick")
                    await HandleBotPickAsync(input, fromUserId, text, SendBotMenuTextAsync);
                break;
            case "token":
                if (step == "pick")
                    await HandleBotPickAsync(input, fromUserId, text, SendTokenTextAsync);
                break;
            case "revoke":
                if (step == "pick")
                    await HandleBotPickAsync(input, fromUserId, text, SendRevokedTokenTextAsync);
                break;
            case "deletebot":
                if (step == "pick")
                    await HandleBotPickAsync(input, fromUserId, text, SendDeleteConfirmTextAsync);
                break;
            case "setuserpic":
            case "setcommands":
            case "setinline":
                if (step == "pick")
                    await HandleBotPickAsync(input, fromUserId, text, SendBotMenuTextAsync);
                break;
        }
    }

    private async Task HandleBotPickAsync(IRequestInput input, long fromUserId, string text, Func<IRequestInput, long, string, BsonDocument, Task> handler)
    {
        var username = text.Trim().TrimStart('@').ToLowerInvariant();
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null)
        {
            await SendAsync(input, fromUserId, $"I couldn't find a bot @{username} owned by you.", new TReplyKeyboardHide());
            return;
        }

        await handler(input, fromUserId, username, bot);
    }

    private async Task HandleEditFieldAsync(IRequestInput input, long fromUserId, int msgId, string field, string username)
    {
        switch (field)
        {
            case "name":
                await SetStateAsync(fromUserId, $"setname:input:{username}");
                await EditMessageAsync(input, fromUserId, msgId,
                    $"OK. Send me the new name for @{username}.",
                    InlineRows(InlineRow(Btn("« Cancel", $"edit_bot:{username}"))));
                break;
            case "about":
                await SetStateAsync(fromUserId, $"setabouttext:input:{username}");
                await EditMessageAsync(input, fromUserId, msgId,
                    $"OK. Send me the new about text for @{username}.",
                    InlineRows(InlineRow(Btn("« Cancel", $"edit_bot:{username}"))));
                break;
            case "desc":
                await SetStateAsync(fromUserId, $"setdescription:input:{username}");
                await EditMessageAsync(input, fromUserId, msgId,
                    $"OK. Send me the new description for @{username}.",
                    InlineRows(InlineRow(Btn("« Cancel", $"edit_bot:{username}"))));
                break;
        }
    }

    // --- Bot menu screens ---

    private async Task SendMyBotsAsync(IRequestInput input, long fromUserId)
    {
        var bots = await GetUserBotsAsync(fromUserId);
        if (bots.Count == 0)
        {
            await SendAsync(input, fromUserId, "You have no bots yet. Use /newbot to create one.");
            return;
        }

        var rows = bots.Select(b =>
            InlineRow(Btn($"@{b["Username"].AsString} — {b["Name"].AsString}", $"bot_select:{b["Username"].AsString}"))
        ).ToList();

        await SendAsync(input, fromUserId, "Choose a bot from the list below:", InlineRows(rows.ToArray()));
    }

    private async Task SendBotMenuAsync(IRequestInput input, long fromUserId, int msgId, string username)
    {
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null) return;
        var name = bot["Name"].AsString;

        await EditMessageAsync(input, fromUserId, msgId,
            $"Here it is: *{Esc(name)}* @{username}.\nWhat do you want to do with the bot?",
            InlineRows(
                InlineRow(Btn("API Token", $"api_token:{username}"), Btn("Edit Bot", $"edit_bot:{username}")),
                InlineRow(Btn("Bot Settings", $"bot_settings:{username}")),
                InlineRow(Btn("Transfer Ownership", $"transfer_ownership:{username}"), Btn("Delete Bot", $"delete_confirm:{username}"))
            ));
    }

    private async Task SendTokenAsync(IRequestInput input, long fromUserId, int msgId, string username)
    {
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null) return;
        var name = bot["Name"].AsString;
        var token = bot["Token"].AsString;

        await EditMessageAsync(input, fromUserId, msgId,
            $"Here is the token for bot *{Esc(name)}* @{username}:\n\n`{token}`",
            InlineRows(
                InlineRow(Btn("Revoke current token", $"revoke_token:{username}"), Btn("« Back to Bot", $"bot_select:{username}"))
            ));
    }

    private async Task SendTokenTextAsync(IRequestInput input, long fromUserId, string username, BsonDocument bot)
    {
        await ClearStateAsync(fromUserId);
        var name = bot["Name"].AsString;
        var token = bot["Token"].AsString;
        await SendAsync(input, fromUserId, $"Here is the token for bot *{Esc(name)}* @{username}:\n\n`{token}`", new TReplyKeyboardHide());
    }

    private async Task SendRevokedTokenTextAsync(IRequestInput input, long fromUserId, string username, BsonDocument bot)
    {
        await ClearStateAsync(fromUserId);
        var newToken = await DoRevokeAsync(fromUserId, username);
        await SendAsync(input, fromUserId, $"Done! New token for @{username}:\n`{newToken}`", new TReplyKeyboardHide());
    }

    private async Task SendDeleteConfirmTextAsync(IRequestInput input, long fromUserId, string username, BsonDocument bot)
    {
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId,
            $"You are about to delete your bot *{Esc(bot["Name"].AsString)}* @{username}. Is that correct?",
            InlineRows(
                InlineRow(Btn("Nope, nevermind", $"bot_select:{username}")),
                InlineRow(Btn("Yes, delete the bot", $"delete_do:{username}"))
            ));
    }

    private async Task SendBotMenuTextAsync(IRequestInput input, long fromUserId, string username, BsonDocument bot)
    {
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId,
            $"Here it is: *{Esc(bot["Name"].AsString)}* @{username}.\nWhat do you want to do with the bot?",
            InlineRows(
                InlineRow(Btn("API Token", $"api_token:{username}"), Btn("Edit Bot", $"edit_bot:{username}")),
                InlineRow(Btn("Bot Settings", $"bot_settings:{username}")),
                InlineRow(Btn("Transfer Ownership", $"transfer_ownership:{username}"), Btn("Delete Bot", $"delete_confirm:{username}"))
            ));
    }

    private async Task SendEditBotAsync(IRequestInput input, long fromUserId, int msgId, string username)
    {
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null) return;
        var name = bot["Name"].AsString;
        var about = bot.Contains("About") && !bot["About"].IsBsonNull ? bot["About"].AsString : "🚫";
        var desc = bot.Contains("Description") && !bot["Description"].IsBsonNull ? bot["Description"].AsString : "🚫";

        await EditMessageAsync(input, fromUserId, msgId,
            $"Edit @{username} info.\n\nName: {Esc(name)}\nAbout: {Esc(about)}\nDescription: {Esc(desc)}\nDescription picture: 🚫 no description picture\nBotpic: 🚫 no botpic\nCommands: no commands yet\nPrivacy Policy: 🚫",
            InlineRows(
                InlineRow(Btn("Edit Name", $"edit_field:name:{username}"), Btn("Edit About", $"edit_field:about:{username}")),
                InlineRow(Btn("Edit Description", $"edit_field:desc:{username}")),
                InlineRow(Btn("Edit Description picture", $"soon:{username}"), Btn("Edit Botpic", $"soon:{username}")),
                InlineRow(Btn("Edit Commands", $"soon:{username}"), Btn("Edit Privacy Policy", $"soon:{username}")),
                InlineRow(Btn("« Back to Bot", $"bot_select:{username}"))
            ));
    }

    private async Task SendBotSettingsAsync(IRequestInput input, long fromUserId, int msgId, string username)
    {
        await EditMessageAsync(input, fromUserId, msgId,
            $"Settings for @{username}.",
            InlineRows(
                InlineRow(Btn("Inline Mode", $"inline_mode:{username}"), Btn("Business Mode", $"business_mode:{username}")),
                InlineRow(Btn("Allow Groups?", $"soon:{username}"), Btn("Group Privacy", $"soon:{username}")),
                InlineRow(Btn("Group Admin Rights", $"soon:{username}"), Btn("Channel Admin Rights", $"soon:{username}")),
                InlineRow(Btn("Payments", $"soon:{username}"), Btn("Domain", $"soon:{username}")),
                InlineRow(Btn("Menu Button", $"soon:{username}"), Btn("Configure Mini App", $"soon:{username}")),
                InlineRow(Btn("Paid Broadcast", $"soon:{username}")),
                InlineRow(Btn("« Back to Bot", $"bot_select:{username}"))
            ));
    }

    private async Task SendBotPickerAsync(IRequestInput input, long fromUserId, string prompt, string action)
    {
        var bots = await GetUserBotsAsync(fromUserId);
        if (bots.Count == 0)
        {
            await SendAsync(input, fromUserId, "You have no bots yet. Use /newbot to create one.");
            return;
        }

        // Reply keyboard (bottom keyboard)
        var replyButtons = bots.Select(b =>
            new TVector<IKeyboardButton>(new List<IKeyboardButton>
            {
                new TKeyboardButton { Text = $"@{b["Username"].AsString}" }
            })
        ).ToList();

        var markup = new TReplyKeyboardMarkup
        {
            Rows = new TVector<IKeyboardButtonRow>(
                replyButtons.Select(r => (IKeyboardButtonRow)new TKeyboardButtonRow { Buttons = r }).ToList()
            ),
            SingleUse = true,
            Selective = false,
            Resize = true
        };

        // Set state to handle the selection
        await SetStateAsync(fromUserId, $"{action}:pick");
        await SendAsync(input, fromUserId, prompt, replyMarkup: markup);
    }

    // --- DB helpers ---

    private async Task<string?> DoCreateBotAsync(IRequestInput input, long fromUserId, string name, string username)
    {
        if (!username.EndsWith("bot"))
            return "Sorry, username must end in `bot`. Like mybot_bot or MyBot.";

        var existing = await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(new BsonDocument("UserName", username)).FirstOrDefaultAsync();
        if (existing != null)
            return "Sorry, this username is already taken. Please choose a different username.";

        var newUserId = await GetNextUserIdAsync();
        var accessHash = Random.Shared.NextInt64();
        var token = $"{newUserId}:{GenerateToken()}";

        // Store bot metadata (owner, token)
        var col = mongoDatabase.GetCollection<BsonDocument>(BotCollection);
        await col.InsertOneAsync(new BsonDocument
        {
            { "OwnerId", fromUserId }, { "BotUserId", newUserId },
            { "Username", username }, { "Name", name }, { "Token", token },
            { "CreatedAt", DateTime.UtcNow }
        });

        // Add bot owner to bot-owners collection for bot_can_edit flag
        var botOwnersCol = mongoDatabase.GetCollection<BsonDocument>("bot-owners");
        await botOwnersCol.InsertOneAsync(new BsonDocument
        {
            { "BotId", newUserId },
            { "OwnerId", fromUserId }
        });

        // Create bot user in read model
        var now = DateTime.UtcNow;
        var userCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        await userCollection.InsertOneAsync(new BsonDocument
        {
            { "_id", $"user-{newUserId}" },
            { "UserId", newUserId },
            { "AccessHash", accessHash },
            { "PhoneNumber", "0" },
            { "FirstName", name },
            { "LastName", "" },
            { "UserName", username },
            { "Bot", true },
            { "CreatorUserId", fromUserId },
            { "About", "" },
            { "AccountTtl", 365 },
            { "Birthday", BsonNull.Value },
            { "BotActiveUsers", BsonNull.Value },
            { "BotHasMainApp", false },
            { "BotInfoVersion", 1 },
            { "BotChatHistory", false },
            { "BotNochats", false },
            { "BotBusiness", false },
            { "Color", new BsonDocument { { "Color", 0 }, { "BackgroundEmojiId", BsonNull.Value } } },
            { "CreationTime", now },
            { "Email", BsonNull.Value },
            { "EmojiStatusDocumentId", BsonNull.Value },
            { "EmojiStatusValidUntil", BsonNull.Value },
            { "FallbackPhotoId", BsonNull.Value },
            { "GlobalPrivacySettings", new BsonDocument {
                { "ArchiveAndMuteNewNoncontactPeers", false },
                { "KeepArchivedUnmuted", false },
                { "KeepArchivedFolders", false },
                { "HideReadMarks", false },
                { "NewNoncontactPeersRequirePremium", false },
                { "NoncontactPeersPaidStars", BsonNull.Value },
                { "DisallowUnlimitedStargifts", false },
                { "DisallowLimitedStargifts", false },
                { "DisallowUniqueStargifts", false },
                { "DisallowPremiumGifts", false }
            }},
            { "HasPassword", false },
            { "IsOnline", false },
            { "LastUpdateDate", now },
            { "PersonalChannelId", BsonNull.Value },
            { "PersonalPhotoId", BsonNull.Value },
            { "PinnedMsgId", BsonNull.Value },
            { "PinnedMsgIdList", new BsonArray() },
            { "Premium", false },
            { "ProfileColor", BsonNull.Value },
            { "ProfilePhoto", BsonNull.Value },
            { "ProfilePhotoId", BsonNull.Value },
            { "ProfilePhotoUpdateDate", BsonNull.Value },
            { "RecentEmojiStatuses", new BsonArray() },
            { "SensitiveCanChange", false },
            { "SensitiveEnabled", false },
            { "ShowContactSignUpNotification", false },
            { "Fake", false },
            { "Scam", false },
            { "Support", false },
            { "Usernames", BsonNull.Value },
            { "UserNameUpdateDate", BsonNull.Value },
            { "IsDeleted", false },
            { "Verified", false },
            { "Version", 1L },
            { "VideoEmojiMarkup", BsonNull.Value }
        });

        // Add username to eventflow-usernamereadmodel for username resolution
        await mongoDatabase.GetCollection<BsonDocument>("eventflow-usernamereadmodel").InsertOneAsync(new BsonDocument
        {
            { "_id", $"username-{username.ToLower()}" },
            { "UserName", username },
            { "PeerId", newUserId },
            { "PeerType", 3 }, // 3 = User
            { "IsActive", true }
        });

        var text = $"Done! Congratulations on your new bot. You will find it at t.me/{username}.\n\n" +
                   $"Use this token to access the HTTP API:\n{token}\n\n" +
                   $"Keep your token secure and store it safely, it can be used by anyone to control your bot.";

        // Build entities: URL for t.me link, Code for token
        var urlText = $"t.me/{username}";
        var urlOffset = text.IndexOf(urlText, StringComparison.Ordinal);
        var tokenOffset = text.IndexOf(token, StringComparison.Ordinal);

        await SendAsync(input, fromUserId, text, entities: new TVector<IMessageEntity>(new List<IMessageEntity>
        {
            new TMessageEntityUrl { Offset = urlOffset, Length = urlText.Length },
            new TMessageEntityCode { Offset = tokenOffset, Length = token.Length }
        }));
        return null!;
    }

    private async Task<string> DoRevokeAsync(long fromUserId, string username)
    {
        var col = mongoDatabase.GetCollection<BsonDocument>(BotCollection);
        var bot = await col.Find(new BsonDocument { { "OwnerId", fromUserId }, { "Username", username } }).FirstOrDefaultAsync();
        if (bot == null) return "not found";
        var newToken = $"{bot["BotUserId"].AsInt64}:{GenerateToken()}";
        await col.UpdateOneAsync(
            new BsonDocument { { "OwnerId", fromUserId }, { "Username", username } },
            new BsonDocument("$set", new BsonDocument("Token", newToken)));
        return newToken;
    }

    private async Task DoDeleteBotAsync(long fromUserId, string username)
    {
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .DeleteOneAsync(new BsonDocument { { "OwnerId", fromUserId }, { "Username", username } });
        await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .UpdateOneAsync(new BsonDocument("UserName", username), new BsonDocument("$set", new BsonDocument("IsDeleted", true)));
    }

    private async Task DoSetNameAsync(long fromUserId, string username, string name)
    {
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .UpdateOneAsync(new BsonDocument { { "OwnerId", fromUserId }, { "Username", username } },
                new BsonDocument("$set", new BsonDocument("Name", name)));
        await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .UpdateOneAsync(new BsonDocument("UserName", username), new BsonDocument("$set", new BsonDocument("FirstName", name)));
    }

    private async Task DoSetFieldAsync(long fromUserId, string username, string field, string value)
    {
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .UpdateOneAsync(new BsonDocument { { "OwnerId", fromUserId }, { "Username", username } },
                new BsonDocument("$set", new BsonDocument(field, value)));
    }

    private async Task<BsonDocument?> GetBotAsync(long fromUserId, string username)
        => await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .Find(new BsonDocument { { "OwnerId", fromUserId }, { "Username", username } }).FirstOrDefaultAsync();

    private async Task<List<BsonDocument>> GetUserBotsAsync(long fromUserId)
        => await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .Find(new BsonDocument("OwnerId", fromUserId)).ToListAsync();

    private async Task<long> GetNextUserIdAsync()
    {
        var filter = new BsonDocument
        {
            { "Bot", true },
            { "UserId", new BsonDocument
                {
                    { "$gte", MyTelegramConsts.BotUserInitId },
                    { "$lt", MyTelegramConsts.ChatIdInitId }
                }
            }
        };
        var last = await mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel")
            .Find(filter).Sort(new BsonDocument("UserId", -1)).Limit(1).FirstOrDefaultAsync();
        return Math.Max(last?["UserId"].AsInt64 + 1 ?? MyTelegramConsts.BotUserInitId + 1, MyTelegramConsts.BotUserInitId + 1);
    }

    private static string GenerateToken()
    {
        // A bot token is a bearer credential for the bot account, so it must be unguessable:
        // Random.Shared is xoshiro256**, whose state can be recovered from previously issued tokens.
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    // --- Messaging helpers ---

    private async Task SendAsync(IRequestInput input, long toUserId, string text,
        IReplyMarkup? replyMarkup = null, TVector<IMessageEntity>? entities = null)
    {
        Console.WriteLine($"[BotFather.SendAsync] toUserId={toUserId}, text={text}, hasReplyMarkup={replyMarkup != null}");
        if (replyMarkup != null)
        {
            Console.WriteLine($"[BotFather.SendAsync] ReplyMarkup type: {replyMarkup.GetType().Name}");
            if (replyMarkup is TReplyInlineMarkup inlineMarkup)
            {
                Console.WriteLine($"[BotFather.SendAsync] Inline markup rows count: {inlineMarkup.Rows?.Count ?? 0}");
                if (inlineMarkup.Rows != null)
                {
                    foreach (var row in inlineMarkup.Rows)
                    {
                        if (row is TKeyboardButtonRow btnRow)
                        {
                            Console.WriteLine($"[BotFather.SendAsync] Row buttons count: {btnRow.Buttons?.Count ?? 0}");
                            if (btnRow.Buttons != null)
                            {
                                foreach (var btn in btnRow.Buttons)
                                {
                                    Console.WriteLine($"[BotFather.SendAsync] Button type: {btn.GetType().Name}");
                                    if (btn is TKeyboardButtonCallback callbackBtn)
                                    {
                                        Console.WriteLine($"[BotFather.SendAsync] Callback button text: {callbackBtn.Text}, data length: {callbackBtn.Data?.Length ?? 0}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        var botReq = RequestInfo.Empty with
        {
            UserId = BotUserId, Layer = input.Layer,
            Date = DateTime.UtcNow.ToTimestamp(), RequestId = Guid.NewGuid()
        };
        var toPeer = new Peer(PeerType.User, toUserId);
        var sendInput = new SendMessageInput(botReq, BotUserId, toPeer, text,
            Random.Shared.NextInt64(), entities, null, false, replyMarkup: replyMarkup);
        await messageAppService.SendMessageAsync([sendInput]);
    }

    private async Task EditMessageAsync(IRequestInput input, long toUserId, int msgId, string text,
        IReplyMarkup? replyMarkup)
    {
        var botReq = RequestInfo.Empty with
        {
            UserId = BotUserId, Layer = input.Layer,
            Date = DateTime.UtcNow.ToTimestamp(), RequestId = Guid.NewGuid()
        };

        // Find the outbox message id for the bot
        var messageReadModel = await queryProcessor.ProcessAsync(
            new GetMessageByIdQuery(MessageId.Create(BotUserId, msgId).Value));

        if (messageReadModel != null)
        {
            var command = new EditOutboxMessageCommand(
                MessageId.Create(BotUserId, msgId, false),
                botReq, msgId, text, DateTime.UtcNow.ToTimestamp(),
                (TVector<IMessageEntity>?)null, null, replyMarkup, false, null, null, null);
            await commandBus.PublishAsync(command);
        }
        else
        {
            await SendAsync(input, toUserId, text, replyMarkup);
        }
    }

    // --- Markup builders ---

    private static TReplyInlineMarkup InlineRows(params TKeyboardButtonRow[] rows)
        => new() { Rows = new TVector<IKeyboardButtonRow>(rows.Cast<IKeyboardButtonRow>().ToList()) };

    private static TKeyboardButtonRow InlineRow(params TKeyboardButtonCallback[] btns)
        => new() { Buttons = new TVector<IKeyboardButton>(btns.Cast<IKeyboardButton>().ToList()) };

    private static TKeyboardButtonCallback Btn(string text, string data)
        => new() { Text = text, Data = data };

    // --- Text helpers ---

    private static string Esc(string s) => s;

    private static List<IMessageEntity> BoldEntities(string text)
    {
        var entities = new List<IMessageEntity>();
        var boldSections = new[] { "Managing bots", "Edit Bots", "Bot Settings" };
        foreach (var section in boldSections)
        {
            var idx = text.IndexOf(section, StringComparison.Ordinal);
            if (idx >= 0)
                entities.Add(new TMessageEntityBold { Offset = idx, Length = section.Length });
        }
        // Add bot command entities for each /command
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            var cmdIdx = line.IndexOf('/');
            if (cmdIdx >= 0)
            {
                var cmdEnd = line.IndexOf(' ', cmdIdx);
                var cmdLen = cmdEnd >= 0 ? cmdEnd - cmdIdx : line.Length - cmdIdx;
                entities.Add(new TMessageEntityBotCommand { Offset = offset + cmdIdx, Length = cmdLen });
            }
            offset += line.Length + 1;
        }
        return entities;
    }

    /// <summary>
    /// Inline mode screen. The InlineEnabled flag on the bot's state document is what
    /// messages.getInlineBotResults checks before forwarding queries to the bot.
    /// </summary>
    private async Task SendInlineModeMenuAsync(IRequestInput input, long fromUserId, int msgId, string username)
    {
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null) return;

        var isEnabled = bot.TryGetValue("InlineEnabled", out var enabledValue) && enabledValue.IsBoolean &&
                        enabledValue.AsBoolean;
        var status = isEnabled ? "✅ Enabled" : "❌ Disabled";

        await EditMessageAsync(input, fromUserId, msgId,
            $"Inline Mode for @{username}\n\nStatus: {status}\n\n" +
            "When enabled, users can call this bot from any chat by typing " +
            $"@{username} followed by a query.\n\n" +
            "The bot must answer each query with messages.setInlineBotResults.",
            InlineRows(
                InlineRow(
                    isEnabled
                        ? Btn("❌ Turn off Inline Mode", $"inline_disable:{username}")
                        : Btn("✅ Turn on Inline Mode", $"inline_enable:{username}")
                ),
                InlineRow(Btn("« Back to Settings", $"bot_settings:{username}"))
            ));
    }

    private async Task SetBotInlineModeAsync(long ownerId, string username, bool enabled)
    {
        var bot = await GetBotAsync(ownerId, username);
        if (bot == null) return;

        await mongoDatabase.GetCollection<BsonDocument>(BotCollection).UpdateOneAsync(
            new BsonDocument("BotUserId", bot["BotUserId"].AsInt64),
            new BsonDocument("$set", new BsonDocument("InlineEnabled", enabled)));
    }

    private async Task SendBusinessModeMenuAsync(IRequestInput input, long fromUserId, int msgId, string username)
    {
        // Check if business mode is enabled for the BOT (not the user)
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null) return;

        var botUserId = bot["BotUserId"].AsInt64;
        var userCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = new BsonDocument("UserId", botUserId);
        var botUser = await userCollection.Find(filter).FirstOrDefaultAsync();

        var isEnabled = botUser != null && botUser.Contains("BotBusiness") && botUser["BotBusiness"].AsBoolean;
        var status = isEnabled ? "✅ Enabled" : "❌ Disabled";

        await EditMessageAsync(input, fromUserId, msgId,
            $"Business Mode for @{username}\n\nStatus: {status}\n\n" +
            "When enabled, this bot can:\n" +
            "• Connect to Telegram Business accounts\n" +
            "• Receive business messages\n" +
            "• Send messages on behalf of business users\n" +
            "• Access business chat history\n\n" +
            "Note: Users must have Telegram Business subscription to connect bots.",
            InlineRows(
                InlineRow(
                    isEnabled
                        ? Btn("❌ Disable Business Mode", $"business_disable:{username}")
                        : Btn("✅ Enable Business Mode", $"business_enable:{username}")
                ),
                InlineRow(Btn("« Back to Settings", $"bot_settings:{username}"))
            ));
    }

    private async Task EnableBotBusinessModeAsync(IRequestInput input, long ownerId, string username)
    {
        var bot = await GetBotAsync(ownerId, username);
        if (bot == null) return;

        var botUserId = bot["BotUserId"].AsInt64;

        // Set BotBusiness flag for the bot user
        var userCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = new BsonDocument("UserId", botUserId);
        var update = new BsonDocument("$set", new BsonDocument
        {
            { "BotBusiness", true }
        });

        await userCollection.UpdateOneAsync(filter, update);
    }

    private async Task DisableBotBusinessModeAsync(IRequestInput input, long ownerId, string username)
    {
        var bot = await GetBotAsync(ownerId, username);
        if (bot == null) return;

        var botUserId = bot["BotUserId"].AsInt64;

        // Remove BotBusiness flag from the bot user
        var userCollection = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = new BsonDocument("UserId", botUserId);
        var update = new BsonDocument("$unset", new BsonDocument
        {
            { "BotBusiness", "" }
        });

        await userCollection.UpdateOneAsync(filter, update);

        // Disconnect all business connections for this bot
        var connectionsCollection = mongoDatabase.GetCollection<BsonDocument>("connected_business_bots");
        await connectionsCollection.DeleteManyAsync(new BsonDocument("BotId", botUserId));
    }
}
