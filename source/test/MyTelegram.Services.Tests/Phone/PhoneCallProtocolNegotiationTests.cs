using CsCheck;
using MyTelegram.Schema;
using MyTelegram.Services.Phone;

namespace MyTelegram.Services.Tests.Phone;

/// <summary>
/// Unit and property coverage for the 1:1 call protocol negotiation logic in
/// <see cref="PhoneCallProtocolHelper"/> (namespace <c>MyTelegram.Services.Phone</c>), which the
/// <c>RequestCallHandler</c>/<c>AcceptCallHandler</c>/<c>ConfirmCallHandler</c> handlers use to:
///   * reject malformed legacy handshake flags (<c>CALL_PROTOCOL_FLAGS_INVALID</c>) and layer ranges
///     (<c>CALL_PROTOCOL_LAYER_INVALID</c>),
///   * select a tgcalls <c>library_versions</c> value supported by both peers, and
///   * reject a call whose caller/callee <c>library_versions</c> lists do not intersect
///     (<c>CALL_PROTOCOL_COMPAT_LAYER_INVALID</c>).
///
/// Covers Requirements 3.1-3.5. The property test encodes design <b>Property 6: Protocol negotiation
/// soundness</b>.
/// </summary>
public class PhoneCallProtocolNegotiationTests
{
    // ---- Legacy flag validation (R3.1, R3.2 -> CALL_PROTOCOL_FLAGS_INVALID) ----------------------

    [Fact]
    public void HasValidLegacyFlags_NullProtocol_IsRejected()
    {
        // A missing protocol has no valid legacy flags -> RequestCall/AcceptCall throw
        // CALL_PROTOCOL_FLAGS_INVALID.
        PhoneCallProtocolHelper.HasValidLegacyFlags(null).ShouldBeFalse();
    }

    [Fact]
    public void HasValidLegacyFlags_MissingUdpP2p_IsRejected()
    {
        var protocol = new TPhoneCallProtocol { UdpP2p = false, UdpReflector = true };
        PhoneCallProtocolHelper.HasValidLegacyFlags(protocol).ShouldBeFalse();
    }

    [Fact]
    public void HasValidLegacyFlags_MissingUdpReflector_IsRejected()
    {
        var protocol = new TPhoneCallProtocol { UdpP2p = true, UdpReflector = false };
        PhoneCallProtocolHelper.HasValidLegacyFlags(protocol).ShouldBeFalse();
    }

    [Fact]
    public void HasValidLegacyFlags_BothUdpFlagsSet_IsAccepted()
    {
        var protocol = new TPhoneCallProtocol { UdpP2p = true, UdpReflector = true };
        PhoneCallProtocolHelper.HasValidLegacyFlags(protocol).ShouldBeTrue();
    }

    // ---- Legacy layer validation (R3.3 -> CALL_PROTOCOL_LAYER_INVALID) ---------------------------

    [Fact]
    public void HasValidLegacyLayers_ExactSupportedRange_IsAccepted()
    {
        var protocol = new TPhoneCallProtocol
        {
            MinLayer = PhoneCallProtocolHelper.LegacyMinLayer,
            MaxLayer = PhoneCallProtocolHelper.LegacyMaxLayer
        };
        PhoneCallProtocolHelper.HasValidLegacyLayers(protocol).ShouldBeTrue();
    }

    [Theory]
    [InlineData(65, 92)]   // the window the official apps advertise
    [InlineData(65, 65)]   // stock TDLib: CallProtocol defaults min_layer = max_layer = 65
    [InlineData(64, 92)]   // starts below our window but overlaps it
    [InlineData(65, 93)]   // extends above our window but overlaps it
    [InlineData(66, 92)]   // strictly inside our window
    [InlineData(1, 200)]   // superset of our window
    [InlineData(92, 92)]   // touches the upper bound only
    public void HasValidLegacyLayers_OverlappingRange_IsAccepted(int minLayer, int maxLayer)
    {
        var protocol = new TPhoneCallProtocol { MinLayer = minLayer, MaxLayer = maxLayer };
        PhoneCallProtocolHelper.HasValidLegacyLayers(protocol).ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 0)]     // unset
    [InlineData(1, 64)]    // entirely below our window
    [InlineData(93, 120)]  // entirely above our window
    [InlineData(92, 65)]   // malformed: min > max
    public void HasValidLegacyLayers_NonOverlappingOrMalformedRange_IsRejected(int minLayer, int maxLayer)
    {
        var protocol = new TPhoneCallProtocol { MinLayer = minLayer, MaxLayer = maxLayer };
        PhoneCallProtocolHelper.HasValidLegacyLayers(protocol).ShouldBeFalse();
    }

    [Fact]
    public void HasValidLegacyLayers_NullProtocol_IsRejected()
    {
        PhoneCallProtocolHelper.HasValidLegacyLayers(null).ShouldBeFalse();
    }

    // ---- Common-version selection (R3.4) ---------------------------------------------------------

    [Fact]
    public void TryGetCommonLibraryVersion_EquallyRankedVersions_FollowTheCallersOrder()
    {
        var found = PhoneCallProtocolHelper.TryGetCommonLibraryVersion(
            callerLibraryVersions: ["v1", "v2", "v3"],
            calleeLibraryVersions: ["v3", "v2"],
            out var selected);

        found.ShouldBeTrue();
        selected.ShouldBe("v2");
    }

    /// <summary>
    /// Every version choice Telegram's production servers made in calls between two real accounts
    /// (2026-11-01): caller list, callee list, the single version put in phoneCallAccepted / phoneCall.
    /// </summary>
    public static TheoryData<string[], string[], string> ProductionChoices() => new()
    {
        { ["13.0.0", "12.0.0", "11.0.0", "10.0.0", "9.0.0", "8.0.0", "7.0.0", "5.0.0", "2.7.7", "2.4.4"], ["11.0.0", "9.0.0", "4.0.1", "2.7.7"], "11.0.0" },
        { ["13.0.0", "12.0.0", "11.0.0", "10.0.0", "9.0.0", "8.0.0", "7.0.0", "5.0.0", "2.7.7", "2.4.4"], ["9.0.0", "11.0.0"], "11.0.0" },
        { ["9.0.0", "13.0.0", "11.0.0", "2.7.7"], ["11.0.0", "13.0.0", "9.0.0"], "13.0.0" },
        { ["10.0.0", "11.0.0", "12.0.0", "13.0.0", "18.0.0", "19.0.0", "2.7.7", "5.0.0", "7.0.0", "8.0.0", "9.0.0"], ["10.0.0", "11.0.0", "12.0.0", "13.0.0", "18.0.0", "19.0.0", "2.7.7", "5.0.0", "7.0.0", "8.0.0", "9.0.0"], "8.0.0" },
        { ["19.0.0", "18.0.0", "2.7.7"], ["18.0.0", "19.0.0", "2.7.7"], "19.0.0" },
        { ["8.0.0", "13.0.0"], ["8.0.0", "13.0.0"], "8.0.0" },
        { ["13.0.0", "8.0.0"], ["13.0.0", "8.0.0"], "8.0.0" },
        { ["8.0.0", "9.0.0"], ["8.0.0", "9.0.0"], "8.0.0" },
        { ["9.0.0", "13.0.0"], ["9.0.0", "13.0.0"], "13.0.0" },
        { ["8.0.0", "19.0.0"], ["8.0.0", "19.0.0"], "8.0.0" },
        { ["12.0.0", "13.0.0"], ["12.0.0", "13.0.0"], "12.0.0" },
        { ["13.0.0", "12.0.0"], ["13.0.0", "12.0.0"], "13.0.0" },
        { ["10.0.0", "11.0.0"], ["10.0.0", "11.0.0"], "10.0.0" },
        { ["12.0.0", "10.0.0"], ["12.0.0", "10.0.0"], "12.0.0" },
        { ["13.0.0", "10.0.0"], ["13.0.0", "10.0.0"], "13.0.0" },
        { ["7.0.0", "8.0.0"], ["7.0.0", "8.0.0"], "8.0.0" },
        { ["2.7.7", "5.0.0"], ["2.7.7", "5.0.0"], "2.7.7" },
        { ["5.0.0", "2.7.7"], ["5.0.0", "2.7.7"], "5.0.0" },
        { ["19.0.0", "12.0.0"], ["19.0.0", "12.0.0"], "12.0.0" },
        { ["19.0.0", "13.0.0"], ["19.0.0", "13.0.0"], "13.0.0" },
        { ["19.0.0", "9.0.0"], ["19.0.0", "9.0.0"], "19.0.0" },
        { ["9.0.0", "7.0.0"], ["9.0.0", "7.0.0"], "9.0.0" },
        { ["7.0.0", "5.0.0"], ["7.0.0", "5.0.0"], "7.0.0" }
    };

    [Theory]
    [MemberData(nameof(ProductionChoices))]
    public void Negotiate_ChoosesWhatTelegramsServersChose(string[] caller, string[] callee, string expected)
    {
        PhoneCallProtocolHelper.Negotiate(caller, callee).LibraryVersions.ShouldBe([expected]);
    }

    [Fact]
    public void HasCommonLibraryVersion_WithOverlap_IsTrue()
    {
        PhoneCallProtocolHelper.HasCommonLibraryVersion(["v1", "v2"], ["v2", "v9"]).ShouldBeTrue();
    }

    [Fact]
    public void Negotiate_WithOverlap_CarriesSingleSharedVersion()
    {
        var negotiated = PhoneCallProtocolHelper.Negotiate(["v1", "v2", "v5"], ["v5", "v2"]);

        // The negotiated protocol carried on the final phoneCall pins exactly one common version.
        negotiated.LibraryVersions.ToList().ShouldBe(new[] { "v2" });
    }

    // ---- Empty intersection (R3.5 -> CALL_PROTOCOL_COMPAT_LAYER_INVALID) --------------------------

    [Fact]
    public void HasCommonLibraryVersion_Disjoint_IsFalse()
    {
        // A false result is what drives ConfirmCallHandler/AcceptCallHandler to throw
        // CALL_PROTOCOL_COMPAT_LAYER_INVALID; the call never reaches `confirmed`.
        PhoneCallProtocolHelper.HasCommonLibraryVersion(["v1", "v2"], ["v8", "v9"]).ShouldBeFalse();
    }

    [Fact]
    public void TryGetCommonLibraryVersion_Disjoint_ReturnsFalseAndNoVersion()
    {
        var found = PhoneCallProtocolHelper.TryGetCommonLibraryVersion(["v1"], ["v2"], out var selected);

        found.ShouldBeFalse();
        selected.ShouldBeNull();
    }

    // ---- Property 6: Protocol negotiation soundness ----------------------------------------------
    // Validates: Requirements 3.4, 3.5
    //
    // For arbitrary caller/callee library_versions lists:
    //   * negotiation succeeds iff the caller and callee lists intersect;
    //   * when it succeeds, the confirmed call's negotiated version is a single value present in BOTH
    //     lists (the ordinal-greatest common version);
    //   * when the intersection is empty, no call reaches `confirmed` (the compat gate rejects it).

    /// <summary>
    /// A non-empty list of 1-4 distinct version tokens drawn from a small pool, so that pairs of
    /// generated lists are frequently overlapping and frequently disjoint. Kept non-empty and
    /// whitespace-free so each list normalises to itself (avoiding the helper's default-version
    /// fallback), which lets the test compute the expected intersection directly as a set operation.
    /// </summary>
    private static readonly Gen<string[]> VersionListGen =
        Gen.Int[1, 6].Select(i => $"v{i}").Array[1, 4].Select(a => a.Distinct().ToArray());

    [Fact]
    public void Property6_NegotiatedVersionIsInBothLists_AndEmptyIntersectionNeverConfirms()
    {
        Gen.Select(VersionListGen, VersionListGen)
            .Sample((caller, callee) =>
            {
                var callerSet = caller.ToHashSet(StringComparer.Ordinal);
                var calleeSet = callee.ToHashSet(StringComparer.Ordinal);
                // These generated versions all rank equally, so the caller's first shared one is chosen.
                var expectedIntersection = caller.Distinct().Where(calleeSet.Contains).ToList();

                var hasCommon = PhoneCallProtocolHelper.HasCommonLibraryVersion(caller, callee);
                var tryCommon = PhoneCallProtocolHelper.TryGetCommonLibraryVersion(caller, callee, out var selected);

                // Negotiation existence matches the actual set intersection, and the two APIs agree.
                hasCommon.ShouldBe(expectedIntersection.Count > 0);
                tryCommon.ShouldBe(hasCommon);

                // A call reaches `confirmed` only when the compat gate (HasCommonLibraryVersion) passes.
                var reachesConfirmed = hasCommon;

                if (!reachesConfirmed)
                {
                    // R3.5: empty intersection -> CALL_PROTOCOL_COMPAT_LAYER_INVALID, never confirmed.
                    expectedIntersection.ShouldBeEmpty();
                    selected.ShouldBeNull();
                    return;
                }

                // R3.4: the negotiated version is a single value present in both peers' lists.
                selected.ShouldNotBeNull();
                selected.ShouldBe(expectedIntersection[0]); // the caller's first common version
                callerSet.ShouldContain(selected!);
                callee.ShouldContain(selected!);

                var negotiated = PhoneCallProtocolHelper.Negotiate(caller, callee).LibraryVersions.ToList();
                negotiated.Count.ShouldBe(1);
                var negotiatedVersion = negotiated[0];

                // The protocol carried on the confirmed phoneCall is in caller ∩ callee.
                callerSet.ShouldContain(negotiatedVersion);
                callee.ShouldContain(negotiatedVersion);
                negotiatedVersion.ShouldBe(selected);
            });
    }
}
