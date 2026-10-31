using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.QueryServer.DomainEventHandlers;

public class UserDomainEventHandler(
    IObjectMessageSender objectMessageSender,
    ICommandBus commandBus,
    IIdGenerator idGenerator,
    IAckCacheService ackCacheService,
    IEventBus eventBus,
    ILogger<UserDomainEventHandler> logger,
    IPhotoAppService photoAppService,
    ILayeredService<IPhotoConverter> photoLayeredConverter,
    ILayeredService<IAuthorizationConverter> layeredAuthorizationService,
    IQueryProcessor queryProcessor,
    IEmojiStatusResolver emojiStatusResolver,
    IUserConverterService userConverterService,
    IUsernameUpdateNotifier usernameUpdateNotifier)
    : DomainEventHandlerBase(objectMessageSender,
            commandBus,
            idGenerator,
            ackCacheService),
        ISubscribeSynchronousTo<UserAggregate, UserId, UserCreatedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserProfileUpdatedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserNameUpdatedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserProfilePhotoChangedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserProfilePhotoUploadedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserEmojiStatusUpdatedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserRecentEmojiStatusesClearedEvent>,
        ISubscribeSynchronousTo<UserAggregate, UserId, UserColorUpdatedEvent>
{
    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserCreatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "User created successfully, userId: {UserId}  phoneNumber: {PhoneNumber} firstName: {FirstName} lastName: {LastName}",
            domainEvent.AggregateEvent.UserId,
            domainEvent.AggregateEvent.PhoneNumber,
            domainEvent.AggregateEvent.FirstName,
            domainEvent.AggregateEvent.LastName
        );

        var userId = domainEvent.AggregateEvent.UserId;

        await eventBus.PublishAsync(new UserSignUpSuccessIntegrationEvent(
            domainEvent.AggregateEvent.RequestInfo.AuthKeyId,
            domainEvent.AggregateEvent.RequestInfo.PermAuthKeyId,
            userId));
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);
        user.Self = true;
        var r = layeredAuthorizationService.GetConverter(domainEvent.AggregateEvent.RequestInfo.Layer)
            .CreateAuthorization(user);
        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo,
            r,
            domainEvent.AggregateEvent.UserId);
    }

    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserNameUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var userId = domainEvent.AggregateEvent.RequestInfo.UserId;
        if (userId == 0)
        {
            return;
        }
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);

        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, user);

        // account.updateUsername answers with the new user object to the calling session only, so
        // without this the other sessions and every contact keep the previous username.
        // See https://corefork.telegram.org/api/peers#handling-updates
        var userItem = domainEvent.AggregateEvent.UserItem;
        await usernameUpdateNotifier.NotifyUserNameChangedAsync(userId,
            domainEvent.AggregateEvent.RequestInfo.PermAuthKeyId,
            new UserNameSnapshot(userItem.FirstName, userItem.LastName, userItem.UserName));
    }

    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserProfilePhotoChangedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var userId = domainEvent.AggregateEvent.RequestInfo.UserId;
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);
        var photoReadModel = await photoAppService.GetAsync(domainEvent.AggregateEvent.PhotoId);

        var photo = new MyTelegram.Schema.Photos.TPhoto
        {
            Photo = photoLayeredConverter.GetConverter(domainEvent.AggregateEvent.RequestInfo.Layer).ToPhoto(photoReadModel),
            Users = [user]
        };

        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, photo);
    }

    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserProfilePhotoUploadedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var userId = domainEvent.AggregateEvent.RequestInfo.UserId;
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);
        var photoReadModel = await photoAppService.GetAsync(domainEvent.AggregateEvent.PhotoId);

        var photo = new MyTelegram.Schema.Photos.TPhoto
        {
            Photo = photoLayeredConverter.GetConverter(domainEvent.AggregateEvent.RequestInfo.Layer).ToPhoto(photoReadModel),
            Users = [user]
        };

        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, photo);
    }

    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserProfileUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var userId = domainEvent.AggregateEvent.RequestInfo.UserId;
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);
        await SendRpcMessageToClientAsync(domainEvent.AggregateEvent.RequestInfo, user, domainEvent.AggregateEvent.UserId);
    }

    /// <summary>
    /// Delivers <c>updateUserEmojiStatus</c> to the user's own sessions and to everyone who has them
    /// in their contact list, so a new
    /// <a href="https://core.telegram.org/api/emoji-status">emoji status</a> shows up without a
    /// forced refetch. The RPC result of the triggering method is sent as well.
    /// </summary>
    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserEmojiStatusUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var requestInfo = domainEvent.AggregateEvent.RequestInfo;
        var userId = domainEvent.AggregateEvent.UserId;
        var emojiStatus = await emojiStatusResolver.ResolveAsync(domainEvent.AggregateEvent.EmojiStatus,
            requestInfo.Layer);

        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateUserEmojiStatus
            {
                UserId = userId,
                EmojiStatus = emojiStatus ?? new TEmojiStatusEmpty()
            }),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp()
        };

        // The background expiration service publishes the same command with no client behind it, so
        // the rpc result is only sent when the change came from an actual request.
        if (!string.IsNullOrEmpty(requestInfo.ConnectionId))
        {
            await SendRpcMessageToClientAsync(requestInfo, new TBoolTrue(), userId);
        }

        await PushUpdatesToPeerAsync(new Peer(PeerType.User, userId), updates);

        // The recent list changed too, but only the owner cares about it.
        await PushUpdatesToPeerAsync(new Peer(PeerType.User, userId), new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateRecentEmojiStatuses()),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp()
        });

        var contactUserIds = await queryProcessor.ProcessAsync(
            new GetContactSelfUserIdListByTargetUserIdQuery(userId), cancellationToken);
        foreach (var contactUserId in contactUserIds.Where(p => p != userId).Distinct())
        {
            await PushUpdatesToPeerAsync(new Peer(PeerType.User, contactUserId), updates);
        }
    }

    /// <summary>
    /// Tells the user's other sessions that the recently used
    /// <a href="https://core.telegram.org/api/emoji-status">emoji statuses</a> were cleared.
    /// </summary>
    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserRecentEmojiStatusesClearedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var requestInfo = domainEvent.AggregateEvent.RequestInfo;
        var userId = domainEvent.AggregateEvent.UserId;

        await SendRpcMessageToClientAsync(requestInfo, new TBoolTrue(), userId);
        await PushUpdatesToPeerAsync(new Peer(PeerType.User, userId), new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateRecentEmojiStatuses()),
            Users = new TVector<IUser>(),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp()
        });
    }
    /// <summary>
    /// Notifies the user's other sessions about a changed
    /// <a href="https://core.telegram.org/api/colors">peer color</a>. There is no per-field color
    /// update constructor in the schema, so the generic updateUser plus the re-converted user is
    /// the update clients expect here.
    /// </summary>
    public async Task HandleAsync(IDomainEvent<UserAggregate, UserId, UserColorUpdatedEvent> domainEvent,
        CancellationToken cancellationToken)
    {
        var userId = domainEvent.AggregateEvent.UserId;
        var user = await userConverterService.GetUserAsync(domainEvent.AggregateEvent.RequestInfo, userId, layer: domainEvent.AggregateEvent.RequestInfo.Layer);

        var updates = new TUpdates
        {
            Updates = new TVector<IUpdate>(new TUpdateUser { UserId = userId }),
            Users = new TVector<IUser>(user),
            Chats = new TVector<IChat>(),
            Date = DateTime.UtcNow.ToTimestamp()
        };

        await PushUpdatesToPeerAsync(new Peer(PeerType.User, userId), updates);
    }
}
