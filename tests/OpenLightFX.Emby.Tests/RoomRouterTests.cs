using System.Text.Json;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Rooms;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class RoomRouterTests
{
    private static string FixturePath()
    {
        var configured = Environment.GetEnvironmentVariable("OLFX_FIXTURES");
        var source = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(configured, "..", "routing", "cases.json"))
            : Path.Combine(AppContext.BaseDirectory, "fixtures", "routing", "cases.json");
        if (File.Exists(source)) return source;
        throw new FileNotFoundException("Set OLFX_FIXTURES to Studio tests/fixtures/sampling or copy the shared routing fixture.", source);
    }

    [Fact(DisplayName = "MODEL-004 @fixture plugin routing reads the shared Studio fixture")]
    public void Shared_fixture_has_cases_and_matching_vendored_copy()
    {
        var vendored = Path.Combine(AppContext.BaseDirectory, "fixtures", "routing", "cases.json");
        Assert.Equal(File.ReadAllBytes(FixturePath()), File.ReadAllBytes(vendored));
        using var document = JsonDocument.Parse(File.ReadAllText(vendored));
        Assert.Equal(6, document.RootElement.GetProperty("cases").GetArrayLength());
    }

    [Fact(DisplayName = "MODEL-006 @fixture Follows overrides replace a bulb's routing position")]
    public void Follows_case_routes_the_override_as_its_effective_position()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var example = document.RootElement.GetProperty("cases").EnumerateArray()
            .Single(testCase => testCase.GetProperty("name").GetString() == "Follows override changes a bulb to Ambient routing");
        var channels = example.GetProperty("channels").EnumerateArray()
            .Select(channel => new RoutingChannel(channel.GetProperty("id").GetString()!, channel.GetProperty("spatialHint").GetString()))
            .ToArray();
        var assignments = example.GetProperty("bulbs").EnumerateArray().Select(bulb => new RoomBulbAssignment
        {
            Id = bulb.GetProperty("id").GetString()!,
            Position = bulb.GetProperty("position").GetString(),
            Follows = bulb.TryGetProperty("follows", out var follows) ? follows.GetString() : null
        }).ToList();
        var room = new RoomRecord { Id = "room", Bulbs = assignments };
        var known = assignments.Select(assignment => new KnownBulbRecord { Id = assignment.Id, Config = new WizBulbConfig() }).ToArray();

        var routing = RoomRouter.Route(channels, room, known);
        var centerBulb = routing.Bulbs.Single(bulb => bulb.BulbId == "center-bulb");
        Assert.Equal(new[] { "left", "center", "right" }, centerBulb.Sources.Select(source => source.ChannelId));
    }

    [Fact(DisplayName = "MODEL-003 @fixture routing lights every bulb and reaches every channel")]
    public void Every_fixture_route_covers_all_bulbs_and_channels()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        foreach (var testCase in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var channels = testCase.GetProperty("channels").EnumerateArray()
                .Select(channel => new RoutingChannel(
                    channel.GetProperty("id").GetString()!,
                    channel.GetProperty("spatialHint").GetString()))
                .ToArray();
            var assignments = testCase.GetProperty("bulbs").EnumerateArray()
                .Select(bulb => new RoomBulbAssignment
                {
                    Id = bulb.GetProperty("id").GetString()!,
                    Position = bulb.GetProperty("position").GetString(),
                    Follows = bulb.TryGetProperty("follows", out var follows) ? follows.GetString() : null
                }).ToList();
            var room = new RoomRecord { Id = "room", Bulbs = assignments };
            var known = assignments.Select(assignment => new KnownBulbRecord
            {
                Id = assignment.Id,
                Config = new WizBulbConfig()
            }).ToArray();

            var routing = RoomRouter.Route(channels, room, known);
            var expectedChannels = testCase.GetProperty("channelBulbs");
            var expectedBulbs = testCase.GetProperty("bulbSources");
            foreach (var channel in channels)
            {
                var expected = expectedChannels.GetProperty(channel.Id).EnumerateArray()
                    .Select(value => value.GetString()!).ToArray();
                var actual = routing.Channels.Single(value => value.ChannelId == channel.Id).BulbIds;
                Assert.True(expected.SequenceEqual(actual), $"{testCase.GetProperty("name").GetString()} channel {channel.Id}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
                Assert.NotEmpty(actual);
            }
            foreach (var assignment in assignments)
            {
                var expected = expectedBulbs.GetProperty(assignment.Id).EnumerateArray()
                    .Select(value => value.GetString()!).ToArray();
                var actual = routing.Bulbs.Single(value => value.BulbId == assignment.Id)
                    .Sources.Select(source => source.ChannelId).ToArray();
                Assert.True(expected.SequenceEqual(actual), $"{testCase.GetProperty("name").GetString()} bulb {assignment.Id}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
                Assert.NotEmpty(actual);
            }
        }
    }

    [Fact(DisplayName = "MODEL-007 @unit legacy UUID channel IDs route from spatial hints without rewriting IDs")]
    public void Legacy_channel_ids_remain_unchanged_while_positions_are_inferred()
    {
        var channels = new[]
        {
            new RoutingChannel("8ab0-left", "SPATIAL_LEFT", "Left"),
            new RoutingChannel("8ab0-right", "SPATIAL_RIGHT", "Right"),
            new RoutingChannel("8ab0-sl", "SPATIAL_SURROUND_LEFT", "Surround Left"),
            new RoutingChannel("8ab0-sr", "SPATIAL_SURROUND_RIGHT", "Surround Right")
        };
        var assignments = new[] { "left", "right", "surround-left", "surround-right" }
            .Select(position => new RoomBulbAssignment { Id = position, Position = position }).ToList();
        var room = new RoomRecord { Id = "room", Bulbs = assignments };
        var known = assignments.Select(assignment => new KnownBulbRecord { Id = assignment.Id, Config = new WizBulbConfig() }).ToArray();

        var routing = RoomRouter.Route(channels, room, known);

        Assert.Equal(channels.Select(channel => channel.Id), routing.Channels.Select(channel => channel.ChannelId));
        Assert.Equal(new[] { "left" }, routing.Channels[0].BulbIds);
        Assert.Equal(new[] { "surround-right" }, routing.Channels[3].BulbIds);
    }

    [Fact(DisplayName = "MODEL-005 @fixture bulbs mix maximum brightness and weighted RGB")]
    public void Mixer_matches_shared_mixing_fixtures()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        foreach (var testCase in document.RootElement.GetProperty("mixing").EnumerateArray())
        {
            var sources = testCase.GetProperty("sources").EnumerateArray().Select(source =>
                new OpenLightFX.Emby.Engine.InterpolationEngine.InterpolatedState(
                    (byte)source.GetProperty("r").GetInt32(),
                    (byte)source.GetProperty("g").GetInt32(),
                    (byte)source.GetProperty("b").GetInt32(),
                    (uint)source.GetProperty("brightness").GetInt32(),
                    0,
                    Openlightfx.ColorMode.Rgb,
                    source.GetProperty("brightness").GetInt32() > 0)).ToArray();
            var expected = testCase.GetProperty("expected");
            var actual = BulbMixer.Mix(sources);

            Assert.Equal((byte)expected.GetProperty("r").GetInt32(), actual.R);
            Assert.Equal((byte)expected.GetProperty("g").GetInt32(), actual.G);
            Assert.Equal((byte)expected.GetProperty("b").GetInt32(), actual.B);
            Assert.Equal((uint)expected.GetProperty("brightness").GetInt32(), actual.Brightness);
        }
    }
}
