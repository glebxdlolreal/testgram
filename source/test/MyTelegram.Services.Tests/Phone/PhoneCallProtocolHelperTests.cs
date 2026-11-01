using MyTelegram.Schema;
using MyTelegram.Services.Phone;

namespace MyTelegram.Services.Tests.Phone;

public class PhoneCallProtocolHelperTests
{
    [Fact]
    public void Normalize_ShouldKeepLibraryOrderAndUseLegacyProtocolValues()
    {
        var protocol = new TPhoneCallProtocol
        {
            MinLayer = 1,
            MaxLayer = 2,
            LibraryVersions = new TVector<string> { "v3", "v2", "v3" }
        };

        var result = PhoneCallProtocolHelper.Normalize(protocol);

        result.UdpP2p.ShouldBeTrue();
        result.UdpReflector.ShouldBeTrue();
        result.MinLayer.ShouldBe(PhoneCallProtocolHelper.LegacyMinLayer);
        result.MaxLayer.ShouldBe(PhoneCallProtocolHelper.LegacyMaxLayer);
        result.LibraryVersions.ShouldBe(new[] { "v3", "v2" });
    }

    [Fact]
    public void Negotiate_ShouldReturnTheCallersFirstSharedVersionAmongEquals()
    {
        var calleeProtocol = new TPhoneCallProtocol
        {
            UdpP2p = true,
            UdpReflector = true,
            MinLayer = PhoneCallProtocolHelper.LegacyMinLayer,
            MaxLayer = PhoneCallProtocolHelper.LegacyMaxLayer,
            LibraryVersions = new TVector<string> { "v3", "v2", "v1" }
        };

        var result = PhoneCallProtocolHelper.Negotiate(["v1", "v2"], calleeProtocol);

        result.LibraryVersions.ShouldBe(new[] { "v1" });
    }

    // ---- Real client version lists ---------------------------------------------------------------
    //
    // Both official clients treat library_versions[0] of the server's reply as THE version:
    //   * tdesktop  - Call::createAndStartController reads vlibrary_versions().value(0, "2.4.4") and
    //                 fails the call when tgcalls::Meta::Create does not know the string.
    //   * Android   - Instance.makeInstance(privateCall.protocol.library_versions.get(0), ...) and,
    //                 crucially, gates video on "2.7.7".compareTo(library_versions.get(0)) <= 0
    //                 (VoIPService.java), destroying the video capturer when that fails.
    //
    // tgcalls registers these versions (InstanceImplLegacy 2.4.4; InstanceImpl 2.7.7/5.0.0;
    // InstanceV2Impl 7/8/9/12/13; InstanceV2ReferenceImpl 10/11). Meta::Versions() enumerates a
    // std::map, so Android advertises them in ascending ordinal order (starting at "10.0.0") while
    // tdesktop advertises the same list reversed.

    private static readonly string[] AndroidVersions =
        ["10.0.0", "11.0.0", "12.0.0", "13.0.0", "2.4.4", "2.7.7", "5.0.0", "7.0.0", "8.0.0", "9.0.0"];

    private static readonly string[] TDesktopVersions =
        ["9.0.0", "8.0.0", "7.0.0", "5.0.0", "2.7.7", "2.4.4", "13.0.0", "12.0.0", "11.0.0", "10.0.0"];

    /// <summary>The Android video-availability gate, transliterated from VoIPService.java.</summary>
    private static bool AndroidKeepsVideoEnabled(string negotiatedVersion)
        => string.CompareOrdinal("2.7.7", negotiatedVersion) <= 0;

    public static TheoryData<string, string[], string[]> RealClientPairs() => new()
    {
        { "android -> android", AndroidVersions, AndroidVersions },
        { "android -> tdesktop", AndroidVersions, TDesktopVersions },
        { "tdesktop -> android", TDesktopVersions, AndroidVersions },
        { "tdesktop -> tdesktop", TDesktopVersions, TDesktopVersions }
    };

    [Theory]
    [MemberData(nameof(RealClientPairs))]
    public void Negotiate_RealClientLists_PicksVersionBothSupport_AndKeepsAndroidVideoEnabled(
        string scenario,
        string[] callerVersions,
        string[] calleeVersions)
    {
        var negotiated = PhoneCallProtocolHelper.Negotiate(callerVersions, calleeVersions).LibraryVersions.ToList();

        negotiated.Count.ShouldBe(1, scenario);
        var selected = negotiated[0];

        callerVersions.ShouldContain(selected, scenario);
        calleeVersions.ShouldContain(selected, scenario);

        // Regression: selecting the callee's first entry would yield "10.0.0" whenever the callee is
        // Android, which silently downgrades every video call to audio. Telegram's servers give two
        // Android clients 8.0.0, which passes the gate.
        AndroidKeepsVideoEnabled(selected).ShouldBeTrue(
            $"{scenario}: negotiated '{selected}' fails Android's \"2.7.7\".compareTo(v) <= 0 video gate");
        selected.ShouldBe("8.0.0", scenario);
    }

    [Fact]
    public void Negotiate_OnlyLegacyVersionInCommon_SelectsIt()
    {
        // Degradation stays correct: with nothing newer shared, the legacy version is used (and Android
        // correctly reports video as unavailable, because libtgvoip 2.4.4 has no video support).
        var negotiated = PhoneCallProtocolHelper.Negotiate(["2.4.4", "9.0.0"], ["2.4.4"]).LibraryVersions.ToList();

        negotiated.ShouldBe(new[] { "2.4.4" });
    }
}
