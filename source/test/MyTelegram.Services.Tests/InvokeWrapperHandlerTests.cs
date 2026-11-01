using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using MyTelegram;
using MyTelegram.Abstractions;
using MyTelegram.Messenger;
using MyTelegram.Schema;
using MyTelegram.Services.Services;
using MyTelegram.Services.Tests.Phone;

namespace MyTelegram.Services.Tests;

/// <summary>
/// Example-based tests for the transparent "invoking" wrappers and their robustness fixes:
/// <c>invokeWithMessagesRange</c>, the three attestation wrappers
/// (<c>invokeWithGooglePlayIntegrity</c> / <c>invokeWithApnsSecret</c> / <c>invokeWithReCaptcha</c>),
/// <c>invokeWithoutUpdates</c> (update-suppression scope), and <c>invokeWithBusinessConnection</c>
/// (null-safe request rebind for a non-<see cref="RequestInput"/> input).
///
/// <para>Each handler is <c>internal</c> in the <c>MyTelegram.Messenger</c> assembly, so it is
/// instantiated via reflection and invoked through the public <see cref="IObjectHandler"/>
/// entrypoint (mirroring <c>InvokeAfterMsgsHandlerTests</c>). A fake <see cref="IHandlerHelper"/>
/// resolves the inner query's constructor id to a recording handler; any other id is unresolved,
/// exercising the standard <c>400 INPUT_CONSTRUCTOR_INVALID</c> error path.</para>
///
/// <b>Validates: Requirements 7.2, 7.4 (covering the behaviour of Requirements 2.x, 3.x, 4.x, 5.x)</b>
/// </summary>
public class InvokeWrapperHandlerTests
{
    // ---- invokeWithMessagesRange (Requirements 2.1, 2.3, 2.4) ------------------------------------

    // 2.1 / 2.3: the inner query is executed and its result returned; the Range field is accepted
    // but ignored (a populated Range must not change the result or cause an error).
    [Fact]
    public void InvokeWithMessagesRange_ExecutesInnerQuery_AndIgnoresRange()
    {
        var handler = new RecordingHandler();
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var request = new RequestInvokeWithMessagesRange
        {
            Range = new TMessageRange { MinId = 5, MaxId = 42 },
            Query = query
        };

        var result = Invoke("InvokeWithMessagesRangeHandler", [handlerHelper], new FakeRequestInput(), request);

        handler.CallCount.ShouldBe(1);
        // BaseObjectHandler pass-through: the inner handler's result is returned as-is.
        result.ShouldBe(handler.Result);
    }

    // 2.2: unknown inner constructor -> INPUT_CONSTRUCTOR_INVALID (400), not NotImplementedException.
    [Fact]
    public void InvokeWithMessagesRange_UnknownInnerConstructor_ThrowsInputConstructorInvalid()
    {
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId + 1, new RecordingHandler());
        var request = new RequestInvokeWithMessagesRange
        {
            Range = new TMessageRange { MinId = 0, MaxId = 0 },
            Query = query
        };

        AssertInputConstructorInvalid(() =>
            Invoke("InvokeWithMessagesRangeHandler", [handlerHelper], new FakeRequestInput(), request));
    }

    // ---- attestation wrappers (Requirements 3.1, 3.3, 3.4) --------------------------------------

    // 3.1 / 3.3: Google Play Integrity wrapper executes the inner query; the attestation fields
    // (Nonce/Token) are set to arbitrary values and ignored.
    [Fact]
    public void InvokeWithGooglePlayIntegrity_ExecutesInnerQuery_AndIgnoresAttestation()
    {
        var handler = new RecordingHandler();
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var request = new RequestInvokeWithGooglePlayIntegrity
        {
            Nonce = "arbitrary-nonce",
            Token = "arbitrary-token",
            Query = query
        };

        var result = Invoke("InvokeWithGooglePlayIntegrityHandler", [handlerHelper], new FakeRequestInput(), request);

        handler.CallCount.ShouldBe(1);
        AssertRpcResultCarries(result, handler.Result);
    }

    // 3.1 / 3.3: APNs secret wrapper executes the inner query; Nonce/Secret ignored.
    [Fact]
    public void InvokeWithApnsSecret_ExecutesInnerQuery_AndIgnoresAttestation()
    {
        var handler = new RecordingHandler();
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var request = new RequestInvokeWithApnsSecret
        {
            Nonce = "arbitrary-nonce",
            Secret = "arbitrary-secret",
            Query = query
        };

        var result = Invoke("InvokeWithApnsSecretHandler", [handlerHelper], new FakeRequestInput(), request);

        handler.CallCount.ShouldBe(1);
        AssertRpcResultCarries(result, handler.Result);
    }

    // 3.1 / 3.3: reCAPTCHA wrapper executes the inner query; the Token (action/token) is ignored.
    [Fact]
    public void InvokeWithReCaptcha_ExecutesInnerQuery_AndIgnoresAttestation()
    {
        var handler = new RecordingHandler();
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var request = new RequestInvokeWithReCaptcha
        {
            Token = "arbitrary-recaptcha-token",
            Query = query
        };

        var result = Invoke("InvokeWithReCaptchaHandler", [handlerHelper], new FakeRequestInput(), request);

        handler.CallCount.ShouldBe(1);
        AssertRpcResultCarries(result, handler.Result);
    }

    // 3.2: unknown inner constructor -> INPUT_CONSTRUCTOR_INVALID for each attestation wrapper.
    [Theory]
    [InlineData("InvokeWithGooglePlayIntegrityHandler")]
    [InlineData("InvokeWithApnsSecretHandler")]
    [InlineData("InvokeWithReCaptchaHandler")]
    public void AttestationWrappers_UnknownInnerConstructor_ThrowInputConstructorInvalid(string handlerTypeName)
    {
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId + 1, new RecordingHandler());

        IObject request = handlerTypeName switch
        {
            "InvokeWithGooglePlayIntegrityHandler" =>
                new RequestInvokeWithGooglePlayIntegrity { Nonce = "n", Token = "t", Query = query },
            "InvokeWithApnsSecretHandler" =>
                new RequestInvokeWithApnsSecret { Nonce = "n", Secret = "s", Query = query },
            _ => new RequestInvokeWithReCaptcha { Token = "t", Query = query }
        };

        AssertInputConstructorInvalid(() =>
            Invoke(handlerTypeName, [handlerHelper], new FakeRequestInput(), request));
    }

    // ---- invokeWithoutUpdates (Requirements 4.1, 4.2) -------------------------------------------

    // 4.1 / 4.2: the inner query runs and its result is returned; NoUpdatesContext.IsSuppressed is
    // true DURING inner execution and false before and after the outer call.
    [Fact]
    public void InvokeWithoutUpdates_SuppressesUpdatesDuringInner_AndClearsAfter()
    {
        var suppressedDuringInner = false;
        var handler = new RecordingHandler(onInvoke: () => suppressedDuringInner = ReadIsSuppressed());
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var request = new RequestInvokeWithoutUpdates { Query = query };

        // Not suppressed before the wrapper runs.
        ReadIsSuppressed().ShouldBeFalse();

        var result = Invoke("InvokeWithoutUpdatesHandler", [handlerHelper], new FakeRequestInput(), request);

        handler.CallCount.ShouldBe(1);
        suppressedDuringInner.ShouldBeTrue("updates must be suppressed while the inner query runs");
        // Scope is well-bracketed: flag resets once the wrapper returns.
        ReadIsSuppressed().ShouldBeFalse("suppression must not leak past the wrapper");
        // BaseObjectHandler pass-through: inner result returned as-is.
        result.ShouldBe(handler.Result);
    }

    // 4.4: unknown inner constructor -> INPUT_CONSTRUCTOR_INVALID (and suppression still clears).
    [Fact]
    public void InvokeWithoutUpdates_UnknownInnerConstructor_ThrowsInputConstructorInvalid()
    {
        var query = new FakeQuery();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId + 1, new RecordingHandler());
        var request = new RequestInvokeWithoutUpdates { Query = query };

        AssertInputConstructorInvalid(() =>
            Invoke("InvokeWithoutUpdatesHandler", [handlerHelper], new FakeRequestInput(), request));

        // The suppression scope is disposed even when the inner dispatch throws.
        ReadIsSuppressed().ShouldBeFalse();
    }

    // ---- invokeWithBusinessConnection robustness (Requirement 5.1) ------------------------------

    // 5.1: when input is NOT a RequestInput, the handler must build the business-user request
    // context through a null-safe path (no NullReferenceException) and still execute the inner
    // query, rebinding the UserId to the business user.
    [Fact]
    public void InvokeWithBusinessConnection_NonRequestInput_DoesNotThrowNre_AndRebindsUser()
    {
        const long botId = 555L;
        const long businessUserId = 777L;
        const string connectionId = "conn-1";

        var store = PhoneTestFixtures.CreateStore();
        store.Database.GetCollection<BsonDocument>("connected_business_bots").InsertOne(new BsonDocument
        {
            { "ConnectionId", connectionId },
            { "BotId", botId },
            { "UserId", businessUserId },
            { "Rights", new BsonDocument { { "EditName", true } } }
        });

        var handler = new RecordingHandler();
        // A real wrappable method covered by the connection's EditName right: the allowlist in
        // BusinessConnectionRightsMap rejects any constructor that is not explicitly permitted, so a
        // synthetic FakeQuery would fail authorization before reaching the identity-rebinding path
        // this test is about. It names no chat, so no recipient check runs either.
        var query = new MyTelegram.Schema.Account.RequestUpdateProfile { FirstName = "x" };
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var request = new RequestInvokeWithBusinessConnection { ConnectionId = connectionId, Query = query };

        // A non-RequestInput IRequestInput whose UserId matches the connection's BotId. The old
        // `(input as RequestInput) with {...}` path would NRE here; the fixed path must not.
        var input = new FakeRequestInput { UserId = botId };

        IObject? result = null;
        Should.NotThrow(() => result = Invoke(
            "InvokeWithBusinessConnectionHandler", BusinessArgs(store.Database, handlerHelper), input, request));

        handler.CallCount.ShouldBe(1);
        handler.LastInput.ShouldNotBeNull();
        handler.LastInput!.UserId.ShouldBe(businessUserId);
        AssertRpcResultCarries(result, handler.Result);
    }

    // The wrapper runs the inner query as the connected USER, so the set of wrappable methods must be
    // closed. Gating only on "Reply" and then dispatching any constructor let a bot holding the
    // minimum grant reach account.* and auth.* as the user — a full account takeover.
    [Fact]
    public void InvokeWithBusinessConnection_RejectsMethodOutsideAllowlist()
    {
        const long botId = 555L;
        const long businessUserId = 777L;
        const string connectionId = "conn-2";

        var store = PhoneTestFixtures.CreateStore();
        store.Database.GetCollection<BsonDocument>("connected_business_bots").InsertOne(new BsonDocument
        {
            { "ConnectionId", connectionId },
            { "BotId", botId },
            { "UserId", businessUserId },
            // Every right granted: the allowlist must still refuse a method that is not wrappable.
            { "Rights", new BsonDocument
                {
                    { "Reply", true }, { "ReadMessages", true }, { "DeleteSentMessages", true },
                    { "EditName", true }, { "EditBio", true }, { "EditUsername", true },
                    { "ManageStories", true }
                }
            }
        });

        var handler = new RecordingHandler();
        var query = new MyTelegram.Schema.Auth.RequestLogOut();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var request = new RequestInvokeWithBusinessConnection { ConnectionId = connectionId, Query = query };
        var input = new FakeRequestInput { UserId = botId };

        var exception = Should.Throw<RpcException>(() => Invoke(
            "InvokeWithBusinessConnectionHandler", BusinessArgs(store.Database, handlerHelper), input, request));

        exception.Message.ShouldBe("BUSINESS_CONNECTION_NOT_ALLOWED");
        handler.CallCount.ShouldBe(0);
    }

    // A wrappable method still requires the specific right that governs it, not merely any right.
    [Fact]
    public void InvokeWithBusinessConnection_RejectsWrappableMethodWithoutItsRight()
    {
        const long botId = 555L;
        const long businessUserId = 777L;
        const string connectionId = "conn-3";

        var store = PhoneTestFixtures.CreateStore();
        store.Database.GetCollection<BsonDocument>("connected_business_bots").InsertOne(new BsonDocument
        {
            { "ConnectionId", connectionId },
            { "BotId", botId },
            { "UserId", businessUserId },
            // Reply only: enough to send, not to rename the user's account.
            { "Rights", new BsonDocument { { "Reply", true } } }
        });

        var handler = new RecordingHandler();
        var query = new MyTelegram.Schema.Account.RequestUpdateUsername();
        var handlerHelper = new FakeHandlerHelper(query.ConstructorId, handler);
        var request = new RequestInvokeWithBusinessConnection { ConnectionId = connectionId, Query = query };
        var input = new FakeRequestInput { UserId = botId };

        Should.Throw<RpcException>(() => Invoke(
            "InvokeWithBusinessConnectionHandler", BusinessArgs(store.Database, handlerHelper), input, request));

        handler.CallCount.ShouldBe(0);
    }

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// Constructor arguments of InvokeWithBusinessConnectionHandler: the connection comes from the given
    /// database, and the bot is a business-enabled bot.
    /// </summary>
    private static object[] BusinessArgs(IMongoDatabase database, IHandlerHelper handlerHelper)
    {
        var userReadModel = FakeProxy<MyTelegram.ReadModel.Interfaces.IUserReadModel>.Create(name => name switch
        {
            "get_Bot" => true,
            "get_BotBusiness" => true,
            _ => null
        });
        var userAppService = FakeProxy<MyTelegram.Messenger.Services.Interfaces.IUserAppService>.Create(name =>
            name == "GetAsync" ? Task.FromResult<MyTelegram.ReadModel.Interfaces.IUserReadModel?>(userReadModel) : null);
        return
        [
            handlerHelper,
            new MyTelegram.Messenger.Services.Business.BusinessConnectionStore(database, null!),
            new PeerHelper(),
            userAppService,
            null!,
            database
        ];
    }

    /// <summary>A reflection proxy answering every member by name; unnamed value types get their default.</summary>
    public class FakeProxy<T> : DispatchProxy where T : class
    {
        private Func<string, object?> _answer = _ => null;

        public static T Create(Func<string, object?> answer)
        {
            var proxy = DispatchProxy.Create<T, FakeProxy<T>>();
            ((FakeProxy<T>)(object)proxy)._answer = answer;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var value = _answer(targetMethod!.Name);
            if (value != null || targetMethod.ReturnType == typeof(void))
            {
                return value;
            }

            return targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    // Reads the internal MyTelegram.Messenger.Services.NoUpdatesContext.IsSuppressed static flag.
    private static readonly Func<bool> ReadIsSuppressed = BuildIsSuppressedReader();

    private static Func<bool> BuildIsSuppressedReader()
    {
        var type = typeof(MyTelegramMessengerServerOptions).Assembly.GetType(
            "MyTelegram.Messenger.Services.NoUpdatesContext", throwOnError: true)!;
        var property = type.GetProperty(
            "IsSuppressed",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return () => (bool)property.GetValue(null)!;
    }

    // Instantiates the internal wrapper handler via reflection and invokes it through the public
    // IObjectHandler entrypoint, unwrapping TargetInvocationException to surface inner exceptions.
    private static IObject? Invoke(string handlerTypeName, object[] ctorArgs, IRequestInput input, IObject request)
    {
        var handlerType = typeof(MyTelegramMessengerServerOptions).Assembly.GetType(
            "MyTelegram.Messenger.Handlers." + handlerTypeName,
            throwOnError: true)!;

        var handler = (IObjectHandler)Activator.CreateInstance(
            handlerType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: ctorArgs,
            culture: null)!;

        try
        {
            return handler.HandleAsync(input, request).GetAwaiter().GetResult();
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    private static void AssertInputConstructorInvalid(Action action)
    {
        var ex = Should.Throw<RpcException>(action);
        ex.RpcError.ErrorCode.ShouldBe(400);
        ex.RpcError.Message.ShouldBe("INPUT_CONSTRUCTOR_INVALID");
    }

    // RpcResultObjectHandler-derived wrappers wrap the inner result in a TRpcResult; assert the
    // wrapper carried through the exact inner-handler result.
    private static void AssertRpcResultCarries(IObject? result, IObject expectedInnerResult)
    {
        var rpcResult = result.ShouldBeOfType<TRpcResult>();
        rpcResult.Result.ShouldBe(expectedInnerResult);
    }

    // Records inner-query invocations and returns a known IObject result. The optional onInvoke
    // callback lets a test observe ambient state (e.g. NoUpdatesContext) at the moment of dispatch.
    private sealed class RecordingHandler(Action? onInvoke = null) : IObjectHandler
    {
        private int _callCount;

        public IObject Result { get; } = new FakeQuery();

        public int CallCount => Volatile.Read(ref _callCount);

        public IRequestInput? LastInput { get; private set; }

        public Task<IObject> HandleAsync(IRequestInput request, IObject obj)
        {
            Interlocked.Increment(ref _callCount);
            LastInput = request;
            onInvoke?.Invoke();
            return Task.FromResult(Result);
        }
    }

    // Fake IHandlerHelper resolving exactly one constructor id to a recording handler; any other
    // id is unresolved (returns false), exercising the INPUT_CONSTRUCTOR_INVALID path.
    private sealed class FakeHandlerHelper(uint queryConstructorId, IObjectHandler resolvedHandler) : IHandlerHelper
    {
        public void InitAllHandlers() { }

        public bool TryGetHandler(uint objectId, [NotNullWhen(true)] out IObjectHandler? handler)
        {
            if (objectId == queryConstructorId)
            {
                handler = resolvedHandler;
                return true;
            }

            handler = null;
            return false;
        }

        public bool TryGetHandlerName(uint objectId, [NotNullWhen(true)] out string? handlerName)
        {
            handlerName = null;
            return false;
        }

        public bool TryGetHandlerShortName(uint objectId, [NotNullWhen(true)] out string? handlerShortName)
        {
            handlerShortName = null;
            return false;
        }

        public string GetHandlerFullName(IObject requestData) => string.Empty;
    }

    // A fake inner-query object with a stable constructor id.
    private sealed class FakeQuery : IObject
    {
        public uint ConstructorId => 0x1234_5678u;

        public void Serialize(IBufferWriter<byte> writer) { }

        public void Deserialize(ref ReadOnlyMemory<byte> buffer) { }
    }

    // A minimal IRequestInput carried through the wrapper. UserId is settable so the business
    // connection test can match the seeded connection's BotId.
    private sealed class FakeRequestInput : IRequestInput
    {
        public string ConnectionId => string.Empty;
        public ConnectionType ConnectionType => default;
        public long AuthKeyId => 0;
        public uint ObjectId { get; set; }
        public long PermAuthKeyId => 0;
        public long ReqMsgId => 0;
        public int SeqNumber => 0;
        public Guid RequestId => Guid.Empty;
        public long Date => 0;
        public DeviceType DeviceType { get; set; }
        public string ClientIp => string.Empty;
        public long SessionId => 0;
        public long UserId { get; set; }
        public long AccessHashKeyId => 0;
        public int Layer { get; set; }
    }
}
