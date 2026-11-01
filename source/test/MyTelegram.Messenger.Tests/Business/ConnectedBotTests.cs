using MongoDB.Bson;
using MyTelegram.Messenger.Handlers;
using MyTelegram.Messenger.Services.Business;
using MyTelegram.Schema;
using MyTelegram.Schema.Account;
using MyTelegram.Schema.Messages;
using MyTelegram.Schema.Payments;

namespace MyTelegram.Messenger.Tests.Business;

/// <summary>
/// Feature: <a href="https://corefork.telegram.org/api/bots/connected-business-bots">connected business bots</a>.
///
/// <para>
/// <c>invokeWithBusinessConnection</c> runs the inner request as the business account, so the set of wrappable
/// methods is closed and every one is gated on its own <c>businessBotRights</c> flag.
/// </para>
/// </summary>
public class ConnectedBotTests
{
    private static readonly BusinessBotRightsItem None = BusinessBotRightsItem.None;

    [Fact]
    public void Sending_needs_reply_and_is_limited_to_chats_the_partner_wrote_in()
    {
        var rule = BusinessConnectionRightsMap.GetRule(new RequestSendMessage { Peer = new TInputPeerUser() })!;

        rule.IsAllowed(None).ShouldBeFalse();
        rule.IsAllowed(None with { Reply = true }).ShouldBeTrue();
        rule.ChatPeer.ShouldNotBeNull();
        rule.NeedsRecentIncomingMessage.ShouldBeTrue();
    }

    [Fact]
    public void Profile_edits_need_the_right_for_each_field_they_change()
    {
        var name = BusinessConnectionRightsMap.GetRule(new RequestUpdateProfile { FirstName = "a" })!;
        var bio = BusinessConnectionRightsMap.GetRule(new RequestUpdateProfile { About = "b" })!;

        name.IsAllowed(None with { EditBio = true }).ShouldBeFalse();
        name.IsAllowed(None with { EditName = true }).ShouldBeTrue();
        bio.IsAllowed(None with { EditName = true }).ShouldBeFalse();
        bio.IsAllowed(None with { EditBio = true }).ShouldBeTrue();
    }

    [Fact]
    public void Methods_outside_the_documented_list_are_not_wrappable()
    {
        BusinessConnectionRightsMap.GetRule(new RequestGetHistory()).ShouldBeNull();
        BusinessConnectionRightsMap.GetRule(new RequestGetDialogs()).ShouldBeNull();
        BusinessConnectionRightsMap.GetRule(new RequestGetStarsStatus { Peer = new TInputPeerSelf() })!
            .IsAllowed(None with { ViewGifts = true }).ShouldBeTrue();
    }

    [Fact]
    public void Reads_connections_stored_by_older_code_with_null_lists()
    {
        var document = new BsonDocument
        {
            ["UserId"] = 1L,
            ["BotId"] = 2L,
            ["ConnectionId"] = "abc",
            ["Recipients"] = new BsonDocument
            {
                ["ExistingChats"] = false, ["NewChats"] = false, ["Contacts"] = false, ["NonContacts"] = false,
                ["ExcludeSelected"] = true, ["Users"] = BsonNull.Value, ["ExcludeUsers"] = BsonNull.Value
            },
            ["Rights"] = new BsonDocument { ["_t"] = "TBusinessBotRights", ["Reply"] = true },
            ["CreatedAt"] = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc)
        };

        var connection = BusinessConnectionStore.Read(document);

        connection.ExcludeUserIds.ShouldBeEmpty();
        connection.Recipients.UserIds.ShouldBeEmpty();
        connection.Recipients.ExcludeSelected.ShouldBeTrue();
        connection.Rights.Reply.ShouldBeTrue();
        connection.Rights.ReadMessages.ShouldBeFalse();
        connection.Date.ShouldBeGreaterThan(0);
    }
}
