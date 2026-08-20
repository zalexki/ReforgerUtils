using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReforgerScenarioRotation.BackgroundServices;

public class AdminListSyncWorker : BackgroundService
{
    public const int DefaultIntervalSeconds = 30;

    private readonly ILogger<AdminListSyncWorker> _logger;
    private readonly ServerConfigStore _serverConfigStore;
    private readonly AdminListLoader _adminListLoader;
    private readonly IReadOnlyList<string> _serverContainerNames;
    private readonly TimeSpan _interval;

    public AdminListSyncWorker(
        ILogger<AdminListSyncWorker> logger,
        ServerConfigStore serverConfigStore,
        AdminListLoader adminListLoader)
    {
        _logger = logger;
        _serverConfigStore = serverConfigStore;
        _adminListLoader = adminListLoader;
        _serverContainerNames = ServerConfigStore.GetServerContainerNames();
        _interval = GetInterval();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                SyncOnce();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AdminListSyncWorker loop");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private void SyncOnce()
    {
        if (!_adminListLoader.TryLoad(out var entries))
        {
            return;
        }

        var adminIds = entries.Select(e => e.Id).ToList();

        foreach (var containerName in _serverContainerNames)
        {
            var configFilePath = ServerConfigStore.GetConfigFilePath(containerName);
            try
            {
                if (_serverConfigStore.TryReplaceAdmins(configFilePath, adminIds))
                {
                    _logger.LogInformation(
                        "Server {ServerName}: synced {Count} admins [{Admins}]",
                        containerName,
                        adminIds.Count,
                        AdminListLoader.FormatEntries(entries));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync admins for {ServerName} at {Path}", containerName, configFilePath);
            }
        }
    }

    private static TimeSpan GetInterval()
    {
        var raw = Environment.GetEnvironmentVariable("ADMIN_SYNC_INTERVAL_SECONDS");
        if (int.TryParse(raw, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.FromSeconds(DefaultIntervalSeconds);
    }
}
