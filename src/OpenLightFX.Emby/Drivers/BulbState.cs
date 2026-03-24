namespace OpenLightFX.Emby.Drivers;

public record BulbState(
    byte R, byte G, byte B,
    uint Brightness,
    uint? ColorTemperature,
    bool IsOn);
