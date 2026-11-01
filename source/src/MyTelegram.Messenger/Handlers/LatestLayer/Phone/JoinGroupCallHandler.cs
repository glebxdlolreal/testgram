using MyTelegram.Messenger.Converters.ConverterServices;
using MyTelegram.Services.Services;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;
using MyTelegram.Schema.Phone;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Phone;

internal sealed class JoinGroupCallHandler(
    IMongoDatabase mongoDatabase,
    IAccessHashHelper2 accessHashHelper2,
    IPeerHelper peerHelper,
    IObjectMessageSender objectMessageSender,
    IChannelAppService channelAppService,
    IChannelAdminRightsChecker channelAdminRightsChecker,
    IUserConverterService userConverterService,
    IChatConverterService chatConverterService,
    ILogger<JoinGroupCallHandler> logger)
    : RpcResultObjectHandler<RequestJoinGroupCall, IUpdates>
{
    private readonly IMongoCollection<GroupCallDocument> _groupCallCollection =
        mongoDatabase.GetCollection<GroupCallDocument>("group_calls");
    private readonly IMongoCollection<BsonDocument> _groupCallBsonCollection =
        mongoDatabase.GetCollection<BsonDocument>("group_calls");

    protected override async Task<IUpdates> HandleCoreAsync(IRequestInput input, RequestJoinGroupCall obj)
    {
        var filter = GroupCallStateHelper.Filter(obj.Call, input, accessHashHelper2);
        var groupCall = await _groupCallCollection.Find(filter).FirstOrDefaultAsync();
        if (groupCall == null || !groupCall.Active)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        // A scheduled call cannot be joined until an admin starts it.
        if (groupCall.ScheduleDate.HasValue)
        {
            RpcErrors.RpcErrors403.GroupcallForbidden.ThrowRpcError();
            return null!;
        }

        if (!await EnsureCanJoinCallAsync(input, obj.Call, groupCall))
        {
            return null!;
        }

        var joinAs = GroupCallStateHelper.NormalizeSelf(peerHelper.GetPeer(obj.JoinAs, input.UserId));
        if (joinAs == null)
        {
            RpcErrors.RpcErrors400.JoinAsPeerInvalid.ThrowRpcError();
            return null!;
        }

        var currentDate = GroupCallStateHelper.CurrentDate();
        var ssrc = GroupCallStateHelper.CreateParticipantSource(
            obj.Params?.Data,
            groupCall,
            input.UserId);

        if (joinAs.PeerType != PeerType.User &&
            !(joinAs.PeerType == PeerType.Channel &&
              await channelAdminRightsChecker.HasChatAdminRightAsync(joinAs.PeerId, input.UserId, _ => true)))
        {
            RpcErrors.RpcErrors400.JoinAsPeerInvalid.ThrowRpcError();
            return null!;
        }

        // In a join_muted call only those who can manage it may speak; everybody else joins muted and has
        // to be allowed to unmute by an admin. Rejoining does not lift an admin's mute.
        var previous = GroupCallStateHelper.FindParticipantByUser(groupCall, input.UserId);
        var mutedByAdmin = previous?.MutedByAdmin == true ||
                           (groupCall.JoinMuted &&
                            !await GroupCallStateHelper.CanManageCallAsync(groupCall, input.UserId, channelAdminRightsChecker));

        var participant = new GroupCallParticipantDoc
        {
            UserId = input.UserId,
            PeerId = joinAs.PeerId,
            PeerType = (int)joinAs.PeerType,
            Source = ssrc,
            Muted = obj.Muted || mutedByAdmin,
            MutedByAdmin = mutedByAdmin,
            VideoStopped = obj.VideoStopped,
            Date = currentDate,
            ParamsJson = obj.Params?.Data,
            PublicKey = obj.PublicKey?.ToArray()
        };

        groupCall = await AddParticipantAtomicallyAsync(groupCall, participant, input.UserId, obj.Block?.ToArray());
        participant = GroupCallStateHelper.FindParticipantByUser(groupCall, input.UserId, ssrc) ?? participant;

        var pushUpdatesList = new List<IUpdate>();
        var responseUpdates = new List<IUpdate>
        {
            GroupCallStateHelper.CreateCallUpdate(groupCall, input.UserId, peerHelper)
        };

        if (!groupCall.RtmpStream)
        {
            var participantsUpdate = GroupCallStateHelper.CreateParticipantsUpdate(groupCall, input.UserId, peerHelper, [participant], true);
            pushUpdatesList.Add(participantsUpdate);
            responseUpdates.Add(participantsUpdate);
        }

        if (groupCall.Conference && obj.Block is { } chainBlock)
        {
            var chainUpdate = GroupCallStateHelper.CreateChainBlocksUpdate(
                groupCall,
                0,
                [chainBlock.ToArray()],
                groupCall.ChainBlocks.Count(block => block.SubChainId == 0));
            pushUpdatesList.Add(chainUpdate);
            responseUpdates.Add(chainUpdate);
        }

        if (pushUpdatesList.Count > 0)
        {
            var pushUpdates = GroupCallStateHelper.Updates(pushUpdatesList.ToArray());
            await GroupCallStateHelper.PushUpdatesToCallSubscribersAsync(
                objectMessageSender,
                groupCall,
                pushUpdates,
                input.UserId);
        }

        var connectionUpdate = GroupCallStateHelper.CreateConnectionUpdate(groupCall);
        if (connectionUpdate != null)
        {
            responseUpdates.Add(connectionUpdate);
        }
        else
        {
            logger.LogWarning(
                "Group call {CallId}: user {UserId} joined, but no media server (SFU) is deployed, so no connection parameters were sent",
                groupCall.CallId,
                input.UserId);
        }

        var response = GroupCallStateHelper.Updates(responseUpdates.ToArray());
        (response.Users, response.Chats) = await GroupCallStateHelper.LoadUsersAndChatsAsync(
            input,
            userConverterService,
            chatConverterService,
            [input.UserId],
            [
                ..GroupCallStateHelper.GetParticipantChannelIds([participant]),
                ..groupCall.PeerType == (int)PeerType.Channel ? [groupCall.PeerId] : Array.Empty<long>()
            ]);

        return response;
    }

    private async Task<bool> EnsureCanJoinCallAsync(
        IRequestInput input,
        IInputGroupCall inputCall,
        GroupCallDocument groupCall)
    {
        // Note: conference calls must NOT be admitted merely for being conferences — that made the
        // invite checks below unreachable and let any user join any private conference. Slug and
        // invite-message lookups already imply a valid invite.
        if (inputCall is TInputGroupCallSlug or TInputGroupCallInviteMessage ||
            groupCall.CreatorId == input.UserId ||
            groupCall.InvitedUserIds.Contains(input.UserId) ||
            groupCall.InviteMessages.Any(p => p.UserId == input.UserId && !p.Declined))
        {
            return true;
        }

        if ((PeerType)groupCall.PeerType == PeerType.Channel)
        {
            return !await channelAppService.SendRpcErrorIfNotChannelMemberAsync(input, groupCall.PeerId);
        }

        // A conference that reached this point carries no invite for the caller.
        if (groupCall.Conference)
        {
            return false;
        }

        return true;
    }

    private async Task<GroupCallDocument> AddParticipantAtomicallyAsync(
        GroupCallDocument groupCall,
        GroupCallParticipantDoc participant,
        long userId,
        byte[]? chainBlock)
    {
        var participantDoc = participant.ToBsonDocument();
        var controlledByUserCondition = CreateControlledByUserCondition(groupCall, userId);
        var set = new BsonDocument
        {
            ["Participants"] = new BsonDocument("$concatArrays", new BsonArray
            {
                new BsonDocument("$filter", new BsonDocument
                {
                    ["input"] = new BsonDocument("$ifNull", new BsonArray { "$Participants", new BsonArray() }),
                    ["as"] = "participant",
                    ["cond"] = new BsonDocument("$not", new BsonArray { controlledByUserCondition })
                }),
                new BsonArray { participantDoc }
            }),
            ["InvitedUserIds"] = new BsonDocument("$filter", new BsonDocument
            {
                ["input"] = new BsonDocument("$ifNull", new BsonArray { "$InvitedUserIds", new BsonArray() }),
                ["as"] = "invitedUserId",
                ["cond"] = new BsonDocument("$ne", new BsonArray { "$$invitedUserId", userId })
            }),
            ["Version"] = new BsonDocument("$add", new BsonArray { "$Version", 1 })
        };

        if (chainBlock != null)
        {
            set["ChainBlocks"] = new BsonDocument("$concatArrays", new BsonArray
            {
                new BsonDocument("$ifNull", new BsonArray { "$ChainBlocks", new BsonArray() }),
                new BsonArray
                {
                    new BsonDocument
                    {
                        ["SubChainId"] = 0,
                        ["Block"] = new BsonBinaryData(chainBlock)
                    }
                }
            });
        }

        var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(
            new[] { new BsonDocument("$set", set) });
        var updated = await _groupCallBsonCollection.FindOneAndUpdateAsync(
            new BsonDocument
            {
                ["_id"] = groupCall.CallId,
                ["Active"] = true
            },
            pipeline,
            new FindOneAndUpdateOptions<BsonDocument>
            {
                ReturnDocument = ReturnDocument.After
            });

        if (updated == null)
        {
            RpcErrors.RpcErrors400.GroupcallInvalid.ThrowRpcError();
            return null!;
        }

        return BsonSerializer.Deserialize<GroupCallDocument>(updated);
    }

    private static BsonDocument CreateControlledByUserCondition(GroupCallDocument groupCall, long userId)
    {
        var conditions = new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { "$$participant.UserId", userId }),
            new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$$participant.PeerType", (int)PeerType.User }),
                new BsonDocument("$eq", new BsonArray { "$$participant.PeerId", userId })
            })
        };

        if (groupCall.CreatorId == userId)
        {
            conditions.Add(new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$eq", new BsonArray { "$$participant.UserId", 0 }),
                new BsonDocument("$eq", new BsonArray { "$$participant.PeerType", groupCall.PeerType }),
                new BsonDocument("$eq", new BsonArray { "$$participant.PeerId", groupCall.PeerId })
            }));
        }

        return new BsonDocument("$or", conditions);
    }
}
