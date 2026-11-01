namespace MyTelegram.Domain.Aggregates.Messaging;

/// <summary>
/// One aggregate per user holds every quick reply shortcut of that user, so the name uniqueness and the
/// <c>quick_replies_limit</c> check stay consistent under optimistic concurrency.
/// </summary>
[JsonConverter(typeof(SystemTextJsonSingleValueObjectConverter<QuickReplyListId>))]
public class QuickReplyListId(string value) : Identity<QuickReplyListId>(value)
{
    public static QuickReplyListId Create(long ownerUserId)
    {
        return NewDeterministic(GuidFactories.Deterministic.Namespaces.Commands, $"quickreplies_{ownerUserId}");
    }
}
