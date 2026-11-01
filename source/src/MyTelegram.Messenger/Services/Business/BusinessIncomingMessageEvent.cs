using System.Buffers;
using System.Buffers.Binary;

namespace MyTelegram.Messenger.Services.Business;

/// <summary>
/// A private message reached a business account that has a greeting or away message set. Raised by the
/// query server, where received messages are observed, and handled by the command server, which is the
/// process that sends messages (see <see cref="Premium.PremiumBotCallbackRequestedEvent"/>).
/// See https://corefork.telegram.org/api/business#greeting-messages
/// </summary>
public sealed record BusinessIncomingMessageEvent(long BusinessUserId, long PeerUserId, int MessageId, int Date);

/// <inheritdoc />
public sealed class BusinessIncomingMessageEventDataSerializer
    : IEventDataSerializer<BusinessIncomingMessageEvent>, ITransientDependency
{
    public void Serialize(IBufferWriter<byte> writer, BusinessIncomingMessageEvent data)
    {
        var span = writer.GetSpan(24);
        BinaryPrimitives.WriteInt64LittleEndian(span, data.BusinessUserId);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], data.PeerUserId);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], data.MessageId);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], data.Date);
        writer.Advance(24);
    }

    public BusinessIncomingMessageEvent Deserialize(ReadOnlyMemory<byte> buffer)
    {
        var span = buffer.Span;
        return new BusinessIncomingMessageEvent(
            BinaryPrimitives.ReadInt64LittleEndian(span),
            BinaryPrimitives.ReadInt64LittleEndian(span[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[20..]));
    }

    public object Deserialize(Type type, ReadOnlyMemory<byte> buffer) => Deserialize(buffer);
}
