using MyTelegram.Messenger.Helpers;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Fetches an <a href="https://corefork.telegram.org/constructor/updatePaidReactionPrivacy">updatePaidReactionPrivacy</a> update with the current <a href="https://corefork.telegram.org/api/reactions#paid-reactions">default paid reaction privacy, see here »</a> for more info.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getPaidReactionPrivacy"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetPaidReactionPrivacyHandler(
    IPaidReactionPrivacyAppService paidReactionPrivacyAppService,
    IAccessHashHelper2 accessHashHelper)
    : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetPaidReactionPrivacy, MyTelegram.Schema.IUpdates>
{
    protected override async Task<MyTelegram.Schema.IUpdates> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetPaidReactionPrivacy obj)
    {
        var setting = await paidReactionPrivacyAppService.GetDefaultAsync(input.UserId);

        var update = new TUpdatePaidReactionPrivacy
        {
            Private = PaidReactionPrivacyConverter.ToTl(setting, input, accessHashHelper)
        };

        return new TUpdates
        {
            Updates = new TVector<IUpdate>(update),
            Chats = new TVector<IChat>(),
            Date = CurrentDate,
            Users = new TVector<IUser>()
        };
    }
}
