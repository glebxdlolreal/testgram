using EventFlow.MongoDB.Extensions;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MyTelegram.Domain.Aggregates.AppCode;
using MyTelegram.Domain.Aggregates.Updates;
using MyTelegram.EventBus.Extensions;
using MyTelegram.EventFlow.MongoDB.Extensions;
using MyTelegram.EventFlow.MongoDB.ReadStores;
using MyTelegram.EventFlow.ReadStores;
using MyTelegram.Messenger.Extensions;
using MyTelegram.Messenger.QueryServer.EventHandlers;
using MyTelegram.QueryHandlers.MongoDB;
using MyTelegram.ReadModel.MongoDB;

namespace MyTelegram.Messenger.QueryServer.Extensions;

public static class MyTelegramMessengerQueryServerExtensions
{

    public static void AddEventHandlers(this IServiceCollection services)
    {
        services.AddSubscription<MessengerQueryDataReceivedEvent, MessengerEventHandler>();
        services.AddSubscription<StickerDataReceivedEvent, MessengerEventHandler>();
        services.AddSubscription<DownloadDataReceivedEvent, MessengerEventHandler>();
        services.AddSubscription<UploadDataReceivedEvent, MessengerEventHandler>();

        services.AddSubscription<UserIsOnlineEvent, UserIsOnlineEventHandler>();
        services.AddSubscription<MyTelegram.Messenger.Services.Bots.BotUserChangedEvent, MyTelegram.Messenger.Services.Bots.BotUserChangedEventHandler>();

        services.AddSubscription<DomainEventMessage, DistributedDomainEventHandler>();
        services.AddSubscription<DuplicateCommandEvent, DuplicateOperationExceptionHandler>();

        // PUSH notification delivery: consume the LayeredPushMessageCreatedIntegrationEvent that
        // DomainEventHandlerBase already publishes with a PushData payload, and dispatch it to
        // FCM/APNS/Web-Push. See https://corefork.telegram.org/api/push-updates
        services.AddSubscription<LayeredPushMessageCreatedIntegrationEvent, PushNotificationEventHandler>();
    }

    public static void AddMyTelegramMessengerQueryServer(this IServiceCollection services, Action<IEventFlowOptions>? configure = null)
    {
        services.RegisterServices();
        services.RegisterMongoDbSerializer();
        services.AddEventFlow(options =>
        {
            options.AddMyTelegramMongoDbReadModel();
            options.AddQueryHandlers();
            options.AddEvents(typeof(AppCodeAggregate).Assembly);
            options.AddEventUpgraders();
            options.AddMongoDbQueryHandlers();
            options.AddSubscribers(Assembly.GetEntryAssembly());

            // TempAggregate commands needed for SendMessageAsync (e.g. gift stars message)
            options.AddDefaults(typeof(MyTelegram.Domain.Specs).Assembly);

            options.UseMongoDbEventStore();
            options.UseMongoDbSnapshotStore();
            options.UseMongoDbReadModel<UpdatesAggregate, UpdatesId, UpdatesReadModel>();
            options.UseMongoDbReadModel<RpcResultAggregate, RpcResultId, RpcResultReadModel>();
            options.UseMongoDbReadModel<PtsAggregate, PtsId, PtsReadModel>();
            options.UseMongoDbReadModel<PtsAggregate, PtsId, PtsForAuthKeyIdReadModel>();
            configure?.Invoke(options);
        });

        services.AddMyTelegramMessengerServices();
        services.AddTransient<IQueryOnlyReadModelStore<IAccessHashReadModel>, MongoDbQueryOnlyReadModelStore<IAccessHashReadModel>>();
        BsonSerializer.RegisterSerializer(typeof(IAccessHashReadModel), new ImpliedImplementationInterfaceSerializer<IAccessHashReadModel, AccessHashReadModel>());

        services.AddReadModelMongoDbContext();
        services.AddEventStoreMongoDbContext<DefaultReadModelMongoDbContext>();
        services.AddEventHandlers();
    }
}
