using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Poster URLs use Emby's own Primary image route, on the same path prefix the
/// request used, with the access token a plain image request can carry.
/// </summary>
public static class StudioPosters
{
    public static string PrimaryImageTag(IImageProcessor images, BaseItem item)
    {
        if (!item.HasImage(ImageType.Primary, 0))
            return "";
        return images.GetImageCacheTag(item, ImageType.Primary) ?? "";
    }

    public static string? PosterUrl(string? absoluteRequestUri, string itemId, string? tag, string? apiKey)
    {
        if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(tag))
            return null;

        var origin = StudioStreamUrls.FromAbsoluteUri(absoluteRequestUri);
        var prefix = string.IsNullOrEmpty(origin.PathBase) ? "/emby" : origin.PathBase;
        var path = $"{origin.Origin}{prefix}/Items/{Uri.EscapeDataString(itemId)}/Images/Primary";
        var query = new List<string>
        {
            "tag=" + Uri.EscapeDataString(tag),
            "maxHeight=240"
        };
        if (!string.IsNullOrEmpty(apiKey))
            query.Add("api_key=" + Uri.EscapeDataString(apiKey));
        return path + "?" + string.Join('&', query);
    }
}
