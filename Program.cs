using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReforgerScenarioRotation.BackgroundServices;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<MultiServerScenarioRotationWorker>();
    })
    .Build();

host.Run();
