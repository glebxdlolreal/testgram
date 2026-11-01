using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyTelegram.Messenger.Services.Entities;
using Moq;
using MyTelegram.Abstractions;
using MyTelegram.Core;
using MyTelegram.Messenger.Services;
using MyTelegram.Messenger.Services.Interfaces;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Messenger.Services.TopPeers;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using System.Threading;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Shared fixtures and fakes for the Phone (calls / group-calls) handler and lifecycle tests.
///
/// Provides:
///   * <see cref="InMemoryMongoStore"/> + <see cref="CreateDatabase"/> - an in-memory
///     <see cref="IMongoDatabase"/> exposing the <c>call_sessions</c> and <c>group_calls</c>
///     collections (and any other named collection) with real filter/update/sort semantics.
///   * <see cref="CapturingObjectMessageSender"/> - an <see cref="IObjectMessageSender"/> that records
///     every pushed update (constructor types, target user ids, excludeUserId / excludeAuthKeyId).
///   * <see cref="FakeMessageAppService"/> - a capturing <see cref="IMessageAppService"/>.
///   * <see cref="FakeAccessHashHelper2"/> + <see cref="FakeUserAccessHashKeyCache"/> - deterministic
///     per-user access-hash fakes.
///   * <see cref="RequestInputBuilder"/> - a builder for <see cref="IRequestInput"/> (auth key id,
///     user id, layer, and multiple sessions / devices per user).
/// </summary>
public static class PhoneTestFixtures
{

    /// <summary>
    /// Fills in a <see cref="NullLogger{T}"/> for every <c>ILogger&lt;T&gt;</c> constructor parameter the
    /// handler declares, so tests only pass the dependencies they actually exercise. Returns
    /// <paramref name="args"/> unchanged when the caller already supplied every parameter.
    /// </summary>
    /// <summary>
    /// Fills in a <see cref="NullLogger{T}"/> for every <c>ILogger&lt;T&gt;</c> constructor parameter the
    /// handler declares, plus a no-op <see cref="ITopPeerUsageRecorder"/> for the top-peer rating a call
    /// contributes to, so tests only pass the dependencies they actually exercise. Returns
    /// <paramref name="args"/> unchanged when the caller already supplied every parameter.
    /// </summary>
    public static object[] WithNullLoggers(Type handlerType, object[] args)
    {
        var constructor = handlerType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var parameters = constructor.GetParameters();
        if (parameters.Length == args.Length)
        {
            return args;
        }

        var filled = new object[parameters.Length];
        var next = 0;
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;
            if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(ILogger<>))
            {
                var nullLogger = typeof(NullLogger<>).MakeGenericType(parameterType.GetGenericArguments()[0]);
                // NullLogger<T>.Instance is a static field, not a property.
                filled[i] = nullLogger.GetField("Instance")!.GetValue(null)!;
            }
            else if (parameterType == typeof(ITopPeerUsageRecorder))
            {
                filled[i] = new Mock<ITopPeerUsageRecorder>(MockBehavior.Loose).Object;
            }
            else
            {
                filled[i] = args[next++];
            }
        }

        return filled;
    }

    /// <summary>
    /// Builds a group-call handler by matching the supplied dependencies to the constructor by type,
    /// filling in a <see cref="NullLogger{T}"/> for loggers, a permissive
    /// <see cref="IChannelAdminRightsChecker"/> for the manage-call gate, and a
    /// <see cref="PeerHelper"/> for peer resolution. Tests that exercise the authorization gate
    /// itself should construct the handler directly with their own checker instead.
    /// </summary>
    public static object CreateGroupCallHandler(Type handlerType, params object[] args)
    {
        var constructor = handlerType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var supplied = args.ToList();
        var filled = new object[constructor.GetParameters().Length];
        var parameters = constructor.GetParameters();

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = parameters[i].ParameterType;

            var match = supplied.FirstOrDefault(a => a != null && parameterType.IsInstanceOfType(a));
            if (match != null)
            {
                supplied.Remove(match);
                filled[i] = match;
                continue;
            }

            if (parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(ILogger<>))
            {
                var nullLogger = typeof(NullLogger<>).MakeGenericType(parameterType.GetGenericArguments()[0]);
                filled[i] = nullLogger.GetField("Instance")!.GetValue(null)!;
                continue;
            }

            if (parameterType == typeof(IChannelAdminRightsChecker))
            {
                filled[i] = PermissiveAdminRightsChecker();
                continue;
            }

            if (parameterType == typeof(IPeerHelper))
            {
                filled[i] = new PeerHelper();
                continue;
            }

            if (parameterType == typeof(global::EventFlow.Queries.IQueryProcessor))
            {
                // No read model answers: a channel looked up through it is not a broadcast channel.
                filled[i] = new Mock<global::EventFlow.Queries.IQueryProcessor>(MockBehavior.Loose).Object;
                continue;
            }

            if (parameterType == typeof(ITopPeerUsageRecorder))
            {
                filled[i] = new Mock<ITopPeerUsageRecorder>(MockBehavior.Loose).Object;
                continue;
            }

            if (TryCreateDefaultDependency(parameterType) is { } dependency)
            {
                filled[i] = dependency;
                continue;
            }

            throw new InvalidOperationException(
                $"{handlerType.Name} needs a {parameterType.Name} that the test did not supply.");
        }

        return Activator.CreateInstance(handlerType, filled)!;
    }

    /// <summary>
    /// Defaults for dependencies most group-call tests do not exercise: the deterministic access-hash fake,
    /// converters that return no users or chats, and a channel service that treats everybody as a member.
    /// </summary>
    public static object? TryCreateDefaultDependency(Type parameterType)
    {
        if (parameterType == typeof(IAccessHashHelper2))
        {
            return new FakeAccessHashHelper2();
        }

        if (parameterType == typeof(MyTelegram.Messenger.Converters.ConverterServices.IUserConverterService))
        {
            var users = new Mock<MyTelegram.Messenger.Converters.ConverterServices.IUserConverterService>();
            users.Setup(x => x.GetUserListAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<List<long>>(),
                    It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<int>()))
                .ReturnsAsync([]);
            return users.Object;
        }

        if (parameterType == typeof(MyTelegram.Messenger.Converters.ConverterServices.IChatConverterService))
        {
            var chats = new Mock<MyTelegram.Messenger.Converters.ConverterServices.IChatConverterService>();
            chats.Setup(x => x.GetChannelListAsync(It.IsAny<IRequestWithAccessHashKeyId>(), It.IsAny<List<long>>(),
                    It.IsAny<IReadOnlyCollection<IChannelMemberReadModel>?>(), It.IsAny<int>()))
                .ReturnsAsync([]);
            return chats.Object;
        }

        if (parameterType == typeof(IChannelAppService))
        {
            var channels = new Mock<IChannelAppService>();
            channels.Setup(x => x.SendRpcErrorIfNotChannelMemberAsync(It.IsAny<IRequestInput>(), It.IsAny<long>()))
                .ReturnsAsync(false);
            return channels.Object;
        }

        if (parameterType == typeof(IChannelAdminRightsChecker))
        {
            return PermissiveAdminRightsChecker();
        }

        if (parameterType == typeof(IGroupCallMediaServer))
        {
            return new FakeGroupCallMediaServer { IsConfigured = false };
        }

        return null;
    }

    /// <summary>
    /// An <see cref="IChannelAdminRightsChecker"/> that authorizes every request, for tests whose
    /// subject is not the authorization gate.
    /// </summary>
    public static IChannelAdminRightsChecker PermissiveAdminRightsChecker()
    {
        var checker = new Mock<IChannelAdminRightsChecker>();
        checker
            .Setup(x => x.HasChatAdminRightAsync(It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<Func<ChatAdminRights, bool>>()))
            .ReturnsAsync(true);
        return checker.Object;
    }

    public const string CallSessionsCollectionName = "call_sessions";
    public const string GroupCallsCollectionName = "group_calls";

    /// <summary>
    /// Creates a fresh in-memory <see cref="IMongoDatabase"/> backed by <paramref name="store"/>.
    /// The <c>call_sessions</c> and <c>group_calls</c> collections are available immediately via
    /// <see cref="IMongoDatabase.GetCollection{TDocument}(string, MongoCollectionSettings)"/>.
    /// </summary>
    public static IMongoDatabase CreateDatabase(out InMemoryMongoStore store)
    {
        store = new InMemoryMongoStore();
        return store.Database;
    }

    /// <summary>Creates an in-memory database and returns the backing store together with it.</summary>
    public static InMemoryMongoStore CreateStore() => new();

    /// <summary>Starts building a request input for the given user.</summary>
    public static RequestInputBuilder RequestInput(long userId) => new(userId);

    /// <summary>
    /// Builds one <see cref="IRequestInput"/> per device for the same user. Each returned input
    /// shares the <paramref name="userId"/> / <paramref name="accessHashKeyId"/> but has a distinct
    /// session id, temp auth key id and (optionally) device type - modelling several logged-in devices.
    /// </summary>
    public static IReadOnlyList<IRequestInput> CreateDeviceInputs(
        long userId,
        int deviceCount,
        long accessHashKeyId = 0,
        int layer = MyTelegramServerDefaultLayer)
    {
        if (accessHashKeyId == 0)
        {
            accessHashKeyId = DefaultAccessHashKeyId(userId);
        }

        var devices = new List<IRequestInput>(deviceCount);
        for (var i = 0; i < deviceCount; i++)
        {
            devices.Add(new RequestInputBuilder(userId)
                .WithAccessHashKeyId(accessHashKeyId)
                .WithLayer(layer)
                .WithSession(sessionId: userId * 1000 + i + 1, authKeyId: userId * 1_000_000 + i + 1)
                .WithDeviceType((DeviceType)((i % 3) + 1))
                .Build());
        }

        return devices;
    }

    public const int MyTelegramServerDefaultLayer = 214;

    internal static long DefaultAccessHashKeyId(long userId) => userId * 31 + 17;
}

/// <summary>
/// Fluent builder for <see cref="IRequestInput"/>. Defaults are deterministic and derived from the
/// user id so tests can create inputs without spelling out every field, while still allowing
/// multiple sessions / devices per user to be modelled.
/// </summary>
public sealed class RequestInputBuilder
{
    private long _userId;
    private long _accessHashKeyId;
    private long _authKeyId;
    private long _permAuthKeyId;
    private long _sessionId;
    private int _layer = PhoneTestFixtures.MyTelegramServerDefaultLayer;
    private string _connectionId = Guid.NewGuid().ToString("N");
    private ConnectionType _connectionType = ConnectionType.Generic;
    private DeviceType _deviceType = DeviceType.Android;
    private string _clientIp = "127.0.0.1";
    private long _reqMsgId;
    private int _seqNumber;
    private long _date = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public RequestInputBuilder(long userId)
    {
        _userId = userId;
        _accessHashKeyId = PhoneTestFixtures.DefaultAccessHashKeyId(userId);
        _permAuthKeyId = _accessHashKeyId;
        _authKeyId = userId * 7 + 3;
        _sessionId = userId * 13 + 5;
        _reqMsgId = userId * 17 + 9;
    }

    public RequestInputBuilder WithUserId(long userId)
    {
        _userId = userId;
        return this;
    }

    public RequestInputBuilder WithAccessHashKeyId(long accessHashKeyId)
    {
        _accessHashKeyId = accessHashKeyId;
        if (_permAuthKeyId == 0)
        {
            _permAuthKeyId = accessHashKeyId;
        }

        return this;
    }

    public RequestInputBuilder WithPermAuthKeyId(long permAuthKeyId)
    {
        _permAuthKeyId = permAuthKeyId;
        return this;
    }

    public RequestInputBuilder WithLayer(int layer)
    {
        _layer = layer;
        return this;
    }

    /// <summary>Sets the session id and temp auth key id for this device.</summary>
    public RequestInputBuilder WithSession(long sessionId, long authKeyId)
    {
        _sessionId = sessionId;
        _authKeyId = authKeyId;
        return this;
    }

    public RequestInputBuilder WithSessionId(long sessionId)
    {
        _sessionId = sessionId;
        return this;
    }

    public RequestInputBuilder WithAuthKeyId(long authKeyId)
    {
        _authKeyId = authKeyId;
        return this;
    }

    public RequestInputBuilder WithDeviceType(DeviceType deviceType)
    {
        _deviceType = deviceType;
        return this;
    }

    public RequestInputBuilder WithConnectionId(string connectionId)
    {
        _connectionId = connectionId;
        return this;
    }

    public RequestInputBuilder WithReqMsgId(long reqMsgId)
    {
        _reqMsgId = reqMsgId;
        return this;
    }

    public RequestInputBuilder WithClientIp(string clientIp)
    {
        _clientIp = clientIp;
        return this;
    }

    public IRequestInput Build()
    {
        return new RequestInput(
            _connectionId,
            _connectionType,
            Guid.NewGuid(),
            0u,
            _reqMsgId,
            _seqNumber,
            _userId,
            _authKeyId,
            _permAuthKeyId,
            _layer,
            _date,
            _deviceType,
            _clientIp,
            _sessionId,
            _accessHashKeyId);
    }

    public static implicit operator RequestInput(RequestInputBuilder builder) => (RequestInput)builder.Build();
}

/// <summary>
/// A single recorded <see cref="IObjectMessageSender.PushMessageToPeerAsync{TData}"/> invocation.
/// </summary>
public sealed record CapturedPush(
    Peer Peer,
    IObject Data,
    long? ExcludeAuthKeyId,
    long? ExcludeUserId,
    long? OnlySendToUserId,
    long? OnlySendToThisAuthKeyId,
    int Pts,
    int? Qts,
    long GlobalSeqNo,
    PushData? PushData,
    IReadOnlyList<long>? ExcludeUserIds)
{
    /// <summary>The target user id when the push is addressed to a user peer, otherwise null.</summary>
    public long? TargetUserId => Peer.PeerType == PeerType.User ? Peer.PeerId : null;

    /// <summary>The runtime type names of the update constructors carried by this push (if any).</summary>
    public IReadOnlyList<string> UpdateConstructorNames => ExtractUpdates(Data).Select(u => u.GetType().Name).ToList();

    /// <summary>The update constructors carried by this push (if the data is an <see cref="IUpdates"/>).</summary>
    public IReadOnlyList<IUpdate> Updates => ExtractUpdates(Data);

    public bool Carries<TUpdate>() where TUpdate : IUpdate => ExtractUpdates(Data).OfType<TUpdate>().Any();

    private static IReadOnlyList<IUpdate> ExtractUpdates(IObject data)
    {
        return data switch
        {
            TUpdates updates => updates.Updates.ToList(),
            TUpdateShort updateShort => [updateShort.Update],
            IUpdate single => [single],
            _ => []
        };
    }
}

/// <summary>
/// A capturing <see cref="IObjectMessageSender"/>. Records every pushed update - the constructor(s),
/// the target peer / user id, and the <c>excludeUserId</c> / <c>excludeAuthKeyId</c> exclusions used to
/// avoid echoing an update back to the originating device.
/// </summary>
public sealed class CapturingObjectMessageSender : IObjectMessageSender
{
    private readonly List<CapturedPush> _pushes = new();
    private readonly List<(long AuthKeyId, IObject Data)> _sessionPushes = new();
    private readonly List<(RequestInfo RequestInfo, IObject Data)> _rpcMessages = new();

    /// <summary>All recorded peer pushes, in order.</summary>
    public IReadOnlyList<CapturedPush> Pushes => _pushes;

    /// <summary>All recorded direct auth-key session pushes, in order.</summary>
    public IReadOnlyList<(long AuthKeyId, IObject Data)> SessionPushes => _sessionPushes;

    /// <summary>All recorded RPC replies sent back to the client.</summary>
    public IReadOnlyList<(RequestInfo RequestInfo, IObject Data)> RpcMessages => _rpcMessages;

    public void Clear()
    {
        _pushes.Clear();
        _sessionPushes.Clear();
        _rpcMessages.Clear();
    }

    /// <summary>All pushes addressed to the given user id.</summary>
    public IEnumerable<CapturedPush> PushesToUser(long userId) => _pushes.Where(p => p.TargetUserId == userId);

    /// <summary>Distinct user ids that received a push (user peers only).</summary>
    public IReadOnlyCollection<long> TargetUserIds =>
        _pushes.Where(p => p.TargetUserId.HasValue).Select(p => p.TargetUserId!.Value).Distinct().ToList();

    public Task PushMessageToPeerAsync<TData>(Peer peer,
        TData data,
        long? excludeAuthKeyId = null,
        long? excludeUserId = null,
        long? onlySendToUserId = null,
        long? onlySendToThisAuthKeyId = null,
        int pts = 0,
        int? qts = null,
        long globalSeqNo = 0,
        PushData? pushData = null,
        List<long>? excludeUserIds = null) where TData : IObject
    {
        _pushes.Add(new CapturedPush(
            peer,
            data,
            excludeAuthKeyId,
            excludeUserId,
            onlySendToUserId,
            onlySendToThisAuthKeyId,
            pts,
            qts,
            globalSeqNo,
            pushData,
            excludeUserIds?.ToList()));
        return Task.CompletedTask;
    }

    public Task PushSessionMessageToAuthKeyIdAsync<TData>(long authKeyId,
        TData data,
        int pts = 0,
        int? qts = null,
        long globalSeqNo = 0) where TData : IObject
    {
        _sessionPushes.Add((authKeyId, data));
        return Task.CompletedTask;
    }

    public Task SendFileDataToPeerAsync<TData>(RequestInfo requestInfo, TData data) where TData : IObject
    {
        _rpcMessages.Add((requestInfo, data));
        return Task.CompletedTask;
    }

    public Task SendMessageToPeerAsync<TData>(RequestInfo requestInfo, TData data) where TData : IObject
    {
        _rpcMessages.Add((requestInfo, data));
        return Task.CompletedTask;
    }

    public Task SendRpcMessageToClientAsync<TData>(RequestInfo requestInfo, TData data, int pts = 0) where TData : IObject
    {
        _rpcMessages.Add((requestInfo, data));
        return Task.CompletedTask;
    }

    public Task SendRpcMessageToClientAsync<TData>(
        string connectionId,
        long tempAuthKeyId,
        long sessionId,
        long reqMsgId,
        TData data,
        int pts = 0,
        long permAuthKeyId = 0) where TData : IObject
    {
        return Task.CompletedTask;
    }

    public Task SendRpcMessageToClientAsync<TData>(
        RequestInfo requestInfo,
        TData data,
        long authKeyId,
        long permAuthKeyId,
        long userId,
        int pts = 0) where TData : IObject
    {
        _rpcMessages.Add((requestInfo, data));
        return Task.CompletedTask;
    }
}

/// <summary>In-memory <see cref="IUserAccessHashKeyCache"/> - remembers the last access-hash key id per user.</summary>
public sealed class FakeUserAccessHashKeyCache : IUserAccessHashKeyCache
{
    private readonly Dictionary<long, long> _keys = new();

    /// <summary>Pre-seeds the remembered access-hash key id for a user (e.g. an offline callee).</summary>
    public FakeUserAccessHashKeyCache Seed(long userId, long accessHashKeyId)
    {
        if (userId != 0 && accessHashKeyId != 0)
        {
            _keys[userId] = accessHashKeyId;
        }

        return this;
    }

    public Task RememberAsync(long userId, long accessHashKeyId)
    {
        if (userId != 0 && accessHashKeyId != 0)
        {
            _keys[userId] = accessHashKeyId;
        }

        return Task.CompletedTask;
    }

    public Task<long?> GetAsync(long userId)
    {
        return Task.FromResult(_keys.TryGetValue(userId, out var value) ? value : (long?)null);
    }
}

/// <summary>
/// Deterministic <see cref="IAccessHashHelper2"/> fake. Access hashes are a stable function of
/// (currentUserId, accessHashKeyId, targetId, normalized-type); <c>Call</c> and <c>GroupCall</c> share
/// the same lane (matching the production helper). Validation succeeds iff the supplied hash equals the
/// value the fake would issue to the requesting user.
/// </summary>
public sealed class FakeAccessHashHelper2 : IAccessHashHelper2
{
    public long GenerateAccessHash(long fromUserId, long accessHashKeyId, long targetId, AccessHashType accessHashType)
    {
        var type = Normalize(accessHashType);
        unchecked
        {
            long h = 1125899906842597L;
            h = h * 31 + (long)type;
            h = h * 31 + fromUserId;
            h = h * 31 + accessHashKeyId;
            h = h * 31 + targetId;
            h &= long.MaxValue;
            return h == 0 ? 1 : h;
        }
    }

    public ValueTask<bool> IsAccessHashValidAsync(long currentUserId, long accessHashKeyId, long targetId, long accessHash, AccessHashType? accessHashType = null)
    {
        var type = accessHashType ?? AccessHashType.User;
        var expected = GenerateAccessHash(currentUserId, accessHashKeyId, targetId, type);
        return ValueTask.FromResult(accessHash != 0 && accessHash == expected);
    }

    public ValueTask<bool> IsAccessHashValidAsync(IRequestWithAccessHashKeyId request, long targetId, long accessHash, AccessHashType? accessHashType = null)
    {
        return IsAccessHashValidAsync(request.UserId, request.AccessHashKeyId, targetId, accessHash, accessHashType);
    }

    public async Task CheckAccessHashAsync(long currentUserId, long accessHashKeyId, long targetId, long accessHash, AccessHashType? accessHashType = null)
    {
        if (!await IsAccessHashValidAsync(currentUserId, accessHashKeyId, targetId, accessHash, accessHashType))
        {
            CreateRpcError(accessHashType).ThrowRpcError();
        }
    }

    public Task CheckAccessHashAsync(IRequestWithAccessHashKeyId request, long targetId, long accessHash, AccessHashType? accessHashType = null)
    {
        return CheckAccessHashAsync(request.UserId, request.AccessHashKeyId, targetId, accessHash, accessHashType);
    }

    public Task CheckAccessHashAsync(long currentUserId, long accessHashKeyId, IInputPeer? inputPeer)
    {
        switch (inputPeer)
        {
            case TInputPeerChannel inputPeerChannel:
                return CheckAccessHashAsync(currentUserId, accessHashKeyId, inputPeerChannel.ChannelId, inputPeerChannel.AccessHash, AccessHashType.Channel);
            case TInputPeerUser inputPeerUser:
                return CheckAccessHashAsync(currentUserId, accessHashKeyId, inputPeerUser.UserId, inputPeerUser.AccessHash, AccessHashType.User);
            default:
                return Task.CompletedTask;
        }
    }

    public Task CheckAccessHashAsync(IRequestWithAccessHashKeyId request, IInputPeer? inputPeer)
    {
        return CheckAccessHashAsync(request.UserId, request.AccessHashKeyId, inputPeer);
    }

    public Task CheckAccessHashAsync(long currentUserId, long accessHashKeyId, IInputUser inputUser)
    {
        if (inputUser is TInputUser tInputUser)
        {
            return CheckAccessHashAsync(currentUserId, accessHashKeyId, tInputUser.UserId, tInputUser.AccessHash, AccessHashType.User);
        }

        return Task.CompletedTask;
    }

    public Task CheckAccessHashAsync(IRequestWithAccessHashKeyId request, IInputUser inputUser)
    {
        return CheckAccessHashAsync(request.UserId, request.AccessHashKeyId, inputUser);
    }

    public Task CheckAccessHashAsync(long currentUserId, long accessHashKeyId, IInputChannel inputChannel)
    {
        if (inputChannel is TInputChannel tInputChannel)
        {
            return CheckAccessHashAsync(currentUserId, accessHashKeyId, tInputChannel.ChannelId, tInputChannel.AccessHash, AccessHashType.Channel);
        }

        return Task.CompletedTask;
    }

    public Task CheckAccessHashAsync(IRequestWithAccessHashKeyId request, IInputChannel inputChannel)
    {
        return CheckAccessHashAsync(request.UserId, request.AccessHashKeyId, inputChannel);
    }

    public RpcError CreateRpcError(AccessHashType? accessHashType)
    {
        return accessHashType switch
        {
            AccessHashType.GroupCall => RpcErrors.RpcErrors400.GroupcallInvalid,
            AccessHashType.Call => RpcErrors.RpcErrors400.CallPeerInvalid,
            AccessHashType.User => RpcErrors.RpcErrors400.UserIdInvalid,
            AccessHashType.Channel => RpcErrors.RpcErrors400.ChannelInvalid,
            _ => RpcErrors.RpcErrors400.PeerIdInvalid
        };
    }

    private static AccessHashType Normalize(AccessHashType accessHashType)
    {
        // Match production: inputPhoneCall access hashes are generated in the GroupCall lane.
        return accessHashType == AccessHashType.Call ? AccessHashType.GroupCall : accessHashType;
    }
}

/// <summary>
/// Capturing <see cref="IMessageAppService"/>. Only <see cref="SendMessageAsync"/> is implemented (it
/// records the service messages the call handlers emit, e.g. <c>messageActionPhoneCall</c>); the read /
/// search members are not exercised by the call handlers and throw if called.
/// </summary>
public sealed class FakeMessageAppService : IMessageAppService
{
    private readonly List<SendMessageInput> _sentMessages = new();

    /// <summary>All service / chat messages sent through this fake, in order.</summary>
    public IReadOnlyList<SendMessageInput> SentMessages => _sentMessages;

    public void Clear() => _sentMessages.Clear();

    public Task SendMessageAsync(List<SendMessageInput> inputs)
    {
        _sentMessages.AddRange(inputs);
        return Task.CompletedTask;
    }

    public void CheckBotPermission(long requestUserId, Peer toPeer) { }

    public Task CheckSendAsAsync(long requestUserId, Peer toPeer, Peer? sendAs) => Task.CompletedTask;

    public Task<Peer?> GetAnonymousSendAsPeerAsync(long channelId, long userId) => Task.FromResult<Peer?>(null);

    public Task<bool> CanSendAsPeerAsync(long channelId, long userId) => Task.FromResult(true);

    public Task<bool> IsValidSendAsPeerAsync(long requestUserId, Peer toPeer, Peer? sendAsPeer) => Task.FromResult(true);

    public List<string> GetHashtags(string? message) => new();

    public Task<MessageEntityProcessingResult> ProcessMessageEntitiesAsync(string? message,
        IEnumerable<IMessageEntity>? entities, Peer toPeer)
        => Task.FromResult(MessageEntityProcessingResult.Empty);

    public (HashSet<long> userIds, HashSet<long> channelIds) GetExtraPeerIds(
        IReadOnlyCollection<IMessageReadModel> messageReadModels)
        => (new HashSet<long>(), new HashSet<long>());

    public Task<GetMessageOutput> GetChannelDifferenceAsync(GetDifferenceInput input) => throw NotUsed();
    public Task<GetMessageOutput> GetDifferenceAsync(GetDifferenceInput input) => throw NotUsed();
    public Task<GetMessageOutput> GetHistoryAsync(GetHistoryInput input) => throw NotUsed();
    public Task<GetMessageOutput> GetMessagesAsync(GetMessagesInput input) => throw NotUsed();
    public Task<GetMessageOutput> GetRepliesAsync(GetRepliesInput input) => throw NotUsed();
    public Task<GetMessageOutput> SearchAsync(SearchInput input) => throw NotUsed();
    public Task<GetMessageOutput> SearchGlobalAsync(SearchGlobalInput input) => throw NotUsed();
    public Task<SearchPostsResult> SearchPostsAsync(long selfUserId, SearchPostsQuery searchPostsQuery) => throw NotUsed();

    private static NotSupportedException NotUsed([System.Runtime.CompilerServices.CallerMemberName] string? member = null)
        => new($"{nameof(FakeMessageAppService)}.{member} is not used by the Phone handlers.");
}

/// <summary>
/// A <see cref="IUserAppService"/> stub for the call handlers, which look the callee up to reject calls to
/// missing, deleted or bot accounts. By default every user id resolves to a plain, callable human.
/// </summary>
public static class FakeUserAppService
{
    /// <summary>Every user id resolves to a callable user.</summary>
    public static IUserAppService AllCallable() => For(_ => Callable());

    /// <summary>Resolves user ids through <paramref name="resolver"/>; return null to make one missing.</summary>
    public static IUserAppService For(Func<long, IUserReadModel?> resolver)
    {
        var mock = new Mock<IUserAppService>();
        mock.Setup(x => x.GetAsync(It.IsAny<long?>()))
            .Returns((long? id) => Task.FromResult(id.HasValue ? resolver(id.Value) : null));
        mock.Setup(x => x.GetAsync(It.IsAny<long>()))
            .Returns((long id) => Task.FromResult(
                resolver(id) ?? throw new ArgumentException($"UserReadModel with id {id} not exists")));
        return mock.Object;
    }

    /// <summary>A user who can be called.</summary>
    public static IUserReadModel Callable(bool isDeleted = false, bool isBot = false)
    {
        var mock = new Mock<IUserReadModel>();
        mock.SetupGet(x => x.IsDeleted).Returns(isDeleted);
        mock.SetupGet(x => x.Bot).Returns(isBot);
        return mock.Object;
    }
}

/// <summary>
/// A recording <see cref="IGroupCallMediaServer"/>. Unconfigured by default, i.e. no SFU deployed.
/// </summary>
public sealed class FakeGroupCallMediaServer : IGroupCallMediaServer
{
    public bool IsConfigured { get; set; } = true;

    /// <summary>Throw this from <see cref="JoinAsync"/> instead of answering.</summary>
    public Exception? JoinFailure { get; set; }

    public HashSet<int> Sources { get; } = new();

    public List<(long CallId, string Key, string Params)> Joins { get; } = new();

    public List<(long CallId, string Key)> Leaves { get; } = new();

    public List<long> Ends { get; } = new();

    public Task<GroupCallMediaJoinResult> JoinAsync(long callId, string key, string paramsJson,
        CancellationToken cancellationToken = default)
    {
        if (JoinFailure != null)
        {
            return Task.FromException<GroupCallMediaJoinResult>(JoinFailure);
        }

        Joins.Add((callId, key, paramsJson));
        var endpoint = $"endpoint-{key}";
        var video = new System.Text.Json.Nodes.JsonObject
        {
            ["endpoint"] = endpoint,
            ["server_sources"] = new System.Text.Json.Nodes.JsonArray(-1931371821)
        };
        return Task.FromResult(new GroupCallMediaJoinResult(new System.Text.Json.Nodes.JsonObject
        {
            ["xmlns"] = "urn:xmpp:jingle:transports:ice-udp:1",
            ["ufrag"] = "sfuufrag",
            ["pwd"] = "sfupwd",
            ["rtcp-mux"] = true,
            ["fingerprints"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["hash"] = "sha-256",
                ["fingerprint"] = "AA:BB",
                ["setup"] = "passive"
            }),
            ["candidates"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["ip"] = "203.0.113.7",
                ["port"] = "32000",
                ["protocol"] = "udp",
                ["type"] = "host"
            })
        }, video, endpoint));
    }

    public Task LeaveAsync(long callId, string key, CancellationToken cancellationToken = default)
    {
        Leaves.Add((callId, key));
        return Task.CompletedTask;
    }

    public Task EndAsync(long callId, CancellationToken cancellationToken = default)
    {
        Ends.Add(callId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlySet<int>?> GetSourcesAsync(long callId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlySet<int>?>(IsConfigured ? Sources : null);
}
