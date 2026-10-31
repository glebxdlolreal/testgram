using EventFlow.Aggregates;
using EventFlow.ReadStores;
using Moq;
using MyTelegram.Domain.Aggregates.Channel;
using MyTelegram.Domain.Aggregates.User;
using MyTelegram.ReadModel.Impl;

namespace MyTelegram.Messenger.Tests.Fragment;

/// <summary>
/// Feature: the usernames vector in the user and channel read models.
/// <para>
/// <c>account.updateUsername</c> and <c>channels.updateUsername</c> used to change only the legacy
/// field, while the converters read the vector once a collectible was attached: everyone kept seeing
/// the previous username, and clearing a channel's public link left the old one active, so the chat
/// never became private.
/// </para>
/// </summary>
public class UsernameReadModelProjectionTests
{
    private static readonly List<UsernameItem> Vector =
    [
        new("nft", false, true),
        new("basic", true, true),
        new("hidden", false, false)
    ];

    [Fact]
    public async Task The_user_vector_comes_from_the_event()
    {
        var readModel = new UserReadModel();

        await ApplyAsync(readModel, new UserUsernamesChangedEvent(RequestInfo.Empty, 1, Vector, null));

        readModel.Usernames!.Select(p => (p.Username, p.Editable, p.Active))
            .ShouldBe([("nft", false, true), ("basic", true, true), ("hidden", false, false)]);
    }

    [Fact]
    public async Task A_new_basic_username_replaces_the_editable_entry_of_the_user()
    {
        var readModel = new UserReadModel();
        await ApplyAsync(readModel, new UserUsernamesChangedEvent(RequestInfo.Empty, 1, Vector, null));

        await ApplyAsync(readModel, new UserNameUpdatedEvent(RequestInfo.Empty,
            new UserItem(1, 0, "", "First", null, "renamed"), "basic", 0));

        readModel.UserName.ShouldBe("renamed");
        readModel.Usernames!.Select(p => p.Username).ShouldBe(["nft", "renamed", "hidden"]);
    }

    [Fact]
    public async Task A_user_without_a_vector_keeps_none()
    {
        var readModel = new UserReadModel();

        await ApplyAsync(readModel, new UserNameUpdatedEvent(RequestInfo.Empty,
            new UserItem(1, 0, "", "First", null, "renamed"), null, 0));

        readModel.UserName.ShouldBe("renamed");
        readModel.Usernames.ShouldBeNull();
    }

    [Fact]
    public async Task Clearing_the_channel_link_drops_the_editable_entry()
    {
        var readModel = new ChannelReadModel();
        await ApplyAsync(readModel, new ChannelUsernamesChangedEvent(RequestInfo.Empty, 1, Vector, null));

        await ApplyAsync(readModel, new ChannelUserNameChangedEvent(RequestInfo.Empty, 1, string.Empty, "basic"));

        readModel.Usernames!.Select(p => p.Username).ShouldBe(["nft", "hidden"]);
    }

    private static Task ApplyAsync<TEvent>(UserReadModel readModel, TEvent aggregateEvent)
        where TEvent : IAggregateEvent<UserAggregate, UserId>
    {
        var domainEvent = new DomainEvent<UserAggregate, UserId, TEvent>(aggregateEvent, global::EventFlow.Aggregates.Metadata.Empty,
            DateTimeOffset.UtcNow, UserId.Create(1), 1);

        return ((IAmReadModelFor<UserAggregate, UserId, TEvent>)readModel).ApplyAsync(
            Mock.Of<IReadModelContext>(), domainEvent, CancellationToken.None);
    }

    private static Task ApplyAsync<TEvent>(ChannelReadModel readModel, TEvent aggregateEvent)
        where TEvent : IAggregateEvent<ChannelAggregate, ChannelId>
    {
        var domainEvent = new DomainEvent<ChannelAggregate, ChannelId, TEvent>(aggregateEvent,
            global::EventFlow.Aggregates.Metadata.Empty, DateTimeOffset.UtcNow, ChannelId.Create(1), 1);

        return ((IAmReadModelFor<ChannelAggregate, ChannelId, TEvent>)readModel).ApplyAsync(
            Mock.Of<IReadModelContext>(), domainEvent, CancellationToken.None);
    }
}
