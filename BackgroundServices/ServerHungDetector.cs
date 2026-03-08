using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReforgerScenarioRotation.BackgroundServices;

public class ServerHungDetector : BackgroundService
{
    private readonly ILogger<ServerHungDetector> _logger;
    private readonly DockerClient _dockerClient;
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(6);
    private readonly ConcurrentDictionary<string, DateTime> _lastAlertTime = new();
    private readonly TimeSpan _alertInterval = TimeSpan.FromMinutes(10);
    private readonly HashSet<string> _flaggedAsHung = new(); // Tracks if a server is currently in a "hung" state
    
    public ServerHungDetector(ILogger<ServerHungDetector> logger)
    {
        _logger = logger;
        _dockerClient = new DockerClientConfiguration().CreateClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string envNames = Environment.GetEnvironmentVariable("SERVER_CONTAINER_NAMES") 
            ?? throw new Exception("SERVER_CONTAINER_NAMES env is missing");
        
        var containerNames = envNames.Split(',').Select(s => s.Trim()).ToList();

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var name in containerNames)
            {
                await InspectContainerLogs(name, stoppingToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task InspectContainerLogs(string containerName, CancellationToken ct)
    {
        var containers = await _dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters { All = true }, ct);

        var target = containers.FirstOrDefault(c => c.Names.Any(n => n.Contains(containerName)));

        if (target == null || target.State != "running") return;

        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Tail = "1"
        };

        using var response = await _dockerClient.Containers.GetContainerLogsAsync(target.ID, false, logParams, ct);
        var (stdout, _) = await response.ReadOutputToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(stdout) || stdout.Length < 38) return;

        string timestampPart = stdout.Substring(8, 30);

        if (DateTime.TryParse(timestampPart, out DateTime lastLogTime))
        {
            var silenceDuration = DateTime.UtcNow - lastLogTime.ToUniversalTime();

            if (silenceDuration > _timeout)
            {
                // Check if we've sent an alert recently for this specific container
                if (_lastAlertTime.TryGetValue(containerName, out DateTime lastSent) &&
                    DateTime.UtcNow - lastSent < _alertInterval)
                {
                    return; // Exit to avoid spamming
                }

                _logger.LogWarning("Container {Name} silent for {Secs}s. Sending Discord alert.", containerName,
                    silenceDuration.TotalSeconds);

                await SendDiscordAlert(containerName, silenceDuration.TotalMinutes);

                // Update the last alert time
                _lastAlertTime[containerName] = DateTime.UtcNow;
            }
            else
            {
                // Reset if the server starts logging again
                _lastAlertTime.TryRemove(containerName, out _);
            }
        }
    }

    private async Task SendDiscordAlert(string serverName, double minutes)
    {
        using var client = new HttpClient();
        var webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");
    
        if (string.IsNullOrEmpty(webhookUrl)) return;

        var content = new { content = $"@here ⚠️ **Server Hung Alert**: `{serverName}` has had no log output for {minutes:F1} minutes." };
        await client.PostAsJsonAsync(webhookUrl, content);
    }
}