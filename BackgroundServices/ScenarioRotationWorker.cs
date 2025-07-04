using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReforgerScenarioRotation.BackgroundServices;

public class MultiServerScenarioRotationWorker : BackgroundService
{
    private readonly ILogger<MultiServerScenarioRotationWorker> _logger;
    private readonly DockerClient _dockerClient;

    // Dictionary to store scenario history for each server
    private readonly ConcurrentDictionary<string, List<string>> _serverScenarioHistory;

    private const string SERVER_CONFIG_FILE_PATH_TEMPLATE = "/server{0}/config.json";
    private const string LIST_SCENARIOS_FILE_PATH_TEMPLATE = "/server{0}/list_scenarios.json";

    // List of container names for all servers
    private readonly List<string> _serverContainerNames;

    public MultiServerScenarioRotationWorker(ILogger<MultiServerScenarioRotationWorker> logger)
    {
        _logger = logger;
        _dockerClient = new DockerClientConfiguration().CreateClient();
        _serverScenarioHistory = new ConcurrentDictionary<string, List<string>>();

        // Get container names from environment variables
        string serverNamesEnv = Environment.GetEnvironmentVariable("SERVER_CONTAINER_NAMES") ?? "arma-server-1,arma-server-2,arma-server-3";
        _serverContainerNames = serverNamesEnv.Split(',').Select(s => s.Trim()).ToList();

        // Initialize history for each server
        foreach (var containerName in _serverContainerNames)
        {
            _serverScenarioHistory[containerName] = new List<string>(SCENARIO_HISTORY_SIZE);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Process each server in parallel
            var tasks = _serverContainerNames.Select(containerName =>
                ProcessServerContainerAsync(containerName, stoppingToken));

            await Task.WhenAll(tasks);
            await Task.Delay(5000, stoppingToken);
        }
    }

    private async Task ProcessServerContainerAsync(string containerName, CancellationToken stoppingToken)
    {
        var listParam = new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                {
                    "name",
                    new Dictionary<string, bool>
                    {
                        { containerName, true }
                    }
                }
            }
        };

        var serverContainer = await _dockerClient.Containers
            .ListContainersAsync(listParam, cancellationToken: stoppingToken);

        if (serverContainer.Any())
        {
            var container = serverContainer.First();
            if (container is not null && container.State == "exited")
            {
                RandomizeScenario(containerName);
                await _dockerClient.Containers.StartContainerAsync(
                    container.ID,
                    new ContainerStartParameters(), stoppingToken);
            }
        }
        else
        {
            _logger.LogCritical("Container with name {ContainerName} not found", containerName);
        }
    }

    private void RandomizeScenario(string containerName)
    {
        // Extract server index from container name patterns like arma3-koth-reforged-3-1
        string serverIndex = GetServerIndex(containerName);

        string configFilePath = string.Format(SERVER_CONFIG_FILE_PATH_TEMPLATE, serverIndex);
        string configText = File.ReadAllText(configFilePath);
        var json = JObject.Parse(configText);

        var scenarioId = PickRandomScenario(containerName, serverIndex);
        json["game"]["scenarioId"] = scenarioId;
        _logger.LogInformation("Server {ServerName}: scenario selected {SelectedScenario}", containerName, scenarioId);

        File.WriteAllText(configFilePath, JsonConvert.SerializeObject(json, Formatting.Indented));
    }

    private string GetServerIndex(string containerName)
    {
        // Handle patterns like arma3-koth-reforged-3-1, koth3-koth-reforged-3-1
        if (containerName.Contains("reforged"))
        {
            var parts = containerName.Split('-');
            if (parts.Length >= 3)
            {
                // Get the number after "reforged-"
                return parts[parts.Length - 2];
            }
        }

        return "1"; // Default fallback
    }

    private string PickRandomScenario(string containerName, string serverIndex)
    {
        string scenariosFilePath = string.Format(LIST_SCENARIOS_FILE_PATH_TEMPLATE, serverIndex);
        var propertyScenarioList = JObject.Parse(File.ReadAllText(scenariosFilePath)).Property("scenarioList");

        if (propertyScenarioList is null)
        {
            throw new Exception($"list_scenarios.json for server {containerName} is missing scenarioList property");
        }

        var list = propertyScenarioList.ToList().Values<string>().ToList();
        if (list.Any())
        {
            return FindNextScenario(containerName, list);
        }

        throw new Exception($"Scenario list for server {containerName} is empty");
    }

    private string FindNextScenario(string containerName, List<string> allScenarios)
    {
        // Dynamically compute history size: total scenarios - 2 (minimum 1)
        int scenarioHistorySize = Math.Max(1, allScenarios.Count - 2);

        var serverHistory = _serverScenarioHistory[containerName];

        if (allScenarios.Count <= scenarioHistorySize)
        {
            _logger.LogWarning("Server {ServerName}: Total scenario count ({Count}) is less than or equal to computed history size ({HistorySize}). " +
                              "Some scenarios will repeat more frequently.", containerName, allScenarios.Count, scenarioHistorySize);
        }

        // Create a list of eligible scenarios (those not in recent history)
        var eligibleScenarios = allScenarios
            .Where(scenario => !serverHistory.Contains(scenario))
            .ToList();

        // If all scenarios are in history, use all scenarios except the most recently used one
        if (!eligibleScenarios.Any())
        {
            eligibleScenarios = allScenarios
                .Where(scenario => scenario != serverHistory.LastOrDefault())
                .ToList();

            _logger.LogInformation("Server {ServerName}: All scenarios have been used recently. Selecting from full list except last used.",
                containerName);
        }

        if (!eligibleScenarios.Any())
        {
            throw new Exception($"No eligible scenarios available to select for server {containerName}");
        }

        // Select random scenario from eligible options
        var random = new Random(System.DateTime.Now.Millisecond);
        var selectedScenario = eligibleScenarios[random.Next(eligibleScenarios.Count)];

        // Update history
        serverHistory.Add(selectedScenario);

        // Maintain history size
        while (serverHistory.Count > scenarioHistorySize)
        {
            serverHistory.RemoveAt(0);
        }

        _serverScenarioHistory[containerName] = serverHistory;

        _logger.LogInformation("Server {ServerName}: Selected scenario {SelectedScenario}. History: [{History}]",
            containerName, selectedScenario, string.Join(", ", serverHistory));

        return selectedScenario;
    }
}
