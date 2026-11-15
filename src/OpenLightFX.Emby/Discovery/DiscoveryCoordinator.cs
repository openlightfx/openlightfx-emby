namespace OpenLightFX.Emby.Discovery;

using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Rooms;

public class DiscoveryCoordinator
{
    private readonly List<IDiscoveryModule> _modules;

    public DiscoveryCoordinator()
    {
        _modules = new List<IDiscoveryModule>
        {
            new WizDiscovery(),
            new HueDiscovery(),
            new LifxDiscovery(),
            new GoveeDiscovery()
        };
    }

    /// <summary>
    /// Run discovery across specified (or all) protocols concurrently.
    /// </summary>
    public async Task<List<DiscoveredBulb>> DiscoverAsync(
        IEnumerable<BulbProtocol>? protocols = null,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        var modules = protocols != null
            ? _modules.Where(m => protocols.Contains(m.Protocol)).ToList()
            : _modules;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs + 2000); // 2s overhead

        var tasks = modules.Select(m => SafeDiscover(m, timeoutMs, cts.Token));
        var results = await Task.WhenAll(tasks);

        var allBulbs = results.SelectMany(r => r).ToList();

        // Deduplicate by MAC address or IP
        return DeduplicateBulbs(allBulbs);
    }

    private static async Task<List<DiscoveredBulb>> SafeDiscover(
        IDiscoveryModule module, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var results = await module.DiscoverAsync(timeoutMs, ct);
            System.Diagnostics.Debug.WriteLine(
                $"[OpenLightFX] {module.Protocol} discovery found {results.Count} bulb(s)");
            return results;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[OpenLightFX] {module.Protocol} discovery failed: {ex.Message}");
            return new List<DiscoveredBulb>();
        }
    }

    private static List<DiscoveredBulb> DeduplicateBulbs(List<DiscoveredBulb> bulbs)
    {
        var seen = new HashSet<string>();
        var result = new List<DiscoveredBulb>();

        foreach (var bulb in bulbs)
        {
            // Identity is protocol-scoped: different protocols may report the same MAC,
            // while Govee's LAN `device` value is a stable identifier but not necessarily a MAC.
            var key = !string.IsNullOrWhiteSpace(bulb.MacAddress)
                ? $"mac:{bulb.Protocol}:{StableBulbId.NormalizeHardwareId(bulb.MacAddress)}"
                : !string.IsNullOrWhiteSpace(bulb.HardwareId)
                    ? $"hardware:{bulb.Protocol}:{StableBulbId.NormalizeHardwareId(bulb.HardwareId)}"
                    : $"ip:{bulb.Protocol}:{bulb.IpAddress}";

            if (seen.Add(key))
                result.Add(bulb);
        }

        return result;
    }
}
