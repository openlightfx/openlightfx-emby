namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Picks the colour a bulb should hold for one zone. Pixels are binned in OKLCh.
/// Near-white and near-black are neutrals, so a red-and-white zone stays red
/// until white is actually most of the zone, and the bin mean is never the
/// average of the two.
/// </summary>
public static class ZoneColor
{
    public const int HueBins = 18;
    public const double NeutralChroma = 0.035;
    public const double BlackLightness = 0.08;
    public const double WhiteLightness = 0.80;
    public const double BlackWeight = 0.30;
    public const double WhiteWeight = 0.70;
    public const double GrayWeight = 0.85;

    /// <summary>Dominant sRGB of the zone. An empty zone is black.</summary>
    public static (byte R, byte G, byte B) Dominant(ReadOnlySpan<byte> rgb, int width, int height, AiTrackZone zone, double centerExclusionPercent)
    {
        var histogram = new ZoneHistogram();
        histogram.AddPixels(rgb, width, height, zone, centerExclusionPercent);
        return histogram.Finish();
    }

    internal static (int Left, int Top, int Right, int Bottom) ZoneBounds(AiTrackZone zone, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(zone.X * width), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Floor(zone.Y * height), 0, Math.Max(0, height - 1));
        var right = Math.Clamp((int)Math.Ceiling((zone.X + zone.Width) * width), left + 1, width);
        var bottom = Math.Clamp((int)Math.Ceiling((zone.Y + zone.Height) * height), top + 1, height);
        return (left, top, right, bottom);
    }
}

/// <summary>
/// Which detection frames belong to the body of a scene. The first frames after
/// a cut and the last frames before the next one are transitions, flashes, or blur.
/// A scene too short for that window uses the frames after a shorter skip, and
/// never a frame from the next scene.
/// </summary>
public static class SceneSampling
{
    /// <summary>12 frames at 24 fps is 500 ms.</summary>
    public const int SkipFrames = 12;

    /// <summary>6 frames at 24 fps is 250 ms.</summary>
    public const int TailFrames = 6;

    public const int FallbackSkipFrames = 3;

    /// <summary>Every 6th detection frame is 4 samples per second.</summary>
    public const int SampleStride = 6;

    /// <summary>
    /// A merged cut is reported 15 frames after the cut frame, and the tail is
    /// another 6. Samples older than this are committed; the tail of a cut that
    /// has not been announced yet is still in the hold.
    /// </summary>
    public const int CommitLagFrames = 22;

    public static bool IsBodyFrame(int frame, int sceneStart, int sceneEnd)
    {
        if (frame < sceneStart || frame >= sceneEnd) return false;
        var length = sceneEnd - sceneStart;
        var mainStart = sceneStart + SkipFrames;
        var mainEnd = sceneEnd - TailFrames;
        // A window shorter than the sample stride can contain no sample. Use the
        // shorter skip so a brief scene still has a frame from inside itself.
        if (mainEnd - mainStart >= SampleStride)
            return frame >= mainStart && frame < mainEnd;
        var fallback = sceneStart + Math.Min(FallbackSkipFrames, length / 2);
        return frame >= fallback && frame < sceneEnd;
    }
}

/// <summary>
/// Running dominant colour for the scene currently being read. Samples land
/// every <see cref="SceneSampling.SampleStride"/> frames. Closing a scene
/// returns one colour per zone and keeps later samples for the next scene.
/// </summary>
public sealed class SceneColorAccumulator
{
    private readonly IReadOnlyList<AiTrackZone> _zones;
    private readonly double _exclusion;
    private readonly int _width;
    private readonly int _height;
    private readonly ZoneHistogram[] _committed;
    private readonly List<HeldSample> _hold = new();
    private int _sceneStart;

    public SceneColorAccumulator(IReadOnlyList<AiTrackZone> zones, double centerExclusionPercent, int width, int height)
    {
        _zones = zones;
        _exclusion = centerExclusionPercent;
        _width = width;
        _height = height;
        _committed = NewHistograms();
    }

    public void Observe(int frameIndex, ReadOnlySpan<byte> rgb)
    {
        if (SceneSampling.SampleStride > 0 && frameIndex % SceneSampling.SampleStride == 0)
        {
            var sample = new HeldSample(frameIndex, NewHistograms());
            for (var i = 0; i < _zones.Count; i++)
                sample.Zones[i].AddPixels(rgb, _width, _height, _zones[i], _exclusion);
            _hold.Add(sample);
        }

        CommitThrough(frameIndex - SceneSampling.CommitLagFrames);
    }

    /// <summary>
    /// Finishes the open scene at <paramref name="endFrame"/> (exclusive).
    /// The returned colours are the dominant colour of each zone.
    /// </summary>
    public Dictionary<string, (byte R, byte G, byte B)> Close(int endFrame)
    {
        for (var i = _hold.Count - 1; i >= 0; i--)
        {
            if (_hold[i].Frame >= endFrame) continue;
            if (SceneSampling.IsBodyFrame(_hold[i].Frame, _sceneStart, endFrame))
            {
                for (var zone = 0; zone < _committed.Length; zone++)
                    _committed[zone].Add(_hold[i].Zones[zone]);
            }
            _hold.RemoveAt(i);
        }

        var colors = new Dictionary<string, (byte R, byte G, byte B)>(StringComparer.Ordinal);
        for (var i = 0; i < _zones.Count; i++)
        {
            colors[_zones[i].ChannelId] = _committed[i].Finish();
            _committed[i].Clear();
        }

        _sceneStart = endFrame;
        return colors;
    }

    private void CommitThrough(int lastFrameInclusive)
    {
        var mainStart = _sceneStart + SceneSampling.SkipFrames;
        for (var i = _hold.Count - 1; i >= 0; i--)
        {
            var frame = _hold[i].Frame;
            if (frame > lastFrameInclusive || frame < mainStart) continue;
            for (var zone = 0; zone < _committed.Length; zone++)
                _committed[zone].Add(_hold[i].Zones[zone]);
            _hold.RemoveAt(i);
        }
    }

    private ZoneHistogram[] NewHistograms()
    {
        var histograms = new ZoneHistogram[_zones.Count];
        for (var i = 0; i < histograms.Length; i++)
            histograms[i] = new ZoneHistogram();
        return histograms;
    }

    private readonly record struct HeldSample(int Frame, ZoneHistogram[] Zones);
}

internal sealed class ZoneHistogram
{
    private const int HueBins = ZoneColor.HueBins;
    private const int BinCount = HueBins * 3 + 3;

    private readonly double[] _weight = new double[BinCount];
    private readonly double[] _sumL = new double[BinCount];
    private readonly double[] _sumA = new double[BinCount];
    private readonly double[] _sumB = new double[BinCount];
    private double _pixelR;
    private double _pixelG;
    private double _pixelB;
    private int _count;

    public void Clear()
    {
        Array.Clear(_weight);
        Array.Clear(_sumL);
        Array.Clear(_sumA);
        Array.Clear(_sumB);
        _pixelR = 0;
        _pixelG = 0;
        _pixelB = 0;
        _count = 0;
    }

    public void Add(ZoneHistogram other)
    {
        for (var i = 0; i < BinCount; i++)
        {
            _weight[i] += other._weight[i];
            _sumL[i] += other._sumL[i];
            _sumA[i] += other._sumA[i];
            _sumB[i] += other._sumB[i];
        }

        _pixelR += other._pixelR;
        _pixelG += other._pixelG;
        _pixelB += other._pixelB;
        _count += other._count;
    }

    public void AddPixels(ReadOnlySpan<byte> rgb, int width, int height, AiTrackZone zone, double centerExclusionPercent)
    {
        if (width <= 0 || height <= 0 || rgb.Length < width * height * 3) return;
        var (left, top, right, bottom) = ZoneColor.ZoneBounds(zone, width, height);
        var cx = (left + right) / 2.0;
        var cy = (top + bottom) / 2.0;
        var rx = (right - left) * centerExclusionPercent / 200.0;
        var ry = (bottom - top) * centerExclusionPercent / 200.0;
        var exclude = rx > 0 && ry > 0;
        for (var y = top; y < bottom; y++)
        for (var x = left; x < right; x++)
        {
            if (exclude)
            {
                var dx = (x - cx) / rx;
                var dy = (y - cy) / ry;
                if (dx * dx + dy * dy <= 1) continue;
            }

            var offset = (y * width + x) * 3;
            AddPixel(rgb[offset], rgb[offset + 1], rgb[offset + 2]);
        }
    }

    public (byte R, byte G, byte B) Finish()
    {
        if (_count == 0) return (0, 0, 0);

        var bestHue = 0;
        var bestL = 0;
        var bestSuper = -1.0;
        for (var hue = 0; hue < HueBins; hue++)
        for (var band = 0; band < 3; band++)
        {
            var super = WeightAt(hue, band) + WeightAt(hue - 1, band) + WeightAt(hue + 1, band);
            if (super > bestSuper)
            {
                bestSuper = super;
                bestHue = hue;
                bestL = band;
            }
        }

        double chromWeight = 0, chromL = 0, chromA = 0, chromB = 0;
        for (var offset = -1; offset <= 1; offset++)
        {
            var index = Index(bestHue + offset, bestL);
            chromWeight += _weight[index];
            chromL += _sumL[index];
            chromA += _sumA[index];
            chromB += _sumB[index];
        }

        var neutralIndex = HueBins * 3;
        var bestNeutral = neutralIndex;
        for (var i = 1; i < 3; i++)
        {
            if (_weight[neutralIndex + i] > _weight[bestNeutral])
                bestNeutral = neutralIndex + i;
        }

        var neutralWeight = _weight[bestNeutral];
        double meanL, meanA, meanB;
        var chromatic = chromWeight >= neutralWeight && chromWeight > 0;
        if (chromatic)
        {
            meanL = chromL / chromWeight;
            meanA = chromA / chromWeight;
            meanB = chromB / chromWeight;
        }
        else if (neutralWeight > 0)
        {
            meanL = _sumL[bestNeutral] / neutralWeight;
            meanA = _sumA[bestNeutral] / neutralWeight;
            meanB = _sumB[bestNeutral] / neutralWeight;
        }
        else
        {
            return (0, 0, 0);
        }

        var color = OklabToSrgb(meanL, meanA, meanB);
        if (!chromatic) return color;

        var zoneLuma = (0.299 * _pixelR + 0.587 * _pixelG + 0.114 * _pixelB) / _count;
        var clusterLuma = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        if (clusterLuma <= zoneLuma + 0.5 || clusterLuma < 1) return color;
        var scale = zoneLuma / clusterLuma;
        return (
            (byte)Math.Clamp(Math.Round(color.R * scale), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * scale), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * scale), 0, 255));
    }

    private void AddPixel(byte r, byte g, byte b)
    {
        var (l, a, b2) = SrgbToOklab(r, g, b);
        var chroma = Math.Sqrt(a * a + b2 * b2);
        var weight = chroma < ZoneColor.NeutralChroma
            ? l < ZoneColor.BlackLightness
                ? ZoneColor.BlackWeight
                : l >= ZoneColor.WhiteLightness ? ZoneColor.WhiteWeight : ZoneColor.GrayWeight
            : l < ZoneColor.BlackLightness ? ZoneColor.BlackWeight : 1.0;
        int bin;
        if (chroma < ZoneColor.NeutralChroma)
        {
            var neutral = l < 0.15 ? 0 : l < 0.75 ? 1 : 2;
            bin = HueBins * 3 + neutral;
        }
        else
        {
            var hue = Math.Atan2(b2, a);
            if (hue < 0) hue += 2 * Math.PI;
            var hueBin = (int)(hue * (HueBins / (2 * Math.PI)));
            if (hueBin >= HueBins) hueBin = HueBins - 1;
            if (hueBin < 0) hueBin = 0;
            var band = l < 0.35 ? 0 : l < 0.70 ? 1 : 2;
            bin = hueBin * 3 + band;
        }

        _weight[bin] += weight;
        _sumL[bin] += weight * l;
        _sumA[bin] += weight * a;
        _sumB[bin] += weight * b2;
        _pixelR += r;
        _pixelG += g;
        _pixelB += b;
        _count++;
    }

    private double WeightAt(int hue, int band) => _weight[Index(hue, band)];

    private static int Index(int hue, int band)
    {
        var wrapped = hue % HueBins;
        if (wrapped < 0) wrapped += HueBins;
        return wrapped * 3 + band;
    }

    private static (double L, double A, double B) SrgbToOklab(byte r8, byte g8, byte b8)
    {
        var r = Linear(r8);
        var g = Linear(g8);
        var b = Linear(b8);
        var l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
        var m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
        var s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;
        var lRoot = Math.Cbrt(l);
        var mRoot = Math.Cbrt(m);
        var sRoot = Math.Cbrt(s);
        return (
            0.2104542553 * lRoot + 0.7936177850 * mRoot - 0.0040720468 * sRoot,
            1.9779984951 * lRoot - 2.4285922050 * mRoot + 0.4505937099 * sRoot,
            0.0259040371 * lRoot + 0.7827717662 * mRoot - 0.8086757660 * sRoot);
    }

    private static (byte R, byte G, byte B) OklabToSrgb(double l, double a, double b)
    {
        var lRoot = l + 0.3963377774 * a + 0.2158037573 * b;
        var mRoot = l - 0.1055613458 * a - 0.0638541728 * b;
        var sRoot = l - 0.0894841775 * a - 1.2914855480 * b;
        var lCube = lRoot * lRoot * lRoot;
        var mCube = mRoot * mRoot * mRoot;
        var sCube = sRoot * sRoot * sRoot;
        var r = +4.0767416621 * lCube - 3.3077115913 * mCube + 0.2309699292 * sCube;
        var g = -1.2684380046 * lCube + 2.6097574011 * mCube - 0.3413193965 * sCube;
        var bl = -0.0041960863 * lCube - 0.7034186147 * mCube + 1.7076147010 * sCube;
        return (Encode(r), Encode(g), Encode(bl));
    }

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static byte Encode(double linear)
    {
        var value = Math.Clamp(linear, 0, 1);
        var encoded = value <= 0.0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(Math.Clamp(encoded, 0, 1) * 255), 0, 255);
    }
}
