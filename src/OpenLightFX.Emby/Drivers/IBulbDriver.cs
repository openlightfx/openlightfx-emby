namespace OpenLightFX.Emby.Drivers;

public interface IBulbDriver : IAsyncDisposable
{
    string DriverName { get; }

    BulbCapabilityProfile GetCapabilities();

    Task SetState(byte r, byte g, byte b, uint brightness, uint transitionMs);

    Task SetColor(byte r, byte g, byte b, uint transitionMs = 0);

    Task SetBrightness(uint brightness, uint transitionMs = 0);

    Task SetColorTemperature(uint kelvin, uint brightness, uint transitionMs = 0);

    Task SetPower(bool on);

    Task<bool> TestConnection();

    Task<BulbState?> GetCurrentState();
}
