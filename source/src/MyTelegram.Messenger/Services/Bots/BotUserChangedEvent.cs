using System.Buffers;
using System.Buffers.Binary;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// A BotFather bot's user read model was written directly (its name, flags, photo...). Those bots have no
/// aggregate, so no domain event tells the other server; this one does, and every server drops its cached
/// copy. Without it the query server kept serving the old user until restart.
/// </summary>
public sealed record BotUserChangedEvent(long BotUserId);

/// <inheritdoc />
public sealed class BotUserChangedEventDataSerializer : IEventDataSerializer<BotUserChangedEvent>, ITransientDependency
{
    public void Serialize(IBufferWriter<byte> writer, BotUserChangedEvent data)
    {
        var span = writer.GetSpan(8);
        BinaryPrimitives.WriteInt64LittleEndian(span, data.BotUserId);
        writer.Advance(8);
    }

    public BotUserChangedEvent Deserialize(ReadOnlyMemory<byte> buffer) =>
        new(BinaryPrimitives.ReadInt64LittleEndian(buffer.Span));

    public object Deserialize(Type type, ReadOnlyMemory<byte> buffer) => Deserialize(buffer);
}

/// <summary>Drops this process's cached copy of a changed bot user. Subscribed by both servers.</summary>
public sealed class BotUserChangedEventHandler(IUserAppService userAppService)
    : IEventHandler<BotUserChangedEvent>, ITransientDependency
{
    public Task HandleEventAsync(BotUserChangedEvent eventData)
    {
        userAppService.InvalidateCache(eventData.BotUserId);
        return Task.CompletedTask;
    }
}
