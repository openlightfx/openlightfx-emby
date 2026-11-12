using System.Text.Json;
using OpenLightFX.Emby.Drivers;
using OpenLightFX.Emby.Effects;
using OpenLightFX.Emby.Engine;
using OpenLightFX.Emby.Services;
using Openlightfx;
using Xunit;

namespace OpenLightFX.Emby.Tests;

/// <summary>
/// Second reader of openlightfx-studio tests/fixtures/sampling.
/// Set OLFX_FIXTURES to that directory to use a sibling checkout; otherwise the
/// vendored copy pinned in Fixtures/sampling/SOURCE is used.
/// </summary>
public class SharedFixtureTests
{
    private static string Root()
    {
        var env = Environment.GetEnvironmentVariable("OLFX_FIXTURES");
        if (!string.IsNullOrWhiteSpace(env))
            return env;

        var vendored = Path.Combine(AppContext.BaseDirectory, "fixtures");
        if (Directory.Exists(vendored))
            return vendored;

        throw new InvalidOperationException(
            "Set OLFX_FIXTURES to openlightfx-studio/tests/fixtures/sampling");
    }

    [Fact(DisplayName = "KEY-009 @fixture step holds the previous state until the keyframe instant")]
    public void Key009() => AssertKeyframeCases("KEY-009");

    [Fact(DisplayName = "KEY-010 @fixture linear blend rounds half-to-even")]
    public void Key010() => AssertKeyframeCases("KEY-010");

    [Fact(DisplayName = "KEY-011 @fixture empty spans are black before the first keyframe")]
    public void Key011() => AssertKeyframeCases("KEY-011");

    [Fact(DisplayName = "KEY-012 @fixture kelvin conversion is truncated Tanner Helland")]
    public void Key012() => AssertKeyframeCases("KEY-012");

    [Fact(DisplayName = "KEY-013 @fixture unspecified interpolation blends as linear")]
    public void Key013() => AssertKeyframeCases("KEY-013");

    [Fact(DisplayName = "KEY-014 @fixture power_on false still previews RGB brightness")]
    public void Key014() => AssertKeyframeCases("KEY-014");

    [Fact(DisplayName = "FX-003 @fixture effect commands match effects.json")]
    public void Effect_commands_match_the_shared_fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "effects.json")));
        var factory = new EffectRendererFactory();
        var context = new EffectContext(
            new BulbCapabilityProfile(true, true, 1000, 10000, 0, 20),
            GlobalBrightnessCap: 100,
            PhotosensitivityEnabled: false);

        foreach (var item in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var input = item.GetProperty("input");
            var keyframe = ToEffect(input);
            var renderer = factory.GetRenderer(keyframe.EffectType);
            Assert.NotNull(renderer);
            var actual = renderer!.Render(keyframe, context);
            var expected = item.GetProperty("commands").EnumerateArray().ToList();
            Assert.True(expected.Count == actual.Count,
                $"{name}: expected {expected.Count} commands, got {actual.Count}");

            for (var i = 0; i < expected.Count; i++)
            {
                var want = expected[i];
                var got = actual[i];
                Assert.True(
                    want.GetProperty("offsetMs").GetUInt32() == got.OffsetMs
                    && want.GetProperty("r").GetInt32() == got.R
                    && want.GetProperty("g").GetInt32() == got.G
                    && want.GetProperty("b").GetInt32() == got.B
                    && want.GetProperty("brightness").GetUInt32() == got.Brightness,
                    $"{name} command {i}: expected {want}, got offset {got.OffsetMs} rgb {got.R},{got.G},{got.B} brightness {got.Brightness}");
            }
        }
    }

    [Fact(DisplayName = "IO-002 @fixture roundtrip.lightfx parses with no validation errors")]
    public void Roundtrip_track_parses()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "roundtrip.lightfx"));
        var parsed = new TrackParser().ParseBytes(bytes, "roundtrip.lightfx");
        Assert.True(parsed.IsValid, string.Join("; ", parsed.ValidationErrors));
        Assert.Empty(parsed.ValidationErrors);
        Assert.Equal(1u, parsed.Track.Version);
        Assert.Equal(10000ul, parsed.Track.Metadata.DurationMs);
        Assert.Equal(new[] { "left", "right" }, parsed.Track.Channels.Select(channel => channel.Id).ToArray());
        Assert.Equal(2700u, parsed.Track.Keyframes[1].ColorTemperature);
        Assert.Equal("PULSE", parsed.Track.EffectKeyframes[0].EffectType.ToString().ToUpperInvariant());
    }

    private static void AssertKeyframeCases(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "keyframes.json")));
        var matches = doc.RootElement.GetProperty("cases").EnumerateArray()
            .Where(item => item.GetProperty("id").GetString() == id)
            .ToList();
        Assert.NotEmpty(matches);

        foreach (var item in matches)
        {
            var timeMs = item.GetProperty("timeMs").GetUInt64();
            var keyframes = item.GetProperty("keyframes").EnumerateArray().Select(ToKeyframe).ToList();
            var sample = InterpolationEngine.SampleChannel(keyframes, timeMs);
            var expect = item.GetProperty("expect");
            var label = $"{id} @ {timeMs}";
            if (expect.TryGetProperty("r", out var r))
                Assert.True(r.GetInt32() == sample.R, $"{label} r expected {r.GetInt32()} got {sample.R}");
            if (expect.TryGetProperty("g", out var g))
                Assert.True(g.GetInt32() == sample.G, $"{label} g expected {g.GetInt32()} got {sample.G}");
            if (expect.TryGetProperty("b", out var b))
                Assert.True(b.GetInt32() == sample.B, $"{label} b expected {b.GetInt32()} got {sample.B}");
            if (expect.TryGetProperty("brightness", out var brightness))
                Assert.True(brightness.GetUInt32() == sample.Brightness, $"{label} brightness expected {brightness.GetUInt32()} got {sample.Brightness}");
            if (expect.TryGetProperty("kelvin", out var kelvin))
                Assert.True(kelvin.GetUInt32() == sample.ColorTemperature, $"{label} kelvin expected {kelvin.GetUInt32()} got {sample.ColorTemperature}");
            if (expect.TryGetProperty("mode", out var mode))
                Assert.Equal(mode.GetString(), sample.ColorMode.ToString().ToUpperInvariant() == "COLORTEMPERATURE"
                    ? "COLOR_TEMPERATURE"
                    : "RGB");
            if (expect.TryGetProperty("powerOn", out var power))
                Assert.Equal(power.GetBoolean(), sample.PowerOn);
        }
    }

    private static Keyframe ToKeyframe(JsonElement element)
    {
        var color = element.GetProperty("color");
        var mode = element.GetProperty("colorMode").GetString();
        var interpolation = element.GetProperty("interpolation").GetString();
        return new Keyframe
        {
            Id = element.GetProperty("id").GetString() ?? "",
            ChannelId = element.GetProperty("channelId").GetString() ?? "",
            TimestampMs = element.GetProperty("timestampMs").GetUInt64(),
            ColorMode = mode == "COLOR_TEMPERATURE" ? ColorMode.ColorTemperature : ColorMode.Rgb,
            Color = new RGBColor
            {
                R = color.GetProperty("r").GetUInt32(),
                G = color.GetProperty("g").GetUInt32(),
                B = color.GetProperty("b").GetUInt32()
            },
            ColorTemperature = element.GetProperty("colorTemperature").GetUInt32(),
            Brightness = element.GetProperty("brightness").GetUInt32(),
            TransitionMs = element.GetProperty("transitionMs").GetUInt32(),
            Interpolation = interpolation switch
            {
                "STEP" => InterpolationMode.Step,
                "LINEAR" => InterpolationMode.Linear,
                _ => InterpolationMode.InterpolationUnspecified
            },
            PowerOn = element.GetProperty("powerOn").GetBoolean()
        };
    }

    private static EffectKeyframe ToEffect(JsonElement input)
    {
        var keyframe = new EffectKeyframe
        {
            EffectType = Enum.Parse<EffectType>(input.GetProperty("effectType").GetString()!, ignoreCase: true),
            DurationMs = input.GetProperty("durationMs").GetUInt64(),
            Intensity = input.GetProperty("intensity").GetUInt32(),
            EffectParams = new EffectParams()
        };

        if (input.TryGetProperty("primaryColor", out var primary) && primary.ValueKind == JsonValueKind.Object)
        {
            keyframe.PrimaryColor = new RGBColor
            {
                R = primary.GetProperty("r").GetUInt32(),
                G = primary.GetProperty("g").GetUInt32(),
                B = primary.GetProperty("b").GetUInt32()
            };
        }

        if (input.TryGetProperty("secondaryColor", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
        {
            keyframe.SecondaryColor = new RGBColor
            {
                R = secondary.GetProperty("r").GetUInt32(),
                G = secondary.GetProperty("g").GetUInt32(),
                B = secondary.GetProperty("b").GetUInt32()
            };
        }

        if (input.TryGetProperty("params", out var parms) && parms.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parms.EnumerateObject())
                keyframe.EffectParams.Params[property.Name] = property.Value.GetDouble();
        }

        return keyframe;
    }
}
