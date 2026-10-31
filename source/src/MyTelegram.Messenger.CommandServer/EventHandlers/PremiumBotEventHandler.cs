using MyTelegram.Messenger.Services.Premium;

namespace MyTelegram.Messenger.CommandServer.EventHandlers;

/// <summary>
/// Sends @PremiumBot's answers to what the query server accepted: a button press, a paid invoice, and
/// the 777000 welcome note. Only this process allocates message ids, so only it may send messages.
/// </summary>
public class PremiumBotEventHandler(IPremiumBotService premiumBotService, IPremiumWelcomeNotifier premiumWelcomeNotifier)
    : IEventHandler<PremiumBotCallbackRequestedEvent>,
        IEventHandler<PremiumBotPaymentSettledEvent>,
        IEventHandler<PremiumWelcomeRequestedEvent>,
        ITransientDependency
{
    public Task HandleEventAsync(PremiumBotCallbackRequestedEvent eventData) =>
        premiumBotService.HandleCallbackAsync(Input(eventData.UserId, eventData.PermAuthKeyId, eventData.Layer), eventData.Data);

    public Task HandleEventAsync(PremiumBotPaymentSettledEvent eventData) =>
        premiumBotService.SettleAsync(Input(eventData.UserId, eventData.PermAuthKeyId, eventData.Layer), eventData);

    public Task HandleEventAsync(PremiumWelcomeRequestedEvent eventData) => premiumWelcomeNotifier.SendAsync(eventData.UserId);

    private static RequestInput Input(long userId, long permAuthKeyId, int layer) =>
        new(string.Empty, ConnectionType.UnKnown, Guid.NewGuid(), 0, 0, 0, userId, permAuthKeyId, permAuthKeyId, layer,
            DateTime.UtcNow.ToTimestamp(), DeviceType.Unknown, string.Empty, 0, 0);
}
