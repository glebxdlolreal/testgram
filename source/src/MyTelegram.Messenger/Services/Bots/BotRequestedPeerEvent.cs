using System.Buffers;
using System.Buffers.Binary;

namespace MyTelegram.Messenger.Services.Bots;

/// <summary>
/// Chats a user picked with a bot's <c>keyboardButtonRequestPeer</c>, validated by the query server
/// (which answers <c>messages.sendBotRequestedPeer</c>) and handed to the command server, which posts
/// the <c>messageActionRequestedPeer</c> service message and lets an in-process bot answer. Message ids
/// come from a per-process HiLo block, so only the command server may send messages.
/// </summary>
public sealed record BotRequestedPeerEvent(long UserId, long PermAuthKeyId, int Layer, long BotUserId, int MsgId, int ButtonId,
    IReadOnlyList<Peer> Peers);

/// <inheritdoc />
public sealed class BotRequestedPeerEventDataSerializer : IEventDataSerializer<BotRequestedPeerEvent>, ITransientDependency
{
    private const int HeaderSize = 8 + 8 + 4 + 8 + 4 + 4 + 4;
    private const int PeerSize = 4 + 8;

    public void Serialize(IBufferWriter<byte> writer, BotRequestedPeerEvent data)
    {
        var size = HeaderSize + data.Peers.Count * PeerSize;
        var span = writer.GetSpan(size);
        BinaryPrimitives.WriteInt64LittleEndian(span, data.UserId);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], data.PermAuthKeyId);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], data.Layer);
        BinaryPrimitives.WriteInt64LittleEndian(span[20..], data.BotUserId);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], data.MsgId);
        BinaryPrimitives.WriteInt32LittleEndian(span[32..], data.ButtonId);
        BinaryPrimitives.WriteInt32LittleEndian(span[36..], data.Peers.Count);
        var offset = HeaderSize;
        foreach (var peer in data.Peers)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], (int)peer.PeerType);
            BinaryPrimitives.WriteInt64LittleEndian(span[(offset + 4)..], peer.PeerId);
            offset += PeerSize;
        }

        writer.Advance(size);
    }

    public BotRequestedPeerEvent Deserialize(ReadOnlyMemory<byte> buffer)
    {
        var span = buffer.Span;
        var count = BinaryPrimitives.ReadInt32LittleEndian(span[36..]);
        var peers = new List<Peer>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderSize + i * PeerSize;
            peers.Add(new Peer((PeerType)BinaryPrimitives.ReadInt32LittleEndian(span[offset..]),
                BinaryPrimitives.ReadInt64LittleEndian(span[(offset + 4)..])));
        }

        return new BotRequestedPeerEvent(
            BinaryPrimitives.ReadInt64LittleEndian(span),
            BinaryPrimitives.ReadInt64LittleEndian(span[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[20..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[28..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[32..]),
            peers);
    }

    public object Deserialize(Type type, ReadOnlyMemory<byte> buffer) => Deserialize(buffer);
}
