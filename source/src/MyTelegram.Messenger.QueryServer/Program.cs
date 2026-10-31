using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MyTelegram;
using MyTelegram.Caching.Redis;
using MyTelegram.EventBus.RabbitMQ.Extensions;
using MyTelegram.Messenger;
using MyTelegram.Messenger.Services.Email;
using MyTelegram.Messenger.QueryServer.BackgroundServices;
using MyTelegram.Messenger.QueryServer.Extensions;
using MyTelegram.Services.NativeAot;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using MyTelegramConsts = MyTelegram.MyTelegramConsts;

Console.Title = $"MyTelegram messenger query server (layer {MyTelegramConsts.Layer})";

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Async(c => c.Console(theme: AnsiConsoleTheme.Code))
    .WriteTo.Async(c => c.File("Logs/startup-log.txt"))
    .CreateLogger();

Log.Information("{Info} {Version}", "MyTelegram messenger query server", typeof(Program).Assembly.GetName().Version);
Log.Information("{Description} {Url}",
    "For more information, please visit",
    MyTelegramConsts.RepositoryUrl);

Log.Information("MyTelegram messenger query server(API layer={Layer}) starting...",
    MyTelegramConsts.Layer);

AppDomain.CurrentDomain.UnhandledException += (_,
    e) =>
{
    Log.Error(e.ExceptionObject.ToString() ?? "UnhandledException");
};
TaskScheduler.UnobservedTaskException += (_,
    e) =>
{
    Log.Error(e.Exception.ToString());
};
var builder = Host.CreateDefaultBuilder(args);
builder.UseSerilog((context,
    configuration) =>
{
    configuration.ReadFrom.Configuration(context.Configuration);
});
builder.ConfigureHostOptions(options =>
{
    options.ServicesStopConcurrently = true;
});

builder.ConfigureAppConfiguration(options =>
{
    options.AddEnvironmentVariables();
    options.AddCommandLine(args);
});
builder.ConfigureServices((ctx,
    services) =>
{
    services.AddOptions<MyTelegramMessengerServerOptions>()
        .Bind(ctx.Configuration.GetRequiredSection("App"))
        .ValidateDataAnnotations()
        .ValidateOnStart()
        ;

    services.Configure<EmailSenderOptions>(ctx.Configuration.GetRequiredSection("EmailSenderOptions"));

    services.Configure<EventBusRabbitMqOptions>(ctx.Configuration.GetRequiredSection("RabbitMQ:EventBus"));
    services.Configure<RabbitMqOptions>(ctx.Configuration.GetRequiredSection("RabbitMQ:Connections:Default"));

    // Verification codes reach the Telegram delivery bot through a queue, not an HTTP call.
    services.Configure<MyTelegram.Messenger.Options.PremiumOptions>(
        ctx.Configuration.GetSection(MyTelegram.Messenger.Options.PremiumOptions.SectionName));
    services.Configure<MyTelegram.Services.Services.BotCodeQueueOptions>(
        ctx.Configuration.GetSection("TelegramBotSms"));

    var eventBusOptions = ctx.Configuration.GetRequiredSection("RabbitMQ:EventBus").Get<EventBusRabbitMqOptions>();
    var rabbitMqOptions = ctx.Configuration.GetRequiredSection("RabbitMQ:Connections:Default").Get<RabbitMqOptions>();

    services.AddMyTelegramRabbitMqEventBus();
    //services.AddRebusEventBus(options =>
    //{
    //    options.Transport(t =>
    //    {
    //        t.UseRabbitMq(
    //                $"amqp://{rabbitMqOptions!.UserName}:{rabbitMqOptions.Password}@{rabbitMqOptions.HostName}:{rabbitMqOptions.Port}",
    //                eventBusOptions!.ClientName)
    //            .ExchangeNames(eventBusOptions.ExchangeName, eventBusOptions.TopicExchangeName ?? "RebusTopics")
    //            ;
    //    });
    //    options.AddSystemTextJson(jsonOptions =>
    //    {
    //        jsonOptions.TypeInfoResolverChain.Add(MyJsonSerializeContext.Default);
    //    });
    //});

    services.AddMyTelegramMessengerQueryServer();
    MyTelegram.Messenger.Extensions.AntiSpamServiceCollectionExtensions.AddAntiSpamServices(services, ctx.Configuration);
    services.AddTransient<MessengerQueryDataProcessor>();
    services.AddTransient<IDataProcessor<MessengerQueryDataReceivedEvent>, MessengerQueryDataProcessor>();
    services.AddSingleton<IMessageQueueProcessor<MessengerQueryDataReceivedEvent>>(sp =>
        new MessageQueueProcessor<MessengerQueryDataReceivedEvent>(
            sp.GetRequiredService<MessengerQueryDataProcessor>(),
            sp.GetRequiredService<ILogger<MessageQueueProcessor<MessengerQueryDataReceivedEvent>>>()));

    services.AddMyTelegramStackExchangeRedisCache(options =>
    {
        options.Configuration = ctx.Configuration.GetValue<string>("Redis:Configuration");
    });
    services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
        StackExchange.Redis.ConnectionMultiplexer.Connect(
            ctx.Configuration.GetValue<string>("Redis:Configuration")!));

    services.AddHostedService<MessageQueueDataProcessorBackgroundService<IDomainEvent>>();
    services.AddHostedService<MyTelegramQueryServerBackgroundService>();
    services.AddHostedService<MyTelegram.Messenger.Services.AdminLog.AdminLogIndexInitializer>();
    services.AddHostedService<DataProcessorBackgroundService>();
    services.AddSingleton<FileDownloadLaneRouter>();
    services.AddSingleton<IFileDownloadLaneRouter>(sp => sp.GetRequiredService<FileDownloadLaneRouter>());
    services.AddHostedService(sp => sp.GetRequiredService<FileDownloadLaneRouter>());
    services.AddHostedService<ObjectMessageSenderBackgroundService>();
    services.AddHostedService<MyTelegramInvokeAfterMsgProcessorBackgroundService>();
    services.AddHostedService<QueuedCommandExecutorBackgroundService>();
    services.AddHostedService<AuctionRoundBackgroundService>();
    services.AddHostedService<MyTelegramQueryServerHandlerBackgroundService>();

    services.Configure<HostOptions>(options =>
    {
        options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    });
});

var app = builder.Build();

Log.Information("Messenger query server starting RunAsync...");
_ = app.RunAsync();
Log.Information("Messenger query server running, waiting...");
Thread.Sleep(Timeout.Infinite);
