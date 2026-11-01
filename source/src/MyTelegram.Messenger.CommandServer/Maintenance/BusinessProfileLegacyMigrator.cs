using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.CommandServer.Maintenance;

/// <summary>
/// One-time carry-over of Telegram Business profile settings written by the old handlers.
///
/// <para>Those handlers stored TL objects straight into <c>eventflow-userreadmodel</c> under
/// <c>BusinessWorkHours</c>, <c>BusinessLocation</c>, <c>BusinessIntro</c>, … with no event behind them, so the
/// next save of the read model would silently drop them. This republishes each one as the corresponding
/// <c>UserAggregate</c> command; once the event lands the read model is saved under the new field names and the
/// legacy field disappears with that save, which also makes the job idempotent. Nothing is written to
/// <c>eventflow-*</c> directly.</para>
/// </summary>
public sealed class BusinessProfileLegacyMigrator(
    IMongoDatabase mongoDatabase,
    ICommandBus commandBus,
    ILogger<BusinessProfileLegacyMigrator> logger) : BackgroundService
{
    private static readonly string[] LegacyFields =
        ["BusinessWorkHours", "BusinessLocation", "BusinessIntro", "BusinessGreetingMessage", "BusinessAwayMessage"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the bus and the read model stores finish starting.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        try
        {
            await MigrateAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Business profile legacy migration failed; it will be retried on the next start");
        }
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var users = mongoDatabase.GetCollection<BsonDocument>("eventflow-userreadmodel");
        var filter = Builders<BsonDocument>.Filter.Or(LegacyFields.Select(f =>
            Builders<BsonDocument>.Filter.Type(f, BsonType.Document)));
        var documents = await users.Find(filter)
            .Project(Builders<BsonDocument>.Projection.Include("UserId").Include(LegacyFields[0])
                .Include(LegacyFields[1]).Include(LegacyFields[2]).Include(LegacyFields[3]).Include(LegacyFields[4]))
            .ToListAsync(cancellationToken);
        var migrated = 0;
        foreach (var document in documents)
        {
            var userId = document["UserId"].ToInt64();
            var aggregateId = UserId.Create(userId);
            try
            {
                if (Get(document, "BusinessWorkHours") is { } workHours)
                {
                    await commandBus.PublishAsync(
                        new UpdateBusinessWorkHoursCommand(aggregateId, ReadWorkHours(workHours)), cancellationToken);
                }

                if (Get(document, "BusinessLocation") is { } location)
                {
                    await commandBus.PublishAsync(
                        new UpdateBusinessLocationCommand(aggregateId, ReadLocation(location)), cancellationToken);
                }

                if (Get(document, "BusinessIntro") is { } intro)
                {
                    await commandBus.PublishAsync(
                        new UpdateBusinessIntroCommand(aggregateId, ReadIntro(intro)), cancellationToken);
                }

                if (Get(document, "BusinessGreetingMessage") != null || Get(document, "BusinessAwayMessage") != null)
                {
                    // The old handlers dropped the chosen recipients, so these cannot be reconstructed faithfully.
                    logger.LogWarning(
                        "User {UserId} has a legacy greeting/away message; it has to be set again by the user",
                        userId);
                }

                migrated++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not migrate legacy business profile of user {UserId}", userId);
            }
        }

        if (documents.Count > 0)
        {
            logger.LogInformation("Migrated legacy business profile of {Migrated}/{Total} users", migrated,
                documents.Count);
        }
    }

    private static BsonDocument? Get(BsonDocument document, string field)
    {
        return document.TryGetValue(field, out var value) && value.IsBsonDocument ? value.AsBsonDocument : null;
    }

    internal static BusinessWorkHoursItem? ReadWorkHours(BsonDocument doc)
    {
        var timezoneId = doc.GetValue("TimezoneId", BsonNull.Value);
        if (!timezoneId.IsString)
        {
            return null;
        }

        var intervals = doc.GetValue("WeeklyOpen", BsonNull.Value) is BsonArray array
            ? array.OfType<BsonDocument>()
                .Select(p => new BusinessWeeklyOpenItem(p.GetValue("StartMinute", 0).ToInt32(),
                    p.GetValue("EndMinute", 0).ToInt32()))
                .ToList()
            : [];

        return intervals.Count == 0 ? null : new BusinessWorkHoursItem(timezoneId.AsString, intervals);
    }

    internal static BusinessLocationItem? ReadLocation(BsonDocument doc)
    {
        var address = doc.GetValue("Address", BsonNull.Value);
        if (!address.IsString || string.IsNullOrWhiteSpace(address.AsString))
        {
            return null;
        }

        var point = doc.GetValue("GeoPoint", BsonNull.Value) as BsonDocument;
        var radius = point?.GetValue("AccuracyRadius", BsonNull.Value);
        return new BusinessLocationItem(address.AsString,
            point?.GetValue("Lat", BsonNull.Value) is { IsNumeric: true } lat ? lat.ToDouble() : null,
            point?.GetValue("Long", BsonNull.Value) is { IsNumeric: true } lng ? lng.ToDouble() : null,
            radius is { IsNumeric: true } ? radius.ToInt32() : null);
    }

    internal static BusinessIntroItem? ReadIntro(BsonDocument doc)
    {
        var title = doc.GetValue("Title", BsonNull.Value);
        var description = doc.GetValue("Description", BsonNull.Value);
        var sticker = doc.GetValue("Sticker", BsonNull.Value) as BsonDocument;
        long? stickerId = sticker?.GetValue("Id", BsonNull.Value) is { IsNumeric: true } id ? id.ToInt64() : null;

        return new BusinessIntroItem(title.IsString ? title.AsString : string.Empty,
            description.IsString ? description.AsString : string.Empty, stickerId);
    }
}
