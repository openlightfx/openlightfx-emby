namespace OpenLightFX.Emby.Drivers.Lifx;

using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Utilities;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// LIFX LAN Protocol driver (UDP, port 56700).
/// Implements SetColor (type 102) and SetPower (type 117) binary messages.
/// </summary>
public class LifxDriver : IBulbDriver
{
    private const int LifxDefaultPort = 56700;
    private const int FrameHeaderSize = 8;
    private const int FrameAddressSize = 16;
    private const int ProtocolHeaderSize = 4;
    private const int HeaderTotalSize = FrameHeaderSize + FrameAddressSize + ProtocolHeaderSize;

    private const ushort MsgTypeSetColor = 102;
    private const ushort MsgTypeGetService = 2;
    private const ushort MsgTypeState = 107;
    private const ushort MsgTypeSetPower = 117;

    private const ushort ProtocolNumber = 1024; // protocol 1024 with addressable bit set
    private const ushort DefaultKelvin = 3500;
    private const int SetPowerMaxRetries = 3;
    private const int SetPowerRetryDelayMs = 200;
    private const int ReceiveTimeoutMs = 2000;

    private readonly BulbConfig _config;
    private readonly IPEndPoint _endpoint;
    private readonly UdpClient _udpClient;
    private readonly uint _source;
    private readonly byte[] _target;
    private readonly BulbCapabilityProfile _capabilities;
    private byte _sequence;
    private BulbState? _lastKnownState;
    private bool _disposed;

    public string DriverName => "LIFX";

    public LifxDriver(BulbConfig config)
    {
        _config = config;
        var port = config.Port > 0 ? config.Port : LifxDefaultPort;
        _endpoint = new IPEndPoint(IPAddress.Parse(config.IpAddress), port);

        _udpClient = new UdpClient();
        _udpClient.Client.ReceiveTimeout = ReceiveTimeoutMs;

        _source = (uint)Random.Shared.Next();
        _target = new byte[8]; // all zeros = all devices (broadcast)
        _sequence = 0;

        _capabilities = new BulbCapabilityProfile(
            SupportsRgb: true,
            SupportsColorTemp: true,
            ColorTempMin: 1500,
            ColorTempMax: 9000,
            MinTransitionMs: 50,
            MaxCommandsPerSecond: 20
        );
    }

    public BulbCapabilityProfile GetCapabilities() => _capabilities;

    public async Task SetState(byte r, byte g, byte b, uint brightness, uint transitionMs)
    {
        var scaledRgb = ScaleRgbByBrightness(r, g, b, brightness);
        var (hue, sat, bri, kelvin) = ColorConverter.RgbToHsbk(scaledRgb.r, scaledRgb.g, scaledRgb.b, DefaultKelvin);
        await SendSetColor(hue, sat, bri, kelvin, transitionMs);
        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetColor(byte r, byte g, byte b, uint transitionMs = 0)
    {
        var (hue, sat, bri, kelvin) = ColorConverter.RgbToHsbk(r, g, b, DefaultKelvin);
        await SendSetColor(hue, sat, bri, kelvin, transitionMs);
        _lastKnownState = new BulbState(r, g, b, _lastKnownState?.Brightness ?? 100, null, true);
    }

    public async Task SetBrightness(uint brightness, uint transitionMs = 0)
    {
        var state = _lastKnownState;
        byte r = state?.R ?? 255, g = state?.G ?? 255, b = state?.B ?? 255;
        var scaledRgb = ScaleRgbByBrightness(r, g, b, brightness);
        var (hue, sat, bri, kelvin) = ColorConverter.RgbToHsbk(scaledRgb.r, scaledRgb.g, scaledRgb.b, DefaultKelvin);
        await SendSetColor(hue, sat, bri, kelvin, transitionMs);
        _lastKnownState = new BulbState(r, g, b, brightness, null, true);
    }

    public async Task SetColorTemperature(uint kelvin, uint brightness, uint transitionMs = 0)
    {
        var clampedKelvin = (ushort)Math.Clamp(kelvin, _capabilities.ColorTempMin, _capabilities.ColorTempMax);
        var briByte = (ushort)Math.Clamp(Math.Round(brightness / 100.0 * 65535.0), 0, 65535);
        await SendSetColor(0, 0, briByte, clampedKelvin, transitionMs);
        _lastKnownState = new BulbState(255, 255, 255, brightness, kelvin, true);
    }

    public async Task SetPower(bool on)
    {
        for (int attempt = 0; attempt < SetPowerMaxRetries; attempt++)
        {
            try
            {
                await SendSetPower(on, 0);
                _lastKnownState = _lastKnownState != null
                    ? _lastKnownState with { IsOn = on }
                    : new BulbState(0, 0, 0, 0, null, on);
                return;
            }
            catch
            {
                if (attempt == SetPowerMaxRetries - 1)
                    throw;
                await Task.Delay(SetPowerRetryDelayMs);
            }
        }
    }

    public async Task<bool> TestConnection()
    {
        try
        {
            var packet = BuildPacket(MsgTypeGetService, Array.Empty<byte>(), resRequired: true);
            await _udpClient.SendAsync(packet, packet.Length, _endpoint);

            using var cts = new CancellationTokenSource(ReceiveTimeoutMs);
            var result = await _udpClient.ReceiveAsync(cts.Token);
            return result.Buffer.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<BulbState?> GetCurrentState()
    {
        if (_lastKnownState != null)
            return _lastKnownState;

        try
        {
            // GetColor (type 101) requests the bulb's current state
            var packet = BuildPacket(101, Array.Empty<byte>(), resRequired: true);
            await _udpClient.SendAsync(packet, packet.Length, _endpoint);

            using var cts = new CancellationTokenSource(ReceiveTimeoutMs);
            var result = await _udpClient.ReceiveAsync(cts.Token);

            if (result.Buffer.Length >= HeaderTotalSize + 8)
            {
                var msgType = BitConverter.ToUInt16(result.Buffer, 32);

                if (msgType == MsgTypeState && result.Buffer.Length >= HeaderTotalSize + 8)
                {
                    ushort hue = BitConverter.ToUInt16(result.Buffer, HeaderTotalSize);
                    ushort sat = BitConverter.ToUInt16(result.Buffer, HeaderTotalSize + 2);
                    ushort bri = BitConverter.ToUInt16(result.Buffer, HeaderTotalSize + 4);
                    ushort kelvin = BitConverter.ToUInt16(result.Buffer, HeaderTotalSize + 6);

                    var (r, g, b) = ColorConverter.HsbkToRgb(hue, sat, bri);
                    uint brightness = (uint)Math.Clamp(Math.Round(bri / 65535.0 * 100.0), 0, 100);

                    _lastKnownState = new BulbState(r, g, b, brightness, kelvin, true);
                    return _lastKnownState;
                }
            }
        }
        catch
        {
            // Bulb may be offline
        }

        return null;
    }

    private async Task SendSetColor(ushort hue, ushort saturation, ushort brightness,
        ushort kelvin, uint transitionMs)
    {
        // SetColor payload: 1 reserved byte + H(16) + S(16) + B(16) + K(16) + duration(32) = 13 bytes
        var payload = new byte[13];
        payload[0] = 0; // reserved
        BitConverter.TryWriteBytes(payload.AsSpan(1), hue);
        BitConverter.TryWriteBytes(payload.AsSpan(3), saturation);
        BitConverter.TryWriteBytes(payload.AsSpan(5), brightness);
        BitConverter.TryWriteBytes(payload.AsSpan(7), kelvin);
        BitConverter.TryWriteBytes(payload.AsSpan(9), transitionMs);

        var packet = BuildPacket(MsgTypeSetColor, payload);
        // Fire-and-forget for playback performance
        await _udpClient.SendAsync(packet, packet.Length, _endpoint);
    }

    private async Task SendSetPower(bool on, uint durationMs)
    {
        // SetPower payload: level(16) + duration(32) = 6 bytes
        var payload = new byte[6];
        ushort level = on ? (ushort)65535 : (ushort)0;
        BitConverter.TryWriteBytes(payload.AsSpan(0), level);
        BitConverter.TryWriteBytes(payload.AsSpan(2), durationMs);

        var packet = BuildPacket(MsgTypeSetPower, payload, ackRequired: true);
        await _udpClient.SendAsync(packet, packet.Length, _endpoint);
    }

    private byte[] BuildPacket(ushort messageType, byte[] payload,
        bool resRequired = false, bool ackRequired = false)
    {
        int totalSize = HeaderTotalSize + payload.Length;
        var packet = new byte[totalSize];

        // --- Frame header (8 bytes) ---
        // size (16-bit LE)
        BitConverter.TryWriteBytes(packet.AsSpan(0), (ushort)totalSize);
        // protocol (12 bits) + addressable (1 bit) + tagged (1 bit) + origin (2 bits)
        // protocol=1024, addressable=1, tagged=0, origin=0 → 0x0400 | 0x1000 = 0x1400
        ushort protocolFlags = ProtocolNumber | (1 << 12); // addressable bit
        BitConverter.TryWriteBytes(packet.AsSpan(2), protocolFlags);
        // source (32-bit)
        BitConverter.TryWriteBytes(packet.AsSpan(4), _source);

        // --- Frame address (16 bytes) ---
        // target (8 bytes at offset 8)
        _target.AsSpan().CopyTo(packet.AsSpan(8));
        // reserved (6 bytes at offset 16) - already zeroed
        // res_required + ack_required (1 byte at offset 22)
        byte flags = 0;
        if (resRequired) flags |= 0x01;
        if (ackRequired) flags |= 0x02;
        packet[22] = flags;
        // sequence (1 byte at offset 23)
        packet[23] = _sequence++;

        // --- Protocol header (4 bytes) ---
        // reserved (1 byte at offset 24) - already zeroed
        // type (16-bit LE at offset 25..26)
        // NOTE: the protocol header is actually at offset 24, laid out as:
        //   reserved(8) + type(16) + reserved(8) = bytes 24,25-26,27
        // But per LIFX spec the header is packed as: 64-bit reserved(0) ignored,
        // actually: offset 24 = reserved(8), offset 25-26 = type(16), offset 27 = reserved(8)
        // Let's correct: total header = 8 + 16 + 4 = 28 bytes
        packet[24] = 0; // reserved
        BitConverter.TryWriteBytes(packet.AsSpan(25), messageType);
        packet[27] = 0; // reserved

        // --- Payload ---
        if (payload.Length > 0)
        {
            payload.AsSpan().CopyTo(packet.AsSpan(HeaderTotalSize));
        }

        return packet;
    }

    private static (byte r, byte g, byte b) ScaleRgbByBrightness(byte r, byte g, byte b, uint brightness)
    {
        double scale = Math.Clamp(brightness, 0, 100) / 100.0;
        return (
            (byte)Math.Clamp(Math.Round(r * scale), 0, 255),
            (byte)Math.Clamp(Math.Round(g * scale), 0, 255),
            (byte)Math.Clamp(Math.Round(b * scale), 0, 255)
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _udpClient.Dispose();
        await ValueTask.CompletedTask;
    }
}
