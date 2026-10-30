using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MyTelegram.ReadModel.Impl;
using MyTelegram.Schema;

namespace MyTelegram.Domain.Tests.UnitTests.Aggregates.Channel;

public class ChannelAntiSpamTests : TestsFor<ChannelAggregate>
{
    private const long CreatorId = 2010001;
    private const long AdminId = 2010002;
    private static readonly long TestChannelId = MyTelegramConsts.ChannelInitId + 1;
    private static RequestInfo Request => RequestInfo.Empty with { UserId = CreatorId };

    public ChannelAntiSpamTests()
    {
        Fixture.Customize<ChannelId>(x => x.FromFactory(() => ChannelId.Create(TestChannelId)));
    }

    private void Create(bool broadcast = false)
    {
        var created = new ChannelCreatedEvent(Request, TestChannelId, CreatorId, broadcast, !broadcast,
            "Anti-spam test", null, null, null, 123, 1, 1,
            new TMessageActionChatCreate { Title = "Anti-spam test", Users = [] },
            null, false, null, null, null, false, false, [], [], false, false, []);
        Sut.ApplyEvents([ADomainEvent<ChannelAggregate, ChannelId, ChannelCreatedEvent>(created, 1)]);
    }

    [Fact]
    public void Missing_channel_is_rejected()
    {
        Assert.Throws<DomainError>(() => Sut.ToggleAntiSpam(Request, true));
    }

    [Fact]
    public void Creator_can_enable_and_disable()
    {
        Create();
        Sut.ToggleAntiSpam(Request, true);
        var enabled = Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<ChannelAntiSpamChangedEvent>();
        enabled.Enabled.ShouldBeTrue();
        enabled.ChannelId.ShouldBe(TestChannelId);
        enabled.RequestInfo.ShouldBe(Request);

        Sut.ToggleAntiSpam(Request, false);
        Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<ChannelAntiSpamChangedEvent>().Enabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unchanged_value_is_rejected(bool enabled)
    {
        Create();
        if (enabled) Sut.ToggleAntiSpam(Request, true);
        var before = Sut.UncommittedEvents.Count();
        Assert.Throws<RpcException>(() => Sut.ToggleAntiSpam(Request, enabled))
            .RpcError.ShouldBe(RpcErrors.RpcErrors400.ChatNotModified);
        Sut.UncommittedEvents.Count().ShouldBe(before);
    }

    [Fact]
    public void Broadcast_is_rejected()
    {
        Create(true);
        Assert.Throws<RpcException>(() => Sut.ToggleAntiSpam(Request, true))
            .RpcError.ShouldBe(RpcErrors.RpcErrors400.MegagroupRequired);
    }

    [Fact]
    public void Ordinary_member_is_rejected()
    {
        Create();
        Assert.Throws<RpcException>(() => Sut.ToggleAntiSpam(Request with { UserId = AdminId }, true))
            .RpcError.ShouldBe(RpcErrors.RpcErrors400.ChatAdminRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Admin_requires_delete_messages(bool canDelete)
    {
        Create();
        // DeleteMessages is bit 3; unrelated ChangeInfo alone must not authorize the toggle.
        var rights = new ChatAdminRights(canDelete ? 1 << 3 : 1);
        Sut.EditChannelAdmin(Request, CreatorId, true, AdminId, false, true, rights, "", 1, false);
        var request = Request with { UserId = AdminId };
        if (canDelete)
        {
            Sut.ToggleAntiSpam(request, true);
            Sut.UncommittedEvents.Last().AggregateEvent.ShouldBeOfType<ChannelAntiSpamChangedEvent>().Enabled.ShouldBeTrue();
        }
        else
        {
            Assert.Throws<RpcException>(() => Sut.ToggleAntiSpam(request, true))
                .RpcError.ShouldBe(RpcErrors.RpcErrors400.ChatAdminRequired);
        }
    }

    [Fact]
    public async Task Event_roundtrip_and_all_appliers_preserve_both_values()
    {
        var state = new ChannelState();
        var channel = new ChannelReadModel();
        var full = new ChannelFullReadModel();
        foreach (var enabled in new[] { true, false })
        {
            var original = new ChannelAntiSpamChangedEvent(Request, TestChannelId, enabled);
            var restored = JsonSerializer.Deserialize<ChannelAntiSpamChangedEvent>(JsonSerializer.Serialize(original))!;
            restored.ChannelId.ShouldBe(TestChannelId);
            restored.RequestInfo.UserId.ShouldBe(CreatorId);
            state.Apply(restored);
            var domainEvent = (IDomainEvent<ChannelAggregate, ChannelId, ChannelAntiSpamChangedEvent>)
                ADomainEvent<ChannelAggregate, ChannelId, ChannelAntiSpamChangedEvent>(restored, 2);
            await channel.ApplyAsync(null!, domainEvent, CancellationToken.None);
            await full.ApplyAsync(null!, domainEvent, CancellationToken.None);
            state.AntiSpam.ShouldBe(enabled);
            channel.AntiSpam.ShouldBe(enabled);
            full.AntiSpam.ShouldBe(enabled);
        }
    }

    [Fact]
    public void Existing_gigagroup_bson_and_legacy_defaults_deserialize()
    {
        var channel = BsonSerializer.Deserialize<ChannelReadModel>(new BsonDocument
        {
            { "Gigagroup", true },
            { "AntiSpam", true }
        });
        channel.Gigagroup.ShouldBeTrue();
        channel.AntiSpam.ShouldBeTrue();
        var legacy = BsonSerializer.Deserialize<ChannelReadModel>(new BsonDocument());
        legacy.Gigagroup.ShouldBeFalse();
        legacy.AntiSpam.ShouldBeFalse();
    }

    [Fact]
    public async Task Snapshot_preserves_enabled_and_old_snapshots_default_to_disabled()
    {
        Create();
        Sut.ToggleAntiSpam(Request, true);
        var method = typeof(ChannelAggregate).GetMethod("CreateSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshot = await (Task<ChannelSnapshot>)method.Invoke(Sut, [CancellationToken.None])!;
        snapshot.AntiSpam.ShouldBeTrue();
        var json = JsonSerializer.Serialize(snapshot);
        var restored = JsonSerializer.Deserialize<ChannelSnapshot>(json)!;
        var state = new ChannelState();
        state.LoadFromSnapshot(restored);
        state.AntiSpam.ShouldBeTrue();

        var legacy = JsonNode.Parse(json)!.AsObject();
        legacy.Remove(nameof(ChannelSnapshot.AntiSpam));
        state.LoadFromSnapshot(JsonSerializer.Deserialize<ChannelSnapshot>(legacy.ToJsonString())!);
        state.AntiSpam.ShouldBeFalse();
    }
}
