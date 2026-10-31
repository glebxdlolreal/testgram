namespace MyTelegram.Messenger.Services.Premium;

/// <summary>
/// The service notification real Telegram sends from 777000 when an account starts a Premium
/// subscription: "Thank you for subscribing to 🌟 Telegram Premium! …" (measured on a real account;
/// it came in English to a Russian-language account, so it is not localised here either).
/// </summary>
public interface IPremiumWelcomeNotifier
{
    Task NotifyAsync(long userId);
}

/// <inheritdoc />
public sealed class PremiumWelcomeNotifier(IMessageAppService messageAppService) : IPremiumWelcomeNotifier, ITransientDependency
{
    // Real Telegram decorates 🌟 with a custom emoji this server does not have, so it goes out plain.
    public const string Text =
        "Thank you for subscribing to 🌟 Telegram Premium! Learn all about the exclusive benefits you've unlocked: @premium.";

    public Task NotifyAsync(long userId)
    {
        var entities = new TVector<IMessageEntity>(
            new TMessageEntityBold { Offset = Text.IndexOf("Telegram Premium", StringComparison.Ordinal), Length = "Telegram Premium".Length },
            new TMessageEntityBold { Offset = Text.IndexOf("exclusive benefits", StringComparison.Ordinal), Length = "exclusive benefits".Length },
            new TMessageEntityMention { Offset = Text.IndexOf("@premium", StringComparison.Ordinal), Length = "@premium".Length });

        return messageAppService.SendMessageAsync([new SendMessageInput(
            RequestInfo.Empty with
            {
                UserId = MyTelegramConsts.NotificationServiceUserId,
                Layer = MyTelegramConsts.Layer,
                Date = DateTime.UtcNow.ToTimestamp(),
                RequestId = Guid.NewGuid()
            },
            MyTelegramConsts.NotificationServiceUserId,
            new Peer(PeerType.User, userId),
            Text,
            Random.Shared.NextInt64(),
            entities: entities)]);
    }
}
