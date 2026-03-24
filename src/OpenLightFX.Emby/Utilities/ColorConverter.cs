namespace OpenLightFX.Emby.Utilities;

/// <summary>
/// Static utility class for color space conversions used by bulb drivers
/// and the interpolation engine.
/// </summary>
public static class ColorConverter
{
    /// <summary>
    /// Converts RGB to CIE xy color space for Philips Hue bulbs.
    /// Uses Wide RGB D65 conversion matrix with sRGB gamma correction.
    /// </summary>
    public static (double x, double y, double brightness) RgbToCieXy(byte r, byte g, byte b)
    {
        // sRGB gamma correction → linear
        double red = GammaToLinear(r / 255.0);
        double green = GammaToLinear(g / 255.0);
        double blue = GammaToLinear(b / 255.0);

        // Wide RGB D65 conversion matrix
        double X = red * 0.664511 + green * 0.154324 + blue * 0.162028;
        double Y = red * 0.283881 + green * 0.668433 + blue * 0.047685;
        double Z = red * 0.000088 + green * 0.072310 + blue * 0.986039;

        double sum = X + Y + Z;
        if (sum == 0.0)
        {
            return (0.0, 0.0, 0.0);
        }

        double x = X / sum;
        double y = Y / sum;
        double brightness = Y;

        return (x, y, brightness);
    }

    /// <summary>
    /// Converts CIE xy color space back to RGB.
    /// </summary>
    public static (byte r, byte g, byte b) CieXyToRgb(double x, double y, double brightness)
    {
        if (y == 0.0)
        {
            return (0, 0, 0);
        }

        double z = 1.0 - x - y;
        double Y = brightness;
        double X = (Y / y) * x;
        double Z = (Y / y) * z;

        // Inverse of Wide RGB D65 matrix
        double red = X * 1.656492 + Y * -0.354851 + Z * -0.255038;
        double green = X * -0.707196 + Y * 1.655397 + Z * 0.036152;
        double blue = X * 0.051713 + Y * -0.121364 + Z * 1.011530;

        // Clamp to [0, 1] before reverse gamma
        red = Math.Clamp(red, 0.0, 1.0);
        green = Math.Clamp(green, 0.0, 1.0);
        blue = Math.Clamp(blue, 0.0, 1.0);

        // Linear → sRGB gamma
        red = LinearToGamma(red);
        green = LinearToGamma(green);
        blue = LinearToGamma(blue);

        return (
            (byte)Math.Clamp(Math.Round(red * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round(green * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round(blue * 255.0), 0, 255)
        );
    }

    /// <summary>
    /// Converts RGB to LIFX HSBK color space.
    /// LIFX uses 0-65535 ranges for hue, saturation, and brightness.
    /// Kelvin is only relevant when saturation is 0.
    /// </summary>
    public static (ushort hue, ushort saturation, ushort brightness, ushort kelvin) RgbToHsbk(
        byte r, byte g, byte b, ushort kelvin = 3500)
    {
        var (h, s, v) = RgbToHsv(r, g, b);

        // Map to LIFX 0-65535 ranges
        ushort hue = (ushort)Math.Clamp(Math.Round(h / 360.0 * 65535.0), 0, 65535);
        ushort saturation = (ushort)Math.Clamp(Math.Round(s * 65535.0), 0, 65535);
        ushort brightness = (ushort)Math.Clamp(Math.Round(v * 65535.0), 0, 65535);

        return (hue, saturation, brightness, kelvin);
    }

    /// <summary>
    /// Converts LIFX HSBK color space back to RGB.
    /// </summary>
    public static (byte r, byte g, byte b) HsbkToRgb(ushort hue, ushort saturation, ushort brightness)
    {
        double h = hue / 65535.0 * 360.0;
        double s = saturation / 65535.0;
        double v = brightness / 65535.0;

        return HsvToRgb(h, s, v);
    }

    /// <summary>
    /// Converts RGB to HSV. H is 0-360 degrees, S and V are 0-1.
    /// </summary>
    public static (double h, double s, double v) RgbToHsv(byte r, byte g, byte b)
    {
        double red = r / 255.0;
        double green = g / 255.0;
        double blue = b / 255.0;

        double max = Math.Max(red, Math.Max(green, blue));
        double min = Math.Min(red, Math.Min(green, blue));
        double delta = max - min;

        double h = 0.0;
        double s = max == 0.0 ? 0.0 : delta / max;
        double v = max;

        if (delta != 0.0)
        {
            if (max == red)
            {
                h = 60.0 * (((green - blue) / delta) % 6.0);
            }
            else if (max == green)
            {
                h = 60.0 * (((blue - red) / delta) + 2.0);
            }
            else
            {
                h = 60.0 * (((red - green) / delta) + 4.0);
            }

            if (h < 0.0)
            {
                h += 360.0;
            }
        }

        return (h, s, v);
    }

    /// <summary>
    /// Converts HSV to RGB. H is 0-360 degrees, S and V are 0-1.
    /// </summary>
    public static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);

        double c = v * s;
        double x = c * (1.0 - Math.Abs((h / 60.0) % 2.0 - 1.0));
        double m = v - c;

        double red, green, blue;

        switch (h)
        {
            case < 60.0:
                (red, green, blue) = (c, x, 0.0);
                break;
            case < 120.0:
                (red, green, blue) = (x, c, 0.0);
                break;
            case < 180.0:
                (red, green, blue) = (0.0, c, x);
                break;
            case < 240.0:
                (red, green, blue) = (0.0, x, c);
                break;
            case < 300.0:
                (red, green, blue) = (x, 0.0, c);
                break;
            default:
                (red, green, blue) = (c, 0.0, x);
                break;
        }

        return (
            (byte)Math.Clamp(Math.Round((red + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((green + m) * 255.0), 0, 255),
            (byte)Math.Clamp(Math.Round((blue + m) * 255.0), 0, 255)
        );
    }

    /// <summary>
    /// Approximates RGB values for a given color temperature in Kelvin
    /// using Tanner Helland's algorithm.
    /// </summary>
    public static (byte r, byte g, byte b) KelvinToRgb(uint kelvin)
    {
        double temp = kelvin / 100.0;
        double red, green, blue;

        // Red
        if (temp <= 66.0)
        {
            red = 255.0;
        }
        else
        {
            red = 329.698727446 * Math.Pow(temp - 60.0, -0.1332047592);
        }

        // Green
        if (temp <= 66.0)
        {
            green = 99.4708025861 * Math.Log(temp) - 161.1195681661;
        }
        else
        {
            green = 288.1221695283 * Math.Pow(temp - 60.0, -0.0755148492);
        }

        // Blue
        if (temp >= 66.0)
        {
            blue = 255.0;
        }
        else if (temp <= 19.0)
        {
            blue = 0.0;
        }
        else
        {
            blue = 138.5177312231 * Math.Log(temp - 10.0) - 305.0447927307;
        }

        return (
            (byte)Math.Clamp(Math.Round(red), 0, 255),
            (byte)Math.Clamp(Math.Round(green), 0, 255),
            (byte)Math.Clamp(Math.Round(blue), 0, 255)
        );
    }

    /// <summary>
    /// Shifts the hue of an RGB color by the specified offset in degrees.
    /// </summary>
    public static (byte r, byte g, byte b) ApplyHueOffset(byte r, byte g, byte b, double hueOffsetDegrees)
    {
        var (h, s, v) = RgbToHsv(r, g, b);
        h = ((h + hueOffsetDegrees) % 360.0 + 360.0) % 360.0;
        return HsvToRgb(h, s, v);
    }

    /// <summary>
    /// Applies a brightness offset to an RGB color and brightness value.
    /// brightnessOffset is -1.0 to 1.0, applied to the 0-100 brightness value, clamped.
    /// </summary>
    public static (byte r, byte g, byte b, uint brightness) ApplyBrightnessOffset(
        byte r, byte g, byte b, uint brightness, double brightnessOffset)
    {
        brightnessOffset = Math.Clamp(brightnessOffset, -1.0, 1.0);

        double adjusted = brightness + brightnessOffset * 100.0;
        uint newBrightness = (uint)Math.Clamp(Math.Round(adjusted), 0, 100);

        // Scale RGB channels proportionally
        double scale = brightness == 0 ? 0.0 : (double)newBrightness / brightness;
        byte newR = (byte)Math.Clamp(Math.Round(r * scale), 0, 255);
        byte newG = (byte)Math.Clamp(Math.Round(g * scale), 0, 255);
        byte newB = (byte)Math.Clamp(Math.Round(b * scale), 0, 255);

        return (newR, newG, newB, newBrightness);
    }

    /// <summary>
    /// Linearly interpolates between two RGB colors. t is 0-1.
    /// </summary>
    public static (byte r, byte g, byte b) InterpolateRgb(
        byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);

        return (
            (byte)Math.Clamp(Math.Round(r1 + (r2 - r1) * t), 0, 255),
            (byte)Math.Clamp(Math.Round(g1 + (g2 - g1) * t), 0, 255),
            (byte)Math.Clamp(Math.Round(b1 + (b2 - b1) * t), 0, 255)
        );
    }

    /// <summary>
    /// sRGB gamma correction: gamma-encoded value → linear.
    /// </summary>
    private static double GammaToLinear(double value)
    {
        return value > 0.04045
            ? Math.Pow((value + 0.055) / 1.055, 2.4)
            : value / 12.92;
    }

    /// <summary>
    /// Reverse sRGB gamma correction: linear → gamma-encoded value.
    /// </summary>
    private static double LinearToGamma(double value)
    {
        return value <= 0.0031308
            ? 12.92 * value
            : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
    }
}
