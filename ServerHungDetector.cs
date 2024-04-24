using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class ServerHungDetector : BackgroundService
{
    private readonly ILogger<ServerHungDetector> _logger;
    private readonly DockerClient _dockerClient;
    
    private List<string> _containerNames;
    private readonly TimeSpan _timeout;

    public ServerHungDetector(ILogger<ServerHungDetector> logger)
    {
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(65);
        _dockerClient = new DockerClientConfiguration().CreateClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var names = Environment.GetEnvironmentVariable("SERVER_CONTAINER_NAMES") ?? throw new Exception("missing SERVER_CONTAINER_NAMES env");
        _containerNames = names.Split(',').ToList();
        // _containerNames = "reforgerscenariorotation-test_container-1,other_container".Split(',').ToList();

        while (true)
        {
            foreach (var containerName in _containerNames)
            {
                _logger.LogInformation($" Start Check {containerName}");
                await CheckAndRestartContainer(containerName, stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task CheckAndRestartContainer(string containerName, CancellationToken stoppingToken)
    {
        var container = (await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true
        })).FirstOrDefault(c => c.Names.Any(name => name.Contains(containerName)));

        if (container == null)
        {
            _logger.LogInformation($"Container {containerName} not found.");
            return;
        }

        // Calculate a minute and a half ago
        DateTime aMinuteAndAHalfAgo = DateTime.UtcNow - _timeout;
        DateTime lastLogTime = DateTime.UtcNow;
        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Follow = false,
            Since = ((DateTimeOffset)aMinuteAndAHalfAgo).ToUnixTimeSeconds().ToString(),
            Tail = "1"
        };

        using (var logs = await _dockerClient.Containers.GetContainerLogsAsync(container.ID, logParams))
        {
            // if (logs != null)
            // {
            //     using (StreamReader reader = new StreamReader(logs))
            //     {
            //         string logLine = await reader.ReadToEndAsync();
            //         _logger.LogInformation($"Container {containerName} logLine is {logLine}.");

            //         if (logLine.Length > 0)
            //         {
            //             var logParts = logLine.Split(" ", StringSplitOptions.RemoveEmptyEntries);

            //             if (DateTime.TryParse(logParts[0], out DateTime logTime))
            //             {
            //                 lastLogTime = logTime;
            //             }
            //         }
            //     }
            // } 
            
            if (logs is null) {
                _logger.LogWarning($"No logs for {_timeout.TotalSeconds} seconds, restarting container: {containerName}");
                await _dockerClient.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters());
            }
        }

        // if (DateTime.UtcNow - lastLogTime > _timeout)
        // {
        //     _logger.LogWarning($"No logs for {_timeout.TotalSeconds} seconds, restarting container: {containerName}");
        //     await _dockerClient.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters());
        // }
    }
}
