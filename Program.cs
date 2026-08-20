using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReforgerScenarioRotation.BackgroundServices;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton<ServerConfigStore>();
        services.AddSingleton<AdminListLoader>();
        services.AddHostedService<MultiServerScenarioRotationWorker>();
        services.AddHostedService<AdminListSyncWorker>();
    })
    .Build();

host.Run();
