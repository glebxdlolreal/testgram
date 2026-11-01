using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Hashing;

namespace MyTelegram.Messenger.Services.Business;

/// <summary>
/// <a href="https://corefork.telegram.org/api/business#quick-reply-shortcuts">Quick reply shortcuts</a>:
/// name rules, limits, id allocation and the hashes clients quote back.
///
/// <para>A shortcut is created implicitly by sending a message with <c>quick_reply_shortcut</c> set to a
/// name that does not exist yet. Its messages are ordinary message aggregates of the owner flagged with
/// <see cref="QuickReplyItem"/>; their ids come from the owner's message id sequence, so they never
/// collide with chat messages.</para>
/// </summary>
public interface IQuickReplyService
{
    /// <summary>
    /// Resolves <c>quick_reply_shortcut</c> of an outgoing message, creating the shortcut when the name is
    /// new. <paramref name="resolvedInThisRequest"/> carries shortcuts already resolved for earlier messages
    /// of the same request (an album creates its shortcut once).
    /// </summary>
    Task<QuickReplyItem> ResolveForSendAsync(long ownerUserId, IInputQuickReplyShortcut inputShortcut,
        Dictionary<string, QuickReplyItem> resolvedInThisRequest);

    Task<IReadOnlyList<QuickReplyShortcut>> GetShortcutsAsync(long ownerUserId);

    Task<IReadOnlyCollection<IMessageReadModel>> GetMessagesAsync(long ownerUserId,
        IReadOnlyCollection<int>? shortcutIds, IReadOnlyCollection<int>? messageIds = null);

    Task<bool> CanAddMessagesAsync(long ownerUserId, string name);

    /// <summary>
    /// Turns stored shortcut messages into sends to <paramref name="toPeer"/>: text (decrypted when stored
    /// encrypted), entities, media, keyboard and albums are carried over. Replies inside the shortcut point at
    /// other shortcut messages and cannot be carried into a chat, so they are dropped.
    /// </summary>
    List<SendMessageInput> BuildSendInputs(RequestInfo requestInfo, long senderUserId, Peer toPeer,
        IReadOnlyList<IMessageReadModel> messages, IReadOnlyList<long> randomIds);

    /// <summary>The listed shortcuts as <c>quickReply</c> constructors, skipping empty ones.</summary>
    Task<List<IQuickReply>> BuildQuickRepliesAsync(long ownerUserId, IReadOnlyList<QuickReplyShortcut> shortcuts);

    /// <summary>
    /// Tells the caller's other sessions about a change (tdlib reloads the shortcut list on
    /// <c>updateQuickReplies</c>/<c>updateDeleteQuickReply</c>).
    /// </summary>
    Task PushToOtherSessionsAsync(IRequestInput input, params IUpdate[] updates);
}

public sealed class QuickReplyService(
    IQueryProcessor queryProcessor,
    ICommandBus commandBus,
    IMongoDatabase mongoDatabase,
    IUserAppService userAppService,
    IMessageConverterService messageConverterService,
    IObjectMessageSender objectMessageSender) : IQuickReplyService, ITransientDependency
{
    /// <summary>appConfig <c>quick_replies_limit</c>.</summary>
    public const int QuickRepliesLimit = MyTelegramConsts.QuickRepliesLimit;

    /// <summary>appConfig <c>quick_reply_messages_limit</c>.</summary>
    public const int QuickReplyMessagesLimit = MyTelegramConsts.QuickReplyMessagesLimit;

    public const int MaxShortcutNameLength = 32;

    public async Task<QuickReplyItem> ResolveForSendAsync(long ownerUserId,
        IInputQuickReplyShortcut inputShortcut,
        Dictionary<string, QuickReplyItem> resolvedInThisRequest)
    {
        if (TryTakeResolved(inputShortcut, resolvedInThisRequest, out var resolved))
        {
            return resolved;
        }

        var shortcuts = await GetShortcutsAsync(ownerUserId);
        QuickReplyShortcut? shortcut;
        switch (inputShortcut)
        {
            case TInputQuickReplyShortcutId byId:
                shortcut = shortcuts.FirstOrDefault(p => p.ShortcutId == byId.ShortcutId);
                if (shortcut == null)
                {
                    RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
                }

                break;
            case TInputQuickReplyShortcut byName:
                if (!IsValidShortcutName(byName.Shortcut))
                {
                    RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
                }

                shortcut = FindByName(shortcuts, byName.Shortcut);
                if (shortcut == null)
                {
                    await userAppService.CheckAccountPremiumStatusAsync(ownerUserId);
                    if (shortcuts.Count >= QuickRepliesLimit)
                    {
                        RpcErrors.RpcErrors400.QuickRepliesTooMuch.ThrowRpcError();
                    }

                    var shortcutId = await AllocateShortcutIdAsync(ownerUserId);
                    await commandBus.PublishAsync(new CreateShortcutCommand(QuickReplyListId.Create(ownerUserId),
                        ownerUserId, shortcutId, byName.Shortcut));
                    var created = new QuickReplyItem(shortcutId, byName.Shortcut, 0, 0, true);
                    resolvedInThisRequest[byName.Shortcut] = created with { Count = 1 };
                    return created;
                }

                break;
            default:
                RpcErrors.RpcErrors400.ShortcutInvalid.ThrowRpcError();
                return null!;
        }

        var count = (await GetMessagesAsync(ownerUserId, [shortcut!.ShortcutId])).Count;
        if (count >= QuickReplyMessagesLimit)
        {
            RpcErrors.RpcErrors400.ReplyMessagesTooMuch.ThrowRpcError();
        }

        var item = new QuickReplyItem(shortcut.ShortcutId, shortcut.Name, 0, count, false);
        resolvedInThisRequest[shortcut.Name] = item with { Count = count + 1 };
        return item;
    }

    /// <summary>
    /// Later messages of one request (an album) reuse what the first one resolved, counting themselves
    /// against the per-shortcut limit.
    /// </summary>
    private static bool TryTakeResolved(IInputQuickReplyShortcut inputShortcut,
        Dictionary<string, QuickReplyItem> resolvedInThisRequest, out QuickReplyItem item)
    {
        var cached = inputShortcut switch
        {
            TInputQuickReplyShortcut byName => resolvedInThisRequest.GetValueOrDefault(byName.Shortcut),
            TInputQuickReplyShortcutId byId => resolvedInThisRequest.Values.FirstOrDefault(p =>
                p.ShortcutId == byId.ShortcutId),
            _ => null
        };
        if (cached == null)
        {
            item = null!;
            return false;
        }

        if (cached.Count >= QuickReplyMessagesLimit)
        {
            RpcErrors.RpcErrors400.ReplyMessagesTooMuch.ThrowRpcError();
        }

        resolvedInThisRequest[cached.Title] = cached with { Count = cached.Count + 1 };
        item = cached with { Count = cached.Count };
        return true;
    }

    public async Task<IReadOnlyList<QuickReplyShortcut>> GetShortcutsAsync(long ownerUserId)
    {
        var list = await queryProcessor.ProcessAsync(new GetQuickReplyListQuery(ownerUserId));
        return list?.Shortcuts ?? [];
    }

    public Task<IReadOnlyCollection<IMessageReadModel>> GetMessagesAsync(long ownerUserId,
        IReadOnlyCollection<int>? shortcutIds, IReadOnlyCollection<int>? messageIds = null)
    {
        return queryProcessor.ProcessAsync(new GetQuickReplyMessagesQuery(ownerUserId, shortcutIds, messageIds));
    }

    /// <summary>
    /// <c>messages.checkQuickReplyShortcut</c>: whether one more message can be added under this name,
    /// either to the existing shortcut or by creating a new one.
    /// </summary>
    public async Task<bool> CanAddMessagesAsync(long ownerUserId, string name)
    {
        var shortcuts = await GetShortcutsAsync(ownerUserId);
        var shortcut = FindByName(shortcuts, name);
        if (shortcut == null)
        {
            return shortcuts.Count < QuickRepliesLimit;
        }

        var count = (await GetMessagesAsync(ownerUserId, [shortcut.ShortcutId])).Count;
        return count < QuickReplyMessagesLimit;
    }

    public List<SendMessageInput> BuildSendInputs(RequestInfo requestInfo, long senderUserId, Peer toPeer,
        IReadOnlyList<IMessageReadModel> messages, IReadOnlyList<long> randomIds)
    {
        // An album is sent again as a new album: the stored grouped id belongs to the shortcut copy.
        var newGroupIds = messages
            .Where(p => p.GroupedId is > 0)
            .Select(p => p.GroupedId!.Value)
            .Distinct()
            .ToDictionary(p => p, _ => Random.Shared.NextInt64(1, long.MaxValue));
        var groupSizes = messages
            .Where(p => p.GroupedId is > 0)
            .GroupBy(p => p.GroupedId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var inputs = new List<SendMessageInput>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var text = message.Message ?? string.Empty;
            if (string.IsNullOrEmpty(text) && message.EncryptedData is { Length: > 0 })
            {
                text = messageConverterService.DecryptMessage(message.OwnerPeerId, message.MessageId,
                    message.EncryptedData);
            }

            long? groupId = message.GroupedId is > 0 ? newGroupIds[message.GroupedId.Value] : null;
            inputs.Add(new SendMessageInput(requestInfo,
                senderUserId,
                toPeer,
                text,
                randomIds[i],
                entities: message.Entities2,
                media: message.Media2,
                sendMessageType: message.SendMessageType,
                messageType: message.MessageType,
                groupId: groupId,
                groupItemCount: groupId.HasValue ? groupSizes[message.GroupedId!.Value] : 1,
                replyMarkup: message.ReplyMarkup2,
                isSendQuickReplyMessage: true,
                silent: message.Silent,
                invertMedia: message.InvertMedia));
        }

        return inputs;
    }

    public async Task<List<IQuickReply>> BuildQuickRepliesAsync(long ownerUserId,
        IReadOnlyList<QuickReplyShortcut> shortcuts)
    {
        if (shortcuts.Count == 0)
        {
            return [];
        }

        var messages = await GetMessagesAsync(ownerUserId, shortcuts.Select(p => p.ShortcutId).ToList());
        var byShortcut = messages.GroupBy(p => p.ShortcutId ?? 0).ToDictionary(g => g.Key, g => g.ToList());

        return shortcuts
            .Where(p => byShortcut.ContainsKey(p.ShortcutId))
            .Select(p => (IQuickReply)new TQuickReply
            {
                ShortcutId = p.ShortcutId,
                Shortcut = p.Name,
                TopMessage = byShortcut[p.ShortcutId].Min(m => m.MessageId),
                Count = byShortcut[p.ShortcutId].Count
            })
            .ToList();
    }

    public Task PushToOtherSessionsAsync(IRequestInput input, params IUpdate[] updates)
    {
        var data = new TUpdates
        {
            Updates = new TVector<IUpdate>(updates),
            Users = [],
            Chats = [],
            Date = DateTime.UtcNow.ToTimestamp(),
            Seq = 0
        };

        return objectMessageSender.PushMessageToPeerAsync(input.UserId.ToUserPeer(), data,
            excludeAuthKeyId: input.PermAuthKeyId);
    }

    public static QuickReplyShortcut? FindByName(IEnumerable<QuickReplyShortcut> shortcuts, string name)
    {
        return shortcuts.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The rule tdlib applies before sending (<c>QuickReplyManager::check_shortcut_name</c>): 1..32
    /// characters, each a letter, a decimal digit, <c>_</c>, ZWNJ, middle dot or a Sinhala character.
    /// </summary>
    public static bool IsValidShortcutName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var length = 0;
        var enumerator = name.EnumerateRunes();
        foreach (var rune in enumerator)
        {
            if (!IsShortcutNameLetter(rune))
            {
                return false;
            }

            length++;
        }

        return length is > 0 and <= MaxShortcutNameLength;
    }

    private static bool IsShortcutNameLetter(Rune rune)
    {
        var code = rune.Value;
        if (code == '_' || code == 0x200c || code == 0xb7 || code is >= 0xd80 and <= 0xdff)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.DecimalDigitNumber => true,
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter => true,
            _ => false
        };
    }

    /// <summary>
    /// <c>messages.getQuickReplies</c> hash as tdlib computes it from its cache
    /// (<c>QuickReplyManager::get_shortcuts_hash</c>): for every shortcut in list order, its id, the
    /// md5-hash of its name, and the id and edit date of its first message.
    /// </summary>
    public static long ComputeShortcutsHash(IEnumerable<(QuickReplyShortcut Shortcut, int FirstMessageId,
        int FirstMessageEditDate)> shortcuts)
    {
        var numbers = new List<long>();
        foreach (var (shortcut, firstMessageId, editDate) in shortcuts)
        {
            numbers.Add(shortcut.ShortcutId);
            numbers.Add((long)Md5StringHash(shortcut.Name));
            numbers.Add(firstMessageId);
            numbers.Add(editDate);
        }

        return VectorHashHelper.ComputeHash(numbers);
    }

    /// <summary>
    /// <c>messages.getQuickReplyMessages</c> hash (<c>QuickReplyManager::get_quick_reply_messages_hash</c>):
    /// id and edit date of every message, ascending by id.
    /// </summary>
    public static long ComputeMessagesHash(IEnumerable<(int MessageId, int EditDate)> messages)
    {
        var numbers = new List<long>();
        foreach (var (messageId, editDate) in messages.OrderBy(p => p.MessageId))
        {
            numbers.Add(messageId);
            numbers.Add(editDate);
        }

        return VectorHashHelper.ComputeHash(numbers);
    }

    /// <summary>tdlib <c>get_md5_string_hash</c>: the first 8 bytes of the MD5 digest, big-endian.</summary>
    public static ulong Md5StringHash(string value)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(value));
        var result = 0UL;
        for (var i = 0; i <= 7; i++)
        {
            result += (ulong)digest[i] << (56 - 8 * i);
        }

        return result;
    }

    private async Task<int> AllocateShortcutIdAsync(long ownerUserId)
    {
        var counters = mongoDatabase.GetCollection<BsonDocument>("counters");
        var result = await counters.FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"quick_reply_shortcut_{ownerUserId}"),
            Builders<BsonDocument>.Update.Inc("seq", 1),
            new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After });

        return result["seq"].ToInt32();
    }
}
