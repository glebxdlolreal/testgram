using MongoDB.Bson;
using MongoDB.Driver;

namespace MyTelegram.Messenger.Handlers.LatestLayer.Contacts;
/// <summary>
/// Resolve a @username to get peer info
/// Possible errors
/// Code Type Description
/// 400 CONNECTION_LAYER_INVALID Layer invalid.
/// 400 STARREF_EXPIRED The specified referral link is invalid.
/// 400 USERNAME_INVALID The provided username is not valid.
/// 400 USERNAME_NOT_OCCUPIED The provided username is not occupied.
/// <para><c>See <a href="https://corefork.telegram.org/method/contacts.resolveUsername"/> </c></para>
/// </summary>
/// <remarks>
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class ResolveUsernameHandler(
    IQueryProcessor queryProcessor,
    IChatConverterService chatConverterService,
    IUserConverterService userConverterService,
    IUserAppService userAppService,
    IChannelAppService channelAppService,
    IPhotoAppService photoAppService,
    IMongoDatabase mongoDatabase) : RpcResultObjectHandler<MyTelegram.Schema.Contacts.RequestResolveUsername, MyTelegram.Schema.Contacts.IResolvedPeer>
{
    protected override async Task<IResolvedPeer> HandleCoreAsync(IRequestInput input, RequestResolveUsername obj)
    {
        // Handle affiliate link referer parameter
        if (!string.IsNullOrEmpty(obj.Referer))
        {
            await ProcessAffiliateReferralAsync(input.UserId, obj.Username, obj.Referer);
        }

        if (!string.IsNullOrEmpty(obj.Username))
        {
            var userNameReadModel = await queryProcessor.ProcessAsync(new GetUserNameByNameQuery(obj.Username), default);
            if (userNameReadModel != null)
            {
                switch (userNameReadModel.PeerType)
                {
                    case PeerType.User:
                        // A collectible that has not been activated is visible to its owner only.
                        // See https://corefork.telegram.org/api/fragment
                        if (userNameReadModel.PeerId != input.UserId &&
                            (await userAppService.GetAsync((long?)userNameReadModel.PeerId))?.Usernames.IsDisabledCollectible(obj.Username) == true)
                        {
                            break;
                        }

                        var user = await userConverterService.GetUserAsync(input, userNameReadModel.PeerId, false, false, input.Layer);
                        return new TResolvedPeer
                        {
                            Chats = new TVector<IChat>(),
                            Peer = new TPeerUser
                            {
                                UserId = userNameReadModel.PeerId
                            },
                            Users = new TVector<IUser>(user)
                        };
                    case PeerType.Channel:
                        {
                            var channelReadModel = await channelAppService.GetAsync(userNameReadModel.PeerId);
                            if (channelReadModel != null &&
                                (channelReadModel.CreatorId == input.UserId ||
                                 !channelReadModel.Usernames.IsDisabledCollectible(obj.Username)))
                            {
                                var photoReadModel = await photoAppService.GetAsync(channelReadModel.PhotoId);
                                var channelMemberReadModel = await queryProcessor.ProcessAsync(new GetChannelMemberByUserIdQuery(channelReadModel.ChannelId, input.UserId));
                                return new TResolvedPeer
                                {
                                    Chats = new TVector<IChat>(chatConverterService.ToChannel(input, channelReadModel, photoReadModel, channelMemberReadModel, channelMemberReadModel?.Left ?? true, input.Layer)),
                                    Peer = new TPeerChannel
                                    {
                                        ChannelId = userNameReadModel.PeerId
                                    },
                                    Users = new TVector<IUser>()
                                };
                            }
                        }

                        break;
                    default:
                        throw new ArgumentOutOfRangeException($"Unknown PeerType: {userNameReadModel.PeerType}");
                }
            }
        }

        RpcErrors.RpcErrors400.UsernameNotOccupied.ThrowRpcError();
        return null !;
    }

    private async Task ProcessAffiliateReferralAsync(long userId, string? botUsername, string referer)
    {
        try
        {
            // Extract link_id from referer (format: "linkId" or full URL)
            var linkId = referer.Contains("?") ? referer.Split('?').Last().Split('=').Last() : referer;

            // Find the affiliate connection by link_id
            var connectionsCol = mongoDatabase.GetCollection<BsonDocument>("connected_star_ref_bots");
            var filter = Builders<BsonDocument>.Filter.Eq("link_id", linkId);
            var connection = await connectionsCol.Find(filter).FirstOrDefaultAsync();

            if (connection == null)
            {
                // Link not found or expired
                RpcErrors.RpcErrors400.StarrefExpired.ThrowRpcError();
                return;
            }

            // Check if link is revoked
            if (connection.Contains("revoked") && connection["revoked"].AsBoolean)
            {
                RpcErrors.RpcErrors400.StarrefExpired.ThrowRpcError();
                return;
            }

            // Check if user already used this bot (first-time referral only)
            var userBotsCol = mongoDatabase.GetCollection<BsonDocument>("user_bot_starts");
            var userBotFilter = Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("user_id", userId),
                Builders<BsonDocument>.Filter.Eq("bot_id", connection["bot_id"].AsInt64)
            );
            var existingStart = await userBotsCol.Find(userBotFilter).FirstOrDefaultAsync();

            if (existingStart != null)
            {
                // User already started this bot before - not a valid referral
                return;
            }

            // Record the referral
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var referralDoc = new BsonDocument
            {
                ["_id"] = $"referral-{userId}-{connection["bot_id"].AsInt64}",
                ["user_id"] = userId,
                ["bot_id"] = connection["bot_id"].AsInt64,
                ["affiliate_peer_id"] = connection["peer_id"].AsInt64,
                ["affiliate_peer_type"] = connection["peer_type"].AsInt32,
                ["link_id"] = linkId,
                ["date"] = now,
                ["commission_permille"] = connection["commission_permille"].AsInt32,
                ["duration_months"] = connection["duration_months"].AsInt32,
                ["expires_at"] = connection["duration_months"].AsInt32 > 0
                    ? now + (connection["duration_months"].AsInt32 * 30 * 24 * 60 * 60)
                    : 0 // 0 = no expiration
            };

            var referralsCol = mongoDatabase.GetCollection<BsonDocument>("star_referrals");
            await referralsCol.InsertOneAsync(referralDoc);

            // Update connection statistics
            var update = Builders<BsonDocument>.Update.Inc("participants", 1);
            await connectionsCol.UpdateOneAsync(filter, update);

            // Mark that user started this bot
            await userBotsCol.InsertOneAsync(new BsonDocument
            {
                ["_id"] = $"userbot-{userId}-{connection["bot_id"].AsInt64}",
                ["user_id"] = userId,
                ["bot_id"] = connection["bot_id"].AsInt64,
                ["date"] = now
            });
        }
        catch (RpcException)
        {
            throw; // Re-throw RPC errors
        }
        catch (Exception)
        {
            // Silently ignore other errors - don't break username resolution
        }
    }
}