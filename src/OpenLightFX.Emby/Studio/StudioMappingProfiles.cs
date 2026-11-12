using System.Text.Json;
using OpenLightFX.Emby.Configuration;
using OpenLightFX.Emby.Models;

namespace OpenLightFX.Emby.Studio;

public static class StudioMappingProfiles
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Replace the channel map on the active profile. One channel may list several bulbs.
    /// </summary>
    public static string WithChannels(
        string? profilesJson,
        string? activeName,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping)
    {
        var service = new ConfigurationService();
        var name = string.IsNullOrWhiteSpace(activeName) ? "Default" : activeName.Trim();
        var profiles = service.ParseMappingProfiles(string.IsNullOrWhiteSpace(profilesJson) ? "[]" : profilesJson);
        var profile = profiles.FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (profile == null)
        {
            profile = new MappingProfile { Name = name };
            profiles.Insert(0, profile);
        }

        profile.Mappings = mapping
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair => new ChannelMapping
            {
                ChannelId = pair.Key,
                BulbIds = (pair.Value ?? Array.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .ToList()
            })
            .ToList();

        return JsonSerializer.Serialize(profiles, Json);
    }
}
