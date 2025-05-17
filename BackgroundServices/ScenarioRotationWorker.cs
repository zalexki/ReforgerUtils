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
    
    // Configure how many previous scenarios to avoid repeating
    private const int SCENARIO_HISTORY_SIZE = 4;

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
        // Extract server index from container name (assuming format like arma-server-1)
        string serverIndex = containerName.Split('-').LastOrDefault() ?? "1";
        
        string configFilePath = string.Format(SERVER_CONFIG_FILE_PATH_TEMPLATE, serverIndex);
        string configText = File.ReadAllText(configFilePath);
        var json = JObject.Parse(configText);

        var scenarioId = PickRandomScenario(containerName, serverIndex);
        json["game"]["scenarioId"] = scenarioId;
        _logger.LogInformation("Server {ServerName}: scenario selected {SelectedScenario}", containerName, scenarioId);

        File.WriteAllText(configFilePath, JsonConvert.SerializeObject(json, Formatting.Indented));
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
        var serverHistory = _serverScenarioHistory[containerName];
        
        if (allScenarios.Count <= SCENARIO_HISTORY_SIZE)
        {
            _logger.LogWarning("Server {ServerName}: Total scenario count ({Count}) is less than or equal to history size ({HistorySize}). " +
                              "Some scenarios will repeat more frequently.", containerName, allScenarios.Count, SCENARIO_HISTORY_SIZE);
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
        var random = new Random();
        var selectedScenario = eligibleScenarios[random.Next(eligibleScenarios.Count)];
        
        // Update history
        serverHistory.Add(selectedScenario);
        
        // Maintain history size
        while (serverHistory.Count > SCENARIO_HISTORY_SIZE)
        {
            serverHistory.RemoveAt(0);
        }

        // Update the dictionary (not strictly necessary with Lists, but good practice for clarity)
        _serverScenarioHistory[containerName] = serverHistory;

        _logger.LogInformation("Server {ServerName}: Selected scenario {SelectedScenario}. History: [{History}]", 
            containerName, selectedScenario, string.Join(", ", serverHistory));

        return selectedScenario;
    }
}