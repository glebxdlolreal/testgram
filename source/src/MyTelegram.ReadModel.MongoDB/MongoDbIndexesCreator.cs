namespace MyTelegram.ReadModel.MongoDB;

public class MongoDbIndexesCreator(
    IMongoDatabase database,
    IReadModelDescriptionProvider descriptionProvider,
    IMongoDbEventPersistenceInitializer eventPersistenceInitializer)
    : MongoDbIndexesCreatorBase(database,
        descriptionProvider,
        eventPersistenceInitializer), ITransientDependency
{
    protected override async Task CreateAllIndexesCoreAsync()
    {
        await CreateIndexAsync<DialogReadModel>(p => p.OwnerId);
        await CreateIndexAsync<DialogReadModel>(p => p.Pinned);

        await CreateIndexAsync<MessageReadModel>(p => p.MessageId);
        await CreateIndexAsync<MessageReadModel>(p => p.OwnerPeerId);
        await CreateIndexAsync<MessageReadModel>(p => p.MessageType);
        await CreateIndexAsync<MessageReadModel>(p => p.Pinned);
        await CreateIndexAsync<MessageReadModel>(p => p.Pts);
        await CreateIndexAsync<MessageReadModel>(p => p.ToPeerType);
        await CreateIndexAsync<MessageReadModel>(p => p.SendMessageType);

        await CreateIndexAsync<UserReadModel>(p => p.UserId);
        await CreateIndexAsync<UserReadModel>(p => p.PhoneNumber);
        await CreateIndexAsync<UserReadModel>(p => p.FirstName);
        await CreateIndexAsync<ChannelReadModel>(p => p.ChannelId);
        await CreateIndexAsync<ChannelFullReadModel>(p => p.ChannelId);
        await CreateIndexAsync<ChannelMemberReadModel>(p => p.ChannelId);
        await CreateIndexAsync<ChannelMemberReadModel>(p => p.UserId);
        await CreateIndexAsync<ChannelMemberReadModel>(p => p.Kicked);
        await CreateIndexAsync<ChannelMemberReadModel>(p => p.IsBot);
        //await CreateIndexAsync<AuthKeyReadModel>(p => p.TempAuthKeyId);

        await CreateIndexAsync<DeviceReadModel>(p => p.PermAuthKeyId);
        await CreateIndexAsync<DeviceReadModel>(p => p.UserId);
        await CreateIndexAsync<DeviceReadModel>(p => p.IsActive);

        // Stats ingestion counts muted subscribers per channel (notify_on/muted gauges).
        await CreateIndexAsync<PeerNotifySettingsReadModel>(p => p.PeerId);

        await CreateIndexAsync<ContactReadModel>(p => p.SelfUserId);
        await CreateIndexAsync<ContactReadModel>(p => p.TargetUserId);
        //await CreateIndexAsync<FileReadModel>(p => p.UserId);
        //await CreateIndexAsync<FileReadModel>(p => p.FileId);
        //await CreateIndexAsync<FileReadModel>(p => p.ServerFileId);
        //await CreateIndexAsync<FileReadModel>(p => p.FileReference);

        await CreateIndexAsync<UserNameReadModel>(p => p.UserName);
        await CreateIndexAsync<UserNameReadModel>(p => p.PeerId);

        //await CreateIndexAsync<PushUpdatesReadModel>(p => p.PeerId);
        //await CreateIndexAsync<PushUpdatesReadModel>(p => p.Pts);
        //await CreateIndexAsync<PushUpdatesReadModel>(p => p.SeqNo);

        await CreateIndexAsync<ReadingHistoryReadModel>(p => p.MessageId);
        await CreateIndexAsync<ReadingHistoryReadModel>(p => p.TargetPeerId);

        await CreateIndexAsync<PtsReadModel>(p => p.PeerId);
        await CreateIndexAsync<EncryptedChatReadModel>(p => p.ChatId);
        await CreateIndexAsync<EncryptedChatReadModel>(p => p.AdminId);
        await CreateIndexAsync<EncryptedChatReadModel>(p => p.ParticipantId);

        // updates.getDifference reads this collection on every call, three times over (the pts box, the
        // channel stream and the secret-chat handshake replay), and it is append-only and never pruned.
        // These were previously declared only in QueryServerMongoDbIndexesCreator, which nothing ever
        // invokes - CreateAllIndexesAsync is called from the data seeder alone, and the seeder resolves
        // THIS creator - so the collection ran unindexed.
        await CreateIndexAsync<UpdatesReadModel>(p => p.OwnerPeerId);
        await CreateIndexAsync<UpdatesReadModel>(p => p.ChannelId);
        await CreateIndexAsync<UpdatesReadModel>(p => p.Pts);
        await CreateIndexAsync<UpdatesReadModel>(p => p.GlobalSeqNo);

        // The handshake replay (GetUpdatesByGlobalSeqNoQuery) filters OwnerPeerId + UpdatesType and then
        // ranges and sorts on GlobalSeqNo. The single-field indexes above cannot serve that shape: measured
        // against a 84k-row collection, OwnerPeerId_1 alone still fetched 13k documents and blocked on an
        // in-memory sort (61ms), while this compound index answers it from the index (0 documents, 1ms).
        // Field order matters - the two equality fields must precede the range/sort field.
        await CreateCompoundIndexAsync<UpdatesReadModel>("idx_updates_owner_type_seq",
            p => p.OwnerPeerId, p => p.UpdatesType, p => p.GlobalSeqNo);

        await CreateIndexAsync<PtsForAuthKeyIdReadModel>(p => p.PeerId);
        await CreateIndexAsync<PtsForAuthKeyIdReadModel>(p => p.PermAuthKeyId);
        await CreateIndexAsync<PtsForAuthKeyIdReadModel>(p => p.GlobalSeqNo);
        await CreateIndexAsync<PtsForAuthKeyIdReadModel>(p => p.Pts);

        await CreateIndexAsync<RpcResultReadModel>(p => p.ReqMsgId);

        await CreateIndexAsync<ReplyReadModel>(p => p.ChannelId);
        await CreateIndexAsync<ReplyReadModel>(p => p.MessageId);

        await CreateIndexAsync<DialogFilterReadModel>(p => p.OwnerUserId);
        await CreateIndexAsync<PollReadModel>(p => p.ToPeerId);
        await CreateIndexAsync<PollReadModel>(p => p.PollId);
        await CreateIndexAsync<PollAnswerVoterReadModel>(p => p.PollId);
        await CreateIndexAsync<PollAnswerVoterReadModel>(p => p.Option);

        await CreateIndexAsync<LanguageReadModel>(p => p.LanguageCode);
        await CreateIndexAsync<LanguageTextReadModel>(p => p.LanguageCode);
        await CreateIndexAsync<LanguageTextReadModel>(p => p.Platform);

		await CreateIndexAsync<UserConfigReadModel>(p => p.UserId);
        await CreateIndexAsync<UserConfigReadModel>(p => p.Key);

        await CreateIndexAsync<MessageTokenReadModel>(p => p.OwnerPeerId);
        await CreateIndexAsync<MessageTokenReadModel>(p => p.ToPeerId);
        await CreateIndexAsync<MessageTokenReadModel>(p => p.MessageId);
        await CreateIndexAsync<MessageTokenReadModel>(p => p.Tokens);

        var snapShotCollectionName = "snapShots";
        await CreateIndexAsync<MongoDbSnapshotDataModel>(p => p.AggregateId, snapShotCollectionName);
        await CreateIndexAsync<MongoDbSnapshotDataModel>(p => p.AggregateName, snapShotCollectionName);
        await CreateIndexAsync<MongoDbSnapshotDataModel>(p => p.AggregateSequenceNumber, snapShotCollectionName);

        // The four messages.getEmoji*Groups methods each filter emoji_groups on For and sort by
        // Order, Title. The collection is seeded outside EventFlow, so it only had the default
        // _id_ index and every category lookup was a collection scan.
        await CreateRawIndexAsync("emoji_groups", "For", "Order", "Title");
    }
}
