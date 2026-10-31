namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Resolves the <c>bot</c> parameter of methods a user calls on behalf of a bot they own, such as
/// <c>bots.toggleUsername</c> and <c>bots.reorderUsernames</c>.
/// </summary>
public interface IOwnedBotResolver
{
    /// <summary>
    /// The bot's read model, read straight from the store so it reflects the latest change. Throws
    /// <c>BOT_INVALID</c> unless <paramref name="bot"/> is a bot the caller owns (or the calling bot
    /// itself), with a valid access hash.
    /// </summary>
    Task<IUserReadModel> GetOwnedBotAsync(IRequestInput input, IInputUser bot);
}

/// <inheritdoc />
public class OwnedBotResolver(
    IQueryProcessor queryProcessor,
    IAccessHashHelper2 accessHashHelper,
    IBotOwnershipChecker botOwnershipChecker) : IOwnedBotResolver, ITransientDependency
{
    public async Task<IUserReadModel> GetOwnedBotAsync(IRequestInput input, IInputUser bot)
    {
        long botUserId;
        switch (bot)
        {
            case TInputUserSelf:
                botUserId = input.UserId;
                break;
            case TInputUser inputUser:
                await accessHashHelper.CheckAccessHashAsync(input, inputUser);
                botUserId = inputUser.UserId;
                break;
            default:
                RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
                return null!;
        }

        var botReadModel = await queryProcessor.ProcessAsync(new GetUserByIdQuery(botUserId));
        if (botReadModel is not { Bot: true })
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
            return null!;
        }

        // Being a bot is not enough: otherwise anybody could rearrange or switch off the usernames of
        // any bot on the server.
        if (botUserId != input.UserId && !await botOwnershipChecker.IsOwnerAsync(botUserId, input.UserId))
        {
            RpcErrors.RpcErrors400.BotInvalid.ThrowRpcError();
        }

        return botReadModel;
    }
}
