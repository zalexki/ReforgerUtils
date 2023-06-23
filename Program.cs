using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReforgerScenarioRotation;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
    })
    .Build();

host.Run();
