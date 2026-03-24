using Openlightfx;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Utilities;

namespace OpenLightFX.Emby.Engine;

/// <summary>
/// Manages per-channel lighting state, channel groups, bulb mapping, and brightness cap.
/// </summary>
public class ChannelManager
{
    private readonly LightFXTrack _track;
    private readonly MappingProfile _profile;
    private readonly uint _brightnessCap; // 0-100

    // Current state per channel
    private readonly Dictionary<string, InterpolationEngine.InterpolatedState> _channelStates = new();

    // Channel definitions indexed by ID
    private readonly Dictionary<string, Channel> _channels = new();

    // Reverse map: bulbId → list of channelIds (first listed in mapping profile wins per EMB-032)
    private readonly Dictionary<string, List<string>> _bulbToChannels = new();

    public ChannelManager(LightFXTrack track, MappingProfile profile, uint brightnessCap)
    {
        _track = track;
        _profile = profile;
        _brightnessCap = Math.Clamp(brightnessCap, 0u, 100u);

        foreach (var ch in track.Channels)
            _channels[ch.Id] = ch;

        // Build reverse bulb-to-channel map; first channel listed in the profile wins (EMB-032)
        foreach (var mapping in profile.Mappings)
        {
            var channelId = mapping.ChannelId;
            foreach (var bulbId in mapping.BulbIds)
            {
                if (!_bulbToChannels.ContainsKey(bulbId))
                    _bulbToChannels[bulbId] = new();
                _bulbToChannels[bulbId].Add(channelId);
            }
        }

        InitializeDefaults();
    }

    private void InitializeDefaults()
    {
        foreach (var ch in _track.Channels)
        {
            var r = ch.DefaultColor?.R ?? 0;
            var g = ch.DefaultColor?.G ?? 0;
            var b = ch.DefaultColor?.B ?? 0;
            _channelStates[ch.Id] = new InterpolationEngine.InterpolatedState(
                (byte)r, (byte)g, (byte)b,
                ch.DefaultBrightness, 0,
                ColorMode.Rgb, false);
        }
    }

    /// <summary>
    /// Update a channel's current state.
    /// </summary>
    public void SetChannelState(string channelId, InterpolationEngine.InterpolatedState state)
    {
        _channelStates[channelId] = state;
    }

    /// <summary>
    /// Get the current state for a channel.
    /// </summary>
    public InterpolationEngine.InterpolatedState? GetChannelState(string channelId)
    {
        return _channelStates.TryGetValue(channelId, out var state) ? state : null;
    }

    /// <summary>
    /// Get the effective bulb command for a specific bulb, resolving priority and applying brightness cap.
    /// Returns null if the bulb has no mapped channels.
    /// </summary>
    public BulbCommand? GetBulbCommand(string bulbId)
    {
        if (!_bulbToChannels.TryGetValue(bulbId, out var channels) || channels.Count == 0)
            return null;

        // First channel listed in the mapping profile wins (EMB-032)
        var channelId = channels[0];
        if (!_channelStates.TryGetValue(channelId, out var state))
            return null;

        var cappedBrightness = (uint)(state.Brightness * _brightnessCap / 100);

        return new BulbCommand(state.R, state.G, state.B, cappedBrightness,
            state.ColorTemperature, state.PowerOn);
    }

    /// <summary>
    /// Get all bulb IDs that have at least one mapped channel.
    /// </summary>
    public IEnumerable<string> GetMappedBulbIds() => _bulbToChannels.Keys;

    /// <summary>
    /// Get all channel IDs in the track.
    /// </summary>
    public IEnumerable<string> GetChannelIds() => _channels.Keys;

    /// <summary>
    /// Auto-mapping suggestions based on spatial hints (EMB-036).
    /// </summary>
    public static Dictionary<string, string> SuggestMapping(
        IEnumerable<Channel> channels, IEnumerable<BulbConfig> bulbs)
    {
        var suggestions = new Dictionary<string, string>();
        var bulbList = bulbs.ToList();

        foreach (var ch in channels)
        {
            if (string.IsNullOrEmpty(ch.SpatialHint)) continue;
            var match = bulbList.FirstOrDefault(b =>
                string.Equals(b.SpatialPosition, ch.SpatialHint, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                suggestions[ch.Id] = match.Id;
        }

        return suggestions;
    }
}

/// <summary>
/// Command to send to a specific bulb after priority resolution and brightness cap.
/// </summary>
public record BulbCommand(byte R, byte G, byte B, uint Brightness, uint ColorTemperature, bool PowerOn);
