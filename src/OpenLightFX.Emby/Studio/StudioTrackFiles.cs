using System.Security.Cryptography;
using System.Text;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Services;

namespace OpenLightFX.Emby.Studio;

public sealed record StudioListedTrack(
    string TrackId,
    string Title,
    string ItemId,
    DateTimeOffset UpdatedAt,
    string FilePath);

public static class StudioTrackFiles
{
    public const int MaxTrackBytes = 32 * 1024 * 1024;

    public static bool IsSafeTrackId(string? trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId) || trackId.Length > 120)
            return false;
        if (trackId is "." or "..")
            return false;
        if (trackId.Contains("..", StringComparison.Ordinal))
            return false;
        if (trackId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        return true;
    }

    /// <summary>
    /// Destination discovery already scans: {movieDir}/lightfx/{trackId}.lightfx.
    /// </summary>
    public static string GetSavePath(string movieFilePath, string trackId)
    {
        if (!IsSafeTrackId(trackId))
            throw new ArgumentException("trackId contains unsupported characters");

        var movieDir = Path.GetDirectoryName(Path.GetFullPath(movieFilePath));
        if (string.IsNullOrEmpty(movieDir))
            throw new ArgumentException("Movie file path is not available");

        var folder = Path.Combine(movieDir, "lightfx");
        var path = Path.GetFullPath(Path.Combine(folder, trackId + ".lightfx"));
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("trackId escapes the movie lightfx folder");
        return path;
    }

    public static string? FindFile(string movieFilePath, string trackId)
    {
        if (!IsSafeTrackId(trackId))
            return null;

        var movieDir = Path.GetDirectoryName(movieFilePath);
        if (string.IsNullOrEmpty(movieDir))
            return null;

        var candidates = new[]
        {
            Path.Combine(movieDir, "lightfx", trackId + ".lightfx"),
            Path.Combine(movieDir, trackId + ".lightfx")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    public static TrackInfo ParseForSave(TrackParser parser, byte[] bytes, string trackId)
    {
        if (!IsSafeTrackId(trackId))
            throw new ArgumentException("trackId contains unsupported characters");
        if (bytes.Length == 0)
            throw new ArgumentException("V-001: Track body is empty");
        if (bytes.Length > MaxTrackBytes)
            throw new ArgumentException("V-001: Track body exceeds 32 MB");

        var info = parser.ParseBytes(bytes, trackId + ".lightfx");
        if (!info.IsValid)
            throw new ArgumentException(string.Join("; ", info.ValidationErrors));
        return info;
    }

    /// <summary>
    /// Replacing an existing track requires <paramref name="ifMatch"/> to equal the
    /// updatedAt token the track list returns. A missing or different token is a conflict.
    /// Creating a file that does not exist yet is not a conflict.
    /// </summary>
    public static void EnsureNoConflict(string path, string? ifMatch)
    {
        if (!File.Exists(path))
            return;

        var token = UpdatedAtToken(path);
        var presented = ifMatch?.Trim().Trim('"');
        if (string.Equals(presented, token, StringComparison.Ordinal))
            return;

        throw new TrackSaveConflictException(
            $"conflict: the server track was updated at {token}. Send If-Match with that updatedAt.");
    }

    public static string UpdatedAtToken(string path)
    {
        var updated = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        return updated.UtcDateTime.ToString("o");
    }

    /// <summary>
    /// Assign stable track ids from discovered files. The lightfx/ copy wins when filenames collide.
    /// </summary>
    public static IReadOnlyList<StudioListedTrack> ToListItems(string itemId, IEnumerable<TrackInfo> tracks)
    {
        var ordered = tracks
            .Where(track => track.IsValid && !string.IsNullOrEmpty(track.FilePath))
            .OrderBy(track => Preference(track.FilePath))
            .ThenBy(track => track.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<StudioListedTrack>(ordered.Count);
        foreach (var track in ordered)
        {
            var stem = Path.GetFileNameWithoutExtension(track.FileName);
            if (string.IsNullOrWhiteSpace(stem))
                stem = "track";

            var id = stem;
            if (!used.Add(id))
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(track.FilePath))))[..8]
                    .ToLowerInvariant();
                id = stem + "~" + hash;
                used.Add(id);
            }

            var updated = DateTimeOffset.UtcNow;
            try
            {
                updated = new DateTimeOffset(File.GetLastWriteTimeUtc(track.FilePath), TimeSpan.Zero);
            }
            catch (IOException)
            {
                // Keep the fallback timestamp when the file disappears mid-list.
            }

            result.Add(new StudioListedTrack(id, track.Title, itemId, updated, Path.GetFullPath(track.FilePath)));
        }

        return result;
    }

    private static int Preference(string path)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return dir.Equals("lightfx", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
