using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Services.Bots;

public partial class BotFatherBotService
{
    // --- Web apps (mini apps) ---
    //
    // A mini app is an ordinary HTTPS page hosted by the bot's developer, so the URL can only come
    // from the bot's owner. These flows are what let them set it; the server never invents one.
    // See https://corefork.telegram.org/api/bots/webapps .

    private const string AppCollection = "bot_apps";

    /// <summary>Short name rules from BotFather: 3-30 chars, a-zA-Z0-9_.</summary>
    private static readonly Regex AppShortNameRegex = new("^[a-zA-Z0-9_]{3,30}$", RegexOptions.Compiled);

    private async Task HandleNewAppStateAsync(IRequestInput input, long fromUserId, string step, string extra,
        string text, string[] stateParts)
    {
        switch (step)
        {
            case "pick":
            {
                var username = text.Trim().TrimStart('@').ToLowerInvariant();
                var bot = await GetBotAsync(fromUserId, username);
                if (bot == null)
                {
                    await SendAsync(input, fromUserId, InvalidBotText,
                        new TReplyKeyboardHide());
                    return;
                }

                await SaveDraftAsync(fromUserId, new BsonDocument
                {
                    ["_id"] = DraftId(fromUserId),
                    ["user_id"] = fromUserId,
                    ["username"] = username
                });

                await SetStateAsync(fromUserId, "newapp:title");
                await SendAsync(input, fromUserId,
                    $"Creating a new web app for @{username}. Please enter a title for the web app.",
                    new TReplyKeyboardHide());
                break;
            }

            case "title":
                await UpdateDraftAsync(fromUserId, "title", text);
                await SetStateAsync(fromUserId, "newapp:description");
                await SendAsync(input, fromUserId, "Please enter a short description of the web app.");
                break;

            case "description":
                await UpdateDraftAsync(fromUserId, "description", text);
                await SetStateAsync(fromUserId, "newapp:photo");
                await SendAsync(input, fromUserId, $"Please upload a photo, {RequiredPhotoWidth}x{RequiredPhotoHeight} pixels.");
                break;

            case "photo":
                // Only a photo upload advances this step; HandleMediaAsync deals with that.
                await SendAsync(input, fromUserId,
                    $"Please upload a photo, {RequiredPhotoWidth}x{RequiredPhotoHeight} pixels.");
                break;

            case "gif":
                if (IsEmpty(text))
                {
                    await SetStateAsync(fromUserId, "newapp:url");
                    await SendAsync(input, fromUserId,
                        "No problem, you can always add a GIF later using the /editapp command. " +
                        "Now please send me the Web App URL that will be opened when users follow a web app direct link.");
                    return;
                }

                await SendAsync(input, fromUserId, "Now upload a demo GIF or send /empty to skip this step.");
                break;

            case "url":
            {
                if (!IsHttpsUrl(text))
                {
                    await SendAsync(input, fromUserId, "Please send me a valid URL. https is required.");
                    return;
                }

                await UpdateDraftAsync(fromUserId, "url", text.Trim());
                await SetStateAsync(fromUserId, "newapp:shortname");

                var draft = await GetDraftAsync(fromUserId);
                await SendAsync(input, fromUserId,
                    "Good! Now please choose a short name for your web app: 3-30 characters, a-zA-Z0-9_. " +
                    $"This short name will be used in URLs like t.me/{GetString(draft, "username")}/myapp and serve " +
                    "as a unique identifier for your web app.");
                break;
            }

            case "shortname":
            {
                var shortName = text.Trim();
                if (!AppShortNameRegex.IsMatch(shortName))
                {
                    await SendAsync(input, fromUserId,
                        "Sorry, this short name is invalid. It must be 3-30 characters long and contain only a-zA-Z0-9_.");
                    return;
                }

                var draft = await GetDraftAsync(fromUserId);
                var username = GetString(draft, "username");

                var bot = await GetBotAsync(fromUserId, username);
                if (bot == null)
                {
                    await ClearStateAsync(fromUserId);
                    await DeleteDraftAsync(fromUserId);
                    await SendAsync(input, fromUserId, InvalidBotText);
                    return;
                }

                var botUserId = bot["BotUserId"].AsInt64;
                if (await GetAppAsync(botUserId, shortName) != null)
                {
                    await SendAsync(input, fromUserId,
                        "Sorry, this short name is already taken. Please choose a different one.");
                    return;
                }

                await CreateAppAsync(botUserId, shortName, draft!);
                await ClearStateAsync(fromUserId);
                await DeleteDraftAsync(fromUserId);
                await SendAsync(input, fromUserId,
                    $"You can now use {shortName} as the short_name parameter value in Bot API. " +
                    $"Your web app link is t.me/{username}/{shortName}. Open it to start developing your web app!");
                break;
            }
        }
    }

    private async Task HandleEditAppStateAsync(IRequestInput input, long fromUserId, string step, string extra,
        string text, string[] stateParts)
    {
        switch (step)
        {
            case "link":
            {
                var app = await ResolveAppLinkAsync(fromUserId, text);
                if (app == null)
                {
                    await SendAsync(input, fromUserId, "Invalid web app link.");
                    return;
                }

                await SetStateAsync(fromUserId, $"editapp:title:{app.Value.Username}:{app.Value.ShortName}");
                await SendAsync(input, fromUserId,
                    $"Ok. Please enter the new title. Current title: {GetString(app.Value.Document, "title")}. " +
                    "Use /skip to leave the title as is.");
                break;
            }

            case "title":
            {
                var shortName = stateParts.Length > 3 ? stateParts[3] : string.Empty;
                if (!IsSkip(text))
                {
                    await UpdateAppFieldAsync(fromUserId, extra, shortName, "title", text);
                    await MarkEditChangedAsync(fromUserId);
                }

                await SetStateAsync(fromUserId, $"editapp:description:{extra}:{shortName}");
                var app = await ResolveAppAsync(fromUserId, extra, shortName);
                await SendAsync(input, fromUserId,
                    $"Please enter the new description. Current description: {GetString(app, "description")}. " +
                    "Use /skip to leave the description as is.");
                break;
            }

            case "description":
            {
                var shortName = stateParts.Length > 3 ? stateParts[3] : string.Empty;
                if (!IsSkip(text))
                {
                    await UpdateAppFieldAsync(fromUserId, extra, shortName, "description", text);
                    await MarkEditChangedAsync(fromUserId);
                }

                await SetStateAsync(fromUserId, $"editapp:photo:{extra}:{shortName}");
                await SendAsync(input, fromUserId, "Please send a new photo. Use /skip to leave the photo as is.");
                break;
            }

            case "photo":
            {
                var shortName = stateParts.Length > 3 ? stateParts[3] : string.Empty;

                // A photo upload is handled by HandleMediaAsync; only /skip moves on from text.
                if (!IsSkip(text))
                {
                    await SendAsync(input, fromUserId, "Please send a new photo. Use /skip to leave the photo as is.");
                    return;
                }

                await SetStateAsync(fromUserId, $"editapp:gif:{extra}:{shortName}");
                await SendAsync(input, fromUserId,
                    "Please send a new GIF. Use /skip to leave the GIF as is or /empty to remove the current GIF.");
                break;
            }

            case "gif":
            {
                var shortName = stateParts.Length > 3 ? stateParts[3] : string.Empty;

                if (IsEmpty(text))
                {
                    await UpdateAppMediaAsync(fromUserId, extra, shortName, "gif", null);
                    await MarkEditChangedAsync(fromUserId);
                }
                else if (!IsSkip(text))
                {
                    await SendAsync(input, fromUserId,
                        "Please send a new GIF. Use /skip to leave the GIF as is or /empty to remove the current GIF.");
                    return;
                }

                await SetStateAsync(fromUserId, $"editapp:url:{extra}:{shortName}");
                await SendAsync(input, fromUserId,
                    "Please send a new web app URL. Use /skip to leave the URL as is.");
                break;
            }

            case "url":
            {
                var shortName = stateParts.Length > 3 ? stateParts[3] : string.Empty;

                if (IsSkip(text))
                {
                    var changed = await ConsumeEditChangedAsync(fromUserId);
                    await ClearStateAsync(fromUserId);
                    await SendAsync(input, fromUserId, changed ? "Success!" : "No changes were made.");
                    return;
                }

                if (!IsHttpsUrl(text))
                {
                    await SendAsync(input, fromUserId, "Please send me a valid URL. https is required.");
                    return;
                }

                await UpdateAppFieldAsync(fromUserId, extra, shortName, "url", text);
                await ConsumeEditChangedAsync(fromUserId);
                await ClearStateAsync(fromUserId);
                await SendAsync(input, fromUserId, "Success!");
                break;
            }
        }
    }

    private async Task HandleDeleteAppStateAsync(IRequestInput input, long fromUserId, string step, string extra,
        string text)
    {
        switch (step)
        {
            case "link":
            {
                var app = await ResolveAppLinkAsync(fromUserId, text);
                if (app == null)
                {
                    await SendAsync(input, fromUserId, "Invalid web app link.");
                    return;
                }

                await SetStateAsync(fromUserId, $"deleteapp:confirm:{app.Value.Username}:{app.Value.ShortName}");
                await SendAsync(input, fromUserId,
                    $"Ok, deleting {app.Value.ShortName} ({GetString(app.Value.Document, "title")} " +
                    $"(t.me/{app.Value.Username}/{app.Value.ShortName})). " +
                    "Please type 'Yes, I am totally sure.' to proceed.");
                break;
            }

            case "confirm":
            {
                if (!text.Trim().Equals("Yes, I am totally sure.", StringComparison.Ordinal))
                {
                    await SendAsync(input, fromUserId,
                        "Please type 'Yes, I am totally sure.' to proceed, or /cancel to abort.");
                    return;
                }

                // State is deleteapp:confirm:<username>:<shortName>.
                var parts = (await GetStateAsync(fromUserId))?.Split(':') ?? [];
                var username = parts.Length > 2 ? parts[2] : extra;
                var shortName = parts.Length > 3 ? parts[3] : string.Empty;

                var bot = await GetBotAsync(fromUserId, username);
                await ClearStateAsync(fromUserId);

                if (bot == null)
                {
                    await SendAsync(input, fromUserId, "Invalid web app link.");
                    return;
                }

                await mongoDatabase.GetCollection<BsonDocument>(AppCollection).DeleteOneAsync(
                    Builders<BsonDocument>.Filter.And(
                        Builders<BsonDocument>.Filter.Eq("bot_id", bot["BotUserId"].AsInt64),
                        Builders<BsonDocument>.Filter.Eq("short_name", shortName)));

                await SendAsync(input, fromUserId, "Success");
                break;
            }
        }
    }

    /// <summary>
    /// Sets or clears the bot's main mini app URL - the one behind the profile's "Open App" button,
    /// used by messages.requestMainWebView.
    /// </summary>
    private async Task HandleMainAppUrlAsync(IRequestInput input, long fromUserId, string username, string text)
    {
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null)
        {
            await ClearStateAsync(fromUserId);
            await SendAsync(input, fromUserId, InvalidBotText);
            return;
        }

        var botUserId = bot["BotUserId"].AsInt64;

        if (text.Trim().Equals("/empty", StringComparison.OrdinalIgnoreCase))
        {
            await ClearStateAsync(fromUserId);
            await mongoDatabase.GetCollection<BsonDocument>(BotCollection).UpdateOneAsync(
                new BsonDocument("BotUserId", botUserId),
                new BsonDocument("$unset", new BsonDocument("MainAppUrl", "")));
            await SetBotHasMainAppAsync(botUserId, false);
            await SendAsync(input, fromUserId, "Success! Mini App disabled for this bot.");
            return;
        }

        if (!IsHttpsUrl(text))
        {
            await SendAsync(input, fromUserId, "Please send me a valid URL. https is required.");
            return;
        }

        await ClearStateAsync(fromUserId);
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection).UpdateOneAsync(
            new BsonDocument("BotUserId", botUserId),
            new BsonDocument("$set", new BsonDocument("MainAppUrl", text.Trim())));

        // BotHasMainApp on the user read model is what makes clients show the "Open App" button.
        await SetBotHasMainAppAsync(botUserId, true);

        await SendAsync(input, fromUserId, "Success! Mini App settings updated.",
            InlineRows(
                InlineRow(Btn("« Back to Bot", $"bot_select:{username}")),
                InlineRow(Btn("« Back to Bot List", "back_to_bots"))
            ));
    }

    private async Task SendMainAppMenuAsync(IRequestInput input, long fromUserId, int msgId, string username)
    {
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null) return;

        var currentUrl = bot.TryGetValue("MainAppUrl", out var urlValue) && urlValue.IsString
            ? urlValue.AsString
            : null;

        var status = string.IsNullOrEmpty(currentUrl)
            ? $"Mini App is currently disabled for @{username}."
            : $"Mini App URL for @{username}: {currentUrl}";

        await SetStateAsync(fromUserId, $"mainapp:url:{username}");
        await EditMessageAsync(input, fromUserId, msgId,
            $"{status}\n\nSend me the Mini App URL that will be opened by tapping the 'Open App' button.\n\n" +
            "Use /empty to disable Mini App for this bot.",
            InlineRows(InlineRow(Btn("« Back to Settings", $"bot_settings:{username}"))));
    }

    /// <summary>Lists every web app the user owns, one inline button per app.</summary>
    private async Task SendMyAppsAsync(IRequestInput input, long fromUserId)
    {
        var bots = await GetUserBotsAsync(fromUserId);
        if (bots.Count == 0)
        {
            await SendAsync(input, fromUserId, "You have no bots yet. Use /newbot to create one.");
            return;
        }

        var rows = new List<TKeyboardButtonRow>();
        foreach (var bot in bots)
        {
            var username = bot["Username"].AsString;
            foreach (var app in await GetBotAppsAsync(bot["BotUserId"].AsInt64))
            {
                var shortName = GetString(app, "short_name");
                rows.Add(InlineRow(Btn($"{GetString(app, "title")} @{username}",
                    $"app_select:{username}:{shortName}")));
            }
        }

        if (rows.Count == 0)
        {
            await SendAsync(input, fromUserId,
                "You currently have no web apps. Use /newapp command to create a first web app.");
            return;
        }

        await SendAsync(input, fromUserId, "Choose a web app:", InlineRows(rows.ToArray()));
    }

    private async Task SendAppListTextAsync(IRequestInput input, long fromUserId, string username, BsonDocument bot)
    {
        await ClearStateAsync(fromUserId);

        var apps = await GetBotAppsAsync(bot["BotUserId"].AsInt64);
        if (apps.Count == 0)
        {
            await SendAsync(input, fromUserId,
                $"@{username} has no web apps. Use /newapp command to create one.", new TReplyKeyboardHide());
            return;
        }

        var lines = apps.Select(a =>
            $"• {GetString(a, "title")} - t.me/{username}/{GetString(a, "short_name")}\n  {GetString(a, "url")}");

        await SendAsync(input, fromUserId,
            $"Web apps of @{username}:\n\n{string.Join("\n", lines)}", new TReplyKeyboardHide());
    }

    private async Task SendAppMenuAsync(IRequestInput input, long fromUserId, int msgId, string username,
        string shortName)
    {
        var app = await ResolveAppAsync(fromUserId, username, shortName);
        if (app == null)
        {
            await EditMessageAsync(input, fromUserId, msgId, "Invalid web app link.", null);
            return;
        }

        await EditMessageAsync(input, fromUserId, msgId,
            $"Web app {GetString(app, "title")} (t.me/{username}/{shortName})\n\n" +
            $"Description: {GetString(app, "description")}\n" +
            $"URL: {GetString(app, "url")}",
            InlineRows(InlineRow(Btn("« Back to Bot List", "back_to_bots"))));
    }

    // --- Web app DB helpers ---

    private async Task CreateAppAsync(long botId, string shortName, BsonDocument draft)
    {
        var document = new BsonDocument
        {
            ["bot_id"] = botId,
            ["app_id"] = await GetNextAppIdAsync(),
            ["access_hash"] = Random.Shared.NextInt64(),
            ["short_name"] = shortName,
            ["title"] = GetString(draft, "title"),
            ["description"] = GetString(draft, "description"),
            ["url"] = GetString(draft, "url"),
            ["hash"] = Random.Shared.NextInt64(),
            ["request_write_access"] = false,
            ["has_settings"] = false,
            ["inactive"] = false,
            ["created_at"] = DateTime.UtcNow.ToTimestamp()
        };

        // Photo is mandatory in the flow, the demo GIF is optional.
        if (draft.TryGetValue("photo", out var photo) && photo.BsonType == BsonType.Binary)
        {
            document["photo"] = photo;
        }

        if (draft.TryGetValue("gif", out var gif) && gif.BsonType == BsonType.Binary)
        {
            document["gif"] = gif;
        }

        await mongoDatabase.GetCollection<BsonDocument>(AppCollection).InsertOneAsync(document);
    }

    private async Task<long> GetNextAppIdAsync()
    {
        var result = await mongoDatabase.GetCollection<BsonDocument>("counters").FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", "bot_app_id"),
            Builders<BsonDocument>.Update.Inc("seq", 1),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });

        return result["seq"].ToInt64();
    }

    private async Task<BsonDocument?> GetAppAsync(long botId, string shortName)
    {
        return await mongoDatabase.GetCollection<BsonDocument>(AppCollection)
            .Find(Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("bot_id", botId),
                Builders<BsonDocument>.Filter.Eq("short_name", shortName)))
            .FirstOrDefaultAsync();
    }

    private async Task<List<BsonDocument>> GetBotAppsAsync(long botId)
    {
        return await mongoDatabase.GetCollection<BsonDocument>(AppCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("bot_id", botId))
            .Sort(Builders<BsonDocument>.Sort.Ascending("created_at"))
            .ToListAsync();
    }

    private async Task<BsonDocument?> ResolveAppAsync(long ownerId, string username, string shortName)
    {
        var bot = await GetBotAsync(ownerId, username);
        return bot == null ? null : await GetAppAsync(bot["BotUserId"].AsInt64, shortName);
    }

    /// <summary>
    /// Parses a web app link (t.me/bot/app, https://t.me/bot/app, @bot/app) and verifies the caller
    /// owns the bot behind it.
    /// </summary>
    private async Task<(string Username, string ShortName, BsonDocument Document)?> ResolveAppLinkAsync(
        long ownerId, string link)
    {
        var value = link.Trim();
        foreach (var prefix in new[] { "https://", "http://" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..];
            }
        }

        foreach (var host in new[] { "t.me/", "telegram.me/" })
        {
            if (value.StartsWith(host, StringComparison.OrdinalIgnoreCase))
            {
                value = value[host.Length..];
            }
        }

        var segments = value.TrimStart('@').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            return null;
        }

        var username = segments[0].ToLowerInvariant();
        var shortName = segments[1];

        var bot = await GetBotAsync(ownerId, username);
        if (bot == null)
        {
            return null;
        }

        var app = await GetAppAsync(bot["BotUserId"].AsInt64, shortName);
        return app == null ? null : (username, shortName, app);
    }

    private async Task UpdateAppFieldAsync(long ownerId, string username, string shortName, string field, string value)
    {
        var bot = await GetBotAsync(ownerId, username);
        if (bot == null) return;

        await mongoDatabase.GetCollection<BsonDocument>(AppCollection).UpdateOneAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("bot_id", bot["BotUserId"].AsInt64),
                Builders<BsonDocument>.Filter.Eq("short_name", shortName)),
            Builders<BsonDocument>.Update
                .Set(field, value.Trim())
                // Bump the hash so clients refetch via messages.getBotApp.
                .Set("hash", Random.Shared.NextInt64()));
    }

    private async Task SetBotHasMainAppAsync(long botUserId, bool hasMainApp)
    {
        await UpdateBotUserAsync(botUserId, Builders<BsonDocument>.Update.Set("BotHasMainApp", hasMainApp));
    }

    private static bool IsSkip(string text) =>
        text.Trim().Equals("/skip", StringComparison.OrdinalIgnoreCase);

    private static bool IsEmpty(string text) =>
        text.Trim().Equals("/empty", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpsUrl(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static string GetString(BsonDocument? doc, string name)
    {
        return doc != null && doc.TryGetValue(name, out var value) && value.IsString ? value.AsString : string.Empty;
    }

    // --- Media uploads ---

    /// <summary>Web app preview photos must be exactly this size, as upstream BotFather requires.</summary>
    private const int RequiredPhotoWidth = BotAppMediaValidator.RequiredPhotoWidth;

    private const int RequiredPhotoHeight = BotAppMediaValidator.RequiredPhotoHeight;

    private const string DraftCollection = "botfather-app-drafts";

    /// <summary>
    /// Handles a photo or GIF sent to BotFather. Which step is waiting for it is decided by the FSM
    /// state, so an upload arriving out of turn is politely ignored rather than mis-filed.
    /// </summary>
    public async Task HandleMediaAsync(IRequestInput input, long fromUserId, IMessageMedia? media)
    {
        if (media == null)
        {
            return;
        }

        var state = await GetStateAsync(fromUserId);
        if (state == null)
        {
            return;
        }

        var parts = state.Split(':');
        var flow = parts[0];
        var step = parts.Length > 1 ? parts[1] : string.Empty;

        switch (flow)
        {
            case "newapp" when step == "photo":
                await HandleNewAppPhotoAsync(input, fromUserId, media);
                break;

            case "newapp" when step == "gif":
                await HandleNewAppGifAsync(input, fromUserId, media);
                break;

            case "editapp" when step == "photo":
                await HandleEditAppPhotoAsync(input, fromUserId, media, parts);
                break;

            case "editapp" when step == "gif":
                await HandleEditAppGifAsync(input, fromUserId, media, parts);
                break;

            case "setuserpic":
            case "setdescriptionpic":
            case "settransferrecepient":
                await HandleBotMediaAsync(input, fromUserId, flow, parts.Length > 2 ? parts[2] : string.Empty, media);
                break;
        }
    }

    private async Task HandleNewAppPhotoAsync(IRequestInput input, long fromUserId, IMessageMedia media)
    {
        if (media is not TMessageMediaPhoto)
        {
            await SendAsync(input, fromUserId,
                $"Please upload a photo, {RequiredPhotoWidth}x{RequiredPhotoHeight} pixels.");
            return;
        }

        if (!BotAppMediaValidator.IsValidPreviewPhoto(media))
        {
            await SendAsync(input, fromUserId,
                $"Sorry, the image dimensions are invalid. Must be {RequiredPhotoWidth}x{RequiredPhotoHeight}.");
            return;
        }

        await UpdateDraftAsync(fromUserId, "photo", media.ToBytes());
        await SetStateAsync(fromUserId, "newapp:gif");
        await SendAsync(input, fromUserId, "Now upload a demo GIF or send /empty to skip this step.");
    }

    private async Task HandleNewAppGifAsync(IRequestInput input, long fromUserId, IMessageMedia media)
    {
        if (!BotAppMediaValidator.IsAnimation(media))
        {
            await SendAsync(input, fromUserId, "Please upload a GIF or send /empty to skip this step.");
            return;
        }

        await UpdateDraftAsync(fromUserId, "gif", media.ToBytes());
        await SetStateAsync(fromUserId, "newapp:url");
        await SendAsync(input, fromUserId,
            "Now please send me the Web App URL that will be opened when users follow a web app direct link.");
    }

    private async Task HandleEditAppPhotoAsync(IRequestInput input, long fromUserId, IMessageMedia media,
        string[] stateParts)
    {
        var username = stateParts.Length > 2 ? stateParts[2] : string.Empty;
        var shortName = stateParts.Length > 3 ? stateParts[3] : string.Empty;

        if (media is not TMessageMediaPhoto)
        {
            await SendAsync(input, fromUserId, "Please send a new photo. Use /skip to leave the photo as is.");
            return;
        }

        if (!BotAppMediaValidator.IsValidPreviewPhoto(media))
        {
            await SendAsync(input, fromUserId,
                $"Sorry, the image dimensions are invalid. Must be {RequiredPhotoWidth}x{RequiredPhotoHeight}.");
            return;
        }

        await UpdateAppMediaAsync(fromUserId, username, shortName, "photo", media.ToBytes());
        await MarkEditChangedAsync(fromUserId);

        await SetStateAsync(fromUserId, $"editapp:gif:{username}:{shortName}");
        await SendAsync(input, fromUserId,
            "Please send a new GIF. Use /skip to leave the GIF as is or /empty to remove the current GIF.");
    }

    private async Task HandleEditAppGifAsync(IRequestInput input, long fromUserId, IMessageMedia media,
        string[] stateParts)
    {
        var username = stateParts.Length > 2 ? stateParts[2] : string.Empty;
        var shortName = stateParts.Length > 3 ? stateParts[3] : string.Empty;

        if (!BotAppMediaValidator.IsAnimation(media))
        {
            await SendAsync(input, fromUserId,
                "Please send a new GIF. Use /skip to leave the GIF as is or /empty to remove the current GIF.");
            return;
        }

        await UpdateAppMediaAsync(fromUserId, username, shortName, "gif", media.ToBytes());
        await MarkEditChangedAsync(fromUserId);

        await SetStateAsync(fromUserId, $"editapp:url:{username}:{shortName}");
        await SendAsync(input, fromUserId, "Please send a new web app URL. Use /skip to leave the URL as is.");
    }

    // --- Web app draft helpers ---
    //
    // A /newapp run spans several messages and now carries binary media, so the draft lives in
    // Mongo rather than being packed into the ':'-delimited FSM state string.

    private static string DraftId(long userId) => $"botfather-app-draft-{userId}";

    private async Task SaveDraftAsync(long userId, BsonDocument draft)
    {
        await mongoDatabase.GetCollection<BsonDocument>(DraftCollection).ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DraftId(userId)),
            draft,
            new ReplaceOptions { IsUpsert = true });
    }

    private async Task UpdateDraftAsync(long userId, string field, BsonValue value)
    {
        await mongoDatabase.GetCollection<BsonDocument>(DraftCollection).UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", DraftId(userId)),
            Builders<BsonDocument>.Update.Set(field, value).SetOnInsert("user_id", userId),
            new UpdateOptions { IsUpsert = true });
    }

    private async Task<BsonDocument?> GetDraftAsync(long userId)
    {
        return await mongoDatabase.GetCollection<BsonDocument>(DraftCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("_id", DraftId(userId)))
            .FirstOrDefaultAsync();
    }

    private async Task DeleteDraftAsync(long userId)
    {
        await mongoDatabase.GetCollection<BsonDocument>(DraftCollection)
            .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", DraftId(userId)));
    }

    /// <summary>
    /// Records that an /editapp run has changed something, so the final step can honestly say
    /// "Success!" versus "No changes were made."
    /// </summary>
    private async Task MarkEditChangedAsync(long userId)
    {
        await UpdateDraftAsync(userId, "edit_changed", true);
    }

    private async Task<bool> ConsumeEditChangedAsync(long userId)
    {
        var draft = await GetDraftAsync(userId);
        await DeleteDraftAsync(userId);

        return draft != null && draft.TryGetValue("edit_changed", out var value) && value.IsBoolean &&
               value.AsBoolean;
    }

    /// <summary>Sets or clears a media field on a stored web app.</summary>
    private async Task UpdateAppMediaAsync(long ownerId, string username, string shortName, string field,
        byte[]? bytes)
    {
        var bot = await GetBotAsync(ownerId, username);
        if (bot == null) return;

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("bot_id", bot["BotUserId"].AsInt64),
            Builders<BsonDocument>.Filter.Eq("short_name", shortName));

        var update = bytes == null
            ? Builders<BsonDocument>.Update.Unset(field)
            : Builders<BsonDocument>.Update.Set(field, bytes);

        // Bump the hash so clients refetch via messages.getBotApp.
        await mongoDatabase.GetCollection<BsonDocument>(AppCollection).UpdateOneAsync(
            filter, update.Set("hash", Random.Shared.NextInt64()));
    }
}
