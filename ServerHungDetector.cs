using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
                var cleanedLog = await CleanLog(logs);

                var logParts = cleanedLog.Split(" ", StringSplitOptions.RemoveEmptyEntries);
                _logger.LogInformation("logParts {logParts}", JsonConvert.SerializeObject(logParts, Formatting.Indented));

                if (DateTime.TryParse(logParts[0], out DateTime logTime))
                {
                    _logger.LogInformation("logTime {logTime}", JsonConvert.SerializeObject(logTime, Formatting.Indented));
                    lastLogTime = logTime;
                }
            }
        }

        if (DateTime.UtcNow - lastLogTime > _timeout)
        {
            _logger.LogWarning($"No logs for {_timeout.TotalSeconds} seconds, restarting container: {containerName}");
            await _dockerClient.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters());
        }
    }

    private async Task<string> CleanLog(Stream logs)
    {
        byte[] buffer = new byte[4096]; // Use a buffer for reading the stream
        int bytesRead;

        using (MemoryStream memoryStream = new MemoryStream())
        {
            while ((bytesRead = await logs.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                int offset = 0;

                while (offset < bytesRead)
                {
                    // Skip the first 8-byte header
                    int segmentHeaderLength = 8;
                    if (bytesRead - offset < segmentHeaderLength)
                    {
                        break; // Not enough data for a complete header
                    }

                    // Read the segment length from the header
                    int segmentLength = BitConverter.ToInt32(buffer, offset + 4); // Last 4 bytes represent length
                    
                    if (offset + segmentHeaderLength + segmentLength > bytesRead)
                    {
                        break; // Not enough data for the entire segment
                    }

                    // Read the actual log content
                    byte[] segment = new byte[segmentLength];
                    Array.Copy(buffer, offset + segmentHeaderLength, segment, 0, segmentLength);

                    // Convert to string and process the log data
                    string logContent = Encoding.UTF8.GetString(segment);
                    memoryStream.Write(segment, 0, segmentLength); // Store in memory stream

                    offset += segmentHeaderLength + segmentLength; // Move the offset
                }
            }

            // Convert the memory stream to a string and process the logs
            return Encoding.UTF8.GetString(memoryStream.ToArray());
        }
    }
}
