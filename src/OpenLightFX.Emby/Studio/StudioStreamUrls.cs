using MediaBrowser.Model.Services;

namespace OpenLightFX.Emby.Studio;

public readonly record struct StudioRequestOrigin(string Origin, string PathBase);

public readonly record struct StudioMediaInfo(
    string ItemId,
    string? Container,
    string? VideoCodec,
    string? AudioCodec,
    string? MediaSourceId);

/// <summary>
/// Builds a URL a browser &lt;video&gt; element can play through Emby's own video endpoint.
/// Direct-plays containers browsers handle, otherwise asks Emby to transcode to H.264/AAC MP4.
/// </summary>
public static class StudioStreamUrls
{
    public static StudioRequestOrigin FromAbsoluteUri(string? absoluteUri)
    {
        if (string.IsNullOrWhiteSpace(absoluteUri) || !Uri.TryCreate(absoluteUri, UriKind.Absolute, out var uri))
            return new StudioRequestOrigin("", "");

        var origin = uri.GetLeftPart(UriPartial.Authority);
        var path = uri.AbsolutePath;
        var marker = path.IndexOf("/OpenLightFX/", StringComparison.OrdinalIgnoreCase);
        var pathBase = marker > 0 ? path[..marker].TrimEnd('/') : "";
        return new StudioRequestOrigin(origin, pathBase);
    }

    public static string? ReadAccessToken(IRequest? request)
    {
        if (request == null)
            return null;

        var header = request.Headers?["X-Emby-Token"];
        if (!string.IsNullOrEmpty(header))
            return header;

        var query = request.QueryString?["api_key"];
        if (!string.IsNullOrEmpty(query))
            return query;

        var authorization = request.Headers?["X-Emby-Authorization"];
        if (string.IsNullOrEmpty(authorization))
            authorization = request.Headers?["Authorization"];
        return TokenFromAuthorization(authorization);
    }

    public static string? TokenFromAuthorization(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization))
            return null;

        const string marker = "Token=\"";
        var start = authorization.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;
        start += marker.Length;
        var end = authorization.IndexOf('"', start);
        if (end <= start)
            return null;
        return authorization[start..end];
    }

    public static string Build(StudioRequestOrigin origin, StudioMediaInfo media, string? accessToken)
    {
        var playback = ChoosePlayback(media);
        var prefix = string.IsNullOrEmpty(origin.PathBase) ? "" : origin.PathBase;
        var path = $"{prefix}/Videos/{Uri.EscapeDataString(media.ItemId)}/stream.{playback.Container}";
        var query = new List<string>();

        if (!string.IsNullOrEmpty(media.MediaSourceId))
            query.Add("MediaSourceId=" + Uri.EscapeDataString(media.MediaSourceId));

        if (playback.Static)
        {
            query.Add("Static=true");
        }
        else
        {
            query.Add("VideoCodec=h264");
            query.Add("AudioCodec=aac");
            query.Add("MaxAudioChannels=2");
            query.Add("DeviceId=openlightfx-studio");
        }

        if (!string.IsNullOrEmpty(accessToken))
            query.Add("api_key=" + Uri.EscapeDataString(accessToken));

        return origin.Origin + path + "?" + string.Join('&', query);
    }

    public static (string Container, bool Static) ChoosePlayback(StudioMediaInfo media)
    {
        var container = (media.Container ?? "").Trim().ToLowerInvariant();
        var video = (media.VideoCodec ?? "").Trim().ToLowerInvariant();
        var audio = (media.AudioCodec ?? "").Trim().ToLowerInvariant();

        if (IsMp4(container) && IsBrowserH264(video) && IsBrowserAac(audio))
            return ("mp4", true);

        if (container == "webm" && IsBrowserWebmVideo(video) && IsBrowserWebmAudio(audio))
            return ("webm", true);

        return ("mp4", false);
    }

    private static bool IsMp4(string container) => container is "mp4" or "m4v";

    private static bool IsBrowserH264(string codec) =>
        codec is "h264" or "avc" or "avc1";

    private static bool IsBrowserAac(string codec) =>
        codec is "aac" or "mp3";

    private static bool IsBrowserWebmVideo(string codec) =>
        codec is "vp8" or "vp9" or "av1";

    private static bool IsBrowserWebmAudio(string codec) =>
        codec is "opus" or "vorbis";
}
