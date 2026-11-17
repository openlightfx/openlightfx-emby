using OpenLightFX.Emby.Studio;
using Xunit;

namespace OpenLightFX.Emby.Tests;

public sealed class ZoneColorTests
{
    private static readonly AiTrackZone Full = new("zone", 0, 0, 1, 1);

    [Fact]
    public void Equal_red_and_white_stays_red()
    {
        var color = ZoneColor.Dominant(Stripes(4, (255, 0, 0), (255, 255, 255)), 4, 1, Full, 0);
        Assert.True(color.R > 200 && color.G < 40 && color.B < 40, $"{color}");
    }

    [Fact]
    public void White_wins_once_it_is_most_of_the_zone()
    {
        var sixty = ZoneColor.Dominant(Blocks((255, 0, 0), 2, (255, 255, 255), 3), 5, 1, Full, 0);
        Assert.True(sixty.R > 230 && sixty.G > 230 && sixty.B > 230, $"{sixty}");

        var half = ZoneColor.Dominant(Stripes(2, (255, 0, 0), (255, 255, 255)), 2, 1, Full, 0);
        Assert.True(half.R > 200 && half.G < 40 && half.B < 40, $"{half}");
    }

    [Fact]
    public void Equal_red_and_cyan_does_not_average_to_gray()
    {
        var color = ZoneColor.Dominant(Stripes(4, (255, 0, 0), (0, 255, 255)), 4, 1, Full, 0);
        var spread = Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
        Assert.True(spread > 180, $"{color}");
    }

    [Fact]
    public void Black_wins_only_when_it_covers_most_of_the_zone()
    {
        var mostlyBlack = ZoneColor.Dominant(Blocks((0, 0, 0), 6, (255, 0, 0), 1), 7, 1, Full, 0);
        Assert.True(mostlyBlack.R < 8 && mostlyBlack.G < 8 && mostlyBlack.B < 8, $"{mostlyBlack}");

        var half = ZoneColor.Dominant(Stripes(2, (0, 0, 0), (255, 0, 0)), 2, 1, Full, 0);
        Assert.InRange(half.R, 80, 180);
        Assert.True(half.G < 20 && half.B < 20, $"{half}");
    }

    [Fact]
    public void Largest_chromatic_region_beats_a_split_picture_and_black_bars()
    {
        // 4 red, 2 blue, 2 green, 2 yellow, 3 black. Unweighted mode would be close;
        // black is down-weighted so the red region wins.
        var pixels = new List<byte>();
        Add(pixels, (220, 30, 30), 4);
        Add(pixels, (30, 90, 200), 2);
        Add(pixels, (40, 140, 50), 2);
        Add(pixels, (200, 160, 40), 2);
        Add(pixels, (0, 0, 0), 3);
        var color = ZoneColor.Dominant(pixels.ToArray(), pixels.Count / 3, 1, Full, 0);
        Assert.True(color.R > 140 && color.G < 80 && color.B < 80, $"{color}");
    }

    [Fact]
    public void Scene_colour_is_the_body_and_a_single_flash_does_not_win()
    {
        var zones = new[] { Full };
        var colors = new SceneColorAccumulator(zones, 0, 2, 2);
        for (var frame = 0; frame < 48; frame++)
            colors.Observe(frame, Solid(220, 20, 20));
        var opening = colors.Close(48);
        Assert.True(opening["zone"].R > 180 && opening["zone"].G < 40, $"{opening["zone"]}");

        for (var frame = 48; frame < 96; frame++)
        {
            var flash = frame == 66;
            colors.Observe(frame, flash ? Solid(255, 255, 255) : Solid(20, 50, 190));
        }

        var scene = colors.Close(96);
        Assert.True(scene["zone"].B > 150 && scene["zone"].R < 80, $"{scene["zone"]}");
    }

    [Fact]
    public void A_short_scene_does_not_take_its_colour_from_the_next_scene()
    {
        var colors = new SceneColorAccumulator(new[] { Full }, 0, 2, 2);
        for (var frame = 0; frame < 20; frame++)
            colors.Observe(frame, Solid(20, 160, 40));
        for (var frame = 20; frame < 40; frame++)
            colors.Observe(frame, Solid(20, 40, 180));

        var first = colors.Close(20);
        var second = colors.Close(40);
        Assert.True(first["zone"].G > first["zone"].B, $"{first["zone"]}");
        Assert.True(second["zone"].B > second["zone"].G, $"{second["zone"]}");
    }

    private static byte[] Stripes(int width, (byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        var frame = new byte[width * 3];
        for (var x = 0; x < width; x++)
        {
            var color = x % 2 == 0 ? a : b;
            frame[x * 3] = color.R;
            frame[x * 3 + 1] = color.G;
            frame[x * 3 + 2] = color.B;
        }
        return frame;
    }

    private static byte[] Blocks((byte R, byte G, byte B) first, int firstCount, (byte R, byte G, byte B) second, int secondCount)
    {
        var pixels = new List<byte>();
        Add(pixels, first, firstCount);
        Add(pixels, second, secondCount);
        return pixels.ToArray();
    }

    private static void Add(List<byte> pixels, (byte R, byte G, byte B) color, int count)
    {
        for (var i = 0; i < count; i++)
        {
            pixels.Add(color.R);
            pixels.Add(color.G);
            pixels.Add(color.B);
        }
    }

    private static byte[] Solid(byte r, byte g, byte b)
    {
        var frame = new byte[2 * 2 * 3];
        for (var i = 0; i < frame.Length; i += 3)
        {
            frame[i] = r;
            frame[i + 1] = g;
            frame[i + 2] = b;
        }
        return frame;
    }
}
