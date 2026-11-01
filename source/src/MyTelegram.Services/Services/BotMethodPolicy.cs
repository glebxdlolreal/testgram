namespace MyTelegram.Services.Services;

/// <summary>
/// Methods Telegram refuses to bot accounts with <c>BOT_METHOD_INVALID</c>, enforced before any handler
/// runs so each handler need not remember to check.
/// </summary>
/// <remarks>
/// The list is what a real bot was refused on Telegram's servers (Uniclient research notes,
/// <c>research/telegram_notes.md</c>, 2026-04-05): history, dialog and search browsing, and the admin
/// actions only user accounts may perform. Wrapped requests (<c>invokeWithLayer</c> and the like) are
/// judged by their innermost query.
/// </remarks>
public static class BotMethodPolicy
{
    public static bool IsForbiddenForBot(long userId, IObject? request)
    {
        if (userId is < MyTelegramConsts.BotUserInitId or > MyTelegramConsts.ChatIdInitId)
        {
            return false;
        }

        while (request is IHasSubQuery wrapper)
        {
            request = wrapper.Query;
        }

        return request is MyTelegram.Schema.Messages.RequestGetHistory
            or MyTelegram.Schema.Messages.LayerN.RequestGetHistory
            or MyTelegram.Schema.Messages.RequestGetDialogs
            or MyTelegram.Schema.Messages.RequestSearch
            or MyTelegram.Schema.Channels.RequestInviteToChannel
            or MyTelegram.Schema.Channels.RequestToggleSlowMode
            or MyTelegram.Schema.Channels.RequestGetAdminLog
            or MyTelegram.Schema.Channels.RequestDeleteParticipantHistory
            or MyTelegram.Schema.Channels.RequestToggleAntiSpam
            or MyTelegram.Schema.Channels.RequestToggleSignatures;
    }
}
