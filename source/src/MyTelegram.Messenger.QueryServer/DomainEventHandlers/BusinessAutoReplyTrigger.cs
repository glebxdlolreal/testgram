using MyTelegram.Messenger.Services.Business;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

/// <summary>
/// Watches private messages arriving at business accounts and hands those that may need a
/// <a href="https://corefork.telegram.org/api/business#greeting-messages">greeting</a> or an
/// <a href="https://corefork.telegram.org/api/business#away-messages">away message</a> to the command
/// server. Only the cheap filters run here; the decision is made where the reply is sent.
/// </summary>
public class BusinessAutoReplyTrigger(IUserAppService userAppService, IEventBus eventBus,
    ILogger<BusinessAutoReplyTrigger> logger) :
    ISubscribeSynchronousTo<SendMessageSaga, SendMessageSagaId, ReceiveInboxMessageCompletedSagaEvent>
{
    public async Task HandleAsync(
        IDomainEvent<SendMessageSaga, SendMessageSagaId, ReceiveInboxMessageCompletedSagaEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        // One inbox copy per recipient; in a private chat that is the business account.
        var item = domainEvent.AggregateEvent.MessageItems.LastOrDefault();
        if (item == null ||
            item.ToPeer.PeerType != PeerType.User ||
            item.OwnerPeer.PeerType != PeerType.User ||
            item.SendMessageType == SendMessageType.MessageService ||
            item.IsBusinessAutoReply ||
            item.QuickReplyItem != null ||
            item.ScheduleDate.HasValue ||
            item.SenderUserId == item.OwnerPeer.PeerId)
        {
            return;
        }

        try
        {
            var businessUserId = item.OwnerPeer.PeerId;
            var business = await userAppService.GetAsync(businessUserId);
            if (business == null || business.Bot ||
                (business.BusinessGreetingSettings == null && business.BusinessAwaySettings == null))
            {
                return;
            }

            var sender = await userAppService.GetAsync(item.SenderUserId);
            if (sender == null || sender.Bot)
            {
                return;
            }

            var first = domainEvent.AggregateEvent.MessageItems.Min(p => p.MessageId);
            await eventBus.PublishAsync(new BusinessIncomingMessageEvent(businessUserId, item.SenderUserId, first,
                item.Date));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not hand over a business message to {UserId}", item.OwnerPeer.PeerId);
        }
    }
}
