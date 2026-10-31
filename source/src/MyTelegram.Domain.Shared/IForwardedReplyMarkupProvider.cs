namespace MyTelegram.Domain.Shared;

/// <summary>The keyboard a forwarded copy of a message carries, which is not always the original's.</summary>
public interface IForwardedReplyMarkupProvider
{
    /// <summary>The reply markup of the forwarded copy of <paramref name="original"/>, or <c>null</c> for none.</summary>
    Task<IReplyMarkup?> GetForwardedReplyMarkupAsync(MessageItem original);
}
