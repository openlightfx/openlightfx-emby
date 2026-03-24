namespace OpenLightFX.Emby.Configuration;

using OpenLightFX.Emby.Models;
using System.Text.Json;

public class ConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public List<BulbConfig> ParseBulbConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return new List<BulbConfig>();

        try { return JsonSerializer.Deserialize<List<BulbConfig>>(json, JsonOptions) ?? new(); }
        catch { return new(); }
    }

    public List<MappingProfile> ParseMappingProfiles(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return new List<MappingProfile>();

        try { return JsonSerializer.Deserialize<List<MappingProfile>>(json, JsonOptions) ?? new(); }
        catch { return new(); }
    }

    public string? ValidateBulbConfigJson(string json)
    {
        try
        {
            var configs = JsonSerializer.Deserialize<List<BulbConfig>>(json, JsonOptions);
            if (configs == null) return "Invalid JSON: could not parse as array";
            foreach (var c in configs)
            {
                if (string.IsNullOrEmpty(c.Id)) return "Each bulb must have an 'id'";
                if (string.IsNullOrEmpty(c.Protocol)) return $"Bulb '{c.Id}' is missing 'protocol'";
            }
            return null; // valid
        }
        catch (JsonException ex) { return $"Invalid JSON: {ex.Message}"; }
    }

    public string? ValidateMappingProfilesJson(string json)
    {
        try
        {
            var profiles = JsonSerializer.Deserialize<List<MappingProfile>>(json, JsonOptions);
            if (profiles == null) return "Invalid JSON: could not parse as array";
            foreach (var p in profiles)
            {
                if (string.IsNullOrEmpty(p.Name)) return "Each profile must have a 'name'";
            }
            return null; // valid
        }
        catch (JsonException ex) { return $"Invalid JSON: {ex.Message}"; }
    }

    public Dictionary<string, string> ParseDeviceProfileOverrides(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
            return new Dictionary<string, string>();

        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions) ?? new(); }
        catch { return new(); }
    }

    public IEnumerable<string> GetScanPaths(string scanPathsText)
    {
        if (string.IsNullOrWhiteSpace(scanPathsText))
            return Enumerable.Empty<string>();
        return scanPathsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p));
    }
}
