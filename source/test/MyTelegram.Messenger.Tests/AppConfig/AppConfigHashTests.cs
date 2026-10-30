using System.Buffers;
using System.IO.Hashing;
using MyTelegram.Messenger.Services.Impl;
using MyTelegram.Schema;

namespace MyTelegram.Messenger.Tests.AppConfig;

/// <summary>
/// help.getAppConfig answers appConfigNotModified when the client quotes the current hash, so the hash
/// must follow the config's content: a stale constant keeps clients on an old config forever.
/// </summary>
public class AppConfigHashTests
{
    [Fact]
    public void The_hash_is_derived_from_the_served_config()
    {
        var helper = new AppConfigHelper();
        var writer = new ArrayBufferWriter<byte>();
        ((TJsonObject)helper.GetAppConfig()).Serialize(writer);

        helper.GetAppConfigHash().ShouldBe((int)XxHash32.HashToUInt32(writer.WrittenSpan));
        helper.GetAppConfigHash().ShouldNotBe(0);
    }

    [Fact]
    public void Every_instance_reports_the_same_hash()
    {
        new AppConfigHelper().GetAppConfigHash().ShouldBe(new AppConfigHelper().GetAppConfigHash());
    }
}
