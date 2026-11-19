namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Lightning, siren, and fade detection on the 24 fps frames the cut detector already decodes.
/// Fades are not effects: playback replaces the scene colour for the whole effect, so a fade
/// is a ramp on the zone keyframes instead.
/// </summary>
public sealed class EffectPrefill
{
    public const int Fps = 24;
    public const int LightningReturnFrames = 4;
    public const int LightningNearCutFrames = 8;
    public const int LightningMinGapFrames = 48;
    public const double LightningMinConfidence = 0.75;
    public const int SirenMinFrames = 36;
    public const int FadeMinFrames = 12;
    public const int FadeMaxFrames = 96;
    public const int FadeBoundaryFrames = 12;

    private readonly List<float> _luma = new();
    private readonly List<float> _chroma = new();
    private readonly List<float> _red = new();
    private readonly List<float> _blue = new();

    public int FrameCount => _luma.Count;

    public void Observe(ReadOnlySpan<byte> rgb, int width, int height, bool detectLightning = true, bool detectSiren = true)
    {
        if (width <= 0 || height <= 0 || rgb.Length < width * height * 3)
        {
            _luma.Add(0);
            _chroma.Add(0);
            _red.Add(0);
            _blue.Add(0);
            return;
        }

        long luma = 0;
        long chroma = 0;
        var red = 0;
        var blue = 0;
        var pixels = width * height;
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * 3;
            int r = rgb[offset];
            int g = rgb[offset + 1];
            int b = rgb[offset + 2];
            luma += (299 * r + 587 * g + 114 * b) / 1000;
            if (detectLightning)
            {
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                chroma += max - min;
            }
            if (detectSiren)
            {
                if (r > 80 && r > g + 40 && r > b + 40) red++;
                else if (b > 80 && b > r + 40 && b > g + 40) blue++;
            }
        }

        _luma.Add(luma / (float)pixels);
        _chroma.Add(chroma / (float)pixels);
        _red.Add(red / (float)pixels);
        _blue.Add(blue / (float)pixels);
    }

    public IReadOnlyList<LightningHit> Lightning(IReadOnlyList<int> cuts)
    {
        var hits = new List<LightningHit>();
        var last = -LightningMinGapFrames;
        for (var i = 1; i < _luma.Count - 1; i++)
        {
            if (i - last < LightningMinGapFrames) continue;
            if (NearCut(i, cuts)) continue;
            var baseline = MedianBefore(i, 8);
            var jump = _luma[i] - baseline;
            if (jump < 60 || _luma[i] < 150 || _chroma[i] > 45) continue;
            var returned = false;
            var limit = Math.Min(_luma.Count - 1, i + LightningReturnFrames);
            for (var j = i + 1; j <= limit; j++)
            {
                if (_luma[j] <= baseline + 25 || _luma[i] - _luma[j] >= 40)
                {
                    returned = true;
                    break;
                }
            }

            if (!returned) continue;
            var confidence = Math.Clamp(jump / 80.0, 0, 1);
            if (confidence < LightningMinConfidence) continue;
            hits.Add(new LightningHit(i, jump, confidence));
            last = i;
        }

        return hits;
    }

    public IReadOnlyList<SirenRun> Sirens()
    {
        var polarity = new int[_luma.Count];
        for (var i = 0; i < _luma.Count; i++)
        {
            if (_red[i] > 0.07 && _red[i] > _blue[i] * 1.4) polarity[i] = 1;
            else if (_blue[i] > 0.07 && _blue[i] > _red[i] * 1.4) polarity[i] = -1;
        }

        var runs = new List<SirenRun>();
        var gaps = new List<int>();
        var runStart = -1;
        var lastFlip = -1;
        var previous = 0;
        var quiet = 0;
        for (var i = 0; i <= polarity.Length; i++)
        {
            var current = i < polarity.Length ? polarity[i] : 0;
            if (current == 0)
            {
                quiet++;
                if (runStart >= 0 && quiet > 2)
                {
                    CloseSiren(runs, gaps, runStart, i - quiet);
                    gaps.Clear();
                    runStart = -1;
                    previous = 0;
                    lastFlip = -1;
                }

                continue;
            }

            quiet = 0;
            if (runStart < 0)
            {
                runStart = i;
                lastFlip = i;
                previous = current;
                continue;
            }

            if (current == previous)
            {
                if (i - lastFlip > 12)
                {
                    CloseSiren(runs, gaps, runStart, lastFlip + 12);
                    gaps.Clear();
                    runStart = i;
                    lastFlip = i;
                }

                continue;
            }

            var gap = i - lastFlip;
            if (gap < 3 || gap > 12)
            {
                CloseSiren(runs, gaps, runStart, lastFlip);
                gaps.Clear();
                runStart = i;
            }
            else
            {
                gaps.Add(gap);
            }

            lastFlip = i;
            previous = current;
        }

        if (runStart >= 0)
            CloseSiren(runs, gaps, runStart, polarity.Length);
        return runs;
    }

    public bool TryFadeIn(int sceneStart, int sceneEnd, out int fadeEnd, out bool fromWhite)
    {
        fadeEnd = sceneStart;
        fromWhite = false;
        if (sceneEnd - sceneStart < FadeMinFrames || sceneStart < 0 || sceneStart >= _luma.Count)
            return false;
        var best = 0;
        var white = false;
        var limit = Math.Min(sceneEnd - 1, Math.Min(_luma.Count - 1, sceneStart + FadeMaxFrames));
        for (var end = sceneStart + FadeMinFrames; end <= limit; end++)
        {
            if (!Ramp(sceneStart, end, out var rising, out var delta)) continue;
            if (rising && _luma[sceneStart] <= 18 && delta >= 40)
            {
                best = end;
                white = false;
            }
            else if (!rising && _luma[sceneStart] >= 225 && delta >= 40)
            {
                best = end;
                white = true;
            }
        }

        if (best == 0) return false;
        fadeEnd = best;
        fromWhite = white;
        return true;
    }

    public bool TryFadeOut(int sceneStart, int sceneEnd, out int fadeStart, out bool toWhite)
    {
        fadeStart = sceneEnd;
        toWhite = false;
        var end = Math.Min(sceneEnd, _luma.Count) - 1;
        if (end - sceneStart < FadeMinFrames || end <= 0) return false;
        var best = -1;
        var white = false;
        var earliest = Math.Max(sceneStart, end - FadeMaxFrames);
        for (var start = end - FadeMinFrames; start >= earliest; start--)
        {
            if (!Ramp(start, end, out var rising, out var delta) || delta < 40) continue;
            if (!rising && _luma[end] <= 18)
            {
                best = start;
                white = false;
            }
            else if (rising && _luma[end] >= 225)
            {
                best = start;
                white = true;
            }
        }

        if (best < 0) return false;
        fadeStart = best;
        toWhite = white;
        return true;
    }

    private bool Ramp(int start, int end, out bool rising, out float delta)
    {
        rising = _luma[end] >= _luma[start];
        delta = Math.Abs(_luma[end] - _luma[start]);
        var wrong = 0;
        var steps = 0;
        for (var i = start; i < end; i++)
        {
            var step = _luma[i + 1] - _luma[i];
            if (Math.Abs(step) < 1.5f) continue;
            steps++;
            if (rising && step < 0) wrong++;
            else if (!rising && step > 0) wrong++;
        }

        return steps == 0 || wrong / (double)steps <= 0.35;
    }

    private bool NearCut(int frame, IReadOnlyList<int> cuts)
    {
        foreach (var cut in cuts)
        {
            if (Math.Abs(cut - frame) <= LightningNearCutFrames) return true;
        }

        return false;
    }

    private float MedianBefore(int frame, int count)
    {
        var start = Math.Max(0, frame - count);
        var window = new float[frame - start];
        for (var i = 0; i < window.Length; i++) window[i] = _luma[start + i];
        Array.Sort(window);
        if (window.Length == 0) return _luma[frame];
        return window[window.Length / 2];
    }

    private static void CloseSiren(List<SirenRun> runs, List<int> gaps, int start, int end)
    {
        if (gaps.Count < 3 || end - start < SirenMinFrames) return;
        var ordered = gaps.ToArray();
        Array.Sort(ordered);
        var median = ordered[ordered.Length / 2];
        var rateHz = Fps / (2.0 * median);
        if (rateHz < 0.9 || rateHz > 4.2) return;
        runs.Add(new SirenRun(start, end, rateHz));
    }
}

public readonly record struct LightningHit(int Frame, float Jump, double Confidence);

public readonly record struct SirenRun(int StartFrame, int EndFrame, double RateHz);
