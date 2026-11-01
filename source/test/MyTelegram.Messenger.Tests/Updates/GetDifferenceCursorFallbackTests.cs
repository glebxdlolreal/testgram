using MyTelegram.Messenger.Tests.TestDoubles;
using EventFlow.Queries;
using MyTelegram.Core;
using MyTelegram.Messenger.Handlers.LatestLayer.Updates;
using MyTelegram.Messenger.Services.SecretChat;
using MyTelegram.Messenger.Tests.SecretChat;
using MyTelegram.Queries;
using MyTelegram.ReadModel.Interfaces;
using MyTelegram.Schema.Updates;

namespace MyTelegram.Messenger.Tests.Updates;

/// <summary>
/// Regression test for the channel-replay cursor in <see cref="GetDifferenceHandler"/>.
///
/// <para>The cursor came from the caller's own <c>PtsForAuthKeyId</c> row, defaulting to 0 when that row
/// did not exist yet — which is the normal state for a device that has never acked a difference. Sequence
/// 0 replays the channel stream from the very beginning of history, so the page came back completely
/// full, <c>channelTruncated</c> was set, and the response was <c>differenceSlice</c> ("there is more,
/// ask again"). The cursor only advances on an ack, so it stayed absent and the next request replayed the
/// same first page — the client polled <c>updates.getDifference</c> forever without converging. Measured
/// on a live session: ~240 calls/minute with pts fully caught up and nothing new to deliver.</para>
///
/// <para>Falling back to the user's own box sequence starts the replay at "current as of now" instead of
/// at the dawn of history.</para>
/// </summary>
public class GetDifferenceCursorFallbackTests
{
    private const long UserId = 3003;
    private const long PermAuthKeyId = 55;
    private const long BoxGlobalSeqNo = 9_140_001;

    [Fact]
    public async Task A_device_with_no_cursor_row_replays_from_the_user_box_sequence()
    {
        var fixture = new CursorFixture(boxGlobalSeqNo: BoxGlobalSeqNo, authKeyCursor: null);

        await fixture.InvokeAsync();

        fixture.ObservedMinGlobalSeqNo.ShouldBe(BoxGlobalSeqNo);
    }

    [Fact]
    public async Task A_device_with_no_cursor_row_does_not_report_a_slice_when_nothing_is_pending()
    {
        // Six rows sit above the box sequence — far short of a full page, so no truncation.
        var fixture = new CursorFixture(boxGlobalSeqNo: BoxGlobalSeqNo, authKeyCursor: null, rowsAboveBox: 6);

        await fixture.InvokeAsync();

        fixture.Converter.UpdatesTruncated.ShouldBeFalse();
    }

    [Fact]
    public async Task A_device_that_has_acked_keeps_using_its_own_cursor()
    {
        // Its own cursor is behind the box; the device must not be fast-forwarded past updates it has
        // not seen, so the box sequence is a fallback only.
        var fixture = new CursorFixture(boxGlobalSeqNo: BoxGlobalSeqNo, authKeyCursor: BoxGlobalSeqNo - 5_000);

        await fixture.InvokeAsync();

        fixture.ObservedMinGlobalSeqNo.ShouldBe(BoxGlobalSeqNo - 5_000);
    }

    [Fact]
    public async Task A_fresh_install_with_no_state_anywhere_still_starts_at_zero()
    {
        var fixture = new CursorFixture(boxGlobalSeqNo: null, authKeyCursor: null);

        await fixture.InvokeAsync();

        fixture.ObservedMinGlobalSeqNo.ShouldBe(0);
    }

    /// <summary>
    /// Drives the real handler with a query processor that reports a configurable cursor state and
    /// records the MinGlobalSeqNo the channel replay was actually issued with.
    /// </summary>
    private sealed class CursorFixture : IQueryProcessor
    {
        private readonly long? _boxGlobalSeqNo;
        private readonly long? _authKeyCursor;
        private readonly int _rowsAboveBox;

        public long? ObservedMinGlobalSeqNo { get; private set; }
        public RecordingDifferenceConverterService Converter { get; } = new();

        public CursorFixture(long? boxGlobalSeqNo, long? authKeyCursor, int rowsAboveBox = 0)
        {
            _boxGlobalSeqNo = boxGlobalSeqNo;
            _authKeyCursor = authKeyCursor;
            _rowsAboveBox = rowsAboveBox;
        }

        public Task InvokeAsync()
        {
            var handler = new GetDifferenceHandler(
                new NoBotQtsCounter(),
                new StubMessageAppService(),
                new StubPtsHelper(),
                this,
                new RecordingAckCacheService(),
                Converter,
                new InMemorySecretChatMessageStore());

            return handler.HandleAsync(
                SecretChatTestHarness.Input(UserId, PermAuthKeyId),
                new RequestGetDifference { Pts = 1, Date = 1_700_000_000, Qts = 0 });
        }

        public Task<TResult> ProcessAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken)
        {
            object result = query switch
            {
                GetPtsByPeerIdQuery => _boxGlobalSeqNo is null
                    ? null!
                    : new CursorPtsReadModel { PeerId = UserId, GlobalSeqNo = _boxGlobalSeqNo.Value, Pts = 1 },
                GetPtsByPermAuthKeyIdQuery => _authKeyCursor is null
                    ? null!
                    : new CursorPtsForAuthKeyIdReadModel
                    {
                        PeerId = UserId,
                        PermAuthKeyId = PermAuthKeyId,
                        GlobalSeqNo = _authKeyCursor.Value
                    },
                GetChannelIdListByMemberUserIdQuery => (IReadOnlyCollection<long>)[800_000_000_001],
                GetUpdatesByGlobalSeqNoQuery => (IReadOnlyCollection<IUpdatesReadModel>)[],
                GetUpdatesQuery => (IReadOnlyCollection<IUpdatesReadModel>)[],
                GetChannelUpdatesByGlobalSeqNoQuery q => RecordChannelReplay(q),
                _ => throw new NotSupportedException($"Unexpected query type {query.GetType().Name}")
            };

            return Task.FromResult((TResult)result);
        }

        private IReadOnlyCollection<IUpdatesReadModel> RecordChannelReplay(GetChannelUpdatesByGlobalSeqNoQuery query)
        {
            ObservedMinGlobalSeqNo = query.MinGlobalSeqNo;

            return Enumerable.Range(1, _rowsAboveBox)
                .Select(i => (IUpdatesReadModel)new StubUpdatesReadModel
                {
                    OwnerPeerId = 800_000_000_001,
                    GlobalSeqNo = query.MinGlobalSeqNo + i,
                    UpdatesType = UpdatesType.Updates
                })
                .ToList();
        }
    }

    private sealed class CursorPtsReadModel : IPtsReadModel
    {
        public int Date { get; init; }
        public long GlobalSeqNo { get; init; }
        public string Id { get; init; } = "pts-test";
        public long PeerId { get; init; }
        public int Pts { get; init; }
        public int Qts { get; init; }
        public int Seq { get; init; } = 1;
        public int UnreadCount { get; init; }
        public int MaxMessageId { get; init; }
    }

    private sealed class CursorPtsForAuthKeyIdReadModel : IPtsForAuthKeyIdReadModel
    {
        public string Id { get; init; } = "pts-authkey-test";
        public long PeerId { get; init; }
        public long PermAuthKeyId { get; init; }
        public int Pts { get; init; }
        public int Qts { get; init; }
        public long GlobalSeqNo { get; init; }
    }
}
