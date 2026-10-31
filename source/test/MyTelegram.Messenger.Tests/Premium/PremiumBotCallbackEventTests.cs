using CommunityToolkit.HighPerformance.Buffers;
using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.Tests.Premium;

public class PremiumBotCallbackEventTests
{
    [Fact]
    public void The_button_press_survives_the_trip_to_the_command_server()
    {
        var serializer = new PremiumBotCallbackRequestedEventDataSerializer();
        var sent = new PremiumBotCallbackRequestedEvent(2010001, -4104433873442056260, 224, "init/24M/Ж11");
        using var writer = new ArrayPoolBufferWriter<byte>();

        serializer.Serialize(writer, sent);

        serializer.Deserialize(writer.WrittenMemory).ShouldBe(sent);
    }
}
