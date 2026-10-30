namespace MyTelegram.Messenger.Handlers.LatestLayer.Users;
/// <summary>
/// Returns basic user info according to their identifiers.
/// Possible errors
/// Code Type Description
/// 400 CHANNEL_INVALID The provided channel is invalid.
/// 400 CHANNEL_MONOFORUM_UNSUPPORTED <a href="https://corefork.telegram.org/api/channel#monoforums">Monoforums</a> do not support this feature.
/// 400 CHANNEL_PRIVATE You haven't joined this channel/supergroup.
/// 400 FROM_MESSAGE_BOT_DISABLED Bots can't use fromMessage min constructors.
/// 400 MSG_ID_INVALID Invalid message ID provided.
/// 400 PEER_ID_INVALID The provided peer id is invalid.
/// 400 USER_BANNED_IN_CHANNEL You're banned from sending messages in supergroups/channels.
/// <para><c>See <a href="https://corefork.telegram.org/method/users.getUsers"/> </c></para>
/// </summary>
/// <remarks>
/// One of the three bulk methods clients use to refresh their
/// <a href="https://corefork.telegram.org/api/peers#peer-info-database">peer info database</a>, so the
/// reply must line up position-by-position with the request: an id that cannot be resolved comes back
/// as <c>userEmpty</c> rather than being dropped.
/// Access: [User ✔] [Bot ✔] [Anonymous ✖]
/// </remarks>
internal sealed class GetUsersHandler(
    IUserConverterService userConverterService,
    IAccessHashHelper2 accessHashHelper,
    IFromMessagePeerResolver fromMessagePeerResolver,
    MyTelegram.Messenger.Services.AntiSpam.IAntiSpamActorService antiSpamActorService)
    : RpcResultObjectHandler<MyTelegram.Schema.Users.RequestGetUsers, TVector<MyTelegram.Schema.IUser>>
{
    /// <summary>
    /// Built-in peers whose info may be fetched with a zero access hash even by ordinary users.
    /// See https://corefork.telegram.org/api/peers#manual-refreshes
    /// </summary>
    private static readonly HashSet<long> WellKnownUserIds =
    [
        MyTelegramConsts.NotificationServiceUserId,
        MyTelegramConsts.RepliesServiceUserId,
        MyTelegramConsts.GroupAnonymousBotUserId,
        MyTelegramConsts.AnonymousUserId,
        MyTelegramConsts.DefaultSupportUserId
    ];

    protected override async Task<TVector<IUser>> HandleCoreAsync(IRequestInput input, MyTelegram.Schema.Users.RequestGetUsers obj)
    {
        var userIds = new List<long>();
        var result = new TVector<IUser>();

        foreach (var inputUser in obj.Id)
        {
            switch (inputUser)
            {
                case TInputUserSelf:
                    userIds.Add(input.UserId);
                    result.Add(new TUserEmpty { Id = input.UserId });
                    break;

                case TInputUser tInputUser:
                {
                    // A zero access hash is only accepted for the built-in service peers; for
                    // anybody else it means the caller never legitimately received the user.
                    if (tInputUser.UserId == MyTelegramConsts.AntiSpamBotUserId)
                    {
                        await antiSpamActorService.EnsureAvailableAsync();
                    }
                    else if (!WellKnownUserIds.Contains(tInputUser.UserId))
                    {
                        await accessHashHelper.CheckAccessHashAsync(input, tInputUser.UserId,
                            tInputUser.AccessHash, AccessHashType.User);
                    }

                    userIds.Add(tInputUser.UserId);
                    result.Add(new TUserEmpty { Id = tInputUser.UserId });
                    break;
                }

                case TInputUserFromMessage inputUserFromMessage:
                {
                    var userId = await fromMessagePeerResolver.ResolveUserIdAsync(input,
                        inputUserFromMessage.Peer, inputUserFromMessage.MsgId, inputUserFromMessage.UserId);

                    userIds.Add(userId);
                    result.Add(new TUserEmpty { Id = userId });
                    break;
                }

                // inputUserEmpty asks for nothing, so it answers with nothing — it must not be
                // silently turned into the caller themselves.
                case TInputUserEmpty:
                default:
                    result.Add(new TUserEmpty { Id = 0 });
                    break;
            }
        }

        if (userIds.Count == 0)
        {
            return result;
        }

        var users = (await userConverterService.GetUserListAsync(input, userIds.Distinct().ToList(), false, false,
            input.Layer)).ToDictionary(k => k.Id);

        for (var i = 0; i < result.Count; i++)
        {
            if (result[i].Id != 0 && users.TryGetValue(result[i].Id, out var user))
            {
                result[i] = user;
            }
        }

        return result;
    }
}
