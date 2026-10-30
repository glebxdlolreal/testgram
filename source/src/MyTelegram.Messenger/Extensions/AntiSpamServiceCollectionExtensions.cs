using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyTelegram.Messenger.Services.AntiSpam;
using MyTelegram.Messenger.Services.Mentions;

namespace MyTelegram.Messenger.Extensions;

public static class AntiSpamServiceCollectionExtensions
{
    /// <summary>
    /// Services every process that resolves RPC handlers or commits events needs for native anti-spam
    /// and mention reads. Call after EventFlow is configured: it replaces the event persistence so that
    /// every committed event indexes its anti-spam effect proof. Background drain loops are not
    /// registered here; only the command server hosts them.
    /// </summary>
    public static IServiceCollection AddAntiSpamServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.Replace(ServiceDescriptor.Transient<global::EventFlow.EventStores.IEventPersistence,
            MyTelegram.EventFlow.MongoDB.AntiSpamIndexedEventPersistence>());
        services.AddSingleton<AntiSpamEffectProofReader>();
        services.AddSingleton<IAntiSpamEffectProofReader>(sp => sp.GetRequiredService<AntiSpamEffectProofReader>());

        services.AddOptions<AntiSpamOptions>().Bind(configuration.GetSection("App:AntiSpam"));
        services.AddSingleton<AntiSpamJobStore>();
        services.AddSingleton<AntiSpamClassifier>();
        services.AddSingleton<AntiSpamCompletionService>();
        services.AddSingleton<AntiSpamDeletionEffectsService>();
        services.AddSingleton<AntiSpamLinkedPostPairValidator>();
        services.AddSingleton<MentionReadIntentService>();
        services.AddSingleton<IMentionReadIntentService>(sp => sp.GetRequiredService<MentionReadIntentService>());
        services.AddSingleton<AntiSpamMentionAdmissionService>();
        services.AddSingleton<AntiSpamMentionRecountService>();
        return services;
    }
}
