extern alias DataSeeder;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EventFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using MyTelegram.Domain.Aggregates.AntiSpamCompletion;
using MyTelegram.Domain.Aggregates.Dialog;
using MyTelegram.Domain.Aggregates.Channel;
using MyTelegram.Domain.Aggregates.Messaging;
using MyTelegram.Domain.Aggregates.Temp;
using MyTelegram.Domain.Sagas;
using MyTelegram.Messenger.NativeAot;
using MyTelegram.Services.Extensions;

namespace MyTelegram.Messenger.Tests.AntiSpam;

public sealed class MessengerAntiSpamReleaseSerializationTests : AntiSpamReleaseSerializationTests
{
    protected override JsonSerializerContext Context => MyMessengerJsonContext.Default;
}

public sealed class DataSeederAntiSpamReleaseSerializationTests : AntiSpamReleaseSerializationTests
{
    protected override JsonSerializerContext Context =>
        DataSeeder::MyTelegram.Messenger.NativeAot.MyMessengerJsonContext.Default;
}

public abstract class AntiSpamReleaseSerializationTests
{
    protected abstract JsonSerializerContext Context { get; }
#if DEBUG
    private const string? ReleaseOnly = "Source-generated domain metadata is enabled only in Release; run dotnet test -c Release.";
#else
    private const string? ReleaseOnly = null;
#endif

    private static readonly Guid DeletionId = Guid.Parse("4179bbef-1881-4122-a530-662287100961");
    private static readonly RequestInfo Request = RequestInfo.Empty with
    {
        UserId = MyTelegramConsts.AntiSpamBotUserId,
        RequestId = DeletionId,
        ReqMsgId = 713,
        Layer = 222
    };

    [Theory(Skip = ReleaseOnly)]
    [InlineData(true)]
    [InlineData(false)]
    public void Channel_toggle_event_round_trips_through_runtime_resolver(bool enabled)
    {
        var restored = RoundTrip(new ChannelAntiSpamChangedEvent(Request, 123, enabled));
        Assert.Equal(enabled, restored.Enabled);
        Assert.Equal(123, restored.ChannelId);
        Assert.Equal(Request, restored.RequestInfo);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Deletion_events_round_trip_with_request_and_acknowledgement_identity()
    {
        var deleted = RoundTrip(new ChannelMessageDeletedEvent(Request, 123, 42, false, null, null, 17));
        Assert.Equal(Request, deleted.RequestInfo);
        Assert.Equal(123, deleted.ChannelId);
        Assert.Equal(42, deleted.MessageId);
        Assert.Equal(17, deleted.ThreadRootMessageId);

        var acknowledged = RoundTrip(new ChannelMessageDeletionAcknowledgedEvent(Request, 123, 42, DeletionId));
        Assert.Equal(Request, acknowledged.RequestInfo);
        Assert.Equal(DeletionId, acknowledged.OriginalDeletionRequestId);
        Assert.Equal(42, acknowledged.MessageId);
        Assert.Equal(123, acknowledged.ChannelId);

        var unknown = RoundTrip(new ChannelMessageDeletionAcknowledgedEvent(Request, 123, 42, null));
        Assert.Null(unknown.OriginalDeletionRequestId);

        var completed = RoundTrip(new DeleteChannelMessagesAlreadyDeletedSagaEvent(Request, 123, true));
        Assert.Equal(Request, completed.RequestInfo);
        Assert.Equal(123, completed.ChannelId);
        Assert.True(completed.IsDeleteHistory);
    }

    [Theory(Skip = ReleaseOnly)]
    [InlineData(true)]
    [InlineData(false)]
    public void Saga_acknowledgement_events_preserve_actual_deletion_flag(bool wasDeleted)
    {
        var channel = RoundTrip(new DeleteChannelMessageAcknowledgedSagaEvent(42, wasDeleted));
        Assert.Equal(42, channel.MessageId);
        Assert.Equal(wasDeleted, channel.WasDeleted);
        var reply = RoundTrip(new DeleteReplyMessageAcknowledgedSagaEvent(43, wasDeleted));
        Assert.Equal(43, reply.MessageId);
        Assert.Equal(wasDeleted, reply.WasDeleted);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Completion_identity_round_trips_canonical_operation_key_with_generated_metadata()
    {
        const string operationId = "antispamcompletion-800000000001-10-00000000000000000000000000000001";
        var restored = RoundTrip(new AntiSpamCompletionId(operationId));
        Assert.Equal(operationId, restored.Value);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Durable_completion_event_preserves_operation_identity_and_assigned_update_numbers()
    {
        var operationId = "antispamcompletion-" + DeletionId.ToString("N");
        var restored = RoundTrip(new AntiSpamCompletionCreatedEvent(operationId, 123, 42,
            271, 4_294_967_299L, 1_700_000_000, 41));
        Assert.Equal(operationId, restored.OperationId);
        Assert.Equal(123, restored.ChannelId);
        Assert.Equal(42, restored.MessageId);
        Assert.Equal(271, restored.Pts);
        Assert.Equal(4_294_967_299L, restored.GlobalSeqNo);
        Assert.Equal(1_700_000_000, restored.Date);
        Assert.Equal(41, restored.NewTopMessageId);
        var published = RoundTrip(new AntiSpamCompletionPublishedEvent(operationId, 123, 42,
            271, 4_294_967_299L, 1_700_000_000, 41));
        Assert.Equal(operationId, published.OperationId);
        Assert.Equal(123, published.ChannelId);
        Assert.Equal(42, published.MessageId);
        Assert.Equal(271, published.Pts);
        Assert.Equal(4_294_967_299L, published.GlobalSeqNo);
        Assert.Equal(1_700_000_000, published.Date);
        Assert.Equal(41, published.NewTopMessageId);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Completion_top_repair_preserves_operation_and_compare_exchange_message_ids()
    {
        var operationId = "antispamcompletion-" + DeletionId.ToString("N");
        var restored = RoundTrip(new AntiSpamCompletionTopRepairedEvent(operationId, 123, 42, 41));
        Assert.Equal(operationId, restored.OperationId);
        Assert.Equal(123, restored.ChannelId);
        Assert.Equal(42, restored.ExpectedTopMessageId);
        Assert.Equal(41, restored.NewTopMessageId);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Reply_deletion_effect_preserves_operation_key_replies_and_assigned_pts()
    {
        const string operationKey = "antispam-reply-4179bbef18814122a530662287100961";
        var restored = RoundTrip(new AntiSpamReplyDeletionAppliedEvent(operationKey, 7, 271,
            800_000_000_123L, 42));
        Assert.Equal(operationKey, restored.OperationKey);
        Assert.Equal(7, restored.Replies);
        Assert.Equal(271, restored.Pts);
        Assert.Equal(800_000_000_123L, restored.PostChannelId);
        Assert.Equal(42, restored.PostMessageId);

        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        var oldJson = JsonNode.Parse(serializer.Serialize(restored, false))!.AsObject();
        Assert.True(oldJson.Remove("PostChannelId"));
        Assert.True(oldJson.Remove("PostMessageId"));
        var legacy = Assert.IsType<AntiSpamReplyDeletionAppliedEvent>(
            serializer.Deserialize(oldJson.ToJsonString(), typeof(AntiSpamReplyDeletionAppliedEvent)));
        Assert.Null(legacy.PostChannelId);
        Assert.Null(legacy.PostMessageId);
        Assert.Equal(operationKey, legacy.OperationKey);
        Assert.Equal(7, legacy.Replies);
        Assert.Equal(271, legacy.Pts);
        var restoredLegacy = RoundTrip(legacy);
        Assert.Null(restoredLegacy.PostChannelId);
        Assert.Null(restoredLegacy.PostMessageId);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Reply_deletion_effect_preserves_full_reply_payload_and_accepts_legacy_absence()
    {
        const string operationKey = "antispam-reply-payload";
        var reply = new MessageReply(800_000_000_123L, 7, 271, 99,
            [new Peer(PeerType.User, 456), new Peer(PeerType.Channel, 800_000_000_456L)]);
        AssertGenerated(typeof(MessageReply));
        var restored = RoundTrip(new AntiSpamReplyDeletionAppliedEvent(operationKey, 7, 271,
            800_000_000_123L, 42, reply));
        var payload = Assert.IsType<MessageReply>(restored.Reply);
        Assert.NotSame(reply, payload);
        Assert.Equal(reply.ChannelId, payload.ChannelId);
        Assert.Equal(reply.Replies, payload.Replies);
        Assert.Equal(reply.RepliesPts, payload.RepliesPts);
        Assert.Equal(reply.MaxId, payload.MaxId);
        Assert.NotNull(payload.RecentRepliers);
        Assert.Equal(reply.RecentRepliers!.ToArray(), payload.RecentRepliers.ToArray());

        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        var oldJson = JsonNode.Parse(serializer.Serialize(restored, false))!.AsObject();
        Assert.True(oldJson.Remove("Reply"));
        var legacy = Assert.IsType<AntiSpamReplyDeletionAppliedEvent>(
            serializer.Deserialize(oldJson.ToJsonString(), typeof(AntiSpamReplyDeletionAppliedEvent)));
        Assert.Null(legacy.Reply);
        Assert.Equal(operationKey, legacy.OperationKey);
        Assert.Equal(7, legacy.Replies);
        Assert.Equal(271, legacy.Pts);
        Assert.Equal(800_000_000_123L, legacy.PostChannelId);
        Assert.Equal(42, legacy.PostMessageId);
        Assert.Null(RoundTrip(legacy).Reply);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Mention_deletion_effect_preserves_operation_key_message_and_unread_count()
    {
        const string operationKey = "antispam-mention-4179bbef18814122a530662287100961";
        var restored = RoundTrip(new MyTelegram.Domain.Aggregates.Dialog.AntiSpamMentionDeletionAppliedEvent(
            operationKey, 42, 3));
        Assert.Equal(operationKey, restored.OperationKey);
        Assert.Equal(42, restored.MessageId);
        Assert.Equal(3, restored.UnreadMentionsCount);
    }

    [Theory(Skip = ReleaseOnly)]
    [InlineData(19)]
    [InlineData(null)]
    public void Delete_start_event_preserves_optional_expected_aggregate_version(int? expectedVersion)
    {
        var value = new DeleteChannelMessagesStartedEvent(Request, 123, [42, 43], 41,
            null, null, null, null, expectedVersion);
        var restored = RoundTrip(value);
        Assert.Equal(Request, restored.RequestInfo);
        Assert.Equal(expectedVersion, restored.ExpectedAggregateVersion);
        Assert.Equal(new[] { 42, 43 }, restored.MessageIds);
        Assert.Equal(41, restored.NewTopMessageId);

        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        var json = JsonNode.Parse(serializer.Serialize(value, false))!.AsObject();
        json.Remove("ExpectedAggregateVersion");
        var legacy = Assert.IsType<DeleteChannelMessagesStartedEvent>(
            serializer.Deserialize(json.ToJsonString(), typeof(DeleteChannelMessagesStartedEvent)));
        Assert.Null(legacy.ExpectedAggregateVersion);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Mention_sync_command_requires_and_preserves_expected_version_in_three_argument_contract()
    {
        var dialogId = DialogId.Create(1001, PeerType.Channel, 123);
        var command = new SyncUnreadMentionsCountCommand(dialogId, 3, 27);
        Assert.Equal(dialogId, command.AggregateId);
        Assert.Equal(3, command.UnreadMentionsCount);
        Assert.Equal(27, command.ExpectedVersion);
    }

    [Theory(Skip = ReleaseOnly)]
    [InlineData(false)]
    [InlineData(true)]
    public void Mention_admission_events_preserve_operation_message_and_consumption_proof(bool consumed)
    {
        const string operationKey = "antispam-mention-admission";
        var admitted = RoundTrip(new AntiSpamMentionAdmittedEvent(operationKey, 42, consumed));
        Assert.Equal(operationKey, admitted.OperationKey);
        Assert.Equal(42, admitted.MessageId);
        Assert.Equal(consumed, admitted.Consumed);
        Assert.Equal(operationKey, RoundTrip(new AntiSpamMentionAdmissionReleasedEvent(operationKey)).OperationKey);
        RoundTrip(new MentionRecountRequestedEvent());
        RoundTrip(new MentionRecountCompletedEvent());
    }

    [Fact(Skip = ReleaseOnly)]
    public void Mention_read_intent_events_and_snapshot_preserve_operation_and_legacy_absence()
    {
        const string operationKey = "mention-read-intent-42";
        Assert.Equal(operationKey, RoundTrip(new MentionReadIntentAdmittedEvent(operationKey)).OperationKey);
        Assert.Equal(operationKey, RoundTrip(new MentionReadIntentAppliedEvent(operationKey)).OperationKey);
        Assert.Equal(operationKey, RoundTrip(new MentionReadIntentReleasedEvent(operationKey)).OperationKey);
        var snapshot = RoundTrip(new DialogSnapshot(1001, 42, 20, 19, 7,
            new Peer(PeerType.Channel, 123), false, false, 0, null, 3, null,
            mentionReadOperation: operationKey));
        Assert.Equal(operationKey, snapshot.MentionReadOperation);

        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        var json = JsonNode.Parse(serializer.Serialize(snapshot, false))!.AsObject();
        Assert.True(json.Remove("MentionReadOperation"));
        var legacy = Assert.IsType<DialogSnapshot>(serializer.Deserialize(json.ToJsonString(), typeof(DialogSnapshot)));
        Assert.Null(legacy.MentionReadOperation);
        var restoredLegacy = RoundTrip(legacy);
        Assert.Null(restoredLegacy.MentionReadOperation);
        Assert.Equal(1001, restoredLegacy.OwnerId);
        Assert.Equal(3, restoredLegacy.UnreadMentionsCount);
        Assert.Equal(snapshot.ToPeer, restoredLegacy.ToPeer);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Reply_deletion_effect_preserves_expected_stale_source_and_defaults_legacy_absence()
    {
        const string operationKey = "antispam-reply-repair-42";
        const string sourceId = "stale-reply-source-42";
        var restored = RoundTrip(new AntiSpamReplyDeletionAppliedEvent(operationKey, 7, 271,
            expectedStaleSourceId: sourceId));
        Assert.Equal(sourceId, restored.ExpectedStaleSourceId);
        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        var json = JsonNode.Parse(serializer.Serialize(restored, false))!.AsObject();
        Assert.True(json.Remove("ExpectedStaleSourceId"));
        var legacy = Assert.IsType<AntiSpamReplyDeletionAppliedEvent>(
            serializer.Deserialize(json.ToJsonString(), typeof(AntiSpamReplyDeletionAppliedEvent)));
        Assert.Null(legacy.ExpectedStaleSourceId);
        var restoredLegacy = RoundTrip(legacy);
        Assert.Null(restoredLegacy.ExpectedStaleSourceId);
        Assert.Equal(operationKey, restoredLegacy.OperationKey);
        Assert.Equal(7, restoredLegacy.Replies);
        Assert.Equal(271, restoredLegacy.Pts);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Dialog_snapshot_preserves_mention_admission_and_defaults_legacy_optional_fields()
    {
        var snapshot = new DialogSnapshot(1001, 42, 20, 19, 7, new Peer(PeerType.Channel, 123),
            false, false, 0, null, 3, null, hasAntiSpamMentionEffects: true,
            antiSpamMentionOperation: "mention-admission-42", antiSpamMentionMessageId: 42,
            antiSpamMentionConsumed: true, mentionRecountRequired: true);
        var restored = RoundTrip(snapshot);
        Assert.True(restored.HasAntiSpamMentionEffects);
        Assert.Equal("mention-admission-42", restored.AntiSpamMentionOperation);
        Assert.Equal(42, restored.AntiSpamMentionMessageId);
        Assert.True(restored.AntiSpamMentionConsumed);
        Assert.True(restored.MentionRecountRequired);

        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        var json = JsonNode.Parse(serializer.Serialize(restored, false))!.AsObject();
        foreach (var field in new[] { "HasAntiSpamMentionEffects", "AntiSpamMentionOperation",
                     "AntiSpamMentionMessageId", "AntiSpamMentionConsumed", "MentionRecountRequired" })
            Assert.True(json.Remove(field));
        var legacy = Assert.IsType<DialogSnapshot>(serializer.Deserialize(json.ToJsonString(), typeof(DialogSnapshot)));
        var restoredLegacy = RoundTrip(legacy);
        Assert.False(restoredLegacy.HasAntiSpamMentionEffects);
        Assert.Null(restoredLegacy.AntiSpamMentionOperation);
        Assert.Equal(0, restoredLegacy.AntiSpamMentionMessageId);
        Assert.False(restoredLegacy.AntiSpamMentionConsumed);
        Assert.False(restoredLegacy.MentionRecountRequired);
        Assert.Equal(1001, restoredLegacy.OwnerId);
        Assert.Equal(42, restoredLegacy.TopMessage);
        Assert.Equal(3, restoredLegacy.UnreadMentionsCount);
        Assert.Equal(snapshot.ToPeer, restoredLegacy.ToPeer);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Channel_snapshot_preserves_antispam_and_defaults_old_snapshot_to_disabled()
    {
        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        AssertGenerated(typeof(ChannelSnapshot));
        const string oldJson = """
            {"ChannelId":123,"Title":"Legacy channel","AdminList":[],"BotUidList":[],"ParticipantCount":100}
            """;
        var legacy = Assert.IsType<ChannelSnapshot>(serializer.Deserialize(oldJson, typeof(ChannelSnapshot)));
        Assert.False(legacy.AntiSpam);
        Assert.False(legacy.Autotranslation);
        Assert.Null(legacy.ProfileColor);
        Assert.Equal(123, legacy.ChannelId);
        Assert.Equal("Legacy channel", legacy.Title);
        Assert.False(RoundTrip(legacy).AntiSpam);

        var current = JsonNode.Parse(oldJson)!.AsObject();
        current["AntiSpam"] = true;
        var snapshot = Assert.IsType<ChannelSnapshot>(serializer.Deserialize(current.ToJsonString(), typeof(ChannelSnapshot)));
        var restored = RoundTrip(snapshot);
        Assert.True(restored.AntiSpam);
        Assert.Equal(100, restored.ParticipantCount);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Cancellation_event_and_snapshot_preserve_request_identity_and_accept_legacy_absence()
    {
        var cancelled = RoundTrip(new AntiSpamDeletionCancelledEvent(DeletionId));
        Assert.Equal(DeletionId, cancelled.RequestId);

        var peer = new Peer(PeerType.Channel, 123);
        var message = new MessageItem(peer, peer, new Peer(PeerType.User, 456), 456, 42,
            "message", 1_700_000_000, 123456789, true);
        var current = RoundTrip(new MessageSnapshot(message, [], 42, false, 0, false,
            27, false, antiSpamDeletionCancelledRequestId: DeletionId));
        Assert.Equal(DeletionId, current.AntiSpamDeletionCancelledRequestId);
        Assert.False(current.IsDeleted);
        Assert.False(current.IsChannelDeleted);
        Assert.Null(current.ChannelDeletionRequestId);
        Assert.Equal(message, current.MessageItem);

        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        var oldJson = JsonNode.Parse(serializer.Serialize(current, false))!.AsObject();
        Assert.True(oldJson.Remove("AntiSpamDeletionCancelledRequestId"));
        var legacy = Assert.IsType<MessageSnapshot>(
            serializer.Deserialize(oldJson.ToJsonString(), typeof(MessageSnapshot)));
        Assert.Null(legacy.AntiSpamDeletionCancelledRequestId);
        var restoredLegacy = RoundTrip(legacy);
        Assert.Null(restoredLegacy.AntiSpamDeletionCancelledRequestId);
        Assert.False(restoredLegacy.IsDeleted);
        Assert.Equal(27, restoredLegacy.Pts);
        Assert.Equal(message, restoredLegacy.MessageItem);
    }

    [Fact(Skip = ReleaseOnly)]
    public void Message_snapshot_preserves_deletion_identity_without_attributing_legacy_deletion_to_antispam()
    {
        var peer = new Peer(PeerType.Channel, 123);
        var message = new MessageItem(peer, peer, new Peer(PeerType.User, 456), 456, 42,
            "message", 1_700_000_000, 123456789, true);
        var current = RoundTrip(new MessageSnapshot(message, [], 42, false, 0, false,
            27, true, true, DeletionId, 1));
        Assert.True(current.IsDeleted);
        Assert.True(current.IsChannelDeleted);
        Assert.Equal(DeletionId, current.ChannelDeletionRequestId);
        Assert.Equal(1, current.ChannelDeletionProofVersion);
        Assert.Equal(message, current.MessageItem);

        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        var oldJson = JsonNode.Parse(serializer.Serialize(current, false))!.AsObject();
        Assert.True(oldJson.Remove("IsChannelDeleted"));
        Assert.True(oldJson.Remove("ChannelDeletionRequestId"));
        Assert.True(oldJson.Remove("ChannelDeletionProofVersion"));
        var legacy = Assert.IsType<MessageSnapshot>(serializer.Deserialize(oldJson.ToJsonString(), typeof(MessageSnapshot)));
        Assert.True(legacy.IsDeleted);
        Assert.False(legacy.IsChannelDeleted);
        Assert.Null(legacy.ChannelDeletionRequestId);
        Assert.Equal(0, legacy.ChannelDeletionProofVersion);
        var restoredLegacy = RoundTrip(legacy);
        Assert.False(restoredLegacy.IsChannelDeleted);
        Assert.Null(restoredLegacy.ChannelDeletionRequestId);
        Assert.Equal(0, restoredLegacy.ChannelDeletionProofVersion);

        // A known channel deletion without an operation id is still unattributed, not antispam.
        var unattributed = RoundTrip(new MessageSnapshot(message, [], 42, false, 0, false,
            27, true, true, null));
        Assert.True(unattributed.IsChannelDeleted);
        Assert.Null(unattributed.ChannelDeletionRequestId);
    }

    private T RoundTrip<T>(T value) where T : class
    {
        AssertGenerated(typeof(T));
        using var provider = CreateProvider();
        var serializer = provider.GetRequiredService<IJsonSerializer>();
        // EventFlow persists runtime objects and deserializes using the registered event Type.
        // Do not use context.Options: production's serializer options use PascalCase names.
        var json = serializer.Serialize((object)value, false);
        var restored = Assert.IsType<T>(serializer.Deserialize(json, typeof(T)));
        Assert.Equal(json, serializer.Serialize((object)restored, false));
        return restored;
    }

    private void AssertGenerated(Type type)
    {
        // Production allows reflection fallback. This assertion makes missing Release roots fail.
        Assert.NotNull(Context.GetTypeInfo(type));
    }

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddSystemTextJson(options => options.TypeInfoResolverChain.Add(Context));
        return services.BuildServiceProvider();
    }
}
