// Feature: push-updates, Property 10: A text message in a private chat yields MESSAGE_TEXT.
//
// For any text message in a personal (user-to-user) chat — non-empty/non-whitespace text, no media,
// not a service message and with MessageActionType.None — the payload builder
// (MessagePushDataBuilder.BuildForPersonalMessageAsync) must set loc_key = MESSAGE_TEXT and
// loc_args = [sender_display_name, message_text]. The sender display name is resolved through the
// injected IUserAppService, so the test drives the production builder with a deterministic stub user
// service (FirstName only => display name == FirstName) and asserts the resulting PushData.
//
// Validates: Requirements 4.2

using FsCheck;
using FsCheck.Xunit;
using MyTelegram.Core;
using MyTelegram.Messenger.QueryServer.DomainEventHandlers;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Push.Tests.Infrastructure;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using Shouldly;

namespace MyTelegram.Push.Tests;

public class Property10_PersonalTextMessageTests
{
    // Property 10: A text message in a private chat yields MESSAGE_TEXT
    // Validates: Requirements 4.2
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Property10Arbitraries) })]
    public void Personal_text_message_yields_MESSAGE_TEXT_with_sender_and_text(PersonalTextMessageCase testCase)
    {
        // Arrange: a deterministic user service so the sender's display name is known and stable.
        var userService = new StubUserAppService();
        var builder = new MessagePushDataBuilder(userService);
        var item = testCase.Item;

        // Act
        var pushData = builder.BuildForPersonalMessageAsync(item).GetAwaiter().GetResult();

        // Assert: a text-only, non-service personal message is always pushed.
        pushData.ShouldNotBeNull();

        var expectedSenderName = StubUserAppService.DisplayNameFor(item.SenderUserId);

        pushData!.LocKey.ShouldBe(PushNotificationTypes.MessageText);
        pushData.LocArgs.ShouldBe(new[] { expectedSenderName, item.Message });
    }
}

/// <summary>A text-only, personal (user-to-user) message fixture for Property 10.</summary>
public sealed record PersonalTextMessageCase(MessageItem Item)
{
    public override string ToString() =>
        $"PersonalText(sender={Item.SenderUserId}, msgId={Item.MessageId}, text='{Item.Message}')";
}

/// <summary>
/// FsCheck arbitrary surface for Property 10. Reuses the Task-1 primitive generators
/// (<see cref="PushGen.PooledUserId"/>, <see cref="PushGen.PositiveId"/>, <see cref="PushGen.NonEmptyToken"/>)
/// to build a text-only personal <see cref="MessageItem"/>: non-empty/non-whitespace <c>Message</c>,
/// <c>Media == null</c>, a non-service <c>SendMessageType</c> and <c>MessageActionType.None</c>.
/// </summary>
public static class Property10Arbitraries
{
    public static Arbitrary<PersonalTextMessageCase> PersonalTextMessageCase() =>
        Arb.From(PersonalTextMessageGen);

    private static Gen<PersonalTextMessageCase> PersonalTextMessageGen =>
        from senderId in PushGen.PooledUserId
        from ownerId in PushGen.PooledUserId
        from msgId in Gen.Choose(1, 100_000)
        from randomId in PushGen.PositiveId
        from text in NonWhitespaceText
        select new PersonalTextMessageCase(new MessageItem(
            OwnerPeer: new Peer(PeerType.User, ownerId),
            ToPeer: new Peer(PeerType.User, ownerId),
            SenderPeer: new Peer(PeerType.User, senderId),
            SenderUserId: senderId,
            MessageId: msgId,
            Message: text,
            Date: 1_700_000_000,
            RandomId: randomId,
            IsOut: false,
            SendMessageType: SendMessageType.Text,
            MessageType: MessageType.Text,
            MessageActionType: MessageActionType.None,
            Media: null));

    /// <summary>Non-empty, non-whitespace message text (so the builder resolves MESSAGE_TEXT, not MESSAGE_NOTEXT).</summary>
    private static Gen<string> NonWhitespaceText =>
        Gen.OneOf(
            Gen.Elements("hi", "hello world", "сообщение", "длинный текст здесь", "👍 emoji body", "a"),
            PushGen.NonEmptyToken);
}

/// <summary>
/// Deterministic <see cref="IUserAppService"/> stub returning a user whose only populated name field
/// is <c>FirstName</c>, so the builder's display-name resolution yields exactly that first name.
/// </summary>
internal sealed class StubUserAppService : IUserAppService
{
    public static string DisplayNameFor(long userId) => $"Sender-{userId}";

    public Task<IUserReadModel?> GetAsync(long? id) =>
        Task.FromResult<IUserReadModel?>(id is null ? null : new StubUserReadModel(id.Value));

    public Task<IUserReadModel> GetAsync(long id) =>
        Task.FromResult<IUserReadModel>(new StubUserReadModel(id));

    public Task<IReadOnlyCollection<IUserReadModel>> GetListAsync(IEnumerable<long> ids) =>
        Task.FromResult<IReadOnlyCollection<IUserReadModel>>(
            ids.Select(i => (IUserReadModel)new StubUserReadModel(i)).ToList());

    public Task CheckAccountPremiumStatusAsync(long userId) => Task.CompletedTask;

    public Task<IUserFullReadModel?> GetUserFullAsync(long userId) =>
        Task.FromResult<IUserFullReadModel?>(null);

    public void InvalidateCache(long userId)
    {
    }
}

/// <summary>Minimal <see cref="IUserReadModel"/> with only <c>FirstName</c>/<c>UserId</c> populated.</summary>
internal sealed class StubUserReadModel : IUserReadModel
{
    public StubUserReadModel(long userId)
    {
        UserId = userId;
        Id = userId.ToString();
        FirstName = StubUserAppService.DisplayNameFor(userId);
    }

    public string? About => null;
    public long AccessHash => 0;
    public int AccountTtl => 0;
    public bool Bot => false;
    public int? BotInfoVersion => null;
    public string FirstName { get; }
    public bool HasPassword => false;
    public string Id { get; }
    public bool IsOnline => false;
    public string? LastName => null;
    public DateTime LastUpdateDate => default;
    public string PhoneNumber => string.Empty;
    public int? PinnedMsgId => null;
    public List<int> PinnedMsgIdList => new();
    public bool SensitiveCanChange => false;
    public bool SensitiveEnabled => false;
    public bool ShowContactSignUpNotification => false;
    public bool Fake => false;
    public bool Scam => false;
    public bool Support => false;
    public long UserId { get; }
    public string? UserName => null;
    public bool Verified => false;
    public bool Premium => false;
    public string? Email => null;
    public long? EmojiStatusDocumentId => null;
    public int? EmojiStatusValidUntil => null;
    public long? EmojiStatusCollectibleId => null;
    public List<long> RecentEmojiStatuses => new();
    public VideoSizeEmojiMarkup? VideoEmojiMarkup => null;
    public long? ProfilePhotoId => null;
    public long? PersonalPhotoId => null;
    public long? FallbackPhotoId => null;
    public PeerColor? Color => null;
    public PeerColor? ProfileColor => null;
    public GlobalPrivacySettings? GlobalPrivacySettings => null;
    public long? PersonalChannelId => null;
    public Birthday? Birthday => null;
    public bool BotHasMainApp => false;
    public int? BotActiveUsers => null;
    public List<UsernameInfo>? Usernames => null;
    public DateTime? CreationTime => null;
    public int? ProfilePhotoUpdateDate => null;
    public int? UserNameUpdateDate => null;
    public bool? IsDeleted => null;
    public BusinessWorkHoursItem? BusinessWorkHoursSettings => null;
    public BusinessLocationItem? BusinessLocationSettings => null;
    public BusinessGreetingItem? BusinessGreetingSettings => null;
    public BusinessAwayItem? BusinessAwaySettings => null;
    public BusinessIntroItem? BusinessIntroSettings => null;
    public bool BotBusiness => false;
    public bool BotNochats => false;
    public string? BotInlinePlaceholder => null;
    public bool BotPrivacyMode => false;
    public int? DefaultHistoryTTL => null;
    public string? MainProfileTab => null;
}
