using MongoDB.Bson;
using MyTelegram.Messenger.Services.Bots;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Returns installed attachment menu <a href="https://corefork.telegram.org/api/bots/attach">bot mini apps »</a>
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getAttachMenuBots"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetAttachMenuBotsHandler(
    IQueryProcessor queryProcessor,
    IAttachMenuBotStore attachMenuBotStore,
    IUserConverterService userConverterService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetAttachMenuBots, MyTelegram.Schema.IAttachMenuBots>
{
    protected override async Task<MyTelegram.Schema.IAttachMenuBots> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetAttachMenuBots obj)
    {
        var entries = await attachMenuBotStore.GetEnabledAsync(input.UserId);
        var botIds = entries.Select(e => GetInt64(e, "bot_id")).Where(id => id != 0).ToList();

        var hash = attachMenuBotStore.ComputeHash(botIds);
        if (obj.Hash != 0 && obj.Hash == hash)
        {
            return new TAttachMenuBotsNotModified();
        }

        var bots = new TVector<IAttachMenuBot>();
        var users = new TVector<IUser>();

        if (botIds.Count > 0)
        {
            // Single batched load rather than one query per bot.
            var botReadModels = await queryProcessor.ProcessAsync(new GetUsersByUserIdListQuery(botIds));
            var botsById = botReadModels.ToDictionary(b => b.UserId);

            foreach (var entry in entries)
            {
                var botId = GetInt64(entry, "bot_id");
                if (!botsById.TryGetValue(botId, out var botReadModel) || !botReadModel.Bot)
                {
                    continue;
                }

                bots.Add(attachMenuBotStore.ToAttachMenuBot(botId, botReadModel.UserName ?? string.Empty, entry));
                users.Add(userConverterService.ToUser(input, botReadModel, layer: input.Layer));
            }
        }

        return new TAttachMenuBots
        {
            Hash = hash,
            Bots = bots,
            Users = users
        };
    }

    private static long GetInt64(BsonDocument doc, string name)
    {
        if (!doc.TryGetValue(name, out var value))
        {
            return 0;
        }

        return value.BsonType switch
        {
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Double => (long)value.AsDouble,
            _ => 0
        };
    }
}
