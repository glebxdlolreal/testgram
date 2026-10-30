using EventFlow.Aggregates;
using EventFlow.Queries;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using Moq;
using MyTelegram.Domain.Aggregates.Messaging;
using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Tests.Stats;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.AntiSpam;

/// <summary>Classifier, sliding window and durable queue together, as the command server runs them.</summary>
public class AntiSpamSubscriberTests
{
    private const long Channel = 1_000_000_123;
    private const long Sender = 2_010_005;
    private const long T0 = 1_800_000_000;
    private const string Spam = "Huge giveaway in our channel, everyone wins a prize today https://spam.example/join";

    private sealed class Harness : IDisposable
    {
        private readonly EmbeddedMongoServer _mongo = EmbeddedMongoServer.Start();
        private readonly Dictionary<int, MessageItem> _messages = new();
        private readonly Dictionary<int, int> _versions = new();
        private readonly AntiSpamSubscriber _subscriber;
        private readonly AntiSpamBurstTrackerTests.ManualTime _time = new(T0);
        public readonly Mock<IChannelReadModel> ChannelModel = new();
        public bool SenderIsBot;
        public AntiSpamJobStore Store { get; }

        public Harness(AntiSpamOptions? options = null)
        {
            var o = Microsoft.Extensions.Options.Options.Create(options ?? new AntiSpamOptions());
            var classifier = new AntiSpamClassifier(o);
            Store = new AntiSpamJobStore(_mongo.Database, o);
            ChannelModel.SetupGet(c => c.MegaGroup).Returns(true);
            ChannelModel.SetupGet(c => c.AntiSpam).Returns(true);
            ChannelModel.SetupGet(c => c.ParticipantsCount).Returns(250);
            ChannelModel.SetupGet(c => c.CreatorId).Returns(2_010_001);
            ChannelModel.SetupGet(c => c.AdminList).Returns([]);

            var queries = new Mock<IQueryProcessor>();
            queries.Setup(q => q.ProcessAsync(It.IsAny<GetChannelByIdQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ChannelModel.Object);
            queries.Setup(q => q.ProcessAsync(It.IsAny<GetUserByIdQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Mock.Of<IUserReadModel>(u => u.Bot == SenderIsBot));
            queries.Setup(q => q.ProcessAsync(It.IsAny<GetMessagesByIdListQuery>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((GetMessagesByIdListQuery q, CancellationToken _) => q.MessageIdList
                    .Select(id => _messages.Values.SingleOrDefault(m => MessageId.Create(Channel, m.MessageId).Value == id))
                    .Where(m => m != null).Select(m => ReadModel(m!)).ToList());

            var converter = new Mock<IMessageConverterService>();
            converter.Setup(c => c.ToMessage(It.IsAny<long>(), It.IsAny<MessageItem>(), It.IsAny<List<long>?>(),
                    It.IsAny<bool>(), It.IsAny<int>()))
                .Returns((long _, MessageItem m, List<long>? _, bool _, int _) => Message(m.MessageId, m.Message, m.Date));
            converter.Setup(c => c.ToMessage(It.IsAny<long>(), It.IsAny<IMessageReadModel>(), It.IsAny<IPollReadModel?>(),
                    It.IsAny<List<string>?>(), It.IsAny<List<IUserReactionReadModel>?>(), It.IsAny<int>()))
                .Returns((long _, IMessageReadModel m, IPollReadModel? _, List<string>? _, List<IUserReactionReadModel>? _, int _) =>
                    Message(m.MessageId, m.Message, m.Date));

            var aggregates = new Mock<IAggregateStore>();
            aggregates.Setup(a => a.LoadAsync<MessageAggregate, MessageId>(It.IsAny<MessageId>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((MessageId id, CancellationToken _) => new MessageAggregate(id));

            _subscriber = new AntiSpamSubscriber(classifier, new AntiSpamBurstTracker(classifier, o, _time), Store,
                queries.Object, converter.Object, aggregates.Object, o, NullLogger<AntiSpamSubscriber>.Instance, _time);
        }

        private static TMessage Message(int id, string text, int date) => new()
        {
            Id = id, Message = text, Date = date, PeerId = new TPeerChannel { ChannelId = Channel },
            FromId = new TPeerUser { UserId = Sender }
        };

        private static IMessageReadModel ReadModel(MessageItem m) => Mock.Of<IMessageReadModel>(r =>
            r.MessageId == m.MessageId && r.OwnerPeerId == Channel && r.SenderUserId == m.SenderUserId &&
            r.SenderPeerId == m.SenderUserId && r.Message == m.Message && r.Entities2 == m.Entities && r.Date == m.Date);

        public Task Send(int id, string text, long date, TVector<IMessageEntity>? entities = null, long sender = Sender,
            bool forward = false, Peer? sendAs = null)
        {
            _time.Now = date;
            var item = new MessageItem(new Peer(PeerType.Channel, Channel), new Peer(PeerType.Channel, Channel),
                new Peer(PeerType.User, sender), sender, id, text, (int)date, Random.Shared.NextInt64(), true,
                MessageSubType: forward ? MessageSubType.ForwardMessage : MessageSubType.Normal, Entities: entities,
                FwdHeader: forward ? new MessageFwdHeader(false, false, new Peer(PeerType.User, 2_010_099), null, null, null, (int)date - 3600, null, null, null, null, null, null, false) : null,
                SendAs: sendAs);
            _messages[id] = item;
            _versions[id] = 1;
            var e = new OutboxMessageCreatedEvent(RequestInfo.Empty with { UserId = sender }, item, null, null, false, 0, null, null);
            return _subscriber.HandleAsync(new DomainEvent<MessageAggregate, MessageId, OutboxMessageCreatedEvent>(
                e, global::EventFlow.Aggregates.Metadata.Empty, DateTimeOffset.UtcNow, MessageId.Create(Channel, id), 1), default);
        }

        public Task Edit(int id, string text, long editDate)
        {
            _time.Now = editDate;
            var old = _messages[id];
            var item = old with { Message = text, EditDate = (int)editDate };
            _messages[id] = item;
            var version = ++_versions[id];
            var e = new OutboxMessageEditedEventV2(RequestInfo.Empty with { UserId = old.SenderUserId }, old, item);
            return _subscriber.HandleAsync(new DomainEvent<MessageAggregate, MessageId, OutboxMessageEditedEventV2>(
                e, global::EventFlow.Aggregates.Metadata.Empty, DateTimeOffset.UtcNow, MessageId.Create(Channel, id), version), default);
        }

        public async Task<List<BsonDocument>> Jobs() => await _mongo.Database.GetCollection<BsonDocument>("antispam_jobs")
            .Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument("message", 1)).ToListAsync();

        public async Task<int[]> Queued() => (await Jobs()).Select(j => j["message"].AsInt32).ToArray();

        public IMongoDatabase Database => _mongo.Database;
        public void Dispose() => _mongo.Dispose();
    }

    // ----- must be caught -----

    [RequiresMongoDbFact]
    public async Task Four_copies_differing_in_spacing_emoji_and_invisibles_delete_the_whole_burst()
    {
        using var h = new Harness();
        await h.Send(1, Spam, T0);
        await h.Send(2, Spam.Replace(" ", "  \n"), T0 + 30);
        await h.Send(3, "\U0001F381 " + Spam.Replace("prize", "pri​ze") + " \U0001F525", T0 + 60);
        (await h.Queued()).ShouldBeEmpty();
        await h.Send(4, Spam + "️✅", T0 + 115);
        var jobs = await h.Jobs();
        jobs.Select(j => j["message"].AsInt32).ShouldBe([1, 2, 3, 4]);
        jobs.Select(j => j["burst"].AsString).Distinct().Count().ShouldBe(1);
        jobs.ShouldAllBe(j => j["mode"].AsString == AntiSpamClassifier.StrictMode);
    }

    [RequiresMongoDbFact]
    public async Task Punctuation_and_case_variants_are_caught_by_the_loose_key_at_five()
    {
        using var h = new Harness();
        string[] variants = [Spam, Spam.ToUpperInvariant(), Spam.Replace(",", "!!!"), Spam.Replace(" in ", " in: "), "*" + Spam + "*"];
        for (var i = 0; i < 4; i++) await h.Send(i + 1, variants[i], T0 + i * 10);
        (await h.Queued()).ShouldBeEmpty();
        await h.Send(5, variants[4], T0 + 50);
        (await h.Queued()).ShouldBe([1, 2, 3, 4, 5]);
        (await h.Jobs()).ShouldAllBe(j => j["mode"].AsString == AntiSpamClassifier.LooseMode);
    }

    [RequiresMongoDbFact]
    public async Task Link_only_in_entities_is_caught()
    {
        using var h = new Harness();
        const string text = "Huge giveaway in our channel, everyone wins a prize today, press here";
        var hidden = new TVector<IMessageEntity>(new TMessageEntityTextUrl { Offset = 58, Length = 10, Url = "https://spam.example" });
        for (var i = 1; i <= 4; i++) await h.Send(i, text, T0 + i, i == 2 ? null : hidden);
        (await h.Queued()).ShouldBeEmpty(); // the copy without the hidden link is not under scrutiny
        await h.Send(5, text, T0 + 5, hidden);
        (await h.Queued()).ShouldBe([1, 3, 4, 5]);
    }

    [RequiresMongoDbFact]
    public async Task Defanged_domain_is_caught()
    {
        using var h = new Harness();
        for (var i = 1; i <= 4; i++) await h.Send(i, "Huge giveaway in our channel, everyone wins, visit spam[.]com now", T0 + i);
        (await h.Queued()).ShouldBe([1, 2, 3, 4]);
    }

    [RequiresMongoDbFact]
    public async Task Editing_the_fourth_message_onto_the_common_text_is_caught()
    {
        using var h = new Harness();
        for (var i = 1; i <= 3; i++) await h.Send(i, Spam, T0 + i);
        await h.Send(4, "An innocent looking message that is long enough to be checked", T0 + 10);
        (await h.Queued()).ShouldBeEmpty();
        await h.Edit(4, Spam, T0 + 20);
        var jobs = await h.Jobs();
        jobs.Select(j => j["message"].AsInt32).ShouldBe([1, 2, 3, 4]);
        jobs.Single(j => j["message"] == 4)["aggregate_version"].AsInt32.ShouldBe(2);
    }

    [RequiresMongoDbFact]
    public async Task Edit_that_keeps_the_text_refences_instead_of_adding_a_second_record()
    {
        using var h = new Harness();
        for (var i = 1; i <= 4; i++) await h.Send(i, Spam, T0 + i);
        await h.Edit(4, Spam + " ", T0 + 10);
        var jobs = await h.Jobs();
        jobs.Count.ShouldBe(4);
        jobs.Single(j => j["message"] == 4)["aggregate_version"].AsInt32.ShouldBe(2);
        await h.Send(5, Spam, T0 + 11);
        (await h.Queued()).ShouldBe([1, 2, 3, 4, 5]);
        (await h.Jobs()).Select(j => j["burst"].AsString).Distinct().Count().ShouldBe(1);
    }

    [RequiresMongoDbFact]
    public async Task Forwards_and_messages_sent_as_a_personal_channel_are_attributed_to_the_sender()
    {
        using var h = new Harness();
        await h.Send(1, Spam, T0 + 1, forward: true);
        await h.Send(2, Spam, T0 + 2, sendAs: new Peer(PeerType.Channel, 1_000_000_999));
        await h.Send(3, Spam, T0 + 3, forward: true);
        await h.Send(4, Spam, T0 + 4, forward: true);
        (await h.Queued()).ShouldBe([1, 2, 3, 4]);
    }

    [RequiresMongoDbFact]
    public async Task Without_DeleteBurst_only_the_triggering_messages_are_queued()
    {
        using var h = new Harness(new AntiSpamOptions { DeleteBurst = false });
        for (var i = 1; i <= 5; i++) await h.Send(i, Spam, T0 + i);
        (await h.Queued()).ShouldBe([4, 5]);
    }

    // ----- must not be caught -----

    [RequiresMongoDbFact]
    public async Task Three_copies_are_not_spam()
    {
        using var h = new Harness();
        for (var i = 1; i <= 3; i++) await h.Send(i, Spam, T0 + i);
        (await h.Queued()).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Four_copies_130_seconds_apart_are_not_spam()
    {
        using var h = new Harness();
        for (var i = 0; i < 4; i++) await h.Send(i + 1, Spam, T0 + i * 130);
        (await h.Queued()).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Different_texts_are_not_spam()
    {
        using var h = new Harness();
        for (var i = 1; i <= 6; i++) await h.Send(i, $"Message number {i} about the meetup, details at https://example.org/{i}", T0 + i);
        (await h.Queued()).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Admins_creator_anonymous_admins_and_bots_are_exempt()
    {
        using var h = new Harness();
        h.ChannelModel.SetupGet(c => c.AdminList).Returns([new ChatAdmin(2_010_001, false, Sender, null!, "")]);
        for (var i = 1; i <= 4; i++) await h.Send(i, Spam, T0 + i);
        for (var i = 11; i <= 14; i++) await h.Send(i, Spam, T0 + i, sender: 2_010_001); // creator
        h.ChannelModel.SetupGet(c => c.AdminList).Returns([]);
        for (var i = 21; i <= 24; i++) await h.Send(i, Spam, T0 + i, sender: 2_010_006, sendAs: new Peer(PeerType.Channel, Channel));
        h.SenderIsBot = true;
        for (var i = 31; i <= 34; i++) await h.Send(i, Spam, T0 + i, sender: 2_010_007);
        (await h.Queued()).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Text_without_a_link_is_not_spam()
    {
        using var h = new Harness();
        for (var i = 1; i <= 6; i++) await h.Send(i, "Good morning everyone, have a wonderful and productive day today!", T0 + i);
        (await h.Queued()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(199, true, false, false)]
    [InlineData(250, false, false, false)]
    [InlineData(250, true, true, false)]
    public async Task Small_disabled_or_broadcast_groups_are_not_checked(int participants, bool antiSpam, bool broadcast, bool _)
    {
        if (EmbeddedMongoServer.LocateMongod() == null) return;
        using var h = new Harness();
        h.ChannelModel.SetupGet(c => c.ParticipantsCount).Returns(participants);
        h.ChannelModel.SetupGet(c => c.AntiSpam).Returns(antiSpam);
        h.ChannelModel.SetupGet(c => c.Broadcast).Returns(broadcast);
        for (var i = 1; i <= 5; i++) await h.Send(i, Spam, T0 + i);
        (await h.Queued()).ShouldBeEmpty();
    }

    [RequiresMongoDbFact]
    public async Task Short_text_is_not_spam()
    {
        using var h = new Harness();
        for (var i = 1; i <= 6; i++) await h.Send(i, "see https://x.io", T0 + i);
        (await h.Queued()).ShouldBeEmpty();
    }

    // ----- recent actions: like Telegram, one delete event per removed message -----

    [RequiresMongoDbFact]
    public async Task Every_deleted_burst_member_gets_its_own_reportable_admin_log_entry()
    {
        using var h = new Harness();
        for (var i = 1; i <= 4; i++) await h.Send(i, Spam, T0 + i);
        var jobs = h.Database.GetCollection<BsonDocument>("antispam_jobs");
        foreach (var job in await h.Jobs())
            await h.Store.ConfirmAsync(Channel, job["message"].AsInt32, Guid.Parse(job["request"].AsString), default);
        await jobs.UpdateManyAsync(FilterDefinition<BsonDocument>.Empty,
            new BsonDocument("$set", new BsonDocument("due", DateTime.UtcNow.AddSeconds(-1))));
        while (await h.Store.ClaimAsync(default) is { } claimed)
            await h.Store.WriteConfirmedLogAsync(claimed, default);
        var log = await h.Database.GetCollection<BsonDocument>("channel_admin_log")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        log.Select(e => e["message_id"].AsInt32).Order().ShouldBe([1, 2, 3, 4]);
        log.Select(e => e["event_id"].ToInt64()).Distinct().Count().ShouldBe(4);
        log.ShouldAllBe(e => e["user_id"].ToInt64() == MyTelegramConsts.AntiSpamBotUserId);
        var reports = new AntiSpamReportStore(h.Database);
        for (var i = 1; i <= 4; i++) (await reports.HasAntiSpamDeletionAsync(Channel, i)).ShouldBeTrue();
        (await reports.HasAntiSpamDeletionAsync(Channel, 5)).ShouldBeFalse();
    }
}
