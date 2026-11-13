namespace OpenLightFX.Emby.Configuration;

public class ConfigurationService
{
    public IEnumerable<string> GetScanPaths(string scanPathsText)
    {
        if (string.IsNullOrWhiteSpace(scanPathsText))
            return Enumerable.Empty<string>();
        return scanPathsText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim())
            .Where(path => !string.IsNullOrEmpty(path));
    }
}
