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
        var envNames = Environment.GetEnvironmentVariable("SERVER_CONTAINER_NAMES")
                       ?? throw new Exception("SERVER_CONTAINER_NAMES env is missing");

        var containerNames = envNames.Split(',').Select(s => s.Trim()).ToList();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var name in containerNames)
                {
                    await InspectContainerLogs(name, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ServerHungDetector loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task InspectContainerLogs(string containerName, CancellationToken ct)
    {
        var containers = await _dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters { All = true }, ct);

        var target = containers.FirstOrDefault(c => c.Names.Any(n => n.Contains(containerName)));

        if (target is not { State: "running" })
        {
            _logger.LogDebug("Container {Name}: not found or not running, skipping", containerName);
            return;
        }

        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Tail = "1"
        };

        using var response = await _dockerClient.Containers.GetContainerLogsAsync(target.ID, false, logParams, ct);
        var (stdout, _) = await response.ReadOutputToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogWarning("Container {Name}: no log output yet", containerName);
            return;
        }
        _logger.LogInformation("Container {Name}: raw stdout='{Stdout}' stderr='{Stderr}'", 
            containerName, stdout, stderr);
        
        // ReadOutputToEndAsync already strips Docker's binary header — no offset needed
        var spaceIndex = stdout.IndexOf(' ');
        if (spaceIndex < 0)
        {
            _logger.LogWarning("Container {Name}: could not find timestamp separator: '{Raw}'", containerName, stdout);
            return;
        }

        var timestampPart = stdout.Substring(0, spaceIndex);
        _logger.LogWarning("Container {Name}: last log timestamp = '{Timestamp}'", containerName, timestampPart);

        if (DateTime.TryParse(timestampPart, null, System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTime lastLogTime))
        {
            var silenceDuration = DateTime.UtcNow - lastLogTime;
            _logger.LogWarning("Container {Name}: silence duration = {Minutes:F1}m", containerName,
                silenceDuration.TotalMinutes);

            if (silenceDuration > _timeout)
            {
                _flaggedAsHung.Add(containerName);

                if (_lastAlertTime.TryGetValue(containerName, out DateTime lastSent) &&
                    DateTime.UtcNow - lastSent < _alertInterval)
                {
                    return;
                }

                await SendDiscordAlert(containerName,
                    $"@here ⚠️ **Server Hung**: `{containerName}` silent for {silenceDuration.TotalMinutes:F1}m.");
                _lastAlertTime[containerName] = DateTime.UtcNow;
            }
            else if (_flaggedAsHung.Contains(containerName))
            {
                await SendDiscordAlert(containerName, $"✅ **Server Recovered**: `{containerName}` is logging again.");
                _flaggedAsHung.Remove(containerName);
                _lastAlertTime.TryRemove(containerName, out _);
            }
        }
        else
        {
            _logger.LogWarning("Container {Name}: failed to parse timestamp '{Timestamp}'", containerName,
                timestampPart);
        }
    }

    private async Task SendDiscordAlert(string serverName, string message)
    {
        using var client = new HttpClient();
        var webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");

        if (string.IsNullOrEmpty(webhookUrl)) return;

        var content = new { content = message };
        await client.PostAsJsonAsync(webhookUrl, content);
    }
}