using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Phone;
using MyTelegram.Schema;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Tests for <see cref="CallSessionExpiryService"/> - the sweeper that terminates 1:1 call sessions no
/// client ever discarded.
///
/// <para>Without it a client that dies mid-call leaves its session live forever, and since both
/// participants count as busy while a live session exists, neither could place or receive another call.
/// Deadlines mirror the <c>call_*_timeout_ms</c> values the server publishes in <c>help.getConfig</c>,
/// plus a grace period so the server never pre-empts the client's own timer.</para>
/// </summary>
public class CallSessionExpiryServiceTests
{
    private const long CallerId = 1;
    private const long CalleeId = 2;
    private const long CallId = 777;

    [Theory]
    [InlineData("requested", 20, typeof(TPhoneCallDiscardReasonMissed))]     // ReceiveTimeoutSeconds
    [InlineData("received", 90, typeof(TPhoneCallDiscardReasonMissed))]      // RingTimeoutSeconds
    [InlineData("accepted", 30, typeof(TPhoneCallDiscardReasonDisconnect))]  // ConnectTimeoutSeconds
    public async Task Sweep_StateOlderThanItsDeadline_IsDiscardedAndBothPartiesNotified(
        string state,
        int deadlineSeconds,
        Type expectedReason)
    {
        var harness = new Harness();
        await harness.InsertSessionAsync(state, ageSeconds: deadlineSeconds + harness.GraceSeconds + 5);

        var expired = await harness.Service.SweepAsync();

        expired.ShouldBe(1);
        (await harness.LoadSessionAsync()).State.ShouldBe("discarded");

        // Neither side initiated this, so both are told.
        harness.Sender.Pushes.Count.ShouldBe(2);
        harness.Sender.TargetUserIds.OrderBy(id => id).ShouldBe(new[] { CallerId, CalleeId });
        foreach (var push in harness.Sender.Pushes)
        {
            var discarded = push.Updates.OfType<TUpdatePhoneCall>().Single().PhoneCall
                .ShouldBeOfType<TPhoneCallDiscarded>();
            discarded.Id.ShouldBe(CallId);
            discarded.Reason.ShouldBeOfType(expectedReason);
            // Every device of both users must stop ringing, so nothing is excluded.
            push.ExcludeAuthKeyId.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("requested", 20)]
    [InlineData("received", 90)]
    [InlineData("accepted", 30)]
    public async Task Sweep_WithinDeadline_LeavesSessionAlone(string state, int deadlineSeconds)
    {
        var harness = new Harness();
        // Past the raw timeout but still inside the grace period - that window belongs to the client.
        await harness.InsertSessionAsync(state, ageSeconds: deadlineSeconds + harness.GraceSeconds - 1);

        var expired = await harness.Service.SweepAsync();

        expired.ShouldBe(0);
        (await harness.LoadSessionAsync()).State.ShouldBe(state);
        harness.Sender.Pushes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_ConnectedCall_SurvivesUntilMaxDuration()
    {
        var harness = new Harness();
        // A two-hour call is perfectly normal; only the 24h backstop may end it.
        await harness.InsertSessionAsync("confirmed", ageSeconds: 2 * 60 * 60);

        (await harness.Service.SweepAsync()).ShouldBe(0);
        (await harness.LoadSessionAsync()).State.ShouldBe("confirmed");
    }

    [Fact]
    public async Task Sweep_ConnectedCallPastMaxDuration_IsDiscardedWithDurationAndFlags()
    {
        var harness = new Harness();
        await harness.InsertSessionAsync("confirmed", ageSeconds: 24 * 60 * 60 + 60);

        (await harness.Service.SweepAsync()).ShouldBe(1);

        var session = await harness.LoadSessionAsync();
        session.State.ShouldBe("discarded");
        session.DiscardReason.ShouldBe("disconnect");
        // A call that actually connected is rateable/debuggable - same policy as phone.discardCall.
        session.Duration.ShouldNotBeNull().ShouldBeGreaterThan(0);
        session.NeedDebug.ShouldBeTrue();
        session.NeedRating.ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_NeverConnectedCall_IsNotRateable()
    {
        var harness = new Harness();
        await harness.InsertSessionAsync("received", ageSeconds: 200);

        await harness.Service.SweepAsync();

        var session = await harness.LoadSessionAsync();
        session.Duration.ShouldBeNull("a call that never connected has no duration");
        session.NeedRating.ShouldBeFalse();
        session.NeedDebug.ShouldBeFalse();
    }

    [Fact]
    public async Task Sweep_DeadlineIsMeasuredFromTheLastTransition_NotCallCreation()
    {
        var harness = new Harness();
        // Rang for 85s, then was answered 5s ago. Measuring the 30s connect deadline from the call's
        // creation date would expire this immediately, killing a call that was just picked up.
        await harness.InsertSessionAsync("accepted", ageSeconds: 85, stateAgeSeconds: 5);

        (await harness.Service.SweepAsync()).ShouldBe(0);
        (await harness.LoadSessionAsync()).State.ShouldBe("accepted");
    }

    [Fact]
    public async Task Sweep_AlreadyDiscardedSession_IsIgnored()
    {
        var harness = new Harness();
        await harness.InsertSessionAsync("discarded", ageSeconds: 10_000);

        (await harness.Service.SweepAsync()).ShouldBe(0);
        harness.Sender.Pushes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_SessionDiscardedConcurrently_IsNotExpiredAgain()
    {
        var harness = new Harness();
        await harness.InsertSessionAsync("received", ageSeconds: 200);

        // phone.discardCall wins the race: the compare-and-set on State must find nothing to claim.
        await harness.Sessions.UpdateOneAsync(
            Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, CallId),
            Builders<CallSessionDocument>.Update.Set(s => s.State, "discarded"));

        (await harness.Service.SweepAsync()).ShouldBe(0);
        harness.Sender.Pushes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_ExpiredVideoCall_KeepsTheVideoFlag()
    {
        var harness = new Harness();
        await harness.InsertSessionAsync("received", ageSeconds: 200, video: true);

        await harness.Service.SweepAsync();

        var discarded = harness.Sender.Pushes[0].Updates.OfType<TUpdatePhoneCall>().Single().PhoneCall
            .ShouldBeOfType<TPhoneCallDiscarded>();
        discarded.Video.ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_ManyExpiredSessions_AreAllTerminated()
    {
        var harness = new Harness();
        for (var i = 0; i < 5; i++)
        {
            await harness.InsertSessionAsync("received", ageSeconds: 200, callId: CallId + i, callerId: 100 + i);
        }

        (await harness.Service.SweepAsync()).ShouldBe(5);
        harness.Sender.Pushes.Count.ShouldBe(10); // two participants each
    }

    // ---- harness ---------------------------------------------------------------------------------

    private sealed class Harness
    {
        public Harness()
        {
            var store = PhoneTestFixtures.CreateStore();
            Sessions = store.Database.GetCollection<CallSessionDocument>(PhoneTestFixtures.CallSessionsCollectionName);
            Sender = new CapturingObjectMessageSender();

            var options = new MyTelegramMessengerServerOptions { Calls = new CallsConfig() };
            GraceSeconds = options.Calls.ExpiryGraceSeconds;

            Service = new CallSessionExpiryService(
                store.Database,
                Sender,
                MessageApp,
                new FixedOptionsMonitor<MyTelegramMessengerServerOptions>(options),
                NullLogger<CallSessionExpiryService>.Instance);
        }

        public IMongoCollection<CallSessionDocument> Sessions { get; }
        public CapturingObjectMessageSender Sender { get; }
        public FakeMessageAppService MessageApp { get; } = new();
        public CallSessionExpiryService Service { get; }
        public int GraceSeconds { get; }

        /// <param name="state">State to persist the session in.</param>
        /// <param name="ageSeconds">How long ago the call was placed.</param>
        /// <param name="stateAgeSeconds">How long ago it entered <paramref name="state"/> (defaults to <paramref name="ageSeconds"/>).</param>
        /// <param name="video">Whether it is a video call.</param>
        /// <param name="callId">Call id to use.</param>
        /// <param name="callerId">Caller id to use.</param>
        public Task InsertSessionAsync(
            string state,
            int ageSeconds,
            int? stateAgeSeconds = null,
            bool video = false,
            long callId = CallId,
            long callerId = CallerId)
        {
            var now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return Sessions.InsertOneAsync(new CallSessionDocument
            {
                Id = callId,
                CallId = callId,
                CallerId = callerId,
                CalleeId = CalleeId,
                State = state,
                Video = video,
                Date = now - ageSeconds,
                StateChangedDate = now - (stateAgeSeconds ?? ageSeconds)
            });
        }

        public async Task<CallSessionDocument> LoadSessionAsync()
            => await Sessions.Find(Builders<CallSessionDocument>.Filter.Eq(s => s.CallId, CallId)).FirstAsync();
    }
}

/// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a fixed value.</summary>
file sealed class FixedOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
