using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Poster URLs use Emby's Primary image route. That route answers without an
/// access token, so the URL does not carry one.
/// </summary>
public static class StudioPosters
{
    public static string PrimaryImageTag(IImageProcessor images, BaseItem item)
    {
        if (!item.HasImage(ImageType.Primary, 0))
            return "";
        return images.GetImageCacheTag(item, ImageType.Primary) ?? "";
    }

    public static string? PosterUrl(string? absoluteRequestUri, string itemId, string? tag)
    {
        if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(tag))
            return null;

        var origin = StudioStreamUrls.FromAbsoluteUri(absoluteRequestUri);
        var prefix = string.IsNullOrEmpty(origin.PathBase) ? "/emby" : origin.PathBase;
        var path = $"{origin.Origin}{prefix}/Items/{Uri.EscapeDataString(itemId)}/Images/Primary";
        return path + "?tag=" + Uri.EscapeDataString(tag) + "&maxHeight=240";
    }
}
