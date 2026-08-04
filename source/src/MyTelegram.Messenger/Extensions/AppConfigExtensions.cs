using MyTelegram.Messenger.Services.Interfaces;

namespace MyTelegram.Messenger.Extensions;

/// <summary>
/// Reads client configuration limits out of <see cref="IAppConfigHelper"/> so handlers enforce the
/// same values the client is told about, instead of hardcoding their own copies.
/// See https://corefork.telegram.org/api/config#client-configuration
/// </summary>
public static class AppConfigExtensions
{
    public static int GetInt32(this IAppConfigHelper appConfigHelper, string key, int defaultValue)
    {
        return appConfigHelper.GetAppConfig() is TJsonObject config
               && config.Value.FirstOrDefault(p => p.Key == key) is TJsonObjectValue { Value: TJsonNumber number }
            ? (int)number.Value
            : defaultValue;
    }

    public static bool GetBoolean(this IAppConfigHelper appConfigHelper, string key, bool defaultValue)
    {
        return appConfigHelper.GetAppConfig() is TJsonObject config
               && config.Value.FirstOrDefault(p => p.Key == key) is TJsonObjectValue { Value: TJsonBool b }
            ? b.Value
            : defaultValue;
    }
}
