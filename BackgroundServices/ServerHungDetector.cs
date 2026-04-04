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
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(3);
    private readonly ConcurrentDictionary<string, DateTime> _lastAlertTime = new();
    private readonly TimeSpan _alertInterval = TimeSpan.FromMinutes(10);
    private readonly HashSet<string> _flaggedAsHung = new(); // Tracks if a server is currently in a "hung" state
    private readonly ConcurrentDictionary<string, DateTime> _serverStartTime = new();
    private readonly HashSet<string> _sixHourAlertSent = new();

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
            _logger.LogWarning("Container {Name}: not found or not running, skipping", containerName);
            return;
        }

        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Since = DateTimeOffset.UtcNow.Subtract(_timeout).ToUnixTimeSeconds()
                .ToString() // only fetch logs within the timeout window
        };

        using var response = await _dockerClient.Containers.GetContainerLogsAsync(target.ID, false, logParams, ct);
        var (stdout, stderr) = await response.ReadOutputToEndAsync(ct);

        var logLine = !string.IsNullOrWhiteSpace(stdout) ? stdout : stderr;
        var serverName = containerName switch
        {
            "koth2-koth-reforged-2-1" => "EU-2",
            "koth3-koth-reforged-3-1" => "EU-3",
            "arma-koth-reforged-1-1" => "NA-1",
            "arma2-koth-reforged-2-1" => "NA-2",
            "arma3-koth-reforged-3-1" => "NA-3",
            _ => "EU-1"
        };
        
        if (string.IsNullOrWhiteSpace(logLine))
        {
            // No logs in the last 3 minutes → server is hung
            _flaggedAsHung.Add(containerName);
            _serverStartTime.TryRemove(containerName, out _); // reset uptime tracking
            _sixHourAlertSent.Remove(containerName);

            if (_lastAlertTime.TryGetValue(containerName, out DateTime lastSent) &&
                DateTime.UtcNow - lastSent < _alertInterval)
                return;

            await SendDiscordAlert(containerName,
                $"@here ⚠️ **Server Hung**: `{serverName}` silent for over {_timeout.TotalMinutes:F0}m.");
            _lastAlertTime[containerName] = DateTime.UtcNow;
        }
        else if (_flaggedAsHung.Contains(containerName))
        {
            // recovery alert - existing code
            await SendDiscordAlert(containerName, $"✅ **Server Recovered**: `{serverName}` is logging again.");
            _flaggedAsHung.Remove(containerName);
            _lastAlertTime.TryRemove(containerName, out _);
        }

        // Track uptime and alert at 6 hours
        if (!_serverStartTime.ContainsKey(containerName))
        {
            _serverStartTime[containerName] = DateTime.UtcNow;
            _sixHourAlertSent.Remove(containerName); // reset if restarted
        }

        if (!_sixHourAlertSent.Contains(containerName) &&
            DateTime.UtcNow - _serverStartTime[containerName] >= TimeSpan.FromHours(6))
        {
            var uptime = DateTime.UtcNow - _serverStartTime[containerName];
            await SendDiscordAlert(containerName,
                $"ℹ️ **Server Uptime**: `{serverName}` has been running for {uptime.TotalHours:F0} hours.");
            _sixHourAlertSent.Add(containerName);
        }
    }

    private static async Task SendDiscordAlert(string serverName, string message)
    {
        using var client = new HttpClient();
        var webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");

        if (string.IsNullOrEmpty(webhookUrl)) return;

        var content = new { content = message };
        await client.PostAsJsonAsync(webhookUrl, content);
    }
}