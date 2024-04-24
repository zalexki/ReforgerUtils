using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<ServerHungDetector>();
        // services.AddHostedService<ScenarioRotationWorker>();
    })
    .Build();

host.Run();
