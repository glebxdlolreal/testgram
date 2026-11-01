namespace MyTelegram.QueryHandlers.InMemory.Updates;

public class GetUpdatesByGlobalSeqNoQueryHandler(IQueryOnlyReadModelStore<UpdatesReadModel> store)
    : IQueryHandler<GetUpdatesByGlobalSeqNoQuery, IReadOnlyCollection<IUpdatesReadModel>>
{
    public async Task<IReadOnlyCollection<IUpdatesReadModel>> ExecuteQueryAsync(GetUpdatesByGlobalSeqNoQuery query, CancellationToken cancellationToken)
    {
        // Parity with the MongoDB handler: EncryptedUpdates marker + device scoping.
        return await store.FindAsync(p => p.OwnerPeerId == query.UserId &&
                                          (p.UpdatesType == UpdatesType.EncryptedUpdates || p.UpdatesType == UpdatesType.BotQtsUpdates) &&
                                          p.GlobalSeqNo > query.MinGlobalSeqNo &&
                                          (p.OnlySendToThisAuthKeyId == null || p.OnlySendToThisAuthKeyId == query.PermAuthKeyId) &&
                                          (p.ExcludeAuthKeyId == null || p.ExcludeAuthKeyId != query.PermAuthKeyId), cancellationToken: cancellationToken);
    }
}