using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// A press on one of @PremiumBot's buttons, handed from the query server (where
/// <c>messages.getBotCallbackAnswer</c> is answered) to the command server, which sends the bot's
/// reply. Message ids for an account come from a per-process HiLo block, so messages sent from both
/// processes collide; only the command server sends them.
/// </summary>
public sealed record PremiumBotCallbackRequestedEvent(long UserId, long PermAuthKeyId, int Layer, string Data);

/// <inheritdoc />
public sealed class PremiumBotCallbackRequestedEventDataSerializer
    : IEventDataSerializer<PremiumBotCallbackRequestedEvent>, ITransientDependency
{
    public void Serialize(IBufferWriter<byte> writer, PremiumBotCallbackRequestedEvent data)
    {
        var text = Encoding.UTF8.GetBytes(data.Data);
        var span = writer.GetSpan(24 + text.Length);
        BinaryPrimitives.WriteInt64LittleEndian(span, data.UserId);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], data.PermAuthKeyId);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], data.Layer);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], text.Length);
        text.CopyTo(span[24..]);
        writer.Advance(24 + text.Length);
    }

    public PremiumBotCallbackRequestedEvent Deserialize(ReadOnlyMemory<byte> buffer)
    {
        var span = buffer.Span;
        var length = BinaryPrimitives.ReadInt32LittleEndian(span[20..]);
        return new PremiumBotCallbackRequestedEvent(
            BinaryPrimitives.ReadInt64LittleEndian(span),
            BinaryPrimitives.ReadInt64LittleEndian(span[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[16..]),
            Encoding.UTF8.GetString(span.Slice(24, length)));
    }

    public object Deserialize(Type type, ReadOnlyMemory<byte> buffer) => Deserialize(buffer);
}
