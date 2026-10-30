using MongoDB.Driver;
using MongoDB.Bson;
using MyTelegram.Messenger.Services.Phone;

namespace MyTelegram.Messenger.Helpers;

public static class AdminLogHelper
{
    public static async Task LogChangeTitle(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        string prevValue,
        string newValue)
    {
        var action = new TChannelAdminLogEventActionChangeTitle
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeAbout(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        string prevValue,
        string newValue)
    {
        var action = new TChannelAdminLogEventActionChangeAbout
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeUsername(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        string prevValue,
        string newValue)
    {
        var action = new TChannelAdminLogEventActionChangeUsername
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the channel's message accent
    /// <a href="https://core.telegram.org/api/colors">peer color</a>.
    /// </summary>
    public static async Task LogChangePeerColor(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IPeerColor? prevValue,
        IPeerColor? newValue)
    {
        // PrevValue/NewValue are non-nullable in the schema and always serialized,
        // so an unset color is represented by an empty TPeerColor.
        var action = new TChannelAdminLogEventActionChangePeerColor
        {
            PrevValue = prevValue ?? new TPeerColor(),
            NewValue = newValue ?? new TPeerColor()
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the channel's profile page
    /// <a href="https://core.telegram.org/api/colors">peer color</a>.
    /// </summary>
    public static async Task LogChangeProfilePeerColor(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IPeerColor? prevValue,
        IPeerColor? newValue)
    {
        var action = new TChannelAdminLogEventActionChangeProfilePeerColor
        {
            PrevValue = prevValue ?? new TPeerColor(),
            NewValue = newValue ?? new TPeerColor()
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the channel's
    /// <a href="https://core.telegram.org/api/emoji-status">emoji status</a>.
    /// </summary>
    public static async Task LogChangeEmojiStatus(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IEmojiStatus? prevValue,
        IEmojiStatus? newValue)
    {
        // PrevValue/NewValue are non-nullable in the schema and always serialized,
        // so an unset status is represented by emojiStatusEmpty.
        var action = new TChannelAdminLogEventActionChangeEmojiStatus
        {
            PrevValue = prevValue ?? new TEmojiStatusEmpty(),
            NewValue = newValue ?? new TEmojiStatusEmpty()
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogToggleInvites(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionToggleInvites
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogToggleSignatures(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionToggleSignatures
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the "show author profiles next to posts" setting, toggled together with
    /// signatures by <a href="https://corefork.telegram.org/method/channels.toggleSignatures">channels.toggleSignatures</a>.
    /// </summary>
    public static async Task LogToggleSignatureProfiles(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionToggleSignatureProfiles
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the <a href="https://corefork.telegram.org/api/antispam">native antispam</a> setting.
    /// </summary>
    /// <remarks>Called for the persisted event, so a refused toggle (CHAT_NOT_MODIFIED) is never logged;
    /// <paramref name="operationKey"/> keeps a redelivered event from adding a second entry.</remarks>
    public static async Task LogToggleAntiSpam(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue,
        string? operationKey = null)
    {
        var action = new TChannelAdminLogEventActionToggleAntiSpam
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action, operationKey);
    }

    /// <summary>
    /// Logs a change of the automatic post translation setting.
    /// </summary>
    public static async Task LogToggleAutotranslation(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionToggleAutotranslation
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the "restrict saving content" setting.
    /// </summary>
    public static async Task LogToggleNoForwards(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionToggleNoForwards
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the <a href="https://corefork.telegram.org/api/reactions">reactions</a> allowed in
    /// the channel.
    /// </summary>
    public static async Task LogChangeAvailableReactions(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IChatReactions prevValue,
        IChatReactions newValue)
    {
        var action = new TChannelAdminLogEventActionChangeAvailableReactions
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the <a href="https://corefork.telegram.org/api/wallpapers">wallpaper</a> of a
    /// channel or supergroup.
    /// </summary>
    public static async Task LogChangeWallpaper(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IWallPaper prevValue,
        IWallPaper newValue)
    {
        var action = new TChannelAdminLogEventActionChangeWallpaper
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a change of the list of active usernames, see
    /// <a href="https://corefork.telegram.org/api/usernames">usernames</a>.
    /// </summary>
    public static async Task LogChangeUsernames(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IEnumerable<string> prevValue,
        IEnumerable<string> newValue)
    {
        var action = new TChannelAdminLogEventActionChangeUsernames
        {
            PrevValue = new TVector<string>(prevValue),
            NewValue = new TVector<string>(newValue)
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// The active usernames of a channel read model, in stored order — the value the admin log reports as
    /// the previous or the new username list.
    /// </summary>
    public static List<string> ActiveUsernames(BsonValue? usernames)
    {
        if (usernames is not BsonArray array)
        {
            return [];
        }

        return array
            .OfType<BsonDocument>()
            .Where(d => d.GetValue("Active", false).ToBoolean() && d.Contains("Username"))
            .Select(d => d["Username"].AsString)
            .ToList();
    }

    /// <summary>
    /// Logs a change of the group's
    /// <a href="https://corefork.telegram.org/api/custom-emoji">custom emoji stickerset</a>.
    /// </summary>
    public static async Task LogChangeEmojiStickerSet(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IInputStickerSet prevStickerset,
        IInputStickerSet newStickerset)
    {
        var action = new TChannelAdminLogEventActionChangeEmojiStickerSet
        {
            PrevStickerset = prevStickerset,
            NewStickerset = newStickerset
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs that an admin added a user to the channel directly.
    /// </summary>
    public static async Task LogParticipantInvite(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        MyTelegram.Schema.IChannelParticipant participant)
    {
        var action = new TChannelAdminLogEventActionParticipantInvite
        {
            Participant = participant
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs that only the custom title of an admin changed, see the <c>edit_rank</c> flag of
    /// <a href="https://corefork.telegram.org/constructor/channelAdminLogEventsFilter">channelAdminLogEventsFilter</a>.
    /// </summary>
    public static async Task LogParticipantEditRank(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        long userId,
        string prevRank,
        string newRank)
    {
        var action = new TChannelAdminLogEventActionParticipantEditRank
        {
            UserId = userId,
            PrevRank = prevRank,
            NewRank = newRank
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogParticipantMute(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IGroupCallParticipant participant)
    {
        var action = new TChannelAdminLogEventActionParticipantMute
        {
            Participant = participant
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogParticipantUnmute(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IGroupCallParticipant participant)
    {
        var action = new TChannelAdminLogEventActionParticipantUnmute
        {
            Participant = participant
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogParticipantVolume(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IGroupCallParticipant participant)
    {
        var action = new TChannelAdminLogEventActionParticipantVolume
        {
            Participant = participant
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs an edit of an <a href="https://corefork.telegram.org/api/invites">invite link</a>. A revocation
    /// is reported with its own constructor, as the client renders the two differently.
    /// </summary>
    public static async Task LogExportedInviteEdit(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        MyTelegram.Schema.IExportedChatInvite prevInvite,
        MyTelegram.Schema.IExportedChatInvite newInvite)
    {
        var action = new TChannelAdminLogEventActionExportedInviteEdit
        {
            PrevInvite = prevInvite,
            NewInvite = newInvite
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogExportedInviteRevoke(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        MyTelegram.Schema.IExportedChatInvite invite)
    {
        var action = new TChannelAdminLogEventActionExportedInviteRevoke
        {
            Invite = invite
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogExportedInviteDelete(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        MyTelegram.Schema.IExportedChatInvite invite)
    {
        var action = new TChannelAdminLogEventActionExportedInviteDelete
        {
            Invite = invite
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs a post published in a channel, see the <c>send</c> flag of
    /// <a href="https://corefork.telegram.org/constructor/channelAdminLogEventsFilter">channelAdminLogEventsFilter</a>.
    /// </summary>
    public static async Task LogSendMessage(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IMessage message)
    {
        var action = new TChannelAdminLogEventActionSendMessage
        {
            Message = message
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    /// <summary>
    /// Logs that a <a href="https://corefork.telegram.org/api/poll">poll</a> was stopped.
    /// </summary>
    public static async Task LogStopPoll(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IMessage message)
    {
        var action = new TChannelAdminLogEventActionStopPoll
        {
            Message = message
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogParticipantJoin(
        IMongoDatabase database,
        long channelId,
        long userId)
    {
        var action = new TChannelAdminLogEventActionParticipantJoin();
        await LogEventAsync(database, channelId, userId, action);
    }

    public static async Task LogParticipantLeave(
        IMongoDatabase database,
        long channelId,
        long userId)
    {
        var action = new TChannelAdminLogEventActionParticipantLeave();
        await LogEventAsync(database, channelId, userId, action);
    }

    public static async Task LogDeleteMessage(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IMessage message)
    {
        var action = new TChannelAdminLogEventActionDeleteMessage
        {
            Message = message
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogEditMessage(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IMessage prevMessage,
        IMessage newMessage)
    {
        var action = new TChannelAdminLogEventActionEditMessage
        {
            PrevMessage = prevMessage,
            NewMessage = newMessage
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogTogglePreHistoryHidden(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionTogglePreHistoryHidden
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogEditAdmin(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        MyTelegram.Schema.IChannelParticipant prevParticipant,
        MyTelegram.Schema.IChannelParticipant newParticipant)
    {
        var action = new TChannelAdminLogEventActionParticipantToggleAdmin
        {
            PrevParticipant = prevParticipant,
            NewParticipant = newParticipant
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogEditBanned(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        MyTelegram.Schema.IChannelParticipant prevParticipant,
        MyTelegram.Schema.IChannelParticipant newParticipant)
    {
        var action = new TChannelAdminLogEventActionParticipantToggleBan
        {
            PrevParticipant = prevParticipant,
            NewParticipant = newParticipant
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeStickerSet(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IInputStickerSet prevStickerset,
        IInputStickerSet newStickerset)
    {
        var action = new TChannelAdminLogEventActionChangeStickerSet
        {
            PrevStickerset = prevStickerset,
            NewStickerset = newStickerset
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangePhoto(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IPhoto prevPhoto,
        IPhoto newPhoto)
    {
        var action = new TChannelAdminLogEventActionChangePhoto
        {
            PrevPhoto = prevPhoto,
            NewPhoto = newPhoto
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeHistoryTTL(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        int prevValue,
        int newValue)
    {
        var action = new TChannelAdminLogEventActionChangeHistoryTTL
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogParticipantJoinByInvite(
        IMongoDatabase database,
        long channelId,
        long userId,
        bool viaChatlist,
        MyTelegram.Schema.IExportedChatInvite invite)
    {
        var action = new TChannelAdminLogEventActionParticipantJoinByInvite
        {
            ViaChatlist = viaChatlist,
            Invite = invite
        };
        await LogEventAsync(database, channelId, userId, action);
    }

    /// <summary>
    /// Logs that an admin let a <a href="https://corefork.telegram.org/api/invites#join-requests">join
    /// request</a> through. The event belongs to the user who joined, the approving admin is carried
    /// by <c>approved_by</c>.
    /// </summary>
    public static async Task LogParticipantJoinByRequest(
        IMongoDatabase database,
        long channelId,
        long userId,
        MyTelegram.Schema.IExportedChatInvite invite,
        long approvedBy)
    {
        var action = new TChannelAdminLogEventActionParticipantJoinByRequest
        {
            Invite = invite,
            ApprovedBy = approvedBy
        };
        await LogEventAsync(database, channelId, userId, action);
    }

    public static async Task LogToggleSlowMode(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        int prevValue,
        int newValue)
    {
        var action = new TChannelAdminLogEventActionToggleSlowMode
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogToggleForum(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        bool newValue)
    {
        var action = new TChannelAdminLogEventActionToggleForum
        {
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogUpdatePinned(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IMessage message)
    {
        var action = new TChannelAdminLogEventActionUpdatePinned
        {
            Message = message
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeLinkedChat(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        long prevValue,
        long newValue)
    {
        var action = new TChannelAdminLogEventActionChangeLinkedChat
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogChangeLocation(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IChannelLocation prevValue,
        IChannelLocation newValue)
    {
        var action = new TChannelAdminLogEventActionChangeLocation
        {
            PrevValue = prevValue,
            NewValue = newValue
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogDefaultBannedRights(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IChatBannedRights prevBannedRights,
        IChatBannedRights newBannedRights)
    {
        var action = new TChannelAdminLogEventActionDefaultBannedRights
        {
            PrevBannedRights = prevBannedRights,
            NewBannedRights = newBannedRights
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogParticipantSubExtend(
        IMongoDatabase database,
        long channelId,
        long userId,
        MyTelegram.Schema.IChannelParticipant prevParticipant,
        MyTelegram.Schema.IChannelParticipant newParticipant)
    {
        var action = new TChannelAdminLogEventActionParticipantSubExtend
        {
            PrevParticipant = prevParticipant,
            NewParticipant = newParticipant
        };
        await LogEventAsync(database, channelId, userId, action);
    }

    public static async Task LogCreateTopic(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IForumTopic topic)
    {
        var action = new TChannelAdminLogEventActionCreateTopic
        {
            Topic = topic
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogEditTopic(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IForumTopic prevTopic,
        IForumTopic newTopic)
    {
        var action = new TChannelAdminLogEventActionEditTopic
        {
            PrevTopic = prevTopic,
            NewTopic = newTopic
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogDeleteTopic(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IForumTopic topic)
    {
        var action = new TChannelAdminLogEventActionDeleteTopic
        {
            Topic = topic
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogPinTopic(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IForumTopic prevTopic,
        IForumTopic newTopic)
    {
        var action = new TChannelAdminLogEventActionPinTopic
        {
            PrevTopic = prevTopic,
            NewTopic = newTopic
        };
        await LogEventAsync(database, channelId, adminUserId, action);
    }

    public static async Task LogStartGroupCall(
        IMongoDatabase database,
        GroupCallDocument call,
        long adminUserId)
    {
        if (call.PeerType != (int)PeerType.Channel)
        {
            return;
        }

        var action = new TChannelAdminLogEventActionStartGroupCall
        {
            Call = GroupCallStateHelper.ToInputGroupCall(call)
        };
        await LogEventAsync(database, call.PeerId, adminUserId, action);
    }

    public static async Task LogDiscardGroupCall(
        IMongoDatabase database,
        GroupCallDocument call,
        long adminUserId)
    {
        if (call.PeerType != (int)PeerType.Channel)
        {
            return;
        }

        var action = new TChannelAdminLogEventActionDiscardGroupCall
        {
            Call = GroupCallStateHelper.ToInputGroupCall(call)
        };
        await LogEventAsync(database, call.PeerId, adminUserId, action);
    }

    public static async Task LogToggleGroupCallSetting(
        IMongoDatabase database,
        GroupCallDocument call,
        long adminUserId,
        bool joinMuted)
    {
        if (call.PeerType != (int)PeerType.Channel)
        {
            return;
        }

        var action = new TChannelAdminLogEventActionToggleGroupCallSetting
        {
            JoinMuted = joinMuted
        };
        await LogEventAsync(database, call.PeerId, adminUserId, action);
    }

    /// <summary>
    /// Writes one admin log entry. The filter tags, the search text and the referenced peers are derived
    /// from the action once, here, so that <c>channels.getAdminLog</c> never has to deserialize the TL blob
    /// to decide whether an entry matches a query.
    /// </summary>
    private static async Task LogEventAsync(
        IMongoDatabase database,
        long channelId,
        long adminUserId,
        IChannelAdminLogEventAction action,
        string? operationKey = null)
    {
        var collection = database.GetCollection<BsonDocument>(AdminLogCollection.Name);
        var id = operationKey == null ? null : $"adminlog-{channelId}-{operationKey}";
        if (id != null && await collection.Find(new BsonDocument("_id", id)).AnyAsync())
        {
            return;
        }
        var eventId = await NextEventIdAsync(database, channelId);

        var buffer = new ArrayBufferWriter<byte>();
        action.Serialize(buffer);

        var document = new BsonDocument
        {
            ["_id"] = id ?? $"adminlog-{channelId}-{eventId}",
            ["event_id"] = eventId,
            ["channel_id"] = channelId,
            ["user_id"] = adminUserId,
            ["date"] = DateTime.UtcNow,
            ["filters"] = new BsonArray(AdminLogMetadata.Filters(action)),
            ["search_text"] = AdminLogMetadata.SearchText(action),
            ["related_user_ids"] = new BsonArray(AdminLogMetadata.RelatedUserIds(action)),
            ["related_channel_ids"] = new BsonArray(AdminLogMetadata.RelatedChannelIds(action)),
            ["action"] = new BsonDocument
            {
                ["type"] = action.GetType().Name,
                ["data"] = buffer.WrittenMemory.ToArray()
            }
        };
        try
        {
            await collection.InsertOneAsync(document);
        }
        catch (MongoWriteException e) when (id != null && e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            // A concurrent delivery of the same operation already logged it.
        }
    }

    /// <summary>
    /// Event ids must increase within a channel: clients paginate by passing the id of the oldest event
    /// they already have as <c>max_id</c>, so a random or wall-clock id makes the log unpaginatable.
    /// </summary>
    private static async Task<long> NextEventIdAsync(IMongoDatabase database, long channelId)
    {
        var result = await database.GetCollection<BsonDocument>("counters").FindOneAndUpdateAsync(
            Builders<BsonDocument>.Filter.Eq("_id", $"adminlog_event_id_{channelId}"),
            Builders<BsonDocument>.Update.Inc("seq", 1L),
            new FindOneAndUpdateOptions<BsonDocument>
            {
                IsUpsert = true,
                ReturnDocument = ReturnDocument.After
            });

        return result["seq"].ToInt64();
    }
}
