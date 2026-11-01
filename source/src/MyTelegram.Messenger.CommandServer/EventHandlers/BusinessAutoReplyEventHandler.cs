using MyTelegram.Messenger.Services.Business;

namespace MyTelegram.Messenger.CommandServer.EventHandlers;

/// <summary>Sends greeting and away messages; see <see cref="IBusinessAutoReplyService"/>.</summary>
public class BusinessAutoReplyEventHandler(IBusinessAutoReplyService businessAutoReplyService)
    : IEventHandler<BusinessIncomingMessageEvent>, ITransientDependency
{
    public Task HandleEventAsync(BusinessIncomingMessageEvent eventData) =>
        businessAutoReplyService.HandleIncomingAsync(eventData);
}
