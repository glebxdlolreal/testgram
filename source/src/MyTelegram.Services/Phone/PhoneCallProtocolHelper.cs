using MyTelegram.Schema;

namespace MyTelegram.Services.Phone;

public static class PhoneCallProtocolHelper
{
    public const int LegacyMinLayer = 65;
    public const int LegacyMaxLayer = 92;

    private static readonly string[] DefaultLibraryVersions = ["2.7.7"];

    public static bool HasValidLegacyFlags(IPhoneCallProtocol? protocol)
    {
        return protocol is { UdpP2p: true, UdpReflector: true };
    }

    /// <summary>
    /// Returns <c>true</c> when the peer's <c>[min_layer, max_layer]</c> window is well-formed and overlaps
    /// the window this server supports (<see cref="LegacyMinLayer"/>..<see cref="LegacyMaxLayer"/>).
    /// </summary>
    /// <remarks>
    /// This deliberately checks for an overlap rather than exact equality. The official apps send 65/92
    /// (Android <c>VoIPService.CALL_MIN_LAYER</c> + <c>Instance.getConnectionMaxLayer()</c>; tdesktop
    /// <c>kMinLayer</c> + <c>tgcalls::Meta::MaxLayer()</c>), but TDLib's <c>CallProtocol</c> defaults to
    /// <c>min_layer = max_layer = 65</c>, and an equality check would reject every stock TDLib client with
    /// <c>CALL_PROTOCOL_LAYER_INVALID</c>.
    /// </remarks>
    public static bool HasValidLegacyLayers(IPhoneCallProtocol? protocol)
    {
        return protocol != null
               && protocol.MinLayer <= protocol.MaxLayer
               && protocol.MinLayer <= LegacyMaxLayer
               && protocol.MaxLayer >= LegacyMinLayer;
    }

    public static IReadOnlyList<string> GetLibraryVersions(IPhoneCallProtocol? protocol)
    {
        return NormalizeVersions(protocol?.LibraryVersions);
    }

    /// <summary>
    /// Returns <c>true</c> when the caller and callee <c>library_versions</c> lists share at least one
    /// common tgcalls version. An empty intersection means the two peers cannot agree on a protocol
    /// version and the call must be rejected with <c>CALL_PROTOCOL_COMPAT_LAYER_INVALID</c>.
    /// </summary>
    public static bool HasCommonLibraryVersion(
        IEnumerable<string>? callerLibraryVersions,
        IEnumerable<string>? calleeLibraryVersions)
    {
        return TryGetCommonLibraryVersion(callerLibraryVersions, calleeLibraryVersions, out _);
    }

    /// <summary>
    /// Selects the tgcalls version both peers will run, the way Telegram's servers do: <c>8.0.0</c> first,
    /// then the <c>10.0.0</c>-<c>13.0.0</c> family, then anything else; within a group the caller's own order
    /// decides.
    /// </summary>
    /// <remarks>
    /// Derived from 26 calls between two real accounts on Telegram's production servers (2026-11-01), every
    /// one of which this rule reproduces. Highest-number, ordinal and callee-preference rules all disagree
    /// with production: two Android clients (which advertise <c>2.7.7</c>-<c>19.0.0</c>) are given
    /// <c>8.0.0</c>, <c>[12, 13]</c> yields 12 while <c>[13, 12]</c> yields 13, and <c>[9, 13]</c> yields 13.
    /// <para>
    /// Both official clients instantiate <c>library_versions[0]</c> of the server's reply (tdesktop
    /// <c>Call::createAndStartController</c>, Android <c>Instance.makeInstance</c>), and Android keeps the
    /// camera only when <c>"2.7.7".compareTo(version) &lt;= 0</c>, a lexicographic comparison that every
    /// two-digit major version fails. Preferring <c>8.0.0</c> is what keeps video working between Android
    /// clients.
    /// </para>
    /// </remarks>
    public static bool TryGetCommonLibraryVersion(
        IEnumerable<string>? callerLibraryVersions,
        IEnumerable<string>? calleeLibraryVersions,
        out string? commonVersion)
    {
        var calleeVersions = new HashSet<string>(
            NormalizeVersions(calleeLibraryVersions),
            StringComparer.Ordinal);
        commonVersion = NormalizeVersions(callerLibraryVersions)
            .Select((version, index) => (version, index))
            .Where(entry => calleeVersions.Contains(entry.version))
            .OrderByDescending(entry => GetVersionPriority(entry.version))
            .ThenBy(entry => entry.index)
            .Select(entry => entry.version)
            .FirstOrDefault();
        return commonVersion != null;
    }

    /// <summary>The preference group Telegram's servers put a tgcalls version in (higher wins).</summary>
    private static int GetVersionPriority(string version)
    {
        return version switch
        {
            "8.0.0" => 2,
            "10.0.0" or "11.0.0" or "12.0.0" or "13.0.0" => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Returns <c>true</c> when the peer explicitly advertised at least one tgcalls <c>library_versions</c>
    /// entry. Only clients that negotiate via <c>library_versions</c> (as opposed to legacy
    /// <c>min_layer</c>/<c>max_layer</c> only) can be upgraded to a conference call, so this signals
    /// whether the peer supports the <c>conference_supported</c> upgrade path.
    /// </summary>
    public static bool AdvertisesConferenceSupport(IPhoneCallProtocol? protocol)
    {
        var versions = protocol?.LibraryVersions;
        return versions != null && versions.Any(version => !string.IsNullOrWhiteSpace(version));
    }

    public static TPhoneCallProtocol Normalize(IPhoneCallProtocol? protocol)
    {
        return FromLibraryVersions(GetLibraryVersions(protocol));
    }

    public static TPhoneCallProtocol FromLibraryVersions(IEnumerable<string>? libraryVersions)
    {
        return new TPhoneCallProtocol
        {
            UdpP2p = true,
            UdpReflector = true,
            MinLayer = LegacyMinLayer,
            MaxLayer = LegacyMaxLayer,
            LibraryVersions = new TVector<string>(NormalizeVersions(libraryVersions))
        };
    }

    public static TPhoneCallProtocol Negotiate(
        IEnumerable<string>? callerLibraryVersions,
        IPhoneCallProtocol? calleeProtocol)
    {
        return Negotiate(callerLibraryVersions, GetLibraryVersions(calleeProtocol));
    }

    public static TPhoneCallProtocol Negotiate(
        IEnumerable<string>? callerLibraryVersions,
        IEnumerable<string>? calleeLibraryVersions)
    {
        return TryGetCommonLibraryVersion(callerLibraryVersions, calleeLibraryVersions, out var selectedVersion)
            ? FromLibraryVersions([selectedVersion!])
            : FromLibraryVersions(NormalizeVersions(calleeLibraryVersions));
    }

    private static IReadOnlyList<string> NormalizeVersions(IEnumerable<string>? libraryVersions)
    {
        if (libraryVersions == null)
        {
            return DefaultLibraryVersions;
        }

        var versions = libraryVersions
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return versions.Length == 0 ? DefaultLibraryVersions : versions;
    }
}
