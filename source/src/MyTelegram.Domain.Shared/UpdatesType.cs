namespace MyTelegram;

public enum UpdatesType
{
    Unknown,

    /// <summary>
    ///     TUpdates
    /// </summary>
    Updates,

    /// <summary>
    ///     Other IUpdates(TUpdateShort,TUpdateShortChatMessage,TUpdateShortMessage,TUpdateShortSentMessage)
    /// </summary>
    NewMessages,
    NewEncryptedMessages,
    EncryptedUpdates,

    /// <summary>
    /// A qts-sequenced update for a bot (callback queries, business messages, ...), kept so
    /// updates.getDifference can replay what the bot missed while offline. Replayed by GlobalSeqNo like
    /// <see cref="EncryptedUpdates"/>.
    /// </summary>
    BotQtsUpdates
}