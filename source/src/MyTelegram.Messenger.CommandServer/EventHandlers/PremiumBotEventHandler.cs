using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.CommandServer.EventHandlers;

/// <summary>Sends @PremiumBot's answer to a button press the query server accepted.</summary>
public class PremiumBotEventHandler(IPremiumBotService premiumBotService)
    : IEventHandler<PremiumBotCallbackRequestedEvent>, ITransientDependency
{
    public Task HandleEventAsync(PremiumBotCallbackRequestedEvent eventData)
    {
        var input = new RequestInput(string.Empty, ConnectionType.UnKnown, Guid.NewGuid(), 0, 0, 0, eventData.UserId,
            eventData.PermAuthKeyId, eventData.PermAuthKeyId, eventData.Layer, DateTime.UtcNow.ToTimestamp(),
            DeviceType.Unknown, string.Empty, 0, 0);
        return premiumBotService.HandleCallbackAsync(input, eventData.Data);
    }
}
