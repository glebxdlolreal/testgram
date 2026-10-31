using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// An @PremiumBot invoice whose card charge has gone through, handed from the query server (where
/// <c>payments.sendPaymentForm</c> is answered) to the command server, which delivers what was bought
/// and sends the receipt messages. Message ids come from a per-process HiLo block, so messages sent
/// from the query server collide with the command server's; only the command server sends them.
/// </summary>
public sealed record PremiumBotPaymentSettledEvent(
    long UserId,
    long PermAuthKeyId,
    int Layer,
    string StoredInvoiceId,
    string PaymentIntentId,
    string StripeCustomerId,
    string StripePaymentMethodId);

/// <summary>Asks the command server to send the 777000 "Thank you for subscribing" note, for the same reason.</summary>
public sealed record PremiumWelcomeRequestedEvent(long UserId);

/// <inheritdoc />
public sealed class PremiumBotPaymentSettledEventDataSerializer
    : IEventDataSerializer<PremiumBotPaymentSettledEvent>, ITransientDependency
{
    public void Serialize(IBufferWriter<byte> writer, PremiumBotPaymentSettledEvent data)
    {
        string[] texts = [data.StoredInvoiceId, data.PaymentIntentId, data.StripeCustomerId, data.StripePaymentMethodId];
        var encoded = texts.Select(Encoding.UTF8.GetBytes).ToArray();
        var size = 20 + encoded.Sum(e => 4 + e.Length);
        var span = writer.GetSpan(size);
        BinaryPrimitives.WriteInt64LittleEndian(span, data.UserId);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], data.PermAuthKeyId);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], data.Layer);
        var offset = 20;
        foreach (var bytes in encoded)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], bytes.Length);
            bytes.CopyTo(span[(offset + 4)..]);
            offset += 4 + bytes.Length;
        }

        writer.Advance(size);
    }

    public PremiumBotPaymentSettledEvent Deserialize(ReadOnlyMemory<byte> buffer)
    {
        var span = buffer.Span;
        var offset = 20;
        var texts = new string[4];
        for (var i = 0; i < texts.Length; i++)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(span[offset..]);
            texts[i] = Encoding.UTF8.GetString(span.Slice(offset + 4, length));
            offset += 4 + length;
        }

        return new PremiumBotPaymentSettledEvent(
            BinaryPrimitives.ReadInt64LittleEndian(span),
            BinaryPrimitives.ReadInt64LittleEndian(span[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[16..]),
            texts[0], texts[1], texts[2], texts[3]);
    }

    public object Deserialize(Type type, ReadOnlyMemory<byte> buffer) => Deserialize(buffer);
}

/// <inheritdoc />
public sealed class PremiumWelcomeRequestedEventDataSerializer
    : IEventDataSerializer<PremiumWelcomeRequestedEvent>, ITransientDependency
{
    public void Serialize(IBufferWriter<byte> writer, PremiumWelcomeRequestedEvent data)
    {
        BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(8), data.UserId);
        writer.Advance(8);
    }

    public PremiumWelcomeRequestedEvent Deserialize(ReadOnlyMemory<byte> buffer) =>
        new(BinaryPrimitives.ReadInt64LittleEndian(buffer.Span));

    public object Deserialize(Type type, ReadOnlyMemory<byte> buffer) => Deserialize(buffer);
}
