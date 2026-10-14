namespace ScoreStudio.Recognition;

/// <summary>
/// Measures the distance between staff lines in a picture (Audiveris' "interline", in pixels), to tell whether the
/// picture must be enlarged before recognition (docs/OMR_IMPORT.md, step 4).
/// </summary>
public static class StaffSpacing
{
    /// <summary>A pixel darker than this counts as ink.</summary>
    private const int InkThreshold = 160;

    private const int Smallest = 3;
    private const int Largest = 120;

    /// <summary>
    /// The usual distance from one staff line to the next, in pixels, or null when the picture shows no five-line
    /// staves. Like Audiveris, it looks down sampled columns at each ink run and the blank run after it: on staff lines
    /// their sum is the line spacing, which is by far the most common sum.
    /// </summary>
    public static double? Estimate(GrayImage image)
    {
        var histogram = new int[Largest + 2];
        var columns = 0;
        var step = Math.Max(1, image.Width / 600);
        for (var x = 0; x < image.Width; x += step)
        {
            columns++;
            var y = 0;
            while (y < image.Height)
            {
                // An ink run, then the blank run after it.
                while (y < image.Height && image[x, y] >= InkThreshold) y++;
                var inkStart = y;
                while (y < image.Height && image[x, y] < InkThreshold) y++;
                var blankStart = y;
                while (y < image.Height && image[x, y] >= InkThreshold) y++;
                if (y >= image.Height) break; // the blank run must end on ink, between two lines
                var ink = blankStart - inkStart;
                var combo = y - inkStart;
                if (ink > 0 && ink * 2 < combo && combo is >= Smallest and <= Largest) histogram[combo]++;
            }
        }

        var peak = Enumerable.Range(Smallest, Largest - Smallest + 1).MaxBy(c => histogram[c]);
        // A staff gives four spacings per column across most of the width; require that for at least one staff.
        if (histogram[peak] < Math.Max(8, columns)) return null;

        var (sum, weight) = (0.0, 0.0);
        for (var c = peak - 1; c <= peak + 1; c++)
        {
            sum += c * histogram[c];
            weight += histogram[c];
        }

        return sum / weight;
    }
}

/// <summary>Enlarging pictures whose staff lines are too close for recognition.</summary>
public static class ImageScaling
{
    /// <summary>Audiveris rejects spacings near 10 pixels; it reads 300 dpi scans, about 20 pixels, well.</summary>
    public const double TargetSpacing = 20;

    /// <summary>Spacings from this up are left as they are.</summary>
    public const double EnoughSpacing = 18;

    public const int MaxFactor = 4;

    /// <summary>The whole factor to enlarge by: 1 when the spacing is enough, 2 when it could not be measured.</summary>
    public static int Factor(double? spacing) => spacing switch
    {
        null => 2,
        >= EnoughSpacing => 1,
        { } s => Math.Clamp((int)Math.Ceiling(TargetSpacing / s), 2, MaxFactor),
    };

    /// <summary>Enlarges by a whole factor with bilinear interpolation (pixel centres kept aligned).</summary>
    public static GrayImage Enlarge(GrayImage image, int factor)
    {
        if (factor <= 1) return image;
        var result = new GrayImage(image.Width * factor, image.Height * factor);
        for (var y = 0; y < result.Height; y++)
        {
            var sy = Math.Clamp((y + 0.5) / factor - 0.5, 0, image.Height - 1);
            var (y0, fy) = ((int)sy, sy - (int)sy);
            var y1 = Math.Min(y0 + 1, image.Height - 1);
            for (var x = 0; x < result.Width; x++)
            {
                var sx = Math.Clamp((x + 0.5) / factor - 0.5, 0, image.Width - 1);
                var (x0, fx) = ((int)sx, sx - (int)sx);
                var x1 = Math.Min(x0 + 1, image.Width - 1);
                var top = image[x0, y0] * (1 - fx) + image[x1, y0] * fx;
                var bottom = image[x0, y1] * (1 - fx) + image[x1, y1] * fx;
                result[x, y] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
            }
        }

        return result;
    }
}
