namespace OpenLightFX.Emby.Api;

using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using System.Text;

public class LightTracksPage : IPluginConfigurationPage
{
    public string Name => "OpenLightFX - Light Tracks";

    public ConfigurationPageType ConfigurationPageType => ConfigurationPageType.None;

    public IPlugin Plugin => OpenLightFX.Emby.Plugin.Instance!;

    public Stream GetHtmlStream()
    {
        // Server-side rendered HTML — no JavaScript (Emby strips it)
        var html = BuildHtml();
        return new MemoryStream(Encoding.UTF8.GetBytes(html));
    }

    private string BuildHtml()
    {
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head>");
        sb.AppendLine("<title>OpenLightFX - Light Tracks</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; margin: 20px; color: #ddd; background: transparent; }");
        sb.AppendLine("h1 { color: #52b54b; font-size: 24px; }");
        sb.AppendLine("h2 { color: #aaa; font-size: 18px; margin-top: 24px; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin: 12px 0; }");
        sb.AppendLine("th { text-align: left; padding: 8px 12px; background: rgba(255,255,255,0.05); color: #aaa; font-weight: 500; }");
        sb.AppendLine("td { padding: 8px 12px; border-bottom: 1px solid rgba(255,255,255,0.05); }");
        sb.AppendLine(".status-active { color: #52b54b; }");
        sb.AppendLine(".status-inactive { color: #888; }");
        sb.AppendLine(".badge { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 12px; }");
        sb.AppendLine(".badge-selected { background: rgba(82,181,75,0.2); color: #52b54b; }");
        sb.AppendLine(".badge-available { background: rgba(255,255,255,0.1); color: #aaa; }");
        sb.AppendLine(".info { background: rgba(82,181,75,0.1); padding: 12px 16px; border-radius: 6px; margin: 12px 0; }");
        sb.AppendLine(".note { color: #888; font-size: 13px; margin-top: 16px; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head><body>");

        sb.AppendLine("<h1>\U0001f4a1 OpenLightFX \u2014 Light Tracks</h1>");

        // Plugin status
        sb.AppendLine("<div class=\"info\">");
        sb.AppendLine("<strong>Plugin Status:</strong> <span class=\"status-active\">Active</span>");

        var options = OpenLightFX.Emby.Plugin.Instance?.GetPluginOptions();
        if (options != null)
        {
            var configService = new Configuration.ConfigurationService();
            var bulbCount = configService.ParseBulbConfig(options.BulbConfigJson).Count;
            var profileCount = configService.ParseMappingProfiles(options.MappingProfilesJson).Count;
            sb.AppendLine($" &nbsp;|&nbsp; <strong>Configured Bulbs:</strong> {bulbCount}");
            sb.AppendLine($" &nbsp;|&nbsp; <strong>Mapping Profiles:</strong> {profileCount}");
            sb.AppendLine($" &nbsp;|&nbsp; <strong>Active Profile:</strong> {options.ActiveProfileName}");
            sb.AppendLine($" &nbsp;|&nbsp; <strong>Photosensitivity:</strong> {(options.PhotosensitivityMode ? "ON" : "OFF")}");
        }
        sb.AppendLine("</div>");

        // Instructions
        sb.AppendLine("<h2>Track Management</h2>");
        sb.AppendLine("<p>Light tracks are discovered automatically from your movie library. ");
        sb.AppendLine("Place <code>.lightfx</code> files next to your movie files (sidecar), ");
        sb.AppendLine("in a <code>lightfx/</code> subfolder, or configure additional scan paths in plugin settings.</p>");

        sb.AppendLine("<p class=\"note\">Track selection and bulb configuration is managed via the ");
        sb.AppendLine("<a href=\"https://openlightfx.com/config\" style=\"color: #52b54b;\">OpenLightFX Setup Wizard</a> ");
        sb.AppendLine("or the REST API at <code>/OpenLightFX/Tracks/ByItem?itemId=</code>.</p>");

        // REST API reference
        sb.AppendLine("<h2>REST API</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Endpoint</th><th>Method</th><th>Description</th></tr>");
        sb.AppendLine("<tr><td><code>/OpenLightFX/Tracks/ByItem?itemId={id}</code></td><td>GET</td><td>List available tracks for a movie</td></tr>");
        sb.AppendLine("<tr><td><code>/OpenLightFX/Tracks/Select</code></td><td>POST</td><td>Set/clear selected track (JSON body: {itemId, trackPath})</td></tr>");
        sb.AppendLine("<tr><td><code>/OpenLightFX/Status</code></td><td>GET</td><td>Plugin status and statistics</td></tr>");
        sb.AppendLine("<tr><td><code>/OpenLightFX/Bulbs/Test?bulbId={id}</code></td><td>GET</td><td>Test bulb connectivity</td></tr>");
        sb.AppendLine("</table>");

        sb.AppendLine("</body></html>");

        return sb.ToString();
    }
}
