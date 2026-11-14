using Openlightfx;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Utilities;
using OpenLightFX.Emby.Rooms;

namespace OpenLightFX.Emby.Engine;

/// <summary>
/// Manages per-channel lighting state, channel groups, bulb mapping, and brightness cap.
/// </summary>
public class ChannelManager
{
    private readonly LightFXTrack _track;
    private readonly uint _brightnessCap; // 0-100

    // Current state per channel
    private readonly Dictionary<string, InterpolationEngine.InterpolatedState> _channelStates = new();

    // Channel definitions indexed by ID
    private readonly Dictionary<string, Channel> _channels = new();

    // Reverse map: bulbId → every routed channel source, in track order.
    private readonly Dictionary<string, List<BulbChannelSource>> _bulbToChannels = new();
    private readonly Dictionary<string, InterpolationEngine.InterpolatedState> _effectStates = new();

    public ChannelManager(LightFXTrack track, PlaybackMapping mapping, uint brightnessCap)
    {
        _track = track;
        _brightnessCap = Math.Clamp(brightnessCap, 0u, 100u);

        foreach (var ch in track.Channels)
            _channels[ch.Id] = ch;

        // Build the reverse source map; mapping IDs may be legacy display names.
        if (mapping.BulbSources.Count > 0)
        {
            foreach (var bulb in mapping.BulbSources)
                _bulbToChannels[bulb.BulbId] = bulb.Sources.Select(source => new BulbChannelSource
                {
                    ChannelId = ResolveChannelId(source.ChannelId),
                    Kind = source.Kind
                }).ToList();
        }
        else
        {
            foreach (var channelMapping in mapping.Mappings)
            {
                var resolvedId = ResolveChannelId(channelMapping.ChannelId);
                foreach (var bulbId in channelMapping.BulbIds)
                {
                    if (!_bulbToChannels.ContainsKey(bulbId)) _bulbToChannels[bulbId] = new();
                    _bulbToChannels[bulbId].Add(new BulbChannelSource { ChannelId = resolvedId, Kind = "native" });
                }
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

    public void ClearEffectStates() => _effectStates.Clear();

    public void SetEffectChannelState(string channelId, InterpolationEngine.InterpolatedState state) =>
        _effectStates[channelId] = state;

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

        var nativeEffects = channels
            .Where(source => source.Kind == "native" && _effectStates.ContainsKey(source.ChannelId))
            .Select(source => _effectStates[source.ChannelId])
            .ToArray();
        var states = nativeEffects.Length > 0
            ? nativeEffects
            : channels
                .Select(source => _effectStates.TryGetValue(source.ChannelId, out var effectState)
                    ? effectState
                    : _channelStates.TryGetValue(source.ChannelId, out var channelState) ? channelState : (InterpolationEngine.InterpolatedState?)null)
                .Where(state => state.HasValue)
                .Select(state => state!.Value)
                .ToArray();
        if (states.Length == 0) return null;
        var state = BulbMixer.Mix(states);
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
    /// Resolve a channel mapping id to a track channel id.
    /// Matches by channel id first, then falls back to display_name (case-insensitive).
    /// Returns the original value unchanged if no match is found.
    /// </summary>
    private string ResolveChannelId(string mappedChannelId)
    {
        // Exact id match
        if (_channels.ContainsKey(mappedChannelId))
            return mappedChannelId;

        // Display name match (case-insensitive)
        var byName = _channels.Values.FirstOrDefault(ch =>
            string.Equals(ch.DisplayName, mappedChannelId, StringComparison.OrdinalIgnoreCase));
        if (byName != null)
            return byName.Id;

        return mappedChannelId; // unresolved — will produce no state match at dispatch time
    }

}

/// <summary>
/// Command to send to a specific bulb after priority resolution and brightness cap.
/// </summary>
public record BulbCommand(byte R, byte G, byte B, uint Brightness, uint ColorTemperature, bool PowerOn);
