using System;
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

namespace ReforgerScenarioRotation;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly DockerClient _dockerClient;
    
    private List<string> _previousPicks { get; set; }
    private const string SERVER_CONFIG_FILE_PATH = "./configsFolder/config.json";
    private const string LIST_SCENARIOS_FILE_PATH = "./configsFolder/list_scenarios.json";

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _dockerClient = new DockerClientConfiguration().CreateClient();
        _previousPicks = new List<string>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

        while (!stoppingToken.IsCancellationRequested)
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
                            { "discordplayerlist-consumer-1", true}
                        }
                    }
                }
            };
            var serverContainer = await _dockerClient.Containers
                .ListContainersAsync(listParam, cancellationToken: stoppingToken);
            
            if (serverContainer.Any())
            {
                var container = serverContainer?.ToList().First();
                if (container is not null)
                {
                    if (container.State == "exited")
                    {
                        RandomizeScenario();
                        await _dockerClient.Containers.StartContainerAsync(
                            container.ID,
                            new ContainerStartParameters(), stoppingToken);
                    }
                }
            }
            else
            {
                _logger.LogCritical("container with name discordplayerlist-consumer-1 not found");
            }
            
            await Task.Delay(5000, stoppingToken);
        }
    }

    private void RandomizeScenario()
    {
        string configText = File.ReadAllText(SERVER_CONFIG_FILE_PATH);
        var json = JObject.Parse(configText);
        var scenar = PickRandomScenario();
        json["game"]["scenarioId"] = scenar;
        _logger.LogInformation("scenario valid {SelectedScenario}", scenar);

            
        var jsonString = JsonConvert.SerializeObject(json, Formatting.Indented);
        File.WriteAllText(SERVER_CONFIG_FILE_PATH, jsonString);
    }
    
    private string PickRandomScenario()
    {
        var property = JObject.Parse(File.ReadAllText(LIST_SCENARIOS_FILE_PATH))
            .Property("scenarioList");

        if (property is null)
        {
            throw new Exception("list_scenarios.json is missing scenarioList key");
        }
            
        var list = property.ToList().Values<string>().ToList();
        if (list.Any())
        {
            return FindNextScenario(list);
        }
        
        throw new Exception("list is null");

    }

    private string FindNextScenario(List<string?> list)
    {
        var scenarioCount = list.Count;
        var i = 0;

        while (true)
        {
            i++;
            if (i > 100)
            {
                throw new Exception("could not randomize scenario, exiting while loop");
            }
            var randomNumber = new Random().Next(0, scenarioCount - 1);
            string selectedScenario = list[randomNumber];
            if (selectedScenario is null)
            {
                throw new Exception("selectedScenario is null");
            }

            if (false == _previousPicks.Any())
            {
                _previousPicks.Add(selectedScenario);

                return selectedScenario;
            }
            
            var latestPick = _previousPicks.Last(); 
            if (latestPick != selectedScenario)
            {
                _previousPicks.Add(selectedScenario);

                if (_previousPicks.Count > 3)
                {
                    _previousPicks.Remove(_previousPicks.First());
                }
                
                return selectedScenario;
            }
            
            _logger.LogInformation("scenario not valid {SelectedScenario}", selectedScenario);
        }
    }
}
