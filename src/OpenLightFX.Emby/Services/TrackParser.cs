namespace OpenLightFX.Emby.Services;

using Google.Protobuf;
using Openlightfx;
using OpenLightFX.Emby.Models;
using System.Text.RegularExpressions;

public class TrackParser
{
    private const uint SupportedVersion = 1;
    private static readonly Regex ImdbPattern = new(@"^tt\d{7,}$", RegexOptions.Compiled);

    public TrackInfo Parse(string filePath)
    {
        var errors = new List<string>();
        LightFXTrack? track = null;

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            track = LightFXTrack.Parser.ParseFrom(bytes);
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse protobuf: {ex.Message}");
            return new TrackInfo
            {
                FilePath = filePath,
                FileName = Path.GetFileName(filePath),
                Track = new LightFXTrack(),
                IsValid = false,
                ValidationErrors = errors
            };
        }

        Validate(track, errors);

        return new TrackInfo
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            Track = track,
            IsValid = errors.Count == 0,
            ValidationErrors = errors
        };
    }

    private void Validate(LightFXTrack track, List<string> errors)
    {
        // V-001: Version must be supported
        if (track.Version == 0 || track.Version > SupportedVersion)
            errors.Add($"V-001: Unsupported version {track.Version} (supported: {SupportedVersion})");

        // V-002: Metadata must be present
        if (track.Metadata == null)
        {
            errors.Add("V-002: Metadata is missing");
            return;
        }

        // V-003: IMDB ID format
        var imdbId = track.Metadata.MovieReference?.ImdbId;
        if (!string.IsNullOrEmpty(imdbId) && !ImdbPattern.IsMatch(imdbId))
            errors.Add($"V-003: Invalid IMDB ID format: {imdbId}");

        // V-004: Duration must be > 0
        if (track.Metadata.DurationMs == 0)
            errors.Add("V-004: Duration must be > 0");

        // V-011: At least one channel must be present
        if (track.Channels.Count == 0)
            errors.Add("V-011: At least one channel is required");

        // V-005: Channel IDs must be unique
        var channelIds = new HashSet<string>();
        foreach (var ch in track.Channels)
        {
            if (!channelIds.Add(ch.Id))
                errors.Add($"V-005: Duplicate channel ID: {ch.Id}");
        }

        // Validate keyframes
        var lastTimestampPerChannel = new Dictionary<string, ulong>();
        foreach (var kf in track.Keyframes)
        {
            // V-006: channel_id must reference a valid channel
            if (!channelIds.Contains(kf.ChannelId))
                errors.Add($"V-006: Keyframe {kf.Id} references invalid channel: {kf.ChannelId}");

            // V-006: Keyframes within each channel must be sorted ascending by timestamp
            if (lastTimestampPerChannel.TryGetValue(kf.ChannelId, out var lastTs) && kf.TimestampMs < lastTs)
                errors.Add($"V-006: Keyframe {kf.Id} in channel {kf.ChannelId} is not sorted (timestamp {kf.TimestampMs} < {lastTs})");
            lastTimestampPerChannel[kf.ChannelId] = kf.TimestampMs;

            // V-007: Brightness must be 0-100
            if (kf.Brightness > 100)
                errors.Add($"V-007: Keyframe {kf.Id} brightness {kf.Brightness} out of range [0, 100]");

            // V-008: RGB values must be 0-255
            if (kf.Color != null)
            {
                if (kf.Color.R > 255 || kf.Color.G > 255 || kf.Color.B > 255)
                    errors.Add($"V-008: Keyframe {kf.Id} has RGB value out of range [0, 255]");
            }

            // V-009: Color temperature 1000-10000
            if (kf.ColorMode == ColorMode.ColorTemperature && (kf.ColorTemperature < 1000 || kf.ColorTemperature > 10000))
                errors.Add($"V-009: Keyframe {kf.Id} color temperature {kf.ColorTemperature} out of range [1000, 10000]");

            // V-010: timestamp <= duration
            if (track.Metadata.DurationMs > 0 && kf.TimestampMs > track.Metadata.DurationMs)
                errors.Add($"V-010: Keyframe {kf.Id} timestamp {kf.TimestampMs} exceeds track duration {track.Metadata.DurationMs}");

            // V-012: transition must not begin before time 0
            if (kf.TransitionMs > kf.TimestampMs)
                errors.Add($"V-012: Keyframe {kf.Id} transition would begin before time 0");
        }

        // Validate effect keyframes
        foreach (var ek in track.EffectKeyframes)
        {
            if (!channelIds.Contains(ek.ChannelId))
                errors.Add($"V-006: Effect keyframe {ek.Id} references invalid channel: {ek.ChannelId}");

            // V-013: duration > 0
            if (ek.DurationMs == 0)
                errors.Add($"V-013: Effect keyframe {ek.Id} duration must be > 0");

            // V-014: intensity 0-100
            if (ek.Intensity > 100)
                errors.Add($"V-014: Effect keyframe {ek.Id} intensity {ek.Intensity} out of range [0, 100]");
        }
    }
}
