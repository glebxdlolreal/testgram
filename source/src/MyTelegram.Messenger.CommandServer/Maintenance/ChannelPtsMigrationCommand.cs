using Microsoft.Extensions.Configuration;
using MongoDB.Driver;
using MyTelegram.Messenger.Services.Pts;

namespace MyTelegram.Messenger.CommandServer.Maintenance;

/// <summary>
/// Offline operator entry point for the channel Pts cutover described in docs/native-antispam.md.
/// <c>--ChannelPtsMigration:Mode=Discover</c> only reads and prints the legacy HiLo inventory.
/// <c>--ChannelPtsMigration:Mode=Bootstrap</c> additionally requires
/// <c>--ChannelPtsMigration:Attestation=...</c>, seeds the durable counters and publishes the fence.
/// Run it only while every command-server and query-server process is stopped; the hosted services
/// of this process are never started in either mode.
/// </summary>
internal static class ChannelPtsMigrationCommand
{
    public const string SectionName = "ChannelPtsMigration";

    public static async Task<int> RunAsync(IServiceProvider services, IConfigurationSection section,
        CancellationToken cancellationToken = default)
    {
        var mode = section["Mode"];
        if (mode is not ("Discover" or "Bootstrap"))
        {
            Console.Error.WriteLine($"{SectionName}:Mode must be Discover or Bootstrap, got '{mode}'.");
            return 2;
        }

        var allocator = new DurableChannelPtsAllocator(services.GetRequiredService<IMongoDatabase>());
        var inventory = await allocator.DiscoverLegacyInventoryAsync(cancellationToken);
        foreach (var seed in inventory)
        {
            Console.WriteLine($"channel={seed.ChannelId} persistedHigh={seed.PersistedHigh} observedPts={seed.ObservedPts} seedPts={seed.HighWatermark()}");
        }
        Console.WriteLine($"Discovered {inventory.Count} channels, {inventory.Count(x => x.PersistedHigh > 0)} with a legacy HiLo sequence.");

        if (mode == "Discover")
        {
            return 0;
        }

        var attestation = section["Attestation"];
        if (string.IsNullOrWhiteSpace(attestation))
        {
            Console.Error.WriteLine($"{SectionName}:Attestation is required for Bootstrap: state who stopped which writers and when.");
            return 2;
        }

        await allocator.BootstrapQuiescedAsync(inventory, attestation, cancellationToken);
        Console.WriteLine($"Channel Pts authority enabled: seeded {inventory.Count} channels and published the {DurableChannelPtsAllocator.FenceCollection} fence.");
        return 0;
    }
}
