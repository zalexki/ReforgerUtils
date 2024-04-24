using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

public class ServerHungDetector : BackgroundService
{
    private readonly ILogger<ServerHungDetector> _logger;
    private readonly DockerClient _dockerClient;
    
    private List<string> _containerNames;
    private readonly TimeSpan _timeout;

    public ServerHungDetector(ILogger<ServerHungDetector> logger)
    {
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(150);
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
                _logger.LogInformation($" End Check {containerName}");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
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

        DateTime lastLogTime = DateTime.UtcNow;

        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Follow = false,
            Tail = "1"
        };

        using (var logs = await _dockerClient.Containers.GetContainerLogsAsync(container.ID, logParams))
        {
            if (logs != null)
            {
                using (StreamReader reader = new StreamReader(logs))
                {
                    string logLine = await reader.ReadToEndAsync();

                    if (logLine.Length > 0)
                    {
                        var logParts = logLine.Split(" ", StringSplitOptions.RemoveEmptyEntries);
                        // _logger.LogInformation("logParts {logParts}", JsonConvert.SerializeObject(logParts, Formatting.Indented));

                        if (DateTime.TryParse(FindDate(logParts[0]), out DateTime logTime))
                        {
                            // _logger.LogInformation("logTime {logTime}", JsonConvert.SerializeObject(logTime, Formatting.Indented));
                            lastLogTime = logTime;
                        }
                    }
                }
            }
        }

        _logger.LogInformation("TimeDiff is {logTime} and timeout is {timeout}", 
            JsonConvert.SerializeObject(DateTime.UtcNow - lastLogTime, Formatting.Indented),
            JsonConvert.SerializeObject(_timeout, Formatting.Indented));
        if (DateTime.UtcNow - lastLogTime > _timeout)
        {
            _logger.LogWarning($"No logs for {_timeout.TotalSeconds} seconds, FAKE restarting container: {containerName}");
            await _dockerClient.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters());
        }
    }

    private string FindDate(string data)
    {
        // Define the regex pattern to find dates in the specified format
        string pattern = @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z";

        // Create a Regex object
        Regex regex = new Regex(pattern);

        // Find all matches in the text
        MatchCollection matches = regex.Matches(data);

        // Iterate through the matches and print them
        foreach (Match match in matches)
        {
            return match.Value;
        }

        return string.Empty;
    }
}
