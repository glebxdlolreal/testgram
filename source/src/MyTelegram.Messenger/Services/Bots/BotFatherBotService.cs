using System.Text;
using MyTelegram.Messenger.Services.Premium;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Passport;
using StackExchange.Redis;

namespace MyTelegram.Messenger.Services.Bots;

public interface IBotFatherBotService
{
    Task HandleMessageAsync(IRequestInput input, long fromUserId, string message);

    /// <param name="msgId">The id of the pressed message in the <em>user's</em> box, as messages.getBotCallbackAnswer carries it.</param>
    Task HandleCallbackAsync(IRequestInput input, long fromUserId, int msgId, string data);

    /// <summary>
    /// Handles a media message sent to BotFather (photo / GIF uploads for web app flows).
    /// </summary>
    /// <param name="media">The saved media, as produced by <c>IMediaHelper.SaveMediaAsync</c>.</param>
    Task HandleMediaAsync(IRequestInput input, long fromUserId, IMessageMedia? media);

    /// <summary>
    /// Checks the 2FA password of a requires_password button (transferring a bot) and throws the RPC
    /// error the client shows; other buttons pass.
    /// </summary>
    Task CheckCallbackPasswordAsync(IRequestInput input, string data, IInputCheckPasswordSRP? password);
}

/// <summary>
/// The in-process @BotFather. Texts, keyboards and entities follow real BotFather (measured 2026-11-02
/// with Telethon); only commands this server can carry out are offered. Menus reached through inline
/// buttons are edited in place, everything typed is answered with a new message.
/// </summary>
public partial class BotFatherBotService(
    IMessageAppService messageAppService,
    IMongoDatabase mongoDatabase,
    IConnectionMultiplexer redis,
    ICommandBus commandBus,
    IQueryProcessor queryProcessor,
    IPassportBotResolver passportBotResolver,
    IPremiumLimitResolver premiumLimitResolver,
    IReadModelCacheHelper<IUserReadModel> userReadModelCacheHelper,
    MyTelegram.Messenger.Services.Business.IBusinessConnectionStore businessConnectionStore,
    MyTelegram.Messenger.Services.Business.IBusinessBotNotifier businessBotNotifier,
    IDataEncryptionHelper dataEncryptionHelper,
    IOptionsMonitor<MyTelegramMessengerServerOptions> serverOptions,
    MyTelegram.Messenger.Services.TwoFactor.ITwoFactorService twoFactorService,
    IBotBroadcastLimiter botBroadcastLimiter,
    IEventBus eventBus,
    ILogger<BotFatherBotService> logger) : IBotFatherBotService, ISingletonDependency
{
    public const long BotUserId = MyTelegramConsts.BotFatherUserId;
    private const string BotCollection = "botfather-bot-state";
    private const string UserCollection = "eventflow-userreadmodel";
    private const int BotsPerPage = 6;

    // Limits of the Bot API methods the same fields are set through.
    private const int MaxNameLength = 64;
    private const int MaxAboutLength = 120;
    private const int MaxDescriptionLength = 512;
    private const int MaxCommandCount = 100;
    private const int MaxInlinePlaceholderLength = 64;
    private const int MaxMenuButtonTextLength = 64;

    private const string BotsManualUrl = "https://core.telegram.org/bots";
    private const string BotApiUrl = "https://core.telegram.org/bots/api";
    private const string DeleteConfirmation = "Yes, I am totally sure.";
    private const string InvalidBotText = "Invalid bot selected.";
    private const string LoginWidgetUrl = "https://core.telegram.org/widgets/login";
    private const string TransferConfirmation = "Yes, I am sure, proceed.";

    /// <summary>Prod refuses a transfer for a week after the 2FA password was set.</summary>
    private static readonly TimeSpan TransferPasswordAge = TimeSpan.FromDays(7);

    private const string UserpicPrompt = "OK. Send me the new profile photo for the bot.";

    private const string DescriptionPicPrompt =
        "OK. Please upload a description photo for the bot, 640x360 pixels. Or a GIF,  320x180, 640x360 or 960x540 " +
        "pixels. People will see this photo or GIF when they open a chat with your bot, in a block titled 'What can " +
        "this bot do?'. Use /empty to remove the current picture.";

    private static readonly BotText DomainPrompt = new BotText("Link your website with your bot to use the ")
        .Bold("Telegram Login Widget")
        .Append($". Widget documentation:\n{LoginWidgetUrl}\n\nUse /empty to remove.");

    private static readonly BotText HelpText = BuildHelpText();

    private const string GenerateKeyInstructions =
        "openssl genrsa 2048 > private.key\n\n" +
        "Keep your PRIVATE key secret!\n\n" +
        "Then print your public key:\n\n" +
        "openssl rsa -in private.key -pubout\n\n" +
        "Send me this public key.";

    private const string PublicKeyPromptText =
        "This will enable your bot requesting information from Telegram Passport. Use /empty to disable.\n\n" +
        "To enable, please generate RSA private key (at least 2048 bits long) using openssl:\n\n" +
        "openssl genrsa 2048 > private.key\n\n" +
        "Keep your private key secret!\n\n" +
        "Then print your public key:\n\n" +
        "openssl rsa -in private.key -pubout\n\n" +
        "Send me this public key.";

    private const string PublicKeyInvalidText =
        "Sorry the public key you provided is invalid. We need RSA public key at least 2048 bits long.\n\n" +
        "Please generate RSA private key using:\n\n" +
        GenerateKeyInstructions;

    private const string PublicKeyIsPrivateText =
        "It seems that you sent me your private key. You should keep your PRIVATE key secret!\n\n" +
        "Your private key was compromised so please generate the new one using:\n\n" +
        GenerateKeyInstructions;

    private const string NamePrompt = "OK. Send me the new name for your bot.";

    private const string AboutPrompt =
        "OK. Send me the new 'About' text. People will see this text on the bot's profile page and it will be " +
        "sent together with a link to your bot when they share it with someone.";

    private const string DescriptionPrompt =
        "OK. Send me the new description for the bot. People will see this description when they open a chat " +
        "with your bot, in a block titled 'What can this bot do?'.";

    private const string CommandsPrompt =
        "OK. Send me a list of commands for your bot. Please use this format:\n\n" +
        "command1 - Description\ncommand2 - Another description\n\nSend /empty to keep the list empty.";

    private const string MenuButtonUrlPrompt =
        "Send me the URL that will be opened by tapping the menu button. \n\n" +
        "Use /empty to use the default behavior for the menu button.";

    private static readonly BotText PrivacyPolicyPrompt = new BotText()
        .Append("Send me a public URL to the new Privacy Policy for the bot or use /empty to remove the current one.\n\n" +
                "If you don't specify a Privacy Policy, the ")
        .Link("Standard Privacy Policy for Bots and Mini Apps", "https://telegram.org/privacy-tpa")
        .Append(" will apply.");

    // --- State keys ---
    private static string StateKey(long userId) => $"botfather:state:{userId}";

    /// <summary>Values that may contain ':' (URLs) and so cannot travel inside the state string.</summary>
    private static string StateValueKey(long userId) => $"botfather:state-value:{userId}";

    private async Task SetStateAsync(long userId, string state, int ttl = 300)
        => await redis.GetDatabase().StringSetAsync(StateKey(userId), state, TimeSpan.FromSeconds(ttl));

    private async Task<string?> GetStateAsync(long userId)
        => await redis.GetDatabase().StringGetAsync(StateKey(userId));

    private async Task ClearStateAsync(long userId)
        => await redis.GetDatabase().KeyDeleteAsync([StateKey(userId), StateValueKey(userId)]);

    // --- Entry points ---

    public async Task HandleMessageAsync(IRequestInput input, long fromUserId, string message)
    {
        try
        {
            await HandleMessageCoreAsync(input, fromUserId, message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BotFather failed to answer {UserId}", fromUserId);
        }
    }

    public async Task HandleCallbackAsync(IRequestInput input, long fromUserId, int msgId, string data)
    {
        try
        {
            await HandleCallbackCoreAsync(input, fromUserId, msgId, data);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BotFather failed to answer callback {Data} of {UserId}", data, fromUserId);
        }
    }

    private async Task HandleMessageCoreAsync(IRequestInput input, long fromUserId, string message)
    {
        var text = message.Trim();
        var cmd = text.Split(' ', '\n')[0].ToLowerInvariant();

        // "/start@BotFather" is how commands look when they are picked from the menu in some clients.
        var at = cmd.IndexOf('@');
        if (cmd.StartsWith('/') && at > 0)
        {
            cmd = cmd[..at];
        }

        if (cmd == "/cancel")
        {
            await CancelAsync(input, fromUserId);
            return;
        }

        // /empty and /skip belong to the flow that offered them rather than to the top level menu.
        var state = await GetStateAsync(fromUserId);
        if (state != null && (!cmd.StartsWith('/') || cmd is "/empty" or "/skip"))
        {
            await HandleStateInputAsync(input, fromUserId, state, text);
            return;
        }

        await ClearStateAsync(fromUserId);
        switch (cmd)
        {
            case "/start":
            case "/help":
                await SendAsync(input, fromUserId, HelpText);
                break;
            case "/newbot":
                if (await IsAtBotLimitAsync(fromUserId) is { } botLimit)
                {
                    await SendAsync(input, fromUserId, BotLimitText(botLimit));
                    break;
                }

                await SetStateAsync(fromUserId, "newbot:name");
                await SendAsync(input, fromUserId,
                    "Alright, a new bot. How are we going to call it? Please choose a name for your bot.");
                break;
            case "/mybots":
                await SendMyBotsAsync(input, fromUserId);
                break;
            case "/token":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to generate a new token.", "token");
                break;
            case "/revoke":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to revoke its access token.", "revoke");
                break;
            case "/deletebot":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to delete.", "deletebot");
                break;
            case "/setname":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change name.", "setname");
                break;
            case "/setdescription":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change description.", "setdescription");
                break;
            case "/setabouttext":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change the about section.", "setabouttext");
                break;
            case "/setcommands":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change the list of commands.", "setcommands");
                break;
            case "/setinline":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change inline queries status.", "setinline");
                break;
            case "/setjoingroups":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change group membership settings.", "setjoingroups");
                break;
            case "/setuserpic":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change profile photo.", "setuserpic");
                break;
            case "/setdescriptionpic":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change description picture.", "setdescriptionpic");
                break;
            case "/setprivacy":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change group messages settings.", "setprivacy");
                break;
            case "/setdomain":
                await SendBotPickerAsync(input, fromUserId,
                    "Choose a bot to set or change its linked domain. See Login Widget documentation for details:\n" +
                    LoginWidgetUrl, "setdomain");
                break;
            case "/setprivacypolicy":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to set a privacy policy URL.", "setprivacypolicy");
                break;
            case "/setmenubutton":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change its menu button.", "setmenubutton");
                break;
            case "/setpublickey":
                await SendBotPickerAsync(input, fromUserId, "Choose a bot to change public key.", "setpublickey");
                break;
            case "/newapp":
                await SendBotPickerAsync(input, fromUserId,
                    "Alright, a new web app. Which bot will be offering the web app?", "newapp");
                break;
            case "/myapps":
                await SendMyAppsAsync(input, fromUserId);
                break;
            case "/listapps":
                await SendBotPickerAsync(input, fromUserId,
                    "Please choose a bot to get a list of its web apps.", "listapps");
                break;
            case "/editapp":
                await SetStateAsync(fromUserId, "editapp:link");
                await SendAsync(input, fromUserId,
                    "Editing web apps. Please send me a web app link (e.g., t.me/bot/app).");
                break;
            case "/deleteapp":
                await SetStateAsync(fromUserId, "deleteapp:link");
                await SendAsync(input, fromUserId,
                    "To delete a web app, please send me the web app link (e.g., t.me/bot/app)");
                break;
            default:
                // Real BotFather answers any other text with the command list.
                await SendAsync(input, fromUserId,
                    cmd.StartsWith('/') ? new BotText("Unrecognized command. Say what?") : HelpText);
                break;
        }
    }

    private async Task CancelAsync(IRequestInput input, long fromUserId)
    {
        var state = await GetStateAsync(fromUserId);
        await ClearStateAsync(fromUserId);
        if (state == null)
        {
            await SendAsync(input, fromUserId, "No active command to cancel. I wasn't doing anything anyway. Zzzzz...");
            return;
        }

        var flow = state.Split(':')[0];
        if (flow is "newapp" or "editapp")
        {
            await DeleteDraftAsync(fromUserId);
        }

        var command = flow == "mainapp" ? "setmainapp" : flow;
        await SendAsync(input, fromUserId,
            $"The command {command} has been cancelled. Anything else I can do for you?\n\n" +
            $"Send /help for a list of commands. To learn more about Telegram Bots, see {BotsManualUrl}");
    }

    private async Task HandleCallbackCoreAsync(IRequestInput input, long fromUserId, int msgId, string data)
    {
        var parts = data.Split(':');
        var action = parts[0];
        var username = parts.Length > 1 ? parts[1] : string.Empty;

        switch (action)
        {
            case "back_to_bots":
                await ClearStateAsync(fromUserId);
                await ShowMyBotsAsync(input, fromUserId, msgId, 0);
                return;
            case "bots_page":
                await ShowMyBotsAsync(input, fromUserId, msgId, int.TryParse(username, out var page) ? page : 0);
                return;
            case "edit_field":
                // edit_field:<field>:<username>
                if (parts.Length > 2)
                {
                    await StartFieldEditAsync(input, fromUserId, parts[1], parts[2]);
                }

                return;
        }

        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null)
        {
            // Deleted since the menu was sent, or not this user's bot at all.
            await EditMessageAsync(input, fromUserId, msgId, InvalidBotText, null);
            return;
        }

        switch (action)
        {
            case "bot_select":
                await ClearStateAsync(fromUserId);
                await EditMessageAsync(input, fromUserId, msgId, BotMenuText(bot), BotMenuMarkup(username));
                break;
            case "api_token":
                await EditMessageAsync(input, fromUserId, msgId,
                    new BotText($"Here is the token for bot {BotName(bot)} @{username}:\n\n").Code(BotToken(bot)),
                    InlineRows(
                        InlineRow(Btn("Revoke current token", $"revoke_token:{username}")),
                        InlineRow(Btn("« Back to Bot", $"bot_select:{username}"))));
                break;
            case "revoke_token":
                var newToken = await DoRevokeAsync(bot);
                await EditMessageAsync(input, fromUserId, msgId,
                    new BotText($"Token for the bot {BotName(bot)} @{username} has been revoked. New token is:\n\n")
                        .Code(newToken),
                    InlineRows(InlineRow(Btn("« Back to Bot", $"bot_select:{username}"))));
                break;
            case "edit_bot":
                await ClearStateAsync(fromUserId);
                await EditMessageAsync(input, fromUserId, msgId, await EditBotTextAsync(bot), EditBotMarkup(username));
                break;
            case "bot_settings":
                await ClearStateAsync(fromUserId);
                await EditMessageAsync(input, fromUserId, msgId, $"Settings for @{username}.", SettingsMarkup(username));
                break;
            case "delete_confirm":
                await EditMessageAsync(input, fromUserId, msgId,
                    new BotText("You are about to delete your bot ").Bold(BotName(bot))
                        .Append($" @{username}. Is that correct?"),
                    DeleteConfirmMarkup(username));
                break;
            case "delete_do":
                await DoDeleteBotAsync(bot);
                await EditMessageAsync(input, fromUserId, msgId, "Done! The bot is gone. /help", null);
                break;
            case "inline_mode":
                await SendInlineModeMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "inline_enable":
                await StartFieldEditAsync(input, fromUserId, "inline", username);
                break;
            case "inline_disable":
                await SetInlineAsync(bot, null);
                await SendInlineModeMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "business_mode":
                await SendBusinessModeMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "business_enable":
                await SetBusinessModeAsync(bot, true);
                await SendBusinessModeMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "business_disable":
                await SetBusinessModeAsync(bot, false);
                await SendBusinessModeMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "groups":
                await SendGroupsMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "groups_on":
                await SetNoChatsAsync(bot, false);
                await SendGroupsMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "groups_off":
                await SetNoChatsAsync(bot, true);
                await SendGroupsMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "menu_button":
                await SendMenuButtonMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "menu_configure":
                await StartFieldEditAsync(input, fromUserId, "menu", username);
                break;
            case "menu_reset":
                await SetMenuButtonAsync(bot, null, null);
                await SendMenuButtonMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "configure_mini_app":
                await SendMainAppMenuAsync(input, fromUserId, msgId, username);
                break;
            case "privacy":
                await SendPrivacyMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "paid_bcast":
                await SendPaidBroadcastMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "paid_bcast_on":
                // The requirements are checked again: the screen may be old.
                if (await GetPaidBroadcastProblemAsync(bot) == null)
                {
                    await botBroadcastLimiter.SetPaidBroadcastAsync(BotUserIdOf(bot), true);
                }

                await SendPaidBroadcastMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "paid_bcast_off":
                await botBroadcastLimiter.SetPaidBroadcastAsync(BotUserIdOf(bot), false);
                await SendPaidBroadcastMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "privacy_on":
                await SetPrivacyModeAsync(bot, true);
                await SendPrivacyMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "privacy_off":
                await SetPrivacyModeAsync(bot, false);
                await SendPrivacyMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "rights":
                // rights:<username>:<g|b>
                await SendAdminRightsMenuAsync(input, fromUserId, msgId, bot, parts.Length > 2 ? parts[2] : "g");
                break;
            case "rights_toggle":
                // rights_toggle:<username>:<g|b>:<choice index>
                if (parts.Length > 3 && int.TryParse(parts[3], out var choice))
                {
                    await ToggleAdminRightAsync(bot, parts[2], choice);
                }

                await SendAdminRightsMenuAsync(input, fromUserId, msgId, bot, parts.Length > 2 ? parts[2] : "g");
                break;
            case "domain":
                await SendDomainMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "domain_set":
                await StartFieldEditAsync(input, fromUserId, "domain", username);
                break;
            case "domain_remove":
                await SetDomainAsync(bot, null);
                await SendDomainMenuAsync(input, fromUserId, msgId, bot);
                break;
            case "transfer":
                await EditMessageAsync(input, fromUserId, msgId, "You can transfer bot ownership to another Telegram user.",
                    InlineRows(
                        InlineRow(Btn("Choose recipient", $"transfer_pick:{username}")),
                        InlineRow(Btn("« Back to Bot", $"bot_select:{username}"))));
                break;
            case "transfer_pick":
                await StartFieldEditAsync(input, fromUserId, "transfer", username);
                break;
            case "transfer_do":
                // transfer_do:<username>:<recipient id>; the password was checked by CheckCallbackPasswordAsync.
                if (parts.Length > 2 && long.TryParse(parts[2], out var recipientId))
                {
                    await DoTransferAsync(input, fromUserId, msgId, bot, recipientId);
                }

                break;
            case "app_select":
                await SendAppMenuAsync(input, fromUserId, msgId, username, parts.Length > 2 ? parts[2] : string.Empty);
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
                await HandleNewBotStateAsync(input, fromUserId, step, extra, text);
                return;
            case "newapp":
                await HandleNewAppStateAsync(input, fromUserId, step, extra, text, parts);
                return;
            case "editapp":
                await HandleEditAppStateAsync(input, fromUserId, step, extra, text, parts);
                return;
            case "deleteapp":
                await HandleDeleteAppStateAsync(input, fromUserId, step, extra, text);
                return;
            case "mainapp":
                if (step == "url")
                    await HandleMainAppUrlAsync(input, fromUserId, extra, text);
                return;
        }

        if (step == "pick")
        {
            var username = text.Trim().TrimStart('@').ToLowerInvariant();
            var picked = await GetBotAsync(fromUserId, username);
            if (picked == null)
            {
                await SendAsync(input, fromUserId, InvalidBotText);
                return;
            }

            await HandleBotPickedAsync(input, fromUserId, flow, picked);
            return;
        }

        var bot = await GetBotAsync(fromUserId, extra);
        if (bot == null)
        {
            await ClearStateAsync(fromUserId);
            await SendAsync(input, fromUserId, InvalidBotText);
            return;
        }

        switch (flow)
        {
            case "setname":
                await HandleNameInputAsync(input, fromUserId, bot, text);
                break;
            case "setabouttext":
                await HandleAboutInputAsync(input, fromUserId, bot, text);
                break;
            case "setdescription":
                await HandleDescriptionInputAsync(input, fromUserId, bot, text);
                break;
            case "setcommands":
                await HandleCommandsInputAsync(input, fromUserId, bot, text);
                break;
            case "setinline":
                await HandleInlineInputAsync(input, fromUserId, bot, text);
                break;
            case "setjoingroups":
                await HandleJoinGroupsInputAsync(input, fromUserId, bot, text);
                break;
            case "setprivacypolicy":
                await HandlePrivacyPolicyInputAsync(input, fromUserId, bot, text);
                break;
            case "setmenubutton" when step == "url":
                await HandleMenuButtonUrlInputAsync(input, fromUserId, bot, text);
                break;
            case "setmenubutton" when step == "title":
                await HandleMenuButtonTitleInputAsync(input, fromUserId, bot, text);
                break;
            case "setuserpic":
                // Only a photo upload moves on; HandleMediaAsync deals with that.
                await SendAsync(input, fromUserId, "Please send a photo.");
                break;
            case "setdescriptionpic":
                if (IsEmpty(text))
                {
                    await UpdateBotInfoAsync(BotUserIdOf(bot), Builders<BsonDocument>.Update.Unset("DescriptionMedia"));
                    await ClearStateAsync(fromUserId);
                    await SendAsync(input, fromUserId, "Success! Description picture removed. /help");
                    break;
                }

                await SendAsync(input, fromUserId, DescriptionPicPrompt);
                break;
            case "setprivacy":
                await HandlePrivacyInputAsync(input, fromUserId, bot, text);
                break;
            case "setdomain":
                await HandleDomainInputAsync(input, fromUserId, bot, text);
                break;
            case "settransferrecepient":
                await HandleTransferRecipientAsync(input, fromUserId, bot, text, null);
                break;
            case "deletebot":
                await HandleDeleteInputAsync(input, fromUserId, bot, text);
                break;
            case "setpublickey":
                await HandlePublicKeyInputAsync(input, fromUserId, bot, text);
                break;
        }
    }

    /// <summary>The bot was picked from the reply keyboard of a typed command.</summary>
    private async Task HandleBotPickedAsync(IRequestInput input, long fromUserId, string flow, BsonDocument bot)
    {
        var username = BotUsername(bot);
        switch (flow)
        {
            case "token":
                await ClearStateAsync(fromUserId);
                await SendAsync(input, fromUserId, TokenText("You can use this token to access HTTP API:\n", BotToken(bot)));
                break;
            case "revoke":
                await ClearStateAsync(fromUserId);
                await SendAsync(input, fromUserId, TokenText(
                    "Your token was replaced with a new one. You can use this token to access HTTP API:\n",
                    await DoRevokeAsync(bot)));
                break;
            case "deletebot":
                await SetStateAsync(fromUserId, $"deletebot:confirm:{username}");
                await SendAsync(input, fromUserId,
                    $"OK, you selected @{username}. Are you sure?\n\n" +
                    $"Send '{DeleteConfirmation}' to confirm you really want to delete this bot.");
                break;
            case "setname":
            case "setabouttext":
            case "setdescription":
            case "setcommands":
            case "setinline":
            case "setprivacypolicy":
            case "setmenubutton":
                await StartFieldEditAsync(input, fromUserId, flow switch
                {
                    "setname" => "name",
                    "setabouttext" => "about",
                    "setdescription" => "desc",
                    "setcommands" => "comm",
                    "setinline" => "inline",
                    "setprivacypolicy" => "privpol",
                    _ => "menu"
                }, username);
                break;
            case "setuserpic":
            case "setdescriptionpic":
            case "setdomain":
                await StartFieldEditAsync(input, fromUserId, flow switch
                {
                    "setuserpic" => "pic",
                    "setdescriptionpic" => "descpic",
                    _ => "domain"
                }, username);
                break;
            case "setprivacy":
                await SetStateAsync(fromUserId, $"setprivacy:input:{username}");
                await SendAsync(input, fromUserId,
                    "'Enable' - your bot will only receive messages that either start with the '/' symbol or mention " +
                    "the bot by username.\n'Disable' - your bot will receive all messages that people send to groups.\n" +
                    $"Current status is: {(await IsPrivacyModeAsync(bot) ? "ENABLED" : "DISABLED")}",
                    ReplyKeyboard([["Enable", "Disable"]]));
                break;
            case "setjoingroups":
                await SetStateAsync(fromUserId, $"setjoingroups:input:{username}");
                var status = await IsNoChatsAsync(bot) ? "DISABLED" : "ENABLED";
                await SendAsync(input, fromUserId,
                    "'Enable' - bot can be added to groups.\n" +
                    "'Disable' - block group invitations, the bot can't be added to groups.\n" +
                    $"Current status is: {status}",
                    ReplyKeyboard([["Enable", "Disable"]]));
                break;
            case "setpublickey":
                await SetStateAsync(fromUserId, $"setpublickey:input:{username}");
                await SendAsync(input, fromUserId, PublicKeyPromptText);
                break;
            case "listapps":
                await SendAppListTextAsync(input, fromUserId, username, bot);
                break;
            case "newapp":
                await HandleNewAppStateAsync(input, fromUserId, "pick", string.Empty, username, []);
                break;
        }
    }

    /// <summary>
    /// Starts asking for a new value of one of the bot's fields. Reached from both a typed command and
    /// an "Edit ..." button; the prompt always comes as a new message, as real BotFather sends it.
    /// </summary>
    private async Task StartFieldEditAsync(IRequestInput input, long fromUserId, string field, string username)
    {
        var bot = await GetBotAsync(fromUserId, username);
        if (bot == null)
        {
            await SendAsync(input, fromUserId, InvalidBotText);
            return;
        }

        switch (field)
        {
            case "name":
                await SetStateAsync(fromUserId, $"setname:input:{username}");
                await SendAsync(input, fromUserId, NamePrompt);
                break;
            case "about":
                await SetStateAsync(fromUserId, $"setabouttext:input:{username}");
                await SendAsync(input, fromUserId, AboutPrompt);
                break;
            case "desc":
                await SetStateAsync(fromUserId, $"setdescription:input:{username}");
                await SendAsync(input, fromUserId, DescriptionPrompt);
                break;
            case "comm":
                await SetStateAsync(fromUserId, $"setcommands:input:{username}");
                await SendAsync(input, fromUserId, CommandsPrompt);
                break;
            case "privpol":
                await SetStateAsync(fromUserId, $"setprivacypolicy:input:{username}");
                await SendAsync(input, fromUserId, PrivacyPolicyPrompt);
                break;
            case "inline":
                await SetStateAsync(fromUserId, $"setinline:input:{username}");
                await SendAsync(input, fromUserId,
                    "This will enable inline queries for your bot. https://core.telegram.org/bots/inline. " +
                    "Use /empty to disable.\n\n" +
                    "To enable inline mode, please send me the placeholder message for queries to your bot. " +
                    $"People will see this placeholder whenever they type @{username} in the message field – in " +
                    "any chat. Tell them what they can get from your bot (e.g., Search GIFs...).");
                break;
            case "menu":
                await SetStateAsync(fromUserId, $"setmenubutton:url:{username}");
                await SendAsync(input, fromUserId, MenuButtonUrlPrompt);
                break;
            case "pic":
                await SetStateAsync(fromUserId, $"setuserpic:input:{username}");
                await SendAsync(input, fromUserId, UserpicPrompt);
                break;
            case "descpic":
                await SetStateAsync(fromUserId, $"setdescriptionpic:input:{username}");
                await SendAsync(input, fromUserId, DescriptionPicPrompt);
                break;
            case "domain":
                await SetStateAsync(fromUserId, $"setdomain:input:{username}");
                await SendAsync(input, fromUserId, DomainPrompt);
                break;
            case "transfer":
                await SetStateAsync(fromUserId, $"settransferrecepient:input:{username}");
                await SendAsync(input, fromUserId, "Please share the new owner's contact or their username.");
                break;
        }
    }

    // --- /newbot ---

    private async Task HandleNewBotStateAsync(IRequestInput input, long fromUserId, string step, string extra, string text)
    {
        if (step == "name")
        {
            if (text.Length is 0 or > MaxNameLength)
            {
                await SendAsync(input, fromUserId, "Sorry, this name is invalid. Please try something different.");
                return;
            }

            // The name may contain ':' itself, so it waits in its own key rather than in the state string.
            await redis.GetDatabase().StringSetAsync(StateValueKey(fromUserId), text, TimeSpan.FromSeconds(300));
            await SetStateAsync(fromUserId, "newbot:username");
            await SendAsync(input, fromUserId,
                "Good. Now let's choose a username for your bot. It must end in `bot`. Like this, for example: " +
                "TetrisBot or tetris_bot.");
            return;
        }

        var name = (string?)await redis.GetDatabase().StringGetAsync(StateValueKey(fromUserId));
        if (string.IsNullOrEmpty(name))
        {
            // The pre-key format kept the name in the state itself.
            name = extra;
        }

        var username = text.Trim().TrimStart('@');
        if (!BotUserNameRegex().IsMatch(username))
        {
            await SendAsync(input, fromUserId, "Sorry, this username is invalid.");
            return;
        }

        if (!username.EndsWith("bot", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync(input, fromUserId,
                "Sorry, the username must end in 'bot'. E.g. 'Tetris_bot' or 'Tetrisbot'");
            return;
        }

        username = username.ToLowerInvariant();
        // Usernames made through the aggregate have a derived _id, so the name is looked up the way
        // contacts.resolveUsername looks it up.
        if (await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(username)) != null)
        {
            await SendAsync(input, fromUserId,
                "Sorry, this username is already taken. Please try something different.");
            return;
        }

        // Checked again at creation: bots may have been made in another session meanwhile.
        if (await IsAtBotLimitAsync(fromUserId) is { } limitAtCreation)
        {
            await ClearStateAsync(fromUserId);
            await SendAsync(input, fromUserId, BotLimitText(limitAtCreation));
            return;
        }

        await ClearStateAsync(fromUserId);
        var token = await DoCreateBotAsync(fromUserId, name, username);
        await SendAsync(input, fromUserId,
            new BotText($"Done! Congratulations on your new bot. You will find it at t.me/{username}. You can now add a " +
                        "description, about section and profile picture for your bot, see /help for a list of commands.\n\n" +
                        "Use this token to access the HTTP API:\n")
                .Code(token)
                .Append("\nKeep your token secure and store it safely, it can be used by anyone to control your bot.\n\n" +
                        $"For a description of the Bot API, see this page: {BotApiUrl}"));
    }

    // --- Field inputs ---

    private async Task HandleNameInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        if (text.Length is 0 or > MaxNameLength)
        {
            await SendAsync(input, fromUserId, "Sorry, this name is invalid. Please try something different.");
            return;
        }

        var botUserId = BotUserIdOf(bot);
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection).UpdateOneAsync(
            new BsonDocument("BotUserId", botUserId), new BsonDocument("$set", new BsonDocument("Name", text)));
        await UpdateBotUserAsync(botUserId, Builders<BsonDocument>.Update.Set("FirstName", text));
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Success! Name updated. /help");
    }

    private async Task HandleAboutInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        var about = IsEmpty(text) ? string.Empty : text;
        if (about.Length > MaxAboutLength)
        {
            await SendAsync(input, fromUserId,
                $"Sorry, the about info you provided is invalid. It must not exceed {MaxAboutLength} characters.");
            return;
        }

        // userFull.about comes from the user read model.
        await UpdateBotUserAsync(BotUserIdOf(bot), Builders<BsonDocument>.Update.Set("About", about));
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Success! About section updated. /help");
    }

    private async Task HandleDescriptionInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        var description = IsEmpty(text) ? null : text;
        if (description?.Length > MaxDescriptionLength)
        {
            await SendAsync(input, fromUserId,
                $"Sorry, the description you provided is invalid. It must not exceed {MaxDescriptionLength} characters.");
            return;
        }

        // botInfo.description is what getFullUser serves, from the document bots.setBotInfo writes.
        await UpdateBotInfoAsync(BotUserIdOf(bot), description == null
            ? Builders<BsonDocument>.Update.Unset("Description")
            : Builders<BsonDocument>.Update.Set("Description", description));
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Success! Description updated. /help");
    }

    private async Task HandleCommandsInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        var commands = new BsonArray();
        if (!IsEmpty(text))
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = line.IndexOf(" - ", StringComparison.Ordinal);
                var command = separator > 0 ? line[..separator].Trim().TrimStart('/').ToLowerInvariant() : string.Empty;
                var description = separator > 0 ? line[(separator + 3)..].Trim() : string.Empty;
                if (!BotCommandNameRegex().IsMatch(command) || description.Length is 0 or > 256 ||
                    commands.Count >= MaxCommandCount)
                {
                    await SendAsync(input, fromUserId,
                        "Sorry, the list of commands is invalid. Please use this format:\n\n" +
                        "command1 - Description\ncommand2 - Another description");
                    return;
                }

                commands.Add(new BsonDocument { ["Command"] = command, ["Description"] = description });
            }
        }

        var botUserId = BotUserIdOf(bot);

        // The default-scope document bots.setBotCommands writes and getFullUser reads.
        await mongoDatabase.GetCollection<BsonDocument>("bot_commands").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
            Builders<BsonDocument>.Filter.Eq("ScopeType", nameof(TBotCommandScopeDefault)) &
            Builders<BsonDocument>.Filter.Eq("PeerId", BsonNull.Value) &
            Builders<BsonDocument>.Filter.Eq("LangCode", ""),
            Builders<BsonDocument>.Update
                .Set("BotId", botUserId)
                .Set("ScopeType", nameof(TBotCommandScopeDefault))
                .Set("PeerId", BsonNull.Value)
                .Set("LangCode", "")
                .Set("Commands", commands)
                .Set("UpdatedAt", DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true });
        await BumpBotInfoVersionAsync(botUserId);

        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Success! Command list updated. /help");
    }

    private async Task HandleInlineInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        if (!IsEmpty(text) && text.Length > MaxInlinePlaceholderLength)
        {
            await SendAsync(input, fromUserId,
                $"Sorry, the placeholder is too long. It must not exceed {MaxInlinePlaceholderLength} characters.");
            return;
        }

        await SetInlineAsync(bot, IsEmpty(text) ? null : text);
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Success! Inline settings updated. /help");
    }

    private async Task HandleJoinGroupsInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        bool noChats;
        if (text.Equals("Enable", StringComparison.OrdinalIgnoreCase))
        {
            noChats = false;
        }
        else if (text.Equals("Disable", StringComparison.OrdinalIgnoreCase))
        {
            noChats = true;
        }
        else
        {
            await SendAsync(input, fromUserId, "Please choose 'Enable' or 'Disable'.",
                ReplyKeyboard([["Enable", "Disable"]]));
            return;
        }

        await SetNoChatsAsync(bot, noChats);
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, $"Success! The new status is: {(noChats ? "DISABLED" : "ENABLED")}. /help");
    }

    private async Task HandlePrivacyPolicyInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        string? url = null;
        if (!IsEmpty(text))
        {
            if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                await SendAsync(input, fromUserId, "Please send me a valid URL or use /empty to remove the current one.");
                return;
            }

            url = uri.ToString();
        }

        await UpdateBotInfoAsync(BotUserIdOf(bot), url == null
            ? Builders<BsonDocument>.Update.Unset("PrivacyPolicyUrl")
            : Builders<BsonDocument>.Update.Set("PrivacyPolicyUrl", url));
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Success! Privacy Policy updated. /help");
    }

    private async Task HandleMenuButtonUrlInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        if (IsEmpty(text))
        {
            await SetMenuButtonAsync(bot, null, null);
            await ClearStateAsync(fromUserId);
            await SendAsync(input, fromUserId, "Success! Menu button updated. /help");
            return;
        }

        // The same rule bots.setBotMenuButton applies: clients open it directly.
        if (!IsHttpsUrl(text) || text.Trim().Length > 1024)
        {
            await SendAsync(input, fromUserId, "Please send me a valid URL. https is required.");
            return;
        }

        await redis.GetDatabase().StringSetAsync(StateValueKey(fromUserId), text.Trim(), TimeSpan.FromSeconds(300));
        await SetStateAsync(fromUserId, $"setmenubutton:title:{BotUsername(bot)}");
        await SendAsync(input, fromUserId, "OK. Now send me the title for the menu button.");
    }

    private async Task HandleMenuButtonTitleInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        var url = (string?)await redis.GetDatabase().StringGetAsync(StateValueKey(fromUserId));
        if (string.IsNullOrEmpty(url))
        {
            await SetStateAsync(fromUserId, $"setmenubutton:url:{BotUsername(bot)}");
            await SendAsync(input, fromUserId, MenuButtonUrlPrompt);
            return;
        }

        if (text.Length is 0 or > MaxMenuButtonTextLength)
        {
            await SendAsync(input, fromUserId,
                $"Sorry, the title is invalid. It must be 1-{MaxMenuButtonTextLength} characters long.");
            return;
        }

        await SetMenuButtonAsync(bot, text, url);
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Success! Menu button updated. /help");
    }

    private async Task HandleDeleteInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        if (!text.Equals(DeleteConfirmation, StringComparison.Ordinal))
        {
            await SendAsync(input, fromUserId,
                $"Please enter the confirmation text exactly like this:\n{DeleteConfirmation}\n\nType /cancel to cancel the operation.");
            return;
        }

        await DoDeleteBotAsync(bot);
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Done! The bot is gone. /help");
    }

    /// <summary>
    /// Stores the bot's Telegram Passport public key. The key is what
    /// <c>account.getAuthorizationForm</c> / <c>account.acceptAuthorization</c> check the caller's
    /// <c>public_key</c> against, and it is also the key the user's client encrypts the credentials to.
    /// See https://corefork.telegram.org/api/passport
    /// </summary>
    private async Task HandlePublicKeyInputAsync(IRequestInput input, long fromUserId, BsonDocument bot, string text)
    {
        var botUserId = BotUserIdOf(bot);

        if (IsEmpty(text))
        {
            await passportBotResolver.SetPublicKeyAsync(botUserId, null);
            await ClearStateAsync(fromUserId);
            await SendAsync(input, fromUserId, "Success! Public key updated. /help");
            return;
        }

        switch (PassportPublicKey.TryNormalize(text, out var normalized))
        {
            case PassportPublicKeyStatus.PrivateKey:
                // The state is kept: the owner still has to send a public key, and they now have to
                // rotate the compromised pair first.
                await SendAsync(input, fromUserId, PublicKeyIsPrivateText);
                return;

            case PassportPublicKeyStatus.Invalid:
                await SendAsync(input, fromUserId, PublicKeyInvalidText);
                return;
        }

        await passportBotResolver.SetPublicKeyAsync(botUserId, normalized);
        await ClearStateAsync(fromUserId);
        await SendAsync(input, fromUserId, "Success! Public key updated. /help");
    }

    // --- Screens ---

    private async Task SendMyBotsAsync(IRequestInput input, long fromUserId)
    {
        var bots = await GetUserBotsAsync(fromUserId);
        if (bots.Count == 0)
        {
            await SendAsync(input, fromUserId, "You have no bots yet. Use /newbot to create one.");
            return;
        }

        await SendAsync(input, fromUserId, "Choose a bot from the list below:", MyBotsMarkup(bots, 0));
    }

    private async Task ShowMyBotsAsync(IRequestInput input, long fromUserId, int msgId, int offset)
    {
        var bots = await GetUserBotsAsync(fromUserId);
        if (bots.Count == 0)
        {
            await EditMessageAsync(input, fromUserId, msgId, "You have no bots yet. Use /newbot to create one.", null);
            return;
        }

        await EditMessageAsync(input, fromUserId, msgId, "Choose a bot from the list below:", MyBotsMarkup(bots, offset));
    }

    /// <summary>Two bots per row, six per page, with « and » between pages — the way real BotFather pages.</summary>
    private static TReplyInlineMarkup MyBotsMarkup(List<BsonDocument> bots, int offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, (bots.Count - 1) / BotsPerPage * BotsPerPage));
        var rows = bots.Skip(offset).Take(BotsPerPage)
            .Select(b => Btn($"@{BotUsername(b)}", $"bot_select:{BotUsername(b)}"))
            .Chunk(2)
            .Select(InlineRow)
            .ToList();

        var navigation = new List<TKeyboardButtonCallback>();
        if (offset > 0)
        {
            navigation.Add(Btn("«", $"bots_page:{offset - BotsPerPage}"));
        }

        if (offset + BotsPerPage < bots.Count)
        {
            navigation.Add(Btn("»", $"bots_page:{offset + BotsPerPage}"));
        }

        if (navigation.Count > 0)
        {
            rows.Add(InlineRow(navigation.ToArray()));
        }

        return InlineRows(rows.ToArray());
    }

    private static BotText BotMenuText(BsonDocument bot) =>
        new($"Here it is: {BotName(bot)} @{BotUsername(bot)}.\nWhat do you want to do with the bot?");

    private static TReplyInlineMarkup BotMenuMarkup(string username) => InlineRows(
        InlineRow(Btn("API Token", $"api_token:{username}"), Btn("Edit Bot", $"edit_bot:{username}")),
        InlineRow(Btn("Bot Settings", $"bot_settings:{username}")),
        InlineRow(Btn("Transfer Ownership", $"transfer:{username}"), Btn("Delete Bot", $"delete_confirm:{username}")),
        InlineRow(Btn("« Back to Bot List", "back_to_bots")));

    private async Task<BotText> EditBotTextAsync(BsonDocument bot)
    {
        var botUserId = BotUserIdOf(bot);
        var user = await GetBotUserAsync(botUserId);
        var info = await GetBotInfoAsync(botUserId);
        var commands = await GetBotCommandCountAsync(botUserId);

        var about = GetString(user, "About");
        var description = GetString(info, "Description");
        var privacyPolicy = GetString(info, "PrivacyPolicyUrl");
        var hasPhoto = user != null && user.TryGetValue("ProfilePhotoId", out var photoId) && !photoId.IsBsonNull;
        var hasDescriptionPicture = info != null && info.Contains("DescriptionMedia");

        return new BotText($"Edit @{BotUsername(bot)} info.\n\n")
            .Bold("Name").Append($": {BotName(bot)}\n")
            .Bold("About").Append($": {(about.Length > 0 ? about : "🚫")}\n")
            .Bold("Description").Append($": {(description.Length > 0 ? description : "🚫")}\n")
            .Bold("Description picture").Append(hasDescriptionPicture ? ": ✅ has a description picture\n" : ": 🚫 no description picture\n")
            .Bold("Botpic").Append(hasPhoto ? ": ✅ has a botpic\n" : ": 🚫 no botpic\n")
            .Bold("Commands").Append(commands > 0 ? $": {commands} commands\n" : ": no commands yet\n")
            .Bold("Privacy Policy").Append($": {(privacyPolicy.Length > 0 ? privacyPolicy : "🚫")}");
    }

    private static TReplyInlineMarkup EditBotMarkup(string username) => InlineRows(
        InlineRow(Btn("Edit Name", $"edit_field:name:{username}"), Btn("Edit About", $"edit_field:about:{username}")),
        InlineRow(Btn("Edit Description", $"edit_field:desc:{username}"), Btn("Edit Description Picture", $"edit_field:descpic:{username}")),
        InlineRow(Btn("Edit Botpic", $"edit_field:pic:{username}"), Btn("Edit Commands", $"edit_field:comm:{username}")),
        InlineRow(Btn("Edit Privacy Policy", $"edit_field:privpol:{username}"), Btn("« Back to Bot", $"bot_select:{username}")));

    private static TReplyInlineMarkup SettingsMarkup(string username) => InlineRows(
        InlineRow(Btn("Inline Mode", $"inline_mode:{username}")),
        InlineRow(Btn("Secretary Mode", $"business_mode:{username}")),
        InlineRow(Btn("Allow Groups?", $"groups:{username}"), Btn("Group Privacy", $"privacy:{username}")),
        InlineRow(Btn("Group Admin Rights", $"rights:{username}:g"), Btn("Channel Admin Rights", $"rights:{username}:b")),
        InlineRow(Btn("Domain", $"domain:{username}")),
        InlineRow(Btn("Menu Button", $"menu_button:{username}")),
        InlineRow(Btn("Configure Mini App", $"configure_mini_app:{username}")),
        InlineRow(Btn("Paid Broadcast", $"paid_bcast:{username}")),
        InlineRow(Btn("« Back to Bot", $"bot_select:{username}")));

    /// <summary>The three answers are shuffled, as real BotFather does, so that deleting takes a look.</summary>
    private static TReplyInlineMarkup DeleteConfirmMarkup(string username)
    {
        var answers = new[]
        {
            Btn("Yes, delete the bot", $"delete_do:{username}"),
            Btn("No", $"bot_select:{username}"),
            Btn("Nope, nevermind", $"bot_select:{username}")
        };
        Random.Shared.Shuffle(answers);

        return InlineRows(answers.Select(p => InlineRow(p))
            .Append(InlineRow(Btn("« Back to Bot", $"bot_select:{username}")))
            .ToArray());
    }

    private static TReplyInlineMarkup BackToSettings(string username, params TKeyboardButtonCallback[] actions) =>
        InlineRows(actions.Select(p => InlineRow(p))
            .Append(InlineRow(Btn("« Back to Settings", $"bot_settings:{username}")))
            .ToArray());

    /// <summary>
    /// Inline mode is <c>InlineEnabled</c> on the bot's state document, which messages.getInlineBotResults
    /// checks, plus <c>user.bot_inline_placeholder</c>, without which clients never send an inline query.
    /// </summary>
    private async Task SendInlineModeMenuAsync(IRequestInput input, long fromUserId, int msgId, BsonDocument bot)
    {
        var username = BotUsername(bot);
        var enabled = await IsInlineEnabledAsync(bot);
        await EditMessageAsync(input, fromUserId, msgId,
            new BotText().Link("Inline mode", "https://core.telegram.org/bots/inline").Append(" is currently ")
                .Bold(enabled ? "enabled" : "disabled").Append($" for {BotName(bot)} @{username}."),
            BackToSettings(username, enabled
                ? Btn("Turn off", $"inline_disable:{username}")
                : Btn("Turn on", $"inline_enable:{username}")));
    }

    private async Task SendBusinessModeMenuAsync(IRequestInput input, long fromUserId, int msgId, BsonDocument bot)
    {
        var username = BotUsername(bot);
        var user = await GetBotUserAsync(BotUserIdOf(bot));
        var enabled = user != null && user.TryGetValue("BotBusiness", out var value) && value.IsBoolean && value.AsBoolean;
        await EditMessageAsync(input, fromUserId, msgId,
            new BotText().Link("Secretary mode", "https://core.telegram.org/bots/features#secretary-bots")
                .Append(" is currently ").Bold(enabled ? "enabled" : "disabled")
                .Append($" for {BotName(bot)} @{username}."),
            BackToSettings(username, enabled
                ? Btn("Turn off", $"business_disable:{username}")
                : Btn("Turn on", $"business_enable:{username}")));
    }

    private async Task SendGroupsMenuAsync(IRequestInput input, long fromUserId, int msgId, BsonDocument bot)
    {
        var username = BotUsername(bot);
        var name = BotName(bot);
        var noChats = await IsNoChatsAsync(bot);
        await EditMessageAsync(input, fromUserId, msgId,
            new BotText("Groups are currently ").Bold(noChats ? "disabled" : "enabled")
                .Append($" for bot {name} @{username}\n\n")
                .Append(noChats
                    ? $"Enabling groups will allow users to add {name} to groups."
                    : $"Disabling groups will forbid users to add {name} to groups."),
            BackToSettings(username, noChats
                ? Btn("Turn groups on", $"groups_on:{username}")
                : Btn("Turn groups off", $"groups_off:{username}")));
    }

    private async Task SendMenuButtonMenuAsync(IRequestInput input, long fromUserId, int msgId, BsonDocument bot)
    {
        var username = BotUsername(bot);
        var button = await GetMenuButtonAsync(BotUserIdOf(bot));
        var isWebApp = GetString(button, "Type") == nameof(TBotMenuButton);

        var text = isWebApp
            ? $"Customized menu button is currently enabled for {BotName(bot)} @{username}.\n\n" +
              $"Title: {GetString(button, "Text")}\nURL: {GetString(button, "Url")}"
            : $"Customized menu button is currently disabled for {BotName(bot)} @{username}.";
        var actions = isWebApp
            ? new[] { Btn("Configure menu button", $"menu_configure:{username}"), Btn("Disable menu button", $"menu_reset:{username}") }
            : [Btn("Configure menu button", $"menu_configure:{username}")];

        await EditMessageAsync(input, fromUserId, msgId, text, BackToSettings(username, actions));
    }

    private async Task SendBotPickerAsync(IRequestInput input, long fromUserId, string prompt, string action)
    {
        var bots = await GetUserBotsAsync(fromUserId);
        if (bots.Count == 0)
        {
            await SendAsync(input, fromUserId, "You have no bots yet. Use /newbot to create one.");
            return;
        }

        await SetStateAsync(fromUserId, $"{action}:pick");
        await SendAsync(input, fromUserId, prompt,
            ReplyKeyboard(bots.Select(b => $"@{BotUsername(b)}").Chunk(2).Select(p => p.ToArray()).ToArray()));
    }

    private static BotText TokenText(string intro, string token) =>
        new BotText(intro).Code(token).Append($"\n\nFor a description of the Bot API, see this page: {BotApiUrl}");

    private static string BotLimitText(int limit) =>
        $"Sorry, you can't add more than {limit} bots. To create a new bot, delete one of your bots.";

    // --- Bot settings writers ---
    //
    // BotFather bots have no aggregate behind them (DoCreateBotAsync makes their read model directly),
    // so these write the same documents the bot's creation did and drop the cached read model.

    private async Task UpdateBotUserAsync(long botUserId, UpdateDefinition<BsonDocument> update)
    {
        await mongoDatabase.GetCollection<BsonDocument>(UserCollection)
            .UpdateOneAsync(Builders<BsonDocument>.Filter.Eq("UserId", botUserId), update);
        userReadModelCacheHelper.RemoveById(botUserId);
        await eventBus.PublishAsync(new BotUserChangedEvent(botUserId));
    }

    /// <summary>A changed <c>user.bot_info_version</c> is what makes clients drop their cached bot info.</summary>
    private async Task BumpBotInfoVersionAsync(long botUserId)
    {
        var user = await GetBotUserAsync(botUserId);
        var version = user != null && user.TryGetValue("BotInfoVersion", out var value) && value.IsNumeric
            ? value.ToInt32()
            : 1;
        await UpdateBotUserAsync(botUserId, Builders<BsonDocument>.Update.Set("BotInfoVersion", version + 1));
    }

    private async Task UpdateBotInfoAsync(long botUserId, UpdateDefinition<BsonDocument> update)
    {
        await mongoDatabase.GetCollection<BsonDocument>("bot_info").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("BotId", botUserId) & Builders<BsonDocument>.Filter.Eq("LangCode", ""),
            Builders<BsonDocument>.Update.Combine(update,
                Builders<BsonDocument>.Update
                    .Set("BotId", botUserId)
                    .Set("LangCode", "")
                    .Set("UpdatedAt", DateTime.UtcNow)),
            new UpdateOptions { IsUpsert = true });
        await BumpBotInfoVersionAsync(botUserId);
    }

    private async Task SetInlineAsync(BsonDocument bot, string? placeholder)
    {
        var botUserId = BotUserIdOf(bot);
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection).UpdateOneAsync(
            new BsonDocument("BotUserId", botUserId),
            new BsonDocument("$set", new BsonDocument("InlineEnabled", placeholder != null)));
        await UpdateBotUserAsync(botUserId, placeholder == null
            ? Builders<BsonDocument>.Update.Unset("BotInlinePlaceholder")
            : Builders<BsonDocument>.Update.Set("BotInlinePlaceholder", placeholder));
    }

    private async Task SetNoChatsAsync(BsonDocument bot, bool noChats)
        => await UpdateBotUserAsync(BotUserIdOf(bot), Builders<BsonDocument>.Update.Set("BotNochats", noChats));

    private async Task SetBusinessModeAsync(BsonDocument bot, bool enabled)
    {
        var botUserId = BotUserIdOf(bot);
        await UpdateBotUserAsync(botUserId, enabled
            ? Builders<BsonDocument>.Update.Set("BotBusiness", true)
            : Builders<BsonDocument>.Update.Unset("BotBusiness"));

        if (enabled)
        {
            return;
        }

        // Disconnect every account that had this bot connected, and tell the bot.
        foreach (var connection in await businessConnectionStore.GetByBotAsync(botUserId))
        {
            await businessConnectionStore.DeleteAsync(connection.UserId);
            await businessConnectionStore.ClearPausedAsync(connection.UserId);
            await businessBotNotifier.NotifyConnectionAsync(connection, true);
        }
    }

    /// <summary>The all-users document bots.setBotMenuButton writes; no url means the default button.</summary>
    private async Task SetMenuButtonAsync(BsonDocument bot, string? text, string? url)
    {
        var botUserId = BotUserIdOf(bot);
        var button = url == null
            ? new BsonDocument { ["Type"] = nameof(TBotMenuButtonDefault) }
            : new BsonDocument { ["Type"] = nameof(TBotMenuButton), ["Text"] = text, ["Url"] = url };

        await mongoDatabase.GetCollection<BsonDocument>("bot_menu_buttons").UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("BotId", botUserId) & Builders<BsonDocument>.Filter.Eq("UserId", BsonNull.Value),
            Builders<BsonDocument>.Update
                .Set("BotId", botUserId)
                .Set("UserId", BsonNull.Value)
                .Set("Button", button)
                .Set("UpdatedAt", DateTime.UtcNow),
            new UpdateOptions { IsUpsert = true });
        await BumpBotInfoVersionAsync(botUserId);
    }

    // --- DB helpers ---

    /// <summary>Creates the bot and returns its token.</summary>
    private async Task<string> DoCreateBotAsync(long fromUserId, string name, string username)
    {
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
        var userCollection = mongoDatabase.GetCollection<BsonDocument>(UserCollection);
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
            // "By default, all bots added to groups run in Privacy Mode."
            { "BotPrivacyMode", true },
            { "BotBusiness", false },
            { "Color", new BsonDocument { { "Color", 0 }, { "BackgroundEmojiId", BsonNull.Value } } },
            { "CreationTime", now },
            { "Email", BsonNull.Value },
            { "EmojiStatusDocumentId", BsonNull.Value },
            { "EmojiStatusValidUntil", BsonNull.Value },
            { "EmojiStatusCollectibleId", BsonNull.Value },
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

        return token;
    }

    private async Task<string> DoRevokeAsync(BsonDocument bot)
    {
        var botUserId = BotUserIdOf(bot);
        var newToken = $"{botUserId}:{GenerateToken()}";
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection).UpdateOneAsync(
            new BsonDocument("BotUserId", botUserId),
            new BsonDocument("$set", new BsonDocument("Token", newToken)));
        return newToken;
    }

    /// <summary>Deletes the bot and releases its username, as real BotFather does.</summary>
    private async Task DoDeleteBotAsync(BsonDocument bot)
    {
        var botUserId = BotUserIdOf(bot);
        await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .DeleteOneAsync(new BsonDocument("BotUserId", botUserId));
        await mongoDatabase.GetCollection<BsonDocument>("bot-owners")
            .DeleteManyAsync(new BsonDocument("BotId", botUserId));
        await mongoDatabase.GetCollection<BsonDocument>("eventflow-usernamereadmodel")
            .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", $"username-{BotUsername(bot)}") &
                            Builders<BsonDocument>.Filter.Eq("PeerId", botUserId));
        await UpdateBotUserAsync(botUserId, Builders<BsonDocument>.Update.Set("IsDeleted", true));
    }

    private async Task<BsonDocument?> GetBotAsync(long fromUserId, string username)
    {
        if (string.IsNullOrEmpty(username))
        {
            return null;
        }

        return await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .Find(new BsonDocument { { "OwnerId", fromUserId }, { "Username", username.ToLowerInvariant() } })
            .FirstOrDefaultAsync();
    }

    private async Task<BsonDocument?> GetBotUserAsync(long botUserId)
        => await mongoDatabase.GetCollection<BsonDocument>(UserCollection)
            .Find(Builders<BsonDocument>.Filter.Eq("UserId", botUserId)).FirstOrDefaultAsync();

    private async Task<BsonDocument?> GetBotInfoAsync(long botUserId)
        => await mongoDatabase.GetCollection<BsonDocument>("bot_info")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) & Builders<BsonDocument>.Filter.Eq("LangCode", ""))
            .FirstOrDefaultAsync();

    private async Task<int> GetBotCommandCountAsync(long botUserId)
    {
        var doc = await mongoDatabase.GetCollection<BsonDocument>("bot_commands")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) &
                  Builders<BsonDocument>.Filter.Eq("ScopeType", nameof(TBotCommandScopeDefault)) &
                  Builders<BsonDocument>.Filter.Eq("PeerId", BsonNull.Value) &
                  Builders<BsonDocument>.Filter.Eq("LangCode", ""))
            .FirstOrDefaultAsync();

        return doc != null && doc.TryGetValue("Commands", out var commands) && commands.IsBsonArray
            ? commands.AsBsonArray.Count
            : 0;
    }

    private async Task<BsonDocument?> GetMenuButtonAsync(long botUserId)
    {
        var doc = await mongoDatabase.GetCollection<BsonDocument>("bot_menu_buttons")
            .Find(Builders<BsonDocument>.Filter.Eq("BotId", botUserId) & Builders<BsonDocument>.Filter.Eq("UserId", BsonNull.Value))
            .FirstOrDefaultAsync();

        return doc != null && doc.TryGetValue("Button", out var button) && button.IsBsonDocument
            ? button.AsBsonDocument
            : null;
    }

    private async Task<bool> IsNoChatsAsync(BsonDocument bot)
    {
        var user = await GetBotUserAsync(BotUserIdOf(bot));
        return user != null && user.TryGetValue("BotNochats", out var value) && value.IsBoolean && value.AsBoolean;
    }

    private static Task<bool> IsInlineEnabledAsync(BsonDocument bot)
        => Task.FromResult(bot.TryGetValue("InlineEnabled", out var value) && value.IsBoolean && value.AsBoolean);

    /// <summary>
    /// The account's bots_create_limit_default / _premium when it already owns that many bots, else null.
    /// See https://corefork.telegram.org/api/premium#doubled-limits
    /// </summary>
    private async Task<int?> IsAtBotLimitAsync(long fromUserId)
    {
        var limit = await premiumLimitResolver.GetLimitAsync(fromUserId, PremiumLimit.BotsCreate);
        var bots = await GetUserBotsAsync(fromUserId);
        return bots.Count >= limit ? limit : null;
    }

    /// <summary>Newest first.</summary>
    private async Task<List<BsonDocument>> GetUserBotsAsync(long fromUserId)
        => await mongoDatabase.GetCollection<BsonDocument>(BotCollection)
            .Find(new BsonDocument("OwnerId", fromUserId))
            .Sort(Builders<BsonDocument>.Sort.Descending("BotUserId"))
            .ToListAsync();

    /// <summary>
    /// The id for the next bot a user creates: one past the highest bot id in use.
    ///
    /// <para>The block reserved for the server's own bots (<c>@gif</c> and anything added later) is
    /// excluded from both the search and the result. Without that exclusion the reserved ids would be
    /// handed to user-created bots — and, since the next id is derived from the highest one in use, a
    /// single system bot at the top would drag every later allocation into the reserved range.</para>
    /// </summary>
    private async Task<long> GetNextUserIdAsync()
    {
        var filter = new BsonDocument
        {
            { "Bot", true },
            { "UserId", new BsonDocument
                {
                    { "$gte", MyTelegramConsts.BotUserInitId },
                    { "$lt", MyTelegramConsts.SystemBotUserIdBase }
                }
            }
        };
        var last = await mongoDatabase.GetCollection<BsonDocument>(UserCollection)
            .Find(filter).Sort(new BsonDocument("UserId", -1)).Limit(1).FirstOrDefaultAsync();
        var next = Math.Max(last?["UserId"].AsInt64 + 1 ?? MyTelegramConsts.BotUserInitId + 1,
            MyTelegramConsts.BotUserInitId + 1);

        if (MyTelegramConsts.IsReservedSystemBotUserId(next))
        {
            next = MyTelegramConsts.SystemBotUserIdBase + MyTelegramConsts.SystemBotUserIdReservedCount;
        }

        return next;
    }

    private static string GenerateToken()
    {
        // A bot token is a bearer credential for the bot account, so it must be unguessable:
        // Random.Shared is xoshiro256**, whose state can be recovered from previously issued tokens.
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    private static long BotUserIdOf(BsonDocument bot) => bot["BotUserId"].ToInt64();

    private static string BotUsername(BsonDocument bot) => GetString(bot, "Username");

    private static string BotName(BsonDocument bot) => GetString(bot, "Name");

    private static string BotToken(BsonDocument bot) => GetString(bot, "Token");

    // --- Messaging helpers ---

    private static RequestInfo BotRequest(IRequestInput input) => RequestInfo.Empty with
    {
        UserId = BotUserId,
        Layer = input.Layer,
        Date = DateTime.UtcNow.ToTimestamp(),
        RequestId = Guid.NewGuid()
    };

    private Task SendAsync(IRequestInput input, long toUserId, string text) =>
        SendAsync(input, toUserId, new BotText(text));

    private Task SendAsync(IRequestInput input, long toUserId, string text, IReplyMarkup? replyMarkup) =>
        SendAsync(input, toUserId, new BotText(text), replyMarkup);

    /// <summary>
    /// Sends a new message. Without other markup it removes the reply keyboard a bot picker left behind,
    /// as every one of real BotFather's answers does.
    /// </summary>
    private async Task SendAsync(IRequestInput input, long toUserId, BotText text, IReplyMarkup? replyMarkup = null)
    {
        // Each send needs its own RequestId: one request is one send saga.
        var sendInput = new SendMessageInput(BotRequest(input), BotUserId, new Peer(PeerType.User, toUserId), text.Text,
            Random.Shared.NextInt64(), text.Entities(), null, false,
            replyMarkup: replyMarkup ?? new TReplyKeyboardHide());
        await messageAppService.SendMessageAsync([sendInput]);
    }

    private Task EditMessageAsync(IRequestInput input, long toUserId, int msgId, string text, IReplyMarkup? replyMarkup) =>
        EditMessageAsync(input, toUserId, msgId, new BotText(text), replyMarkup);

    /// <summary>
    /// Edits the menu message whose button was pressed. <paramref name="msgId"/> is the id in the user's
    /// box; BotFather's copy of a private message has its own id, which the user's copy records as
    /// SenderMessageId. Only when that copy cannot be found is the menu sent anew.
    /// </summary>
    private async Task EditMessageAsync(IRequestInput input, long toUserId, int msgId, BotText text,
        IReplyMarkup? replyMarkup)
    {
        var userCopy = await queryProcessor.ProcessAsync(new GetMessageByIdQuery(MessageId.Create(toUserId, msgId).Value));
        if (userCopy == null || userCopy.SenderPeerId != BotUserId || userCopy.SenderMessageId <= 0)
        {
            await SendAsync(input, toUserId, text, replyMarkup);
            return;
        }

        var botMessageId = userCopy.SenderMessageId;
        var message = text.Text;
        byte[]? encryptedData = null;
        byte[]? inboxEncryptedData = null;
        var encryption = serverOptions.CurrentValue.EncryptionConfig;
        if (encryption is { Enabled: true, MessageKeys.Count: > 0 })
        {
            // Stored the way EditMessageHandler stores an edited text: encrypted per owner, never in clear.
            var keyConfig = encryption.MessageKeys[0];
            var key = Encoding.UTF8.GetBytes(keyConfig.Key);
            encryptedData = dataEncryptionHelper.Encrypt(keyConfig.Id, key, BotUserId, text.Text);
            inboxEncryptedData = dataEncryptionHelper.Encrypt(keyConfig.Id, key, toUserId, text.Text);
            message = string.Empty;
        }

        // Commands are deduplicated per aggregate by ReqMsgId; the message was created with 0, so an edit
        // carrying 0 too would be dropped as a repeat of its creation.
        var request = BotRequest(input) with { ReqMsgId = Random.Shared.NextInt64(1, long.MaxValue) };
        await commandBus.PublishAsync(new EditOutboxMessageCommand(MessageId.Create(BotUserId, botMessageId), request,
            botMessageId, message, DateTime.UtcNow.ToTimestamp(), text.Entities(), null, replyMarkup, false,
            messageAppService.GetHashtags(text.Text), encryptedData, inboxEncryptedData));
    }

    // --- Markup builders ---

    private static TReplyInlineMarkup InlineRows(params TKeyboardButtonRow[] rows)
        => new() { Rows = new TVector<IKeyboardButtonRow>(rows.Cast<IKeyboardButtonRow>().ToList()) };

    private static TKeyboardButtonRow InlineRow(params TKeyboardButtonCallback[] btns)
        => new() { Buttons = new TVector<IKeyboardButton>(btns.Cast<IKeyboardButton>().ToList()) };

    private static TKeyboardButtonCallback Btn(string text, string data)
        => new() { Text = text, Data = data };

    /// <summary>A resized, reusable reply keyboard, like the bot pickers of real BotFather.</summary>
    private static TReplyKeyboardMarkup ReplyKeyboard(string[][] rows) => new()
    {
        Rows = new TVector<IKeyboardButtonRow>(rows.Select(r => (IKeyboardButtonRow)new TKeyboardButtonRow
        {
            Buttons = new TVector<IKeyboardButton>(r.Select(t => (IKeyboardButton)new TKeyboardButton { Text = t }).ToList())
        }).ToList()),
        Resize = true
    };

    // --- Text helpers ---

    private static BotText BuildHelpText()
    {
        var text = new BotText("I can help you create and manage Xiegram bots. If you're new to the Bot API, please see the ")
            .Link("manual", BotsManualUrl)
            .Append(".\n\nYou can control me by sending these commands:\n\n")
            .Append("/newbot - create a new bot\n/mybots - edit your bots\n\n")
            .Bold("Edit Bots").Append("\n")
            .Append("/setname - change a bot's name\n" +
                    "/setdescription - change bot description\n" +
                    "/setabouttext - change bot about info\n" +
                    "/setuserpic - change bot profile photo\n" +
                    "/setdescriptionpic - change the description picture\n" +
                    "/setcommands - change the list of commands\n" +
                    "/setprivacypolicy - change the privacy policy\n" +
                    "/deletebot - delete a bot\n\n")
            .Bold("Bot Settings").Append("\n")
            .Append("/token - get authorization token\n/revoke - revoke bot access token\n/setinline - toggle ")
            .Link("inline mode", "https://core.telegram.org/bots/inline")
            .Append("\n/setjoingroups - can your bot be added to groups?\n/setprivacy - toggle ")
            .Link("privacy mode", "https://core.telegram.org/bots/features#privacy-mode")
            .Append(" in groups\n/setdomain - link website's domain to the bot\n" +
                    "/setmenubutton - change the menu button\n" +
                    "/setpublickey - set the public key for Telegram Passport\n\n")
            .Bold("Web Apps").Append("\n")
            .Append("/myapps - edit your ").Link("web apps", "https://core.telegram.org/bots/webapps")
            .Append("\n/newapp - create a new ").Link("web app", "https://core.telegram.org/bots/webapps")
            .Append("\n/listapps - get a list of your web apps\n" +
                    "/editapp - edit a web app\n" +
                    "/deleteapp - delete an existing web app");

        return text;
    }

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9_]{4,31}$")]
    private static partial Regex BotUserNameRegex();

    [GeneratedRegex("^[a-z0-9_]{1,32}$")]
    private static partial Regex BotCommandNameRegex();
}
