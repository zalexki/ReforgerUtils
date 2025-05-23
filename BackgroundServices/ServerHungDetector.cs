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

namespace ReforgerScenarioRotation.BackgroundServices;

public class ServerHungDetector : BackgroundService
{
    private readonly ILogger<ServerHungDetector> _logger;
    private readonly DockerClient _dockerClient;

    private List<string> _containerNames;
    private readonly TimeSpan _timeout;

    public ServerHungDetector(ILogger<ServerHungDetector> logger)
    {
        _logger = logger;
        _timeout = TimeSpan.FromSeconds(360);
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

        DateTime lastLogTime = DateTime.UtcNow;

        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Follow = false,
            Tail = "1"
        };

        var dateNow = DateTime.UtcNow;
        
        var applicationHang = false;
        using (var logs = await _dockerClient.Containers.GetContainerLogsAsync(container.ID, false, logParams, stoppingToken))
        {
            if (logs != null)
            {
                var (stdout, stderr) = await logs.ReadOutputToEndAsync(stoppingToken);
                string logLine = stdout; // Use stdout or stderr as needed

                if (logLine.Length > 0)
                {
                    var logParts = logLine.Split(" ", StringSplitOptions.RemoveEmptyEntries);
                    // _logger.LogInformation("logParts {logParts}", JsonConvert.SerializeObject(logParts, Formatting.Indented));
                    foreach (var item in logParts)
                    {
                        // _logger.LogInformation("item {item}", JsonConvert.SerializeObject(item, Formatting.Indented));
                        var dateString = FindDate(item);

                        if (DateTime.TryParse(FindDate(dateString), out DateTime logTime))
                        {
                            _logger.LogInformation("logTime {logTime}", logTime);
                            lastLogTime = logTime;
                        }
                    }
                }
            }
        }

        var logParamsMoreLines = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Follow = false,
            Tail = "10"
        };

        using (var logs = await _dockerClient.Containers.GetContainerLogsAsync(container.ID, false, logParamsMoreLines))
        {
            if (logs != null)
            {
                var (stdout, stderr) = await logs.ReadOutputToEndAsync(stoppingToken);
                string logLines = stdout; // Use stdout or stderr as needed

                if (logLines.Length > 0)
                {
                    if (logLines.Contains("Application hangs"))
                    {
                        _logger.LogWarning("Application hangs found gonna restart");
                        applicationHang = true;
                    }
                }
            }
        }


        var delta = dateNow - lastLogTime;
        _logger.LogInformation("TimeDiff is {lastLogTime} and timeout is {timeout} so delta is {delta} for {containerName}",
            JsonConvert.SerializeObject(lastLogTime, Formatting.Indented),
            JsonConvert.SerializeObject(_timeout, Formatting.Indented),
            JsonConvert.SerializeObject(delta, Formatting.Indented),
            containerName);
        if (delta > _timeout || applicationHang)
        {
            _logger.LogWarning($"No logs for {_timeout.TotalSeconds} seconds or app hung, restarting container: {containerName}");
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
