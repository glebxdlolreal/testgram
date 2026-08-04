namespace MyTelegram.Messenger.Services.Interfaces;

/// <summary>
/// Serves <a href="https://corefork.telegram.org/api/emoji-categories">emoji categories</a> for the
/// four messages.getEmoji*Groups methods, which differ only by the category set they read.
/// </summary>
public interface IEmojiGroupsAppService
{
    Task<MyTelegram.Schema.Messages.IEmojiGroups> GetEmojiGroupsAsync(EmojiGroupType groupType, int hash);
}

/// <summary>
/// Identifies which category set a request wants; maps to the <c>For</c> field in the
/// <c>emoji_groups</c> collection.
/// </summary>
public enum EmojiGroupType
{
    /// <summary>messages.getEmojiGroups — emojis, custom emojis and GIFs.</summary>
    Default,

    /// <summary>messages.getEmojiStickerGroups — choosing a sticker.</summary>
    Stickers,

    /// <summary>messages.getEmojiStatusGroups — choosing a custom emoji status.</summary>
    Status,

    /// <summary>messages.getEmojiProfilePhotoGroups — choosing a profile picture.</summary>
    ProfilePhoto
}
