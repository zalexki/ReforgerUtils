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
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, byte> _flaggedAsHung = new();
    private readonly ConcurrentDictionary<string, DateTime> _serverStartTime = new();
    private readonly ConcurrentDictionary<string, byte> _twelveHourAlertSent = new();

    public ServerHungDetector(ILogger<ServerHungDetector> logger)
    {
        _logger = logger;
        _dockerClient = new DockerClientConfiguration().CreateClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var envNames = Environment.GetEnvironmentVariable("SERVER_CONTAINER_NAMES")
                       ?? throw new Exception("SERVER_CONTAINER_NAMES env is missing");

        var containerNames = envNames.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

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

        var target = containers.FirstOrDefault(c =>
            HungLogEvaluator.MatchesContainerName(c.Names, containerName) && c.State == "running");

        if (target is null)
        {
            _logger.LogWarning("Container {Name}: not found or not running, skipping", containerName);
            return;
        }

        var inspect = await _dockerClient.Containers.InspectContainerAsync(target.ID, ct);
        var tty = inspect.Config?.Tty ?? false;

        var logParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Timestamps = true,
            Tail = "50"
        };

        using var response = await _dockerClient.Containers.GetContainerLogsAsync(target.ID, tty, logParams, ct);
        var (stdout, stderr) = await response.ReadOutputToEndAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var result = HungLogEvaluator.Evaluate(stdout, stderr, now, _timeout);
        var lastLog = HungLogEvaluator.TryGetLastLogTimestamp(stdout, stderr);
        var serverName = HungLogEvaluator.ToDisplayName(containerName);
        var wasHung = _flaggedAsHung.ContainsKey(containerName);

        _logger.LogInformation(
            "Hung check {Container} ({Display}): result={Result} lastLog={LastLog} stdoutChars={Stdout} stderrChars={Stderr}",
            containerName, serverName, result, lastLog, stdout?.Length ?? 0, stderr?.Length ?? 0);

        if (result == HungCheckResult.Inconclusive)
        {
            _logger.LogWarning(
                "Container {Name}: no parseable docker log timestamp, not treating as hung",
                containerName);
        }

        if (HungLogEvaluator.ShouldSendHungAlert(wasHung, result))
        {
            await SendDiscordAlert(
                $"@here ⚠️ **Server Hung**: `{serverName}` last docker log is older than {_timeout.TotalMinutes:F0}m ({lastLog:u}).");
        }
        else if (HungLogEvaluator.ShouldSendRecoveryAlert(wasHung, result))
        {
            await SendDiscordAlert($"✅ **Server Recovered**: `{serverName}` is logging again.");
        }

        if (HungLogEvaluator.IsNowHung(wasHung, result))
            _flaggedAsHung.TryAdd(containerName, 0);
        else
            _flaggedAsHung.TryRemove(containerName, out _);

        if (!_serverStartTime.ContainsKey(containerName))
        {
            _serverStartTime[containerName] = DateTime.UtcNow;
            _twelveHourAlertSent.TryRemove(containerName, out _);
        }

        if (!_twelveHourAlertSent.ContainsKey(containerName) &&
            DateTime.UtcNow - _serverStartTime[containerName] >= TimeSpan.FromHours(12))
        {
            var uptime = DateTime.UtcNow - _serverStartTime[containerName];
            await SendDiscordAlert(
                $"ℹ️ **Server Uptime**: `{serverName}` has been running for {uptime.TotalHours:F0} hours.");
            _twelveHourAlertSent.TryAdd(containerName, 0);
        }
    }

    private static async Task SendDiscordAlert(string message)
    {
        using var client = new HttpClient();
        var webhookUrl = Environment.GetEnvironmentVariable("DISCORD_WEBHOOK_URL");

        if (string.IsNullOrEmpty(webhookUrl)) return;

        var content = new { content = message };
        await client.PostAsJsonAsync(webhookUrl, content);
    }
}
