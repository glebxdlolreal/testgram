namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;
/// <summary>
/// Represents a list of <a href="https://corefork.telegram.org/api/emoji-categories">emoji categories</a>.
/// <para><c>See <a href="https://corefork.telegram.org/method/messages.getEmojiGroups"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✖] [Anonymous ✖]
/// </remarks>
internal sealed class GetEmojiGroupsHandler(IEmojiGroupsAppService emojiGroupsAppService) : RpcResultObjectHandler<MyTelegram.Schema.Messages.RequestGetEmojiGroups, MyTelegram.Schema.Messages.IEmojiGroups>
{
    protected override Task<MyTelegram.Schema.Messages.IEmojiGroups> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Messages.RequestGetEmojiGroups obj)
    {
        return emojiGroupsAppService.GetEmojiGroupsAsync(EmojiGroupType.Default, obj.Hash);
    }
}
