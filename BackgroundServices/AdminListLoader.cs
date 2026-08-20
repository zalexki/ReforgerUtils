using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace ReforgerScenarioRotation.BackgroundServices;

public sealed class AdminEntry
{
    public AdminEntry(string name, string id)
    {
        Name = name;
        Id = id;
    }

    public string Name { get; }
    public string Id { get; }
}

public class AdminListLoader
{
    public const int MaxAdmins = 20;
    public const string DefaultFilePath = "/admins.json";

    private readonly ILogger<AdminListLoader> _logger;
    private readonly string _path;

    public AdminListLoader(ILogger<AdminListLoader> logger)
    {
        _logger = logger;
        _path = Environment.GetEnvironmentVariable("ADMIN_LIST_FILE_PATH") ?? DefaultFilePath;
    }

    public bool TryLoad(out IReadOnlyList<AdminEntry> entries)
    {
        entries = Array.Empty<AdminEntry>();

        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogWarning("Admin list file not found at {Path}; skipping sync", _path);
                return false;
            }

            var json = JObject.Parse(File.ReadAllText(_path));
            var listToken = json["list"];
            if (listToken is null)
            {
                _logger.LogError("Admin list file {Path} is missing list property; skipping sync", _path);
                return false;
            }

            var parsed = ParseList(listToken);
            entries = CapAtMax(parsed);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read admin list from {Path}; skipping sync", _path);
            return false;
        }
    }

    private List<AdminEntry> ParseList(JToken listToken)
    {
        var result = new List<AdminEntry>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            id = id.Trim();
            name = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
            if (!seenIds.Add(id))
            {
                return;
            }

            result.Add(new AdminEntry(name, id));
        }

        if (listToken is JObject map)
        {
            foreach (var prop in map.Properties())
            {
                Add(prop.Name, prop.Value?.Value<string>());
            }

            return result;
        }

        if (listToken is JArray array)
        {
            foreach (var item in array)
            {
                if (item is not JObject itemObj)
                {
                    continue;
                }

                var name = itemObj.Value<string>("adminName");
                var id = itemObj.Value<string>("adminBIUID");
                if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(id))
                {
                    Add(name, id);
                    continue;
                }

                foreach (var prop in itemObj.Properties())
                {
                    Add(prop.Name, prop.Value?.Value<string>());
                }
            }

            return result;
        }

        throw new InvalidDataException("list must be an object map or an array of objects");
    }

    private IReadOnlyList<AdminEntry> CapAtMax(List<AdminEntry> parsed)
    {
        if (parsed.Count <= MaxAdmins)
        {
            return parsed;
        }

        var dropped = parsed.Skip(MaxAdmins).Select(e => e.Name);
        _logger.LogWarning(
            "Admin list has {Count} unique IDs; Arma allows {Max}. Dropping: {Dropped}",
            parsed.Count,
            MaxAdmins,
            string.Join(", ", dropped));

        return parsed.Take(MaxAdmins).ToList();
    }

    public static string FormatEntries(IReadOnlyList<AdminEntry> entries)
    {
        return string.Join(", ", entries.Select(e => $"{e.Name}={e.Id}"));
    }
}
