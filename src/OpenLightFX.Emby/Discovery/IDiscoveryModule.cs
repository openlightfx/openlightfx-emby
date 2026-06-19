namespace OpenLightFX.Emby.Discovery;

using OpenLightFX.Emby.Models;

public interface IDiscoveryModule
{
    BulbProtocol Protocol { get; }
    Task<List<DiscoveredBulb>> DiscoverAsync(int timeoutMs, CancellationToken ct);
}
