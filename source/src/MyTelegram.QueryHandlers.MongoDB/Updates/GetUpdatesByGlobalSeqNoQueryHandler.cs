namespace MyTelegram.QueryHandlers.MongoDB.Updates;

public class GetUpdatesByGlobalSeqNoQueryHandler(IQueryOnlyReadModelStore<UpdatesReadModel> store)
    : IQueryHandler<GetUpdatesByGlobalSeqNoQuery, IReadOnlyCollection<IUpdatesReadModel>>
{
    public async Task<IReadOnlyCollection<IUpdatesReadModel>> ExecuteQueryAsync(GetUpdatesByGlobalSeqNoQuery query, CancellationToken cancellationToken)
    {
        // Scoped to the EncryptedUpdates marker so no pre-existing pts=0 row from the generic update
        // producers is replayed, and to the caller's Authorization_Key, because these rows are
        // device-targeted: the accept-side encryptedChat carries g_b and key_fingerprint for one device
        // only, and the teardown update deliberately excludes the device that triggered it.
        // The Pts == 0 predicate is intentionally gone: it is implied by the marker, and keeping it would
        // silently drop these rows if they were ever given a pts.
        return await store.FindAsync(p => p.OwnerPeerId == query.UserId &&
                                          (p.UpdatesType == UpdatesType.EncryptedUpdates || p.UpdatesType == UpdatesType.BotQtsUpdates) &&
                                          p.GlobalSeqNo > query.MinGlobalSeqNo &&
                                          (p.OnlySendToThisAuthKeyId == null || p.OnlySendToThisAuthKeyId == query.PermAuthKeyId) &&
                                          (p.ExcludeAuthKeyId == null || p.ExcludeAuthKeyId != query.PermAuthKeyId),
            0,
            query.Limit,
            sort: new SortOptions<UpdatesReadModel>(p => p.GlobalSeqNo, SortType.Ascending),
            cancellationToken: cancellationToken);
    }
}