using System.Text;

namespace MyTelegram;

public record Reaction(
    long UserId,
    string? Emoticon,
    long? CustomEmojiDocumentId,
    int? Date = 0,
    bool Big = false,
    bool IsPaid = false,
    bool Anonymous = false,
    long AnonymousPeerId = 0)
{
    public long UserId { get; set; } = UserId;
    public string? Emoticon { get; set; } = Emoticon;
    public long? CustomEmojiDocumentId { get; set; } = CustomEmojiDocumentId;

    public int? Date { get; set; } = Date;
    public bool Big { get; set; } = Big;
    public bool IsPaid { get; set; } = IsPaid;

    /// <summary>
    /// Paid reactions only: hide the sender in the top reactors leaderboard.
    /// See https://corefork.telegram.org/api/reactions#paid-reactions
    /// </summary>
    public bool Anonymous { get; set; } = Anonymous;

    /// <summary>
    /// Paid reactions only: attribute the reaction to this peer instead of the sender.
    /// </summary>
    public long AnonymousPeerId { get; set; } = AnonymousPeerId;

    public long GetReactionId()
    {
        if (IsPaid) return 0x523da4eb;
        if (CustomEmojiDocumentId.HasValue)
        {
            return CustomEmojiDocumentId.Value;
        }

        if (string.IsNullOrEmpty(Emoticon))
        {
            throw new InvalidOperationException("Emotion and CustomEmojiDocumentId is null");
        }
        var bytes = Encoding.UTF8.GetBytes(Emoticon);
        if (bytes.Length >= 8)
        {
            return BitConverter.ToInt64(bytes);
        }

        var newBytes = new byte[8];
        Buffer.BlockCopy(bytes, 0, newBytes, 0, bytes.Length);

        return BitConverter.ToInt64(newBytes);
    }
}