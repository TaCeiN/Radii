namespace Radii.Editors;

/// <summary>All the sliders of the colour editor. Ranges are −1..1 unless noted; 0 means "leave alone".</summary>
internal sealed record ColorParams(
    double Exposure = 0,      // EV, -3..3
    double Brightness = 0,
    double Contrast = 0,
    double Highlights = 0,
    double Shadows = 0,
    double Temperature = 0,
    double Tint = 0,
    double Saturation = 0,
    double Vibrance = 0,
    double TealOrange = 0,
    double Fade = 0,          // 0..1
    double Sharpness = 0,     // 0..1
    double Vignette = 0,
    double Grain = 0,         // 0..1
    double BlackPoint = 0,    // 0..1 of the range, raises the black level
    double WhitePoint = 0)    // 0..1, lowers the white level
{
    public bool IsIdentity => this == new ColorParams();
}

/// <summary>The colour maths on raw BGRA pixels. Exposure and white balance work in linear light, the rest in the encoded values, like most photo editors.</summary>
internal static class ColorPipeline
{
    private static readonly float[] ToLinear = BuildToLinear();
    private static readonly float[] FromLinear = BuildFromLinear();

    private static float[] BuildToLinear()
    {
        var table = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255f;
            table[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        return table;
    }

    private static float[] BuildFromLinear()
    {
        var table = new float[4096];
        for (var i = 0; i < table.Length; i++)
        {
            var l = i / 4095f;
            table[i] = l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1 / 2.4f) - 0.055f;
        }
        return table;
    }

    private static float Encode(float linear) => FromLinear[(int)(Math.Clamp(linear, 0f, 1f) * 4095f)];

    /// <summary>Returns a new array; the input stays untouched.</summary>
    public static byte[] Apply(byte[] source, int width, int height, ColorParams p)
    {
        var result = new byte[source.Length];
        if (p.IsIdentity)
        {
            Buffer.BlockCopy(source, 0, result, 0, source.Length);
            return result;
        }

        var exposureGain = MathF.Pow(2, (float)p.Exposure);
        var useLinear = p.Exposure != 0 || p.Temperature != 0 || p.Tint != 0;
        float tr = 1 + 0.30f * (float)p.Temperature + 0.10f * (float)p.Tint;
        float tg = 1 - 0.22f * (float)p.Tint;
        float tb = 1 - 0.30f * (float)p.Temperature + 0.10f * (float)p.Tint;
        var black = (float)p.BlackPoint * 0.4f;
        var white = 1 - (float)p.WhitePoint * 0.4f;
        var levelsScale = 1f / Math.Max(0.05f, white - black);
        var contrast = MathF.Pow(2, (float)p.Contrast * 1.1f);
        var fade = (float)p.Fade;
        var vignette = (float)p.Vignette;
        var grain = (float)p.Grain;
        var halfW = Math.Max(1, width / 2f);
        var halfH = Math.Max(1, height / 2f);

        Parallel.For(0, height, y =>
        {
            var row = y * width * 4;
            var dy = (y - halfH) / halfH;
            for (var x = 0; x < width; x++)
            {
                var i = row + x * 4;
                float b = source[i] / 255f, g = source[i + 1] / 255f, r = source[i + 2] / 255f;

                if (useLinear)
                {
                    var lr = ToLinear[source[i + 2]] * exposureGain * tr;
                    var lg = ToLinear[source[i + 1]] * exposureGain * tg;
                    var lb = ToLinear[source[i]] * exposureGain * tb;
                    r = Encode(lr);
                    g = Encode(lg);
                    b = Encode(lb);
                }
                if (black != 0 || white != 1)
                {
                    r = (r - black) * levelsScale;
                    g = (g - black) * levelsScale;
                    b = (b - black) * levelsScale;
                }

                var lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                lum = Math.Clamp(lum, 0f, 1f);
                if (p.Shadows != 0)
                {
                    var amount = (float)p.Shadows * 0.45f * (1 - lum) * (1 - lum);
                    r += amount * (amount > 0 ? 1 - r : r);
                    g += amount * (amount > 0 ? 1 - g : g);
                    b += amount * (amount > 0 ? 1 - b : b);
                }
                if (p.Highlights != 0)
                {
                    var amount = (float)p.Highlights * 0.45f * lum * lum;
                    r += amount * (amount > 0 ? 1 - r : r);
                    g += amount * (amount > 0 ? 1 - g : g);
                    b += amount * (amount > 0 ? 1 - b : b);
                }
                if (p.Brightness != 0)
                {
                    var add = (float)p.Brightness * 0.3f;
                    r += add;
                    g += add;
                    b += add;
                }
                if (p.Contrast != 0)
                {
                    r = (r - 0.5f) * contrast + 0.5f;
                    g = (g - 0.5f) * contrast + 0.5f;
                    b = (b - 0.5f) * contrast + 0.5f;
                }

                if (p.Saturation != 0 || p.Vibrance != 0)
                {
                    var gray = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    var chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                    var factor = (1 + (float)p.Saturation) * (1 + (float)p.Vibrance * (1 - Math.Clamp(chroma, 0f, 1f)));
                    factor = Math.Max(0f, factor);
                    r = gray + (r - gray) * factor;
                    g = gray + (g - gray) * factor;
                    b = gray + (b - gray) * factor;
                }

                if (p.TealOrange != 0)
                {
                    var l = Math.Clamp(0.2126f * r + 0.7152f * g + 0.0722f * b, 0f, 1f);
                    var amount = (float)p.TealOrange;
                    var dark = (1 - l) * amount;
                    var light = l * amount;
                    r += -0.08f * dark + 0.07f * light;
                    g += 0.02f * dark + 0.02f * light;
                    b += 0.06f * dark - 0.07f * light;
                }

                if (fade > 0)
                {
                    r = r * (1 - 0.14f * fade) + 0.07f * fade;
                    g = g * (1 - 0.14f * fade) + 0.07f * fade;
                    b = b * (1 - 0.14f * fade) + 0.07f * fade;
                }

                if (vignette != 0)
                {
                    var dx = (x - halfW) / halfW;
                    var d = (dx * dx + dy * dy) / 2f;
                    var m = 1 - vignette * 0.75f * d * d * 1.6f;
                    r *= m;
                    g *= m;
                    b *= m;
                }

                if (grain > 0)
                {
                    var noise = Hash(x, y) * grain * 0.22f;
                    r += noise;
                    g += noise;
                    b += noise;
                }

                result[i] = ToByte(b);
                result[i + 1] = ToByte(g);
                result[i + 2] = ToByte(r);
                result[i + 3] = source[i + 3];
            }
        });

        return p.Sharpness > 0 ? Sharpen(result, width, height, (float)p.Sharpness * 1.6f) : result;
    }

    private static byte ToByte(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 255f + 0.5f);

    /// <summary>Stable value in -0.5..0.5 per pixel.</summary>
    private static float Hash(int x, int y)
    {
        var h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFF) / 65535f - 0.5f;
    }

    /// <summary>Unsharp mask with a 3×3 average as the blur.</summary>
    private static byte[] Sharpen(byte[] source, int width, int height, float amount)
    {
        var result = new byte[source.Length];
        Parallel.For(0, height, y =>
        {
            var y0 = Math.Max(0, y - 1);
            var y1 = Math.Min(height - 1, y + 1);
            for (var x = 0; x < width; x++)
            {
                var x0 = Math.Max(0, x - 1);
                var x1 = Math.Min(width - 1, x + 1);
                var i = (y * width + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var sum = 0;
                    for (var yy = y0; yy <= y1; yy++)
                    {
                        for (var xx = x0; xx <= x1; xx++)
                            sum += source[(yy * width + xx) * 4 + c];
                    }
                    var blur = sum / (float)((y1 - y0 + 1) * (x1 - x0 + 1));
                    var v = source[i + c] + amount * (source[i + c] - blur);
                    result[i + c] = (byte)Math.Clamp(v + 0.5f, 0f, 255f);
                }
                result[i + 3] = source[i + 3];
            }
        });
        return result;
    }

    /// <summary>Luminance histogram (256 bins) of BGRA pixels, sampled to stay fast on big pictures.</summary>
    public static int[] Histogram(byte[] pixels)
    {
        var bins = new int[256];
        var step = Math.Max(1, pixels.Length / 4 / 400_000);
        for (var i = 0; i + 3 < pixels.Length; i += 4 * step)
            bins[(int)Math.Round(0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i])]++;
        return bins;
    }

    /// <summary>Black and white points (as slider fractions) that stretch the 0.5%..99.5% range of the picture to full.</summary>
    public static (double Black, double White) AutoLevels(byte[] pixels)
    {
        var bins = Histogram(pixels);
        var total = bins.Sum();
        if (total == 0)
            return (0, 0);
        long run = 0;
        int low = 0, high = 255;
        for (var i = 0; i < 256; i++)
        {
            run += bins[i];
            if (run >= total * 0.005)
            {
                low = i;
                break;
            }
        }
        run = 0;
        for (var i = 255; i >= 0; i--)
        {
            run += bins[i];
            if (run >= total * 0.005)
            {
                high = i;
                break;
            }
        }
        // slider units: black = fraction * 0.4 of the range, white = 1 - fraction * 0.4
        var black = Math.Clamp(low / 255.0 / 0.4, 0, 1);
        var white = Math.Clamp((1 - high / 255.0) / 0.4, 0, 1);
        return (black, white);
    }
}
