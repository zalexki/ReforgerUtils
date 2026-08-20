using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReforgerScenarioRotation.BackgroundServices;

public class ServerConfigStore
{
    public const string ConfigFilePathTemplate = "/server{0}/config.json";

    private readonly object _gate = new();

    public static IReadOnlyList<string> GetServerContainerNames()
    {
        string serverNamesEnv = Environment.GetEnvironmentVariable("SERVER_CONTAINER_NAMES")
                                ?? "arma-server-1,arma-server-2,arma-server-3";
        return serverNamesEnv.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    public static string GetServerIndex(string containerName)
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

        return "1";
    }

    public static string GetConfigFilePath(string containerName)
    {
        return string.Format(ConfigFilePathTemplate, GetServerIndex(containerName));
    }

    public void Update(string configFilePath, Action<JObject> mutate)
    {
        lock (_gate)
        {
            var json = JObject.Parse(File.ReadAllText(configFilePath));
            mutate(json);
            Write(configFilePath, json);
        }
    }

    public bool TryReplaceAdmins(string configFilePath, IReadOnlyList<string> adminIds)
    {
        lock (_gate)
        {
            var json = JObject.Parse(File.ReadAllText(configFilePath));
            var game = json["game"] as JObject;
            if (game is null)
            {
                throw new InvalidOperationException($"Config {configFilePath} is missing game object");
            }

            var currentIds = ReadAdminIds(game["admins"]);
            if (new HashSet<string>(currentIds, StringComparer.Ordinal).SetEquals(adminIds))
            {
                return false;
            }

            game["admins"] = new JArray(adminIds);
            Write(configFilePath, json);
            return true;
        }
    }

    private static List<string> ReadAdminIds(JToken adminsToken)
    {
        if (adminsToken is not JArray array)
        {
            return new List<string>();
        }

        return array.Values<string>().Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
    }

    private static void Write(string configFilePath, JObject json)
    {
        File.WriteAllText(configFilePath, JsonConvert.SerializeObject(json, Formatting.Indented));
    }
}
