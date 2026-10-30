using System.Buffers;
using MyTelegram.Core;
using MyTelegram.Schema.Extensions;
using MyTelegram.Services.Serializers;
using Shouldly;
using Xunit;

namespace MyTelegram.Push.Tests;

/// <summary>
/// The external session-server binary (no source here) deserializes LayeredPushMessageCreatedIntegrationEvent
/// with the PushData layout it was built with. A field added inside PushData shifted every later field for
/// it: the ExcludeUserIds length came from the wrong bytes and it threw OutOfMemoryException on every
/// message push. New fields therefore travel after everything that binary reads.
/// </summary>
public class PushDataWireCompatibilityTests
{
    private static LayeredPushMessageCreatedIntegrationEvent Event(int? reportDeliveryUntilDate) => new(
        PeerType.Channel, 800000000001, new byte[] { 1, 2, 3 }, 11, 12, null, null, 363118, null, 9790080,
        new PushData("CHANNEL_MESSAGE_TEXT", ["Chat", "spam"], 2010001,
            new PushNotificationCustomData { ChannelId = 800000000001, MsgId = 10734, ReportDeliveryUntilDate = reportDeliveryUntilDate },
            "default"),
        [2077001L, 2010001L]);

    private static ReadOnlyMemory<byte> Serialize(LayeredPushMessageCreatedIntegrationEvent e)
    {
        var writer = new ArrayBufferWriter<byte>();
        new LayeredPushMessageCreatedIntegrationEventEventDataSerializer().Serialize(writer, e);
        return writer.WrittenMemory;
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1793392616)]
    public void The_session_server_layout_still_reads_every_field(int? report)
    {
        var buffer = Serialize(Event(report));
        // Field order of the session-server's reader: everything up to and including ExcludeUserIds.
        buffer.ReadByte(); buffer.ReadInt64(); buffer.ReadMemory();
        buffer.ReadNullableInt64(); buffer.ReadNullableInt64(); buffer.ReadNullableInt64(); buffer.ReadNullableInt64();
        buffer.ReadInt32(); buffer.ReadNullableInt32(); buffer.ReadInt64();
        var pushData = buffer.ReadPushData()!;
        var excludeUserIds = buffer.ReadList();

        pushData.Sound.ShouldBe("default");
        pushData.Custom!.MsgId.ShouldBe(10734);
        excludeUserIds.ShouldBe([2077001L, 2010001L]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1793392616)]
    public void Our_reader_recovers_the_trailing_fields(int? report)
    {
        var restored = new LayeredPushMessageCreatedIntegrationEventEventDataSerializer().Deserialize(Serialize(Event(report)));
        restored.PushData!.Custom!.ReportDeliveryUntilDate.ShouldBe(report);
        restored.ExcludeUserIds.ShouldBe([2077001L, 2010001L]);
    }
}
