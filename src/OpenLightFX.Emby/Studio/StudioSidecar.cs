using System.Text.Json;

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Studio-only project document stored beside the track. Playback does not read it.
/// </summary>
public static class StudioSidecar
{
    public const int MaxBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> MarkerKinds = new(StringComparer.Ordinal)
    {
        "manual", "chapter", "detected"
    };

    private static readonly HashSet<string> GroupModes = new(StringComparer.Ordinal)
    {
        "mirror", "spread", "alternate"
    };

    public static string GetPath(string movieFilePath, string trackId)
    {
        var track = StudioTrackFiles.GetSavePath(movieFilePath, trackId);
        return Path.Combine(Path.GetDirectoryName(track)!, trackId + ".studio.json");
    }

    /// <summary>
    /// Accept version 1 JSON up to 4 MiB. Drop undo and duration so the file stays editor state.
    /// </summary>
    public static byte[] Normalize(byte[] body)
    {
        if (body.Length == 0)
            throw new ArgumentException("Project body is empty");
        if (body.Length > MaxBytes)
            throw new ArgumentException("Project body exceeds 4 MB");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Project body is not JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Project body must be a JSON object");

            if (!TryGetProperty(doc.RootElement, "version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != 1)
            {
                throw new ArgumentException("version is not 1");
            }

            ValidateCollections(doc.RootElement);

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("undo") || property.NameEquals("Undo")
                        || property.NameEquals("duration") || property.NameEquals("Duration")
                        || property.NameEquals("undoStack") || property.NameEquals("UndoStack"))
                        continue;
                    property.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }
    }

    private static void ValidateCollections(JsonElement root)
    {
        if (TryGetProperty(root, "sceneMarkers", out var markers) && markers.ValueKind == JsonValueKind.Array)
        {
            foreach (var marker in markers.EnumerateArray())
            {
                if (TryGetProperty(marker, "kind", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && !MarkerKinds.Contains(kind.GetString() ?? ""))
                {
                    throw new ArgumentException($"scene marker kind '{kind.GetString()}' is not manual, chapter, or detected");
                }
            }
        }

        if (TryGetProperty(root, "channelGroups", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (TryGetProperty(group, "mode", out var mode)
                    && mode.ValueKind == JsonValueKind.String
                    && !GroupModes.Contains(mode.GetString() ?? ""))
                {
                    throw new ArgumentException($"channel group mode '{mode.GetString()}' is not mirror, spread, or alternate");
                }
            }
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
