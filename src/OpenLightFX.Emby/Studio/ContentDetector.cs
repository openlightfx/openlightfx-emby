// Portions of this file are derived from PySceneDetect by Brandon Castellano.
// https://github.com/Breakthrough/PySceneDetect
//
// Copyright (C) 2014, Brandon Castellano
//
// BSD 3-Clause License
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are met:
//
// 1. Redistributions of source code must retain the above copyright notice, this
//    list of conditions and the following disclaimer.
//
// 2. Redistributions in binary form must reproduce the above copyright notice,
//    this list of conditions and the following disclaimer in the documentation
//    and/or other materials provided with the distribution.
//
// 3. Neither the name of the copyright holder nor the names of its
//    contributors may be used to endorse or promote products derived from
//    this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
// DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
// FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
// SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
// CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
// OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// C# port of PySceneDetect <c>ContentDetector</c> (default weights, edges off) and its
/// default <c>FlashFilter</c> in MERGE mode. The score is the weighted mean absolute
/// difference of OpenCV 8-bit HSV planes between consecutive frames. Hue is not circular.
/// </summary>
public sealed class ContentDetector
{
    public const string Name = "ContentDetector";

    /// <summary>Bump when the saved cut stream would no longer match this port.</summary>
    public const string PersistenceVersion = "1";

    public const double DefaultThreshold = 27.0;
    public const int DefaultMinSceneLength = 15;

    /// <summary>Studio slider value that maps to <see cref="DefaultThreshold"/>.</summary>
    public const double StudioSettingDefault = 0.27;
    public const double StudioSettingMin = 0.01;
    public const double StudioSettingMax = 1.0;

    private const double HueWeight = 1.0;
    private const double SaturationWeight = 1.0;
    private const double ValueWeight = 1.0;
    private const double EdgeWeight = 0.0;
    private const int HsvShift = 12;

    private static readonly int[] SaturationDiv = BuildSaturationDiv();
    private static readonly int[] HueDiv = BuildHueDiv();

    private readonly double _threshold;
    private readonly FlashFilter _flash;
    private byte[] _hue = Array.Empty<byte>();
    private byte[] _sat = Array.Empty<byte>();
    private byte[] _val = Array.Empty<byte>();
    private byte[] _previousHue = Array.Empty<byte>();
    private byte[] _previousSat = Array.Empty<byte>();
    private byte[] _previousVal = Array.Empty<byte>();
    private bool _hasPrevious;
    private int _frameNumber;

    public ContentDetector(double threshold = DefaultThreshold, int minSceneLength = DefaultMinSceneLength)
    {
        _threshold = threshold;
        _flash = new FlashFilter(minSceneLength);
    }

    public double LastScore { get; private set; }

    /// <summary>
    /// Maps Studio's 0.01–1 scene-cut slider onto PySceneDetect's content score.
    /// 0.27 becomes 27. Values outside the slider are clamped before scaling.
    /// </summary>
    public static double ThresholdFromStudioSetting(double sceneCutThreshold)
    {
        var slider = Math.Clamp(sceneCutThreshold, StudioSettingMin, StudioSettingMax);
        return Math.Round(slider * 100.0, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// OpenCV <c>COLOR_RGB2HSV</c> for 8-bit pixels (hue 0–179). <paramref name="rgb"/> is
    /// tightly packed RGB24.
    /// </summary>
    public static void ConvertRgbToHsv(ReadOnlySpan<byte> rgb, Span<byte> hue, Span<byte> saturation, Span<byte> value)
    {
        if (hue.Length != saturation.Length || hue.Length != value.Length)
            throw new ArgumentException("HSV planes must be the same length");
        if (rgb.Length < hue.Length * 3)
            throw new ArgumentException("RGB buffer is shorter than the HSV planes");

        const int round = 1 << (HsvShift - 1);
        for (var i = 0; i < hue.Length; i++)
        {
            var offset = i * 3;
            int r = rgb[offset];
            int g = rgb[offset + 1];
            int b = rgb[offset + 2];
            int v = b;
            int vmin = b;
            if (g > v) v = g;
            if (r > v) v = r;
            if (g < vmin) vmin = g;
            if (r < vmin) vmin = r;

            int diff = v - vmin;
            int vr = v == r ? -1 : 0;
            int vg = v == g ? -1 : 0;
            int s = (diff * SaturationDiv[v] + round) >> HsvShift;
            int h = (vr & (g - b)) +
                (~vr & ((vg & (b - r + 2 * diff)) + ((~vg) & (r - g + 4 * diff))));
            h = (h * HueDiv[diff] + round) >> HsvShift;
            if (h < 0) h += 180;
            if (h < 0) h = 0;
            else if (h > 255) h = 255;
            if (s < 0) s = 0;
            else if (s > 255) s = 255;

            hue[i] = (byte)h;
            saturation[i] = (byte)s;
            value[i] = (byte)v;
        }
    }

    /// <summary>
    /// Scores <paramref name="rgb"/> against the previous frame. Returns the frame numbers
    /// where a cut was confirmed. A cut can be an earlier frame when the flash filter is
    /// still merging a run shorter than the minimum scene length.
    /// </summary>
    public IReadOnlyList<int> ProcessFrame(ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var pixels = width * height;
        if (rgb.Length < pixels * 3)
            throw new ArgumentException("RGB buffer is shorter than width*height*3");

        LastScore = Score(rgb[..(pixels * 3)], pixels);
        var above = LastScore >= _threshold;
        var frame = _frameNumber++;
        if (!_flash.TryAccept(frame, above, out var cut))
            return Array.Empty<int>();
        return new[] { cut };
    }

    private double Score(ReadOnlySpan<byte> rgb, int pixels)
    {
        if (_hue.Length != pixels)
        {
            _hue = new byte[pixels];
            _sat = new byte[pixels];
            _val = new byte[pixels];
            _previousHue = new byte[pixels];
            _previousSat = new byte[pixels];
            _previousVal = new byte[pixels];
            _hasPrevious = false;
        }

        ConvertRgbToHsv(rgb, _hue, _sat, _val);
        if (!_hasPrevious)
        {
            Swap();
            _hasPrevious = true;
            return 0;
        }

        long hue = 0, sat = 0, val = 0;
        for (var i = 0; i < pixels; i++)
        {
            hue += Math.Abs(_hue[i] - _previousHue[i]);
            sat += Math.Abs(_sat[i] - _previousSat[i]);
            val += Math.Abs(_val[i] - _previousVal[i]);
        }

        Swap();
        var count = (double)pixels;
        // Edges stay at 0 because the default weight is 0, which is also the path that
        // skips Canny. The denominator still includes that weight, matching upstream.
        var weighted = hue / count * HueWeight
            + sat / count * SaturationWeight
            + val / count * ValueWeight
            + 0 * EdgeWeight;
        var weightSum = Math.Abs(HueWeight) + Math.Abs(SaturationWeight) + Math.Abs(ValueWeight) + Math.Abs(EdgeWeight);
        return weighted / weightSum;
    }

    private void Swap()
    {
        (_previousHue, _hue) = (_hue, _previousHue);
        (_previousSat, _sat) = (_sat, _previousSat);
        (_previousVal, _val) = (_val, _previousVal);
    }

    private static int[] BuildSaturationDiv()
    {
        var table = new int[256];
        for (var i = 1; i < table.Length; i++)
            table[i] = (int)((255 << HsvShift) / (double)i);
        return table;
    }

    private static int[] BuildHueDiv()
    {
        var table = new int[256];
        for (var i = 1; i < table.Length; i++)
            table[i] = (int)((180 << HsvShift) / (6.0 * i));
        return table;
    }

    /// <summary>PySceneDetect <c>FlashFilter</c> MERGE mode, comparing integer frame counts.</summary>
    private sealed class FlashFilter
    {
        private readonly int _length;
        private int? _lastAbove;
        private bool _mergeEnabled;
        private bool _mergeTriggered;
        private int _mergeStart;

        public FlashFilter(int length) => _length = length;

        public bool TryAccept(int frame, bool above, out int cut)
        {
            cut = 0;
            if (_length <= 0)
            {
                if (!above) return false;
                cut = frame;
                return true;
            }

            _lastAbove ??= frame;
            var minLengthMet = frame - _lastAbove.Value >= _length;
            if (above)
                _lastAbove = frame;

            if (_mergeTriggered)
            {
                if (minLengthMet && !above && _lastAbove.Value - _mergeStart >= _length)
                {
                    _mergeTriggered = false;
                    cut = _lastAbove.Value;
                    return true;
                }

                return false;
            }

            if (!above)
                return false;
            if (minLengthMet)
            {
                _mergeEnabled = true;
                cut = frame;
                return true;
            }

            if (_mergeEnabled)
            {
                _mergeTriggered = true;
                _mergeStart = frame;
            }

            return false;
        }
    }
}
