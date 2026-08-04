using EventFlow.MongoDB.EventStore;
using EventFlow.MongoDB.ReadStores;
using EventFlow.MongoDB.ValueObjects;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Linq.Expressions;

namespace MyTelegram.EventFlow.MongoDB;

public abstract class MongoDbIndexesCreatorBase(
    IMongoDatabase database,
    IReadModelDescriptionProvider descriptionProvider,
    IMongoDbEventPersistenceInitializer eventPersistenceInitializer)
    : IMongoDbIndexesCreator
{
    public async Task CreateAllIndexesAsync()
    {
        eventPersistenceInitializer.Initialize();
        var snapShotCollectionName = "snapShots";
        await CreateIndexAsync<MongoDbSnapshotDataModel>(p => p.AggregateId, snapShotCollectionName);
        await CreateIndexAsync<MongoDbSnapshotDataModel>(p => p.AggregateName, snapShotCollectionName);
        await CreateIndexAsync<MongoDbSnapshotDataModel>(p => p.AggregateSequenceNumber, snapShotCollectionName);

        await CreateAllIndexesCoreAsync();
    }

    protected abstract Task CreateAllIndexesCoreAsync();

    protected async Task CreateIndexAsync<TReadModel>(Expression<Func<TReadModel, object>> field)
        where TReadModel : IMongoDbReadModel
    {
        var indexDefine = Builders<TReadModel>.IndexKeys.Ascending(field);
        var collectionName = descriptionProvider.GetReadModelDescription<TReadModel>().RootCollectionName;
        await database.GetCollection<TReadModel>(collectionName.Value).Indexes
            .CreateOneAsync(new CreateIndexModel<TReadModel>(indexDefine));
    }

    /// <summary>
    /// Creates a compound index over <paramref name="fields"/>, in the order given. Needed where a query
    /// filters on several fields and sorts on another: single-field indexes cannot serve that without
    /// fetching and sorting every candidate document, whereas one correctly ordered compound index
    /// (equality fields first, then the range/sort field) answers it from the index alone.
    /// </summary>
    protected async Task CreateCompoundIndexAsync<TReadModel>(string name,
        params Expression<Func<TReadModel, object>>[] fields)
        where TReadModel : IMongoDbReadModel
    {
        var indexDefine = Builders<TReadModel>.IndexKeys.Combine(
            fields.Select(f => Builders<TReadModel>.IndexKeys.Ascending(f)));
        var collectionName = descriptionProvider.GetReadModelDescription<TReadModel>().RootCollectionName;
        await database.GetCollection<TReadModel>(collectionName.Value).Indexes
            .CreateOneAsync(new CreateIndexModel<TReadModel>(indexDefine, new CreateIndexOptions { Name = name }));
    }

    protected async Task CreateIndexAsync<TSnapshot>(Expression<Func<TSnapshot, object>> field,
        string collectionName)
    {
        var indexDefine = Builders<TSnapshot>.IndexKeys.Ascending(field);
        await database.GetCollection<TSnapshot>(collectionName).Indexes
            .CreateOneAsync(new CreateIndexModel<TSnapshot>(indexDefine));
    }

    /// <summary>
    /// Creates an ascending index on a plain BsonDocument collection - those seeded outside of
    /// EventFlow (emoji_groups, emoji_keywords and friends) have no read model type, so the
    /// typed overloads above cannot reach them.
    /// </summary>
    protected async Task CreateRawIndexAsync(string collectionName, params string[] fieldNames)
    {
        var indexDefine = Builders<BsonDocument>.IndexKeys.Combine(
            fieldNames.Select(name => Builders<BsonDocument>.IndexKeys.Ascending(new StringFieldDefinition<BsonDocument, BsonValue>(name))));
        await database.GetCollection<BsonDocument>(collectionName).Indexes
            .CreateOneAsync(new CreateIndexModel<BsonDocument>(indexDefine,
                new CreateIndexOptions { Name = $"idx_{collectionName}_{string.Join("_", fieldNames).ToLowerInvariant()}" }));
    }
}